using System;
using FlightDynamics;
using FlightDynamics.IO;
using FlightDynamics.Math;

namespace FlightDynamics.Tests
{
    /// <summary>
    /// Phase 2 验收模拟 (开发提示词第 10 节: "能手动起飞、巡航、降落"):
    /// 开环脚本化全任务 —— 起飞滑跑 → 拉起离地 → 爬升 → 巡航 → 减速进近 →
    /// 拉平接地 → 刹车停稳。物理核心层 (无渲染) 的完整链路验证。
    /// Unity 侧的手动操控 (60fps/FixedUpdate 解耦) 由 MonoBehaviour 层实现。
    /// </summary>
    public static class MissionTests
    {
        private const double Dt = 1.0 / 120.0;

        public static void Register(Aircraft plane, RigidBodyIntegrator integ, Trimmer trimmer,
                                    AircraftConfigJson.LoadedAircraft cfg)
        {
            TestSuite.Add("起落架: 静止停放平衡 (弹簧承重)", () =>
            {
                var s = new AircraftState();
                s.Position = new Vec3(0, 0, -0.31);   // 轮胎预压 ~4cm
                var c = ControlInputs.Zero;
                // 让它稳定 5 秒
                for (double t = 0; t < 5.0; t += Dt)
                    integ.Step(s, c, t, Dt);

                AeroResult aero; double thrust;
                Vec3 f = plane.ComputeForce(s, c, 5.0, out aero, out thrust);
                // 合力应接近零 (重量由弹簧+气动平衡)
                TestSuite.AssertTrue(f.Length < plane.Mass.MassKg * AtmosphereModel.G0 * 0.5,
                    "静止合力应 < 半个重量 (69N), 实际 " + f.Length + " N");
                TestSuite.AssertTrue(plane.LastGroundContact.WheelsOnGround == 3,
                    "三点接地, 实际 " + plane.LastGroundContact.WheelsOnGround);
                TestSuite.AssertLess(plane.LastGroundContact.MaxPenetration, 0.10,
                    "静穿透应 < 10cm");
                TestSuite.AssertTrue(!double.IsNaN(s.Position.Z), "无 NaN");
            });

            TestSuite.Add("起落架: 1 米坠落回弹收敛 (阻尼有效)", () =>
            {
                var s = new AircraftState();
                s.Position = new Vec3(0, 0, -1.0);   // 轮子离地 0.65m
                var c = ControlInputs.Zero;
                for (double t = 0; t < 10.0; t += Dt)
                    integ.Step(s, c, t, Dt);
                // 收敛到静平衡高度附近 (0.35 - 静压缩 ~0.023)
                double altErr = System.Math.Abs(s.Altitude - 0.327);
                TestSuite.AssertLess(altErr, 0.06, "10 秒后应回到静平衡高度±6cm, 偏差 " + altErr);
                TestSuite.AssertLess(System.Math.Abs(s.VelocityNed.Z), 0.2,
                    "垂向速度应衰减 (阻尼)");
                double[] e = s.Euler321();
                TestSuite.AssertLess(System.Math.Abs(e[0]), 10.0 * Mathd.Deg2Rad,
                    "不应翻覆, 滚转 " + e[0] * Mathd.Rad2Deg + "°");
                TestSuite.AssertLess(System.Math.Abs(e[1]), 10.0 * Mathd.Deg2Rad,
                    "俯仰应回正, 当前 " + e[1] * Mathd.Rad2Deg + "°");
            });

            TestSuite.Add("验收: 全任务飞行 (起飞→巡航→降落→停稳)", () =>
            {
                // ---- 初始: 跑道上静止, 三点接地 ----
                var s = new AircraftState();
                s.Position = new Vec3(0, 0, -0.327);
                var c = ControlInputs.Zero;

                // "等效飞行员" PD 增稳 (开环脚本在百秒航程中不现实;
                // 等效于人工保持姿态, 对应验收标准的"手动"飞行)
                double kpPitch = 0.8, kdPitch = 0.18;
                double kpRoll = 0.6, kdRoll = 0.15;
                double trimElevator = 0.0;

                double t = 0;
                bool liftoff = false, reachedCruise = false, cruiseDone = false,
                     touchedDown = false, stopped = false;
                double liftoffTime = -1, liftoffRoll = -1;
                double maxAlt = 0, touchdownSink = 99, touchdownPitch = 99, touchdownDist = -1;
                double maxPenetration = 0, maxBank = 0;
                string maxBankInfo = "";
                // 舵机一阶滞后 (§6): 滚转模态时间常数 ~4ms, 无滤波的 PD 输出
                // 会与显式 RK4 定步长发生数值自激; τ=40ms 模拟舵机响应速率
                double elevFilt = 0.0, ailFilt = 0.0;
                const double ServoTau = 0.04;
                double cruiseStart = -1;

                for (int step = 0; step < (int)(160.0 / Dt); step++, t += Dt)
                {
                    double alt = s.Altitude;
                    double spd = s.Airspeed(Vec3.Zero);
                    double[] e = s.Euler321();
                    double pitch = e[1], bank = e[0];
                    double q = s.AngularVelocityBody.Y, p = s.AngularVelocityBody.X;
                    if (t >= 14.0 && t <= 25.0 && step % 60 == 0)
                        Console.WriteLine(string.Format(
                            "        [t={0:F1}] bank={1:F1}° p={2:F3} da={3:F3} beta={4:F1}° r={5:F3} V={6:F1} alt={7:F1}",
                            t, bank * Mathd.Rad2Deg, p, c.Aileron,
                            s.Beta(Vec3.Zero) * Mathd.Rad2Deg, s.AngularVelocityBody.Z, spd, alt));
                    if (System.Math.Abs(bank) > maxBank)
                    {
                        maxBank = System.Math.Abs(bank);
                        maxBankInfo = string.Format("t={0:F1}s alt={1:F1} V={2:F1} wheels={3}",
                            t, alt, spd, plane.LastGroundContact.WheelsOnGround);
                    }

                    // ---- 飞行脚本 (带姿态增稳) ----
                    c = ControlInputs.Zero;
                    double pitchTarget = 0.0;

                    if (!liftoff)
                    {
                        // 滑跑: 满油门; 15 m/s 后带杆抬头
                        c.Throttle = 1.0;
                        pitchTarget = spd > 15.0 ? 8.0 * Mathd.Deg2Rad : 0.0;
                        if (alt > 1.0)
                        {
                            liftoff = true; liftoffTime = t;
                            liftoffRoll = s.Position.X;
                        }
                    }
                    else if (!reachedCruise)
                    {
                        // 爬升到 50m: 目标俯仰 10°
                        c.Throttle = 0.95;
                        pitchTarget = 10.0 * Mathd.Deg2Rad;
                        if (alt > 48.0) { reachedCruise = true; cruiseStart = t; }
                    }
                    else if (!cruiseDone)
                    {
                        // 巡航: 配平舵面, 平飞 6 秒
                        TrimResult trim = trimmer.TrimLevelFlight(20.0, 50.0);
                        trimElevator = trim.Elevator;
                        c.Throttle = trim.Throttle;
                        pitchTarget = trim.Alpha;
                        if (t - cruiseStart > 6.0) cruiseDone = true;
                    }
                    else if (!touchedDown)
                    {
                        // 进近: 3.5° 下滑道高度剖面 -> 2.5m 处拉平
                        // 剖面目标高度 = 拉平高度 + (当前高度-拉平高度) 对应的下滑道
                        const double flareAlt = 2.5;
                        double pathAlt = flareAlt + System.Math.Max(alt - flareAlt, 0.0);
                        // 级联: alt 误差 -> 目标垂直速度 -> 目标俯仰
                        double vzTarget = alt > flareAlt
                            ? -System.Math.Tan(3.5 * Mathd.Deg2Rad) * spd   // 沿 3.5° 下滑道
                            : -0.6;                                            // 拉平缓慢下沉
                        c.Throttle = 0.10;
                        double vz = s.VelocityNed.Z;
                        pitchTarget = Mathd.Clamp(0.20 * (vzTarget - vz) + (alt > flareAlt ? 1.0 : 5.0) * Mathd.Deg2Rad,
                                                  -6.0 * Mathd.Deg2Rad, 10.0 * Mathd.Deg2Rad);
                        if (alt < flareAlt) c.Throttle = 0.0;
                        // 接地判定: 持续接触 (双轮+) 且低且缓
                        if (plane.LastGroundContact.WheelsOnGround >= 2 && alt < 0.42
                            && s.VelocityNed.Z < 1.5)
                        {
                            touchedDown = true;
                            touchdownSink = s.VelocityNed.Z;   // NED z 正 = 下沉
                            touchdownPitch = pitch;
                            touchdownDist = s.Position.X;
                        }
                    }
                    else
                    {
                        // 地面滑跑减速至停
                        c.Throttle = 0.0;
                        c.Brake = 1.0;
                        pitchTarget = 0.0;
                        if (spd < 0.5 && s.VelocityNed.Length < 0.5)
                        {
                            stopped = true;
                            break;
                        }
                    }

                    // 姿态增稳 (俯仰 PD + 滚转保持)。
                    // 极性按数据符号约定: Cmδe<0 (负偏=抬头), Clda<0 (正偏=左滚)
                    double cmdSign = System.Math.Sign(cfg.Aero.Cmde);   // = -1
                    double elevatorCmd = trimElevator
                        + cmdSign * (kpPitch * (pitchTarget - pitch) + kdPitch * (0.0 - q));
                    double ailCmd = Mathd.Clamp(kpRoll * bank + kdRoll * p, -0.3, 0.3);
                    // 舵机一阶滞后滤波
                    elevFilt += (Mathd.Clamp(elevatorCmd, -0.35, 0.35) - elevFilt) * Dt / ServoTau;
                    ailFilt += (ailCmd - ailFilt) * Dt / ServoTau;
                    c.Elevator = elevFilt;
                    c.Aileron = ailFilt;

                    maxAlt = System.Math.Max(maxAlt, alt);
                    if (plane.LastGroundContact.AnyContact)
                        maxPenetration = System.Math.Max(maxPenetration, plane.LastGroundContact.MaxPenetration);

                    integ.Step(s, c, t, Dt);

                    if (double.IsNaN(s.Position.X) || double.IsNaN(s.Position.Z))
                    {
                        TestSuite.Fail("出现 NaN (t=" + t + ")");
                        return;
                    }
                }

                Console.WriteLine(string.Format(
                    "        离地: {0:F1}s / 滑跑 {1:F0}m; 最大高度 {2:F1}m; 接地: 下沉 {3:F2}m/s, 俯仰 {4:F1}°, 距起点 {5:F0}m",
                    liftoffTime < 0 ? -1 : liftoffTime, liftoffRoll, maxAlt,
                    touchdownSink, touchdownPitch * Mathd.Rad2Deg, touchdownDist));
                Console.WriteLine(string.Format(
                    "        停稳: {0}; 最大穿透 {1:F3}m; 最大滚转 {2:F1}°",
                    stopped, maxPenetration, maxBank * Mathd.Rad2Deg));

                TestSuite.AssertTrue(liftoff, "应成功离地");
                TestSuite.AssertLess(liftoffRoll, 150.0, "起飞滑跑距离 < 150m");
                TestSuite.AssertTrue(reachedCruise, "应爬升到巡航高度 (48m+)");
                TestSuite.AssertGreater(maxAlt, 45.0, "最大高度 > 45m");
                TestSuite.AssertTrue(touchedDown, "应接地");
                TestSuite.AssertLess(touchdownSink, 3.0, "接地下沉率 < 3 m/s");
                TestSuite.AssertGreater(touchdownPitch, -5.0 * Mathd.Deg2Rad, "接地俯仰 > -5° (非机头撞击)");
                TestSuite.AssertLess(touchdownPitch, 15.0 * Mathd.Deg2Rad, "接地俯仰 < 15° (非尾撞)");
                Console.WriteLine("        滚转峰值出现于: " + maxBankInfo);
                TestSuite.AssertTrue(stopped, "应刹停");
                TestSuite.AssertLess(maxPenetration, 0.25, "机轮穿透 < 25cm");
                TestSuite.AssertLess(maxBank, 25.0 * Mathd.Deg2Rad, "全程滚转 < 25° (无侧翻)");
            });
        }

        private static double touchdownTime; // 仅日志用
    }
}
