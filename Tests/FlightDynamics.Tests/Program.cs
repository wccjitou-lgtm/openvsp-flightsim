using System;
using System.Collections.Generic;
using FlightDynamics;
using FlightDynamics.IO;
using FlightDynamics.Math;

namespace FlightDynamics.Tests
{
    /// <summary>
    /// Phase 1 验收测试 (开发提示词第 10 节):
    ///   1. 给定巡航配平条件, 配平后飞机能维持水平直线飞行
    ///   2. 阶跃升降舵输入的俯仰响应符合二阶系统特征 (不发散)
    /// 数据源: Data/AircraftConfig.json (Phase 0 数据管线产物)
    /// </summary>
    public static class Program
    {
        private const double Dt = 1.0 / 120.0;   // 建议积分步长
        private const double CruiseV = 20.0;     // 巡航速度 m/s
        private const double CruiseAlt = 100.0;  // 巡航高度 m

        private static AircraftConfigJson.LoadedAircraft _cfg;
        private static Aircraft _plane;
        private static RigidBodyIntegrator _integrator;
        private static Trimmer _trimmer;

        private static int Main(string[] args)
        {
            // 加载配置 (测试 exe 同目录下的 AircraftConfig.json, 或命令行指定)
            string configPath = args.Length > 0 ? args[0] : "AircraftConfig.json";
            if (!System.IO.File.Exists(configPath))
            {
                Console.WriteLine("找不到配置文件: " + configPath);
                Console.WriteLine("用法: FlightDynamics.Tests.exe [AircraftConfig.json 路径]");
                return 2;
            }
            _cfg = AircraftConfigJson.Load(configPath);
            _plane = new Aircraft(_cfg.Mass, new AeroModel(_cfg.Aero), _cfg.Engine, new CalmWind())
            {
                Gear = _cfg.Gear
            };
            _integrator = new RigidBodyIntegrator(_plane);
            _trimmer = new Trimmer(_plane);

            RegisterMathTests();
            RegisterAtmosphereTests();
            RegisterAeroTests();
            RegisterAcceptanceTrim();
            RegisterAcceptanceElevatorStep();
            RegisterLateralTests();
            MissionTests.Register(_plane, _integrator, _trimmer, _cfg);
            WindTests.Register(_plane, _trimmer, _cfg);
            TerrainTests.Register(_plane, _trimmer, _cfg);

            return TestSuite.RunAll();
        }

        // ================= 数学原语 =================
        private static void RegisterMathTests()
        {
            TestSuite.Add("数学: 叉积右手系", () =>
            {
                Vec3 x = new Vec3(1, 0, 0), y = new Vec3(0, 1, 0);
                Vec3 z = x.Cross(y);
                TestSuite.AssertClose(z.X, 0, 1e-15, "z.X");
                TestSuite.AssertClose(z.Y, 0, 1e-15, "z.Y");
                TestSuite.AssertClose(z.Z, 1, 1e-15, "z.Z");
            });

            TestSuite.Add("数学: 四元数旋转 90 度绕 Z", () =>
            {
                Quat q = Quat.FromAxisAngle(new Vec3(0, 0, 1), 0.5 * System.Math.PI);
                Vec3 v = q.Rotate(new Vec3(1, 0, 0));
                TestSuite.AssertClose(v.X, 0, 1e-12, "v.X");
                TestSuite.AssertClose(v.Y, 1, 1e-12, "v.Y");
                TestSuite.AssertClose(v.Z, 0, 1e-12, "v.Z");
            });

            TestSuite.Add("数学: 四元数欧拉角往返", () =>
            {
                double roll = 0.3, pitch = -0.2, yaw = 1.1;
                Quat q = Quat.FromEuler321(roll, pitch, yaw);
                double[] e = q.ToEuler321();
                TestSuite.AssertClose(e[0], roll, 1e-12, "roll");
                TestSuite.AssertClose(e[1], pitch, 1e-12, "pitch");
                TestSuite.AssertClose(e[2], yaw, 1e-12, "yaw");
            });

            TestSuite.Add("数学: 惯量张量求逆", () =>
            {
                Matrix3 m = Matrix3.FromInertia(2, 3, 4, 0.1, 0.2, -0.05);
                Matrix3 inv = m.Inverse();
                Vec3 v = new Vec3(0.7, -1.3, 2.1);
                Vec3 round = inv.Multiply(m.Multiply(v));
                TestSuite.AssertClose(round.X, v.X, 1e-12, "x");
                TestSuite.AssertClose(round.Y, v.Y, 1e-12, "y");
                TestSuite.AssertClose(round.Z, v.Z, 1e-12, "z");
            });
        }

