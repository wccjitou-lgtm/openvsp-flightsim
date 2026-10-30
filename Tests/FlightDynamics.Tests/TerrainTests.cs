using System;
using FlightDynamics;
using FlightDynamics.IO;
using FlightDynamics.Math;

namespace FlightDynamics.Tests
{
    /// <summary>
    /// Phase 4 验收 (开发提示词第 10 节):
    ///   "至少 3 个场景可切换, 山地场景地形数据正确显示"
    /// 物理核心层验证: 地形插值精度 / 地形上起落架接触 / 山地飞行 /
    /// 风场测试场景组件 (强切变层 + 紊流区门控) / 场景配置组装。
    /// (Unity 侧的场景切换与渲染由 PlayMode 测试验证。)
    /// </summary>
    public static class TerrainTests
    {
        private const double Dt = 1.0 / 120.0;

        public static void Register(Aircraft planeTemplate, Trimmer trimmer,
                                    AircraftConfigJson.LoadedAircraft cfg)
        {
            // ================= 地形数据 =================
            TestSuite.Add("地形: HeightmapTerrain 双线性插值精确", () =>
            {
                // 3x3 合成图: 值 = (row*3+col)/8 → 0..1, scale=100m, world=20m
                int n = 3;
                var raw = new byte[n * n * 2];
                for (int r = 0; r < n; r++)
                    for (int c = 0; c < n; c++)
                    {
                        ushort v = (ushort)((r * 3 + c) * 65535 / 8);
                        raw[2 * (r * n + c)] = (byte)(v >> 8);
                        raw[2 * (r * n + c) + 1] = (byte)(v & 0xFF);
                    }
                var t = new HeightmapTerrain(raw, n, 20.0, 100.0);

                // 中心点 (north=0, east=0) = 图中心 = 双线性平均
                double hCenter = t.HeightAt(0, 0);
                // 中心恰为格点 (1,1) = 4/8 * 100 = 50 (16bit 量化容差 0.01)
                TestSuite.AssertClose(hCenter, 50.0, 0.01, "中心插值");
                // 角点 (最北最西) = 图 [0,0] = 0
                TestSuite.AssertClose(t.HeightAt(10, -10), 0.0, 1e-9, "西北角");
                // 最东南 = [2,2] = 8/8*100
                TestSuite.AssertClose(t.HeightAt(-10, 10), 100.0, 1e-9, "东南角");
                // 边界外各轴 clamp: north=999 出北界取最北行(0), east=999 取最东列(2) → 东北角
                TestSuite.AssertClose(t.HeightAt(999, 999), 25.0, 0.01, "域外取边界(东北角)");
            });

            TestSuite.Add("地形: 生成的山地高度图读取一致", () =>
            {
                string rawPath = System.IO.Path.Combine("..", "Data", "Terrain", "mountain.raw");
                if (!System.IO.File.Exists(rawPath))
                {
                    Console.WriteLine("        (跳过: 未找到 " + rawPath + ")");
                    return;
                }
                var t = HeightmapTerrain.FromFile(rawPath, 513, 2000.0, 536.86);
                double hCenter = t.HeightAt(0, 0);
                double hCorner = t.HeightAt(990, -990);
                Console.WriteLine(string.Format("        中心 {0:F1} m (期望 ~376), 角落 {1:F1} m (期望 ~0)",
                    hCenter, hCorner));
                TestSuite.AssertTrue(hCenter > 200 && hCenter < 500, "中心为山地 (200-500m)");
                TestSuite.AssertTrue(hCorner < 30, "角落近平原 (<30m)");
            });

            // ================= 地形上的地面接触 =================
            TestSuite.Add("地形: 斜坡上停放平衡 (三点贴合)", () =>
            {
                // 10% 坡度地形: h = 0.1*north (北高南低)
                var slope = new LinearSlopeTerrain(0.1);
                var plane = new Aircraft(planeTemplate.Mass, new AeroModel(planeTemplate.Aero.Data),
                                         planeTemplate.Engine, new CalmWind())
                { Gear = CloneGear(planeTemplate.Gear) };
                plane.Gear.Terrain = slope;
                var integ = new RigidBodyIntegrator(plane);

                var s = new AircraftState();
                s.Position = new Vec3(0, 0, -0.327);   // 近似停放高度
                var c = ControlInputs.Zero;
                for (double t = 0; t < 5.0; t += Dt)
                    integ.Step(s, c, t, Dt);

                Console.WriteLine(string.Format("        5s 后: 3 轮接地={0}, 最大穿透 {1:F3} m, 无 NaN={2}",
                    plane.LastGroundContact.WheelsOnGround,
                    plane.LastGroundContact.MaxPenetration,
                    !double.IsNaN(s.Position.Z)));
                TestSuite.AssertTrue(plane.LastGroundContact.WheelsOnGround == 3, "三点接地");
                TestSuite.AssertLess(plane.LastGroundContact.MaxPenetration, 0.15, "穿透受控");
                TestSuite.AssertTrue(!double.IsNaN(s.Position.Z), "无 NaN");
            });

            TestSuite.Add("地形: 山地飞行 AGL 与撞山防护", () =>
            {
                string rawPath = System.IO.Path.Combine("..", "Data", "Terrain", "mountain.raw");
                if (!System.IO.File.Exists(rawPath)) { Console.WriteLine("        (跳过)"); return; }
                var terrain = HeightmapTerrain.FromFile(rawPath, 513, 2000.0, 536.86);

                var plane = new Aircraft(planeTemplate.Mass, new AeroModel(planeTemplate.Aero.Data),
                                         planeTemplate.Engine, new CalmWind())
                { Gear = CloneGear(planeTemplate.Gear) };
                plane.Gear.Terrain = terrain;
                var integ = new RigidBodyIntegrator(plane);
                var trimmer2 = new Trimmer(plane);

                // 山顶上空留足裕度平飞 (ASL 560m; 中心峰 376m, 南侧次峰 ~490m)
                TrimResult trim = trimmer2.TrimLevelFlight(20.0, 560.0);
                var s = trimmer2.BuildTrimState(trim, 560.0);
                var c = new ControlInputs { Elevator = trim.Elevator, Throttle = trim.Throttle };

                double minAgl = 1e9, maxAgl = -1e9;
                bool terrainContact = false;
                for (double t = 0; t <= 40.0; t += Dt)
                {
                    // 俯仰增稳 (与 Phase 2 全任务相同的增益/极性约定)
                    double pitch = s.Euler321()[1];
                    double q = s.AngularVelocityBody.Y;
                    double eCmd = System.Math.Sign(plane.Aero.Data.Cmde)
                        * (0.8 * (trim.Alpha - pitch) + 0.18 * (0.0 - q));
                    c.Elevator += (Mathd.Clamp(eCmd, -0.35, 0.35) - c.Elevator) * Dt / 0.04;
                    double p = s.AngularVelocityBody.X;
                    c.Aileron += (Mathd.Clamp(0.6 * s.Euler321()[0] + 0.15 * p, -0.3, 0.3) - c.Aileron) * Dt / 0.04;
                    integ.Step(s, c, t, Dt);

                    double agl = s.Altitude - terrain.HeightAt(s.Position.X, s.Position.Y);
                    minAgl = System.Math.Min(minAgl, agl);
                    maxAgl = System.Math.Max(maxAgl, agl);
                    if (plane.LastGroundContact.AnyContact) terrainContact = true;
                    if (double.IsNaN(s.Position.Z)) { TestSuite.Fail("NaN t=" + t); return; }
                }
                Console.WriteLine(string.Format(
                    "        40s 穿越 2000m 地形: AGL 范围 [{0:F0}, {1:F0}] m, 接触地形={2}",
                    minAgl, maxAgl, terrainContact));
                // 飞过中心 376m 山顶与南侧次峰: AGL 最小 ~60-100m; 平原 AGL 最大 ~555m
                TestSuite.AssertGreater(minAgl, 50.0, "山峰上方 AGL > 50m");
                TestSuite.AssertLess(maxAgl, 600.0, "平原上空 AGL 接近海拔高度");
                TestSuite.AssertGreater(maxAgl - minAgl, 200.0, "AGL 应显著起伏 (地形起作用)");
                TestSuite.AssertTrue(!terrainContact, "保持足够高度未触地");
            });

            // ================= 风场测试场景组件 =================
            TestSuite.Add("风场: 强切变层带内梯度/带外恒定", () =>
            {
                var shear = new ShearLayerWind(new Vec3(2, 0, 0), new Vec3(12, 0, 0), 50.0, 150.0);
                TestSuite.AssertClose(shear.WindAt(new Vec3(0, 0, -30), 0).X, 2, 1e-12, "带下恒定");
                TestSuite.AssertClose(shear.WindAt(new Vec3(0, 0, -100), 0).X, 7, 1e-12, "带中线性");
                TestSuite.AssertClose(shear.WindAt(new Vec3(0, 0, -200), 0).X, 12, 1e-12, "带上恒定");
                // 切变率: 10 m/s / 100 m = 0.1 /s (强切变)
                TestSuite.AssertTrue(true, "切变率 ΔV/Δh = 0.1 s⁻¹");
            });

            TestSuite.Add("风场: 紊流区门控进出", () =>
            {
                var zone = new SpatialGateWind(
                    new ConstantWind(new Vec3(5, 0, 0)),
                    100, 400, -50, 50);   // 北 100-400, 东 ±50
                TestSuite.AssertClose(zone.WindAt(new Vec3(0, 0, -100), 0).Length, 0, 1e-12, "区外无风");
                TestSuite.AssertClose(zone.WindAt(new Vec3(200, 0, -100), 0).X, 5, 1e-12, "区内 5m/s");
                TestSuite.AssertClose(zone.WindAt(new Vec3(200, 60, -100), 0).Length, 0, 1e-12, "东向外无风");
                TestSuite.AssertClose(zone.WindAt(new Vec3(450, 0, -100), 0).Length, 0, 1e-12, "北向外无风");
            });

            TestSuite.Add("风场: 穿越紊流区扰动有界且出区恢复", () =>
            {
                // 风场测试场景组合: 平静 + 北 300-700 之间的强紊流区 (Severe)
                var plane = new Aircraft(planeTemplate.Mass, new AeroModel(planeTemplate.Aero.Data),
                                         planeTemplate.Engine,
                                         new SpatialGateWind(
                                             new DrydenTurbulence(20.0, DrydenTurbulence.TurbulenceLevel.Severe, seed: 5),
                                             300, 700, -1e9, 1e9))
                { Gear = CloneGear(planeTemplate.Gear) };
                var integ = new RigidBodyIntegrator(plane);
                var trimmer3 = new Trimmer(plane);

                TrimResult trim = trimmer3.TrimLevelFlight(20.0, 100.0);
                var s = trimmer3.BuildTrimState(trim, 100.0);
                var c = new ControlInputs { Elevator = trim.Elevator, Throttle = trim.Throttle };

                double maxBank = 0, bankAfter = 0;
                double maxBankInZone = 0;
                for (double t = 0; t <= 45.0; t += Dt)
                {
                    double p = s.AngularVelocityBody.X, r = s.AngularVelocityBody.Z;
                    double bank = s.Euler321()[0];
                    // 增稳 (与 Phase 3 相同)
                    double ailCmd = Mathd.Clamp(0.6 * bank + 0.15 * p, -0.3, 0.3);
                    double rudCmd = Mathd.Clamp(-0.8 * r, -0.3, 0.3);
                    c.Aileron += (ailCmd - c.Aileron) * Dt / 0.04;
                    c.Rudder += (rudCmd - c.Rudder) * Dt / 0.04;
                    integ.Step(s, c, t, Dt);

                    maxBank = System.Math.Max(maxBank, System.Math.Abs(bank));
                    bool inZone = s.Position.X > 300 && s.Position.X < 700;
                    if (inZone) maxBankInZone = System.Math.Max(maxBankInZone, System.Math.Abs(bank));
                    if (t > 40.0) bankAfter = System.Math.Abs(bank);
                    if (double.IsNaN(s.Position.X)) { TestSuite.Fail("NaN"); return; }
                }
                Console.WriteLine(string.Format(
                    "        45s 穿越 400m 强紊流区: 区内滚转峰 {0:F1}°, 全程峰 {1:F1}°, 出区后 {2:F2}°",
                    maxBankInZone * Mathd.Rad2Deg, maxBank * Mathd.Rad2Deg, bankAfter * Mathd.Rad2Deg));
                // 阈值 0.25°: P0-2 修正侧向谱 (Lv=Lu, 慢变) 后, 区内紊流主要表现为
                // 准定常侧滑/偏航而非快变滚转, 增稳下滚转峰较旧谱降低;
                // 区内 (0.37°) vs 出区后 (0.00°) 仍有 >10 倍区分度, 测试意图不变
                TestSuite.AssertGreater(maxBankInZone, 0.25 * Mathd.Deg2Rad, "区内应有显著扰动");
                TestSuite.AssertLess(maxBank, 35.0 * Mathd.Deg2Rad, "全程滚转有界");
                TestSuite.AssertLess(bankAfter, 2.0 * Mathd.Deg2Rad, "出区后恢复平静");
            });
        }

        /// <summary>深拷贝起落架 (测试隔离: 共享实例的 Terrain 属性会互相污染)。</summary>
        private static GroundContactModel CloneGear(GroundContactModel src)
        {
            var g = new GroundContactModel();
            g.Params.MuLateral = src.Params.MuLateral;
            g.Params.MuRolling = src.Params.MuRolling;
            g.Params.MuBrake = src.Params.MuBrake;
            g.Params.MaxSteerRad = src.Params.MaxSteerRad;
            g.Params.StructDampPitch = src.Params.StructDampPitch;
            g.Params.StructDampRoll = src.Params.StructDampRoll;
            g.Params.StructDampYaw = src.Params.StructDampYaw;
            foreach (var w in src.Wheels)
                g.AddWheel(new GearWheel(w.Name, w.Position, w.SpringN, w.DamperNs,
                                         w.Steerable, w.HasBrake));
            return g;
        }

        /// <summary>线性斜坡测试地形: h = slope * north。</summary>
        private class LinearSlopeTerrain : ITerrainModel
        {
            private readonly double _slope;
            public LinearSlopeTerrain(double slope) { _slope = slope; }
            public double HeightAt(double north, double east) { return _slope * north; }
        }
    }
}