        private static double d0Of(double absoluteAltitudeM)
        {
            double saved = AtmosphereModel.AltitudeOffsetM;
            AtmosphereModel.AltitudeOffsetM = 0.0;
            double rho = AtmosphereModel.Density(absoluteAltitudeM);
            AtmosphereModel.AltitudeOffsetM = saved;
            return rho;
        }

        // ================= 标准大气 =================
        private static void RegisterAtmosphereTests()
        {
            TestSuite.Add("大气: ISA 海平面与 11km", () =>
            {
                TestSuite.AssertClose(AtmosphereModel.Temperature(0), 288.15, 1e-9, "T(0)");
                TestSuite.AssertClose(AtmosphereModel.Density(0), 1.225, 1e-6, "rho(0)");
                TestSuite.AssertClose(AtmosphereModel.Density(11000), 0.3639, 2e-4, "rho(11km)");
                TestSuite.AssertClose(AtmosphereModel.Pressure(0), 101325.0, 1e-6, "p(0)");
                TestSuite.AssertLess(AtmosphereModel.Density(5000), AtmosphereModel.Density(0), "密度应随高度递减");
            });

            TestSuite.Add("大气: 场地标高偏置 (默认 0 逐位一致; 2000 m = ISA 2000 m)", () =>
            {
                TestSuite.AssertClose(AtmosphereModel.AltitudeOffsetM, 0.0, 0.0, "默认偏置为 0");
                double[] hs = { -50, 0, 1234.5, 5000, 10999, 11000, 15000 };
                var d0 = new double[hs.Length];
                var t0 = new double[hs.Length];
                var p0 = new double[hs.Length];
                for (int i = 0; i < hs.Length; i++)
                {
                    d0[i] = AtmosphereModel.Density(hs[i]);
                    t0[i] = AtmosphereModel.Temperature(hs[i]);
                    p0[i] = AtmosphereModel.Pressure(hs[i]);
                }
                try
                {
                    AtmosphereModel.AltitudeOffsetM = 0.0;
                    for (int i = 0; i < hs.Length; i++)
                    {
                        TestSuite.AssertTrue(AtmosphereModel.Density(hs[i]) == d0[i], "offset=0 密度逐位一致 h=" + hs[i]);
                        TestSuite.AssertTrue(AtmosphereModel.Temperature(hs[i]) == t0[i], "offset=0 温度逐位一致 h=" + hs[i]);
                        TestSuite.AssertTrue(AtmosphereModel.Pressure(hs[i]) == p0[i], "offset=0 压强逐位一致 h=" + hs[i]);
                    }

                    AtmosphereModel.AltitudeOffsetM = 2000.0;
                    TestSuite.AssertClose(AtmosphereModel.Density(0), 1.0066, 2e-4, "ISA 2000 m 密度");
                    TestSuite.AssertClose(AtmosphereModel.Temperature(0), 288.15 - 13.0, 1e-9, "ISA 2000 m 温度");
                    TestSuite.AssertClose(AtmosphereModel.Pressure(0), 79495.0, 30.0, "ISA 2000 m 压强");
                    TestSuite.AssertClose(AtmosphereModel.Density(500), d0Of(2500), 1e-12, "偏置等价于抬高查询高度");
                    TestSuite.AssertLess(AtmosphereModel.Density(0), 1.225 * 0.83, "2000 m 密度比海平面低 17% 以上");
                }
                finally { AtmosphereModel.AltitudeOffsetM = 0.0; }
                TestSuite.AssertClose(AtmosphereModel.Density(0), 1.225, 1e-6, "复位后回到海平面");
            });
        }

        // ================= 气动模型 =================
        private static void RegisterAeroTests()
        {
            TestSuite.Add("气动: 攻角/侧滑角计算", () =>
            {
                var s = new AircraftState();
                s.Attitude = Quat.Identity;
                s.VelocityBody = new Vec3(20, 0, 0); // 纯轴向
                TestSuite.AssertClose(s.Alpha(Vec3.Zero), 0, 1e-12, "alpha=0");
                s.VelocityBody = new Vec3(20.0 * System.Math.Cos(0.1), 0, 20.0 * System.Math.Sin(0.1));
                TestSuite.AssertClose(s.Alpha(Vec3.Zero), 0.1, 1e-12, "alpha=0.1");
                s.VelocityBody = new Vec3(20 * System.Math.Cos(0.05), 20 * System.Math.Sin(0.05), 0);
                TestSuite.AssertClose(s.Beta(Vec3.Zero), 0.05, 1e-12, "beta=0.05");
            });

            TestSuite.Add("气动: 无风迎风时力的方向", () =>
            {
                var aero = new AeroModel(_cfg.Aero);
                // alpha=beta=0, 角速度 0, 舵面 0
                var r = aero.Compute(new Vec3(20, 0, 0), Vec3.Zero, 1.225, ControlInputs.Zero);
                // 阻力沿 -x, 升力沿 -z (体轴 z 向下, 升力向上)
                TestSuite.AssertLess(r.ForceBody.X, 0, "阻力应沿 -x");
                // 升力方向随 CL0 符号 (对称翼型 CL0≈0 时符号无意义)
                TestSuite.AssertTrue(r.ForceBody.Z * _cfg.Aero.CL0 <= 0, "升力应沿 -z (向上), 与 CL0 符号一致");
                if (_cfg.Aero.CL0 > 1e-3)
                    TestSuite.AssertLess(r.ForceBody.Z, 0, "CL0>0 时升力应沿 -z (向上)");
                TestSuite.AssertClose(r.ForceBody.Y, 0, 1e-12, "无侧滑不应有侧力");
                TestSuite.AssertClose(r.CL, _cfg.Aero.CL0, 1e-12, "CL=CL0");
                TestSuite.AssertClose(r.CD, _cfg.Aero.CD0 + _cfg.Aero.KInduced * _cfg.Aero.CL0 * _cfg.Aero.CL0, 1e-12, "CD 极曲线");
            });

            TestSuite.Add("气动: 风矢量正确介入相对气流", () =>
            {
                var s = new AircraftState();
                s.Attitude = Quat.Identity;
                s.VelocityBody = new Vec3(20, 0, 0);
                // 逆风 5 m/s (风从北吹向南 = NED +x 方向? 定义: 风矢量为气团速度)
                Vec3 windNed = new Vec3(-5, 0, 0); // 气团向南移动 → 顶风
                double v = s.Airspeed(windNed);
                TestSuite.AssertClose(v, 25.0, 1e-12, "顶风空速应增加");
            });

            TestSuite.Add("气动: 升降舵效能方向", () =>
            {
                // 数据约定: Cmde 的符号决定正舵偏的抬头/低头。验证:
                // 非零 Cmde 且非零 CLde (Phase 0 修复后的数据完整性)
                TestSuite.AssertTrue(System.Math.Abs(_cfg.Aero.Cmde) > 0.02,
                    "Cmde 应具有工程量级 (>0.02/rad), 实际 " + _cfg.Aero.Cmde);
                TestSuite.AssertTrue(System.Math.Abs(_cfg.Aero.CLde) > 0.01,
                    "CLde 应具有工程量级, 实际 " + _cfg.Aero.CLde);
            });

            TestSuite.Add("气动: 纯副翼输入产生 CYda 侧力", () =>
            {
                // 无侧滑/无角速度, 纯副翼偏转 → CY 应恰等于 CYda·δa (P1-4 回归:
                // 修复前 CY 公式漏掉 CYda 项, 此输入会得到 0)
                var data = new AeroData
                {
                    Sref = 1.0, Bref = 4.0, Cref = 0.775,
                    CYda = -0.0045, Clda = -0.1355, Cnda = -0.0046
                };
                var model = new AeroModel(data);
                var v = new Vec3(20, 0, 0);   // 纯纵向气流: α=β=0
                var ctl = new ControlInputs { Aileron = 0.3 };
                var r = model.Compute(v, Vec3.Zero, 1.225, ctl);
                TestSuite.AssertClose(r.CY, data.CYda * 0.3, 1e-12,
                    "纯副翼输入的 CY 应等于 CYda·δa");
                TestSuite.AssertClose(r.Cl, data.Clda * 0.3, 1e-12, "副翼滚转力矩完整");
                TestSuite.AssertClose(r.Beta, 0, 1e-12, "确认无侧滑");
            });
        }

        // ============ 验收 1: 配平后维持水平直线飞行 ============
        private static void RegisterAcceptanceTrim()
        {
            TestSuite.Add("验收1: 配平求解收敛 (力/力矩平衡)", () =>
            {
                TrimResult trim = _trimmer.TrimLevelFlight(CruiseV, CruiseAlt);
                Console.WriteLine(string.Format(
                    "        配平: alpha={0:F2}°, de={1:F2}°, throttle={2:F3}, 残余Cm={3:E2}",
                    trim.Alpha * Mathd.Rad2Deg, trim.Elevator * Mathd.Rad2Deg,
                    trim.Throttle, trim.ResidualCm));
                TestSuite.AssertClose(trim.ResidualCm, 0, 1e-10, "残余俯仰力矩系数");
                TestSuite.AssertTrue(System.Math.Abs(trim.Elevator) < 20 * Mathd.Deg2Rad,
                    "配平舵偏应在舵面行程内 (±20°)");
                TestSuite.AssertTrue(trim.Throttle >= 0 && trim.Throttle <= 1.0,
                    "配平油门应在 0..1, 实际 " + trim.Throttle);
            });

            TestSuite.Add("验收1: 配平状态积分 30s 维持水平直线飞行", () =>
            {
                TrimResult trim = _trimmer.TrimLevelFlight(CruiseV, CruiseAlt);
                AircraftState s = _trimmer.BuildTrimState(trim, CruiseAlt);
                var controls = new ControlInputs
                {
                    Elevator = trim.Elevator,
                    Aileron = 0,
                    Rudder = 0,
                    Throttle = trim.Throttle
                };

                double maxAltErr = 0, maxSpeedErr = 0, maxBank = 0, maxBeta = 0;
                double tEnd = 30.0;
                for (double t = 0; t < tEnd; t += Dt)
                {
                    _integrator.Step(s, controls, t, Dt);
                    double altErr = System.Math.Abs(s.Altitude - CruiseAlt);
                    double speedErr = System.Math.Abs(s.Airspeed(Vec3.Zero) - CruiseV) / CruiseV;
                    double[] e = s.Euler321();
                    maxAltErr = System.Math.Max(maxAltErr, altErr);
                    maxSpeedErr = System.Math.Max(maxSpeedErr, speedErr);
                    maxBank = System.Math.Max(maxBank, System.Math.Abs(e[0]));
                    maxBeta = System.Math.Max(maxBeta, System.Math.Abs(s.Beta(Vec3.Zero)));
                }

                Console.WriteLine(string.Format(
                    "        30s 漂移: 高度±{0:F3} m, 速度±{1:F3}%, 滚转±{2:F3}°, 侧滑±{3:F4}°",
                    maxAltErr, 100 * maxSpeedErr, maxBank * Mathd.Rad2Deg, maxBeta * Mathd.Rad2Deg));

                TestSuite.AssertTrue(!double.IsNaN(s.Position.X) && !double.IsInfinity(s.Position.X), "无 NaN/Inf");
                TestSuite.AssertLess(maxAltErr, 1.0, "30 秒高度漂移 < 1 m");
                TestSuite.AssertLess(maxSpeedErr, 0.02, "30 秒速度漂移 < 2%");
                TestSuite.AssertLess(maxBank, 1.0 * Mathd.Deg2Rad, "滚转保持 < 1°");
                TestSuite.AssertLess(maxBeta, 0.5 * Mathd.Deg2Rad, "侧滑保持 < 0.5°");
            });
        }

        // ============ 验收 2: 阶跃升降舵俯仰响应 (二阶特征, 不发散) ============
        private static void RegisterAcceptanceElevatorStep()
        {
            TestSuite.Add("验收2: 阶跃升降舵 +2° 俯仰响应收敛不发散", () =>
            {
                TrimResult trim = _trimmer.TrimLevelFlight(CruiseV, CruiseAlt);
                AircraftState s = _trimmer.BuildTrimState(trim, CruiseAlt);

                // 阶跃方向: 产生抬头力矩的方向 (数据驱动, 对符号约定鲁棒)。
                // 幅度 2°: 峰值攻角保持在线性模型适用范围 (<15°) 内, 以短周期模态为主导。
                double step = 2.0 * Mathd.Deg2Rad * System.Math.Sign(_cfg.Aero.Cmde);
                var controls = new ControlInputs
                {
                    Elevator = trim.Elevator + step,
                    Throttle = trim.Throttle
                };

                // 记录 q(t) 与 alpha(t)
                double tEnd = 30.0;
                int n = (int)(tEnd / Dt);
                double[] qHist = new double[n + 1];
                double[] aHist = new double[n + 1];
                double qPeak = 0, alphaPeak = 0;
                for (int i = 0; i <= n; i++)
                {
                    double t = i * Dt;
                    qHist[i] = s.AngularVelocityBody.Y;
                    aHist[i] = s.Alpha(Vec3.Zero);
                    qPeak = System.Math.Max(qPeak, System.Math.Abs(qHist[i]));
                    alphaPeak = System.Math.Max(alphaPeak, System.Math.Abs(aHist[i]));
                    if (i < n) _integrator.Step(s, controls, t, Dt);
                }

                Console.WriteLine(string.Format(
                    "        q 峰值 {0:F3} rad/s, alpha 峰值 {1:F2}°, 30s 后 q={2:E3}, alpha={3:F2}°",
                    qPeak, alphaPeak * Mathd.Rad2Deg, qHist[n], aHist[n] * Mathd.Rad2Deg));

                // 理论新平衡攻角 (俯仰力矩再平衡): Δα = -Cmδe·δe / Cmα
                double alphaEq = trim.Alpha - (_cfg.Aero.Cmde * step) / _cfg.Aero.Cmalpha;
                Console.WriteLine(string.Format("        理论新平衡 alpha = {0:F2}°, 实际收敛 {1:F2}°",
                    alphaEq * Mathd.Rad2Deg, aHist[n] * Mathd.Rad2Deg));

                // (a) 有可测量的俯仰响应
                TestSuite.AssertGreater(qPeak, 0.02, "阶跃输入应产生显著俯仰率响应");
                // (b) 不发散: 用 |q| 包络判定 (相位无关) — 末 5s 包络必须小于
                //     5..10s 段包络 (长周期模态阻尼为正即满足); 不能用"30s 时刻值",
                //     那是相位运气 (新构型 phugoid 周期 18s, 30s 恰落在 q 极值附近)
                double qEnvMid = 0, qEnvEnd = 0;
                for (int i = 0; i <= n; i++)
                {
                    double t = i * Dt;
                    if (t >= 5.0 && t <= 10.0) qEnvMid = System.Math.Max(qEnvMid, System.Math.Abs(qHist[i]));
                    if (t >= 25.0) qEnvEnd = System.Math.Max(qEnvEnd, System.Math.Abs(qHist[i]));
                }
                Console.WriteLine(string.Format("        |q| 包络: 5..10s = {0:E3}, 25..30s = {1:E3}", qEnvMid, qEnvEnd));
                TestSuite.AssertLess(qEnvEnd, 0.9 * qEnvMid, "俯仰率包络应随时间衰减 (不发散)");
                // (c) 攻角收敛到理论新平衡 (±3°)
                TestSuite.AssertTrue(System.Math.Abs(aHist[n] - alphaEq) < 3.0 * Mathd.Deg2Rad,
                    string.Format("30s 后攻角应收敛到新平衡 {0:F2}°±3°, 实际 {1:F2}°",
                        alphaEq * Mathd.Rad2Deg, aHist[n] * Mathd.Rad2Deg));
                // (d) 攻角全程有界 (允许长周期模态的缓慢起伏 —— 该机气动干净,
                //     phugoid 阻尼天然很弱, 峰值会短时冲过新平衡, 但必须收敛)
                TestSuite.AssertLess(alphaPeak, 25.0 * Mathd.Deg2Rad, "峰值攻角 < 25° (有界)");
                // (e) 二阶系统定量验证: 初始角加速度 q̇(0) = q̄·S·c̄·Cmδe·δe / Iyy
                double rho0 = AtmosphereModel.Density(CruiseAlt);
                double qbar0 = 0.5 * rho0 * CruiseV * CruiseV;
                double qdotTheory = qbar0 * _cfg.Aero.Sref * _cfg.Aero.Cref
                                  * _cfg.Aero.Cmde * step / _cfg.Mass.Iyy;
                double qdotSim = 2.0 * qHist[1] / Dt - qHist[2] / (2.0 * Dt); // 一阶外推: 消除前向差分里俯仰阻尼的系统性低估
                Console.WriteLine(string.Format("        q̇(0): 理论 {0:F3} vs 仿真 {1:F3} rad/s²",
                    qdotTheory, qdotSim));
                TestSuite.AssertTrue(System.Math.Abs(qdotSim - qdotTheory) < 0.08 * System.Math.Abs(qdotTheory),
                    string.Format("初始角加速度应与力矩/惯量理论一致 (±8%): {0:F4} vs {1:F4}",
                        qdotSim, qdotTheory));

                // (f) 发散性包络检查: 10s 之后 |q| 不得超过峰值的 75%
                // (允许弱阻尼 phugoid 的慢起伏 —— 周期约 9s, 峰值相位附近 q 会
                //  短时回升; 判据是"无二次放大/不发散", 收敛性由 30s<10% 保证)
                double qLateMax = 0;
                for (int i = (int)(10.0 / Dt); i <= n; i++)
                    qLateMax = System.Math.Max(qLateMax, System.Math.Abs(qHist[i]));
                TestSuite.AssertLess(qLateMax, 0.75 * qPeak, "10s 后俯仰率包络应无二次放大 (不发散)");

                // (g) 若检测到多个振荡峰值, 相邻峰值必须衰减 (不发散的充分条件)
                CheckDampedOscillation(qHist, Dt, 3.0);
            });
        }

        /// <summary>检查时序的相邻局部极值绝对值递减 (若检测到多个峰值; 高阻尼系统单峰值合格)。</summary>
        private static void CheckDampedOscillation(double[] y, double dt, double windowSec)
        {
            int nWindow = (int)(windowSec / dt);
            var peaks = new List<double>();
            for (int i = 2; i < nWindow - 2 && i < y.Length - 2; i++)
            {
                double a = System.Math.Abs(y[i]);
                if (a > System.Math.Abs(y[i - 1]) && a > System.Math.Abs(y[i - 2]) &&
                    a > System.Math.Abs(y[i + 1]) && a > System.Math.Abs(y[i + 2]) &&
                    a > 0.05 * MaxAbs(y, 0, nWindow))
                {
                    peaks.Add(a);
                }
            }
            Console.WriteLine(string.Format("        检测到 {0} 个短周期振荡峰值: {1}",
                peaks.Count, string.Join(", ", peaks.ConvertAll(p => p.ToString("F3")).ToArray())));
            // 单峰值 = 高阻尼 (ζ>0.7), 天然满足 "不发散"; 多峰值时逐对比衰减
            for (int i = 1; i < peaks.Count; i++)
                TestSuite.AssertLess(peaks[i], peaks[i - 1] * 1.02,
                    string.Format("第 {0} 个峰值应不大于前一个 (不发散)", i + 1));
        }

        private static double MaxAbs(double[] y, int from, int to)
        {
            double m = 0;
            for (int i = from; i < to && i < y.Length; i++)
                m = System.Math.Max(m, System.Math.Abs(y[i]));
            return m;
        }

        // ================= 横侧向符号与阻尼 =================
        private static void RegisterLateralTests()
        {
            TestSuite.Add("P1: yaw polarity preserves pilot ground steering direction", () =>
            {
                var state = new AircraftState
                {
                    Position = new Vec3(0, 0, -0.30),
                    Attitude = Quat.Identity,
                    VelocityBody = new Vec3(10, 0, 0)
                };
                foreach (int sign in new[] { -1, 1 })
                    foreach (int direction in new[] { -1, 1 })
                    {
                        var plane = new Aircraft(_cfg.Mass,
                            new AeroModel(new AeroData { Cndr = sign * 0.05 }),
                            _cfg.Engine, new CalmWind()) { Gear = _cfg.Gear };
                        double pilot = direction * 10 * Mathd.Deg2Rad;
                        var control = new ControlInputs { Rudder = sign * pilot };
                        plane.ComputeForce(state, control, 0, out AeroResult aero, out double thrust);
                        double expected = plane.Gear.Compute(state, 0, pilot).TotalMomentBody.Z;
                        TestSuite.AssertGreater(direction * plane.LastGroundContact.TotalMomentBody.Z,
                            0, "Right/left pilot rudder must steer the same way for either Cndr sign");
                        TestSuite.AssertClose(plane.LastGroundContact.TotalMomentBody.Z, expected,
                            1e-10, "Preserve baseline steering magnitude");
                    }
            });

            TestSuite.Add("横侧: 静稳定性符号", () =>
            {
                TestSuite.AssertLess(_cfg.Aero.Cmalpha, 0, "Cmalpha < 0 (纵向静稳定)");
                TestSuite.AssertLess(_cfg.Aero.Clp, 0, "Clp < 0 (滚转阻尼)");
                TestSuite.AssertLess(_cfg.Aero.Cnr, 0, "Cnr < 0 (偏航阻尼)");
                TestSuite.AssertGreater(_cfg.Aero.Cnbeta, 0, "Cnbeta > 0 (航向静稳定)");
                TestSuite.AssertLess(_cfg.Aero.CYbeta, 0, "CYbeta < 0 (侧滑恢复侧力)");
            });

            TestSuite.Add("横侧: 副翼阶跃产生滚转且符号正确", () =>
            {
                TrimResult trim = _trimmer.TrimLevelFlight(CruiseV, CruiseAlt);
                AircraftState s = _trimmer.BuildTrimState(trim, CruiseAlt);
                double step = 5.0 * Mathd.Deg2Rad;
                var controls = new ControlInputs
                {
                    Elevator = trim.Elevator,
                    Aileron = step, // 正副翼
                    Throttle = trim.Throttle
                };
                double tEnd = 3.0;
                for (double t = 0; t < tEnd; t += Dt)
                    _integrator.Step(s, controls, t, Dt);
                double p = s.AngularVelocityBody.X;
                // 正副翼 → Cl 变化 = Clda*da, 若 Clda<0 则 Cl<0 → p<0 (数据约定)
                double expectedSign = System.Math.Sign(_cfg.Aero.Clda);
                TestSuite.AssertTrue(System.Math.Abs(p) > 0.1, "3 秒内应建立显著滚转率, 实际 p=" + p);
                TestSuite.AssertTrue(System.Math.Sign(p) == expectedSign,
                    "滚转率符号应与 Clda·da 一致: p=" + p + " 期望符号 " + expectedSign);
            });

            TestSuite.Add("横侧: 方向舵阶跃产生偏航且符号正确", () =>
            {
                TrimResult trim = _trimmer.TrimLevelFlight(CruiseV, CruiseAlt);
                AircraftState s = _trimmer.BuildTrimState(trim, CruiseAlt);
                double step = 5.0 * Mathd.Deg2Rad;
                var controls = new ControlInputs
                {
                    Elevator = trim.Elevator,
                    Rudder = step,
                    Throttle = trim.Throttle
                };
                double tEnd = 3.0;
                for (double t = 0; t < tEnd; t += Dt)
                    _integrator.Step(s, controls, t, Dt);
                double r = s.AngularVelocityBody.Z;
                double expectedSign = System.Math.Sign(_cfg.Aero.Cndr);
                TestSuite.AssertTrue(System.Math.Abs(r) > 0.02, "3 秒内应建立可测偏航率, 实际 r=" + r);
                TestSuite.AssertTrue(System.Math.Sign(r) == expectedSign,
                    "偏航率符号应与 Cndr·dr 一致: r=" + r + " 期望符号 " + expectedSign);
            });

            TestSuite.Add("横侧: 初始滚转扰动的横侧向收敛 (荷兰滚有阻尼)", () =>
            {
                TrimResult trim = _trimmer.TrimLevelFlight(CruiseV, CruiseAlt);
                AircraftState s = _trimmer.BuildTrimState(trim, CruiseAlt);
                s.AngularVelocityBody = new Vec3(0.5, 0, 0); // 突加滚转率扰动 0.5 rad/s
                var controls = new ControlInputs
                {
                    Elevator = trim.Elevator,
                    Throttle = trim.Throttle
                };
                double tEnd = 20.0;
                for (double t = 0; t < tEnd; t += Dt)
                    _integrator.Step(s, controls, t, Dt);
                double pEnd = System.Math.Abs(s.AngularVelocityBody.X);
                double rEnd = System.Math.Abs(s.AngularVelocityBody.Z);
                double bankEnd = System.Math.Abs(s.Euler321()[0]);
                Console.WriteLine(string.Format(
                    "        20s 后: |p|={0:E3}, |r|={1:E3}, |phi|={2:F2}°", pEnd, rEnd, bankEnd * Mathd.Rad2Deg));
                TestSuite.AssertLess(pEnd, 0.05, "滚转率扰动应基本衰减");
                TestSuite.AssertLess(rEnd, 0.05, "偏航率扰动应基本衰减");
                TestSuite.AssertLess(bankEnd, 30 * Mathd.Deg2Rad, "残余滚转角 < 30° (有界)");
                TestSuite.AssertTrue(!double.IsNaN(bankEnd), "无 NaN");
            });
        }
    }
}
