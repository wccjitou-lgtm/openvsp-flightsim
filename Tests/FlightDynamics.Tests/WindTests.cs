using System;
using FlightDynamics;
using FlightDynamics.IO;
using FlightDynamics.Math;

namespace FlightDynamics.Tests
{
    /// <summary>
    /// Phase 3 验收 (开发提示词第 10 节):
    ///   "施加侧风阵风后飞机产生合理的滚转/偏航扰动, 且可通过操纵回稳"
    /// 以及 5.4 节要求: Dryden 紊流统计特性 (均值/方差) 在合理范围。
    /// </summary>
    public static class WindTests
    {
        private const double Dt = 1.0 / 120.0;

        public static void Register(Aircraft planeTemplate, Trimmer trimmer,
                                    AircraftConfigJson.LoadedAircraft cfg)
        {
            // ================= Dryden 紊流统计 =================
            TestSuite.Add("紊流: Dryden 垂向分量均值/方差符合理论", () =>
            {
                // 100m 高度, 中度紊流: σw = 0.1·15.4 = 1.54 m/s
                var turb = new DrydenTurbulence(20.0, DrydenTurbulence.TurbulenceLevel.Moderate, seed: 7);
                var pos = new Vec3(0, 0, -100);

                const int N = 30000;   // 250s
                double sum = 0, sum2 = 0;
                double t = 0;
                for (int i = 0; i < N; i++, t += Dt)
                {
                    Vec3 w = turb.WindAt(pos, t);
                    sum += w.Z; sum2 += w.Z * w.Z;
                }
                double mean = sum / N;
                double sigma = System.Math.Sqrt(sum2 / N - mean * mean);
                double sigmaTheory = 0.1 * 15.4;
                Console.WriteLine(string.Format(
                    "        垂向: 均值 {0:F3} m/s, 实测σ {1:F3} vs 理论σ {2:F3} (±25% 判定)",
                    mean, sigma, sigmaTheory));
                TestSuite.AssertTrue(System.Math.Abs(mean) < 0.25, "均值应接近 0");
                TestSuite.AssertTrue(System.Math.Abs(sigma - sigmaTheory) < 0.25 * sigmaTheory,
                    string.Format("垂向标准差应在理论值 ±25% 内: 实测 {0}", sigma));
            });

            TestSuite.Add("紊流: Dryden 侧向分量均值/方差符合理论", () =>
            {
                // 100m 高度, 中度紊流: σv = σu = σw/(0.177+0.000823·h_ft)^0.4 ≈ 2.12 m/s
                var turb = new DrydenTurbulence(20.0, DrydenTurbulence.TurbulenceLevel.Moderate, seed: 13);
                var pos = new Vec3(0, 0, -100);   // 航向 0 → 侧向输出即 NED 东向分量

                const int N = 30000;   // 250s
                double sum = 0, sum2 = 0;
                double t = 0;
                for (int i = 0; i < N; i++, t += Dt)
                {
                    Vec3 w = turb.WindAt(pos, t);
                    sum += w.Y; sum2 += w.Y * w.Y;
                }
                double mean = sum / N;
                double sigma = System.Math.Sqrt(sum2 / N - mean * mean);
                double sigmaTheory = 1.54 / System.Math.Pow(0.177 + 0.000823 * (100.0 / 0.3048), 0.4);
                Console.WriteLine(string.Format(
                    "        侧向: 均值 {0:F3} m/s, 实测σ {1:F3} vs 理论σ {2:F3} (±25% 判定)",
                    mean, sigma, sigmaTheory));
                TestSuite.AssertTrue(System.Math.Abs(mean) < 0.3, "均值应接近 0");
                TestSuite.AssertTrue(System.Math.Abs(sigma - sigmaTheory) < 0.25 * sigmaTheory,
                    string.Format("侧向标准差应在理论值 ±25% 内: 实测 {0}", sigma));
            });

            TestSuite.Add("紊流: Dryden 侧向相关时间尺度接近纵向而非垂向", () =>
            {
                // MIL-F-8785C 低空: Lv=Lu → 侧向去相关应明显慢于垂向 (Lw=h 更短)。
                // 修复前侧向误用 aW, 与垂向动态完全相同, 本测试可判别回归。
                // 理论 (h=100m, V=20): aU≈0.0761, aW=0.2;
                //   一阶 OU: R_u(τ)=e^{-aU·τ}; 二阶: R(τ)=e^{-aτ}(1-aτ/2)
                //   lag 10s 理论值: u=0.467, v=0.289, w=-0.048
                // 采样需足够长: 侧向相关时间 ~13s, 短采样的估计方差会淹没差异。
                var turb = new DrydenTurbulence(20.0, DrydenTurbulence.TurbulenceLevel.Moderate, seed: 23);
                var pos = new Vec3(0, 0, -100);

                const int N = 120000, Lag = 1200;  // 1000s 采样, 滞后 10s
                double[] u = new double[N], v = new double[N], wArr = new double[N];
                double t = 0;
                for (int i = 0; i < N; i++, t += Dt)
                {
                    Vec3 w = turb.WindAt(pos, t);
                    u[i] = w.X; v[i] = w.Y; wArr[i] = w.Z;
                }
                double NormAutocorr(double[] x)
                {
                    double s2 = 0; for (int i = 0; i < N; i++) s2 += x[i] * x[i];
                    double c = 0; for (int i = 0; i < N - Lag; i++) c += x[i] * x[i + Lag];
                    return (c / (N - Lag)) / (s2 / N);
                }
                double rU = NormAutocorr(u), rV = NormAutocorr(v), rW = NormAutocorr(wArr);
                Console.WriteLine(string.Format(
                    "        归一化自相关@10s: 纵向 {0:F2}(理论0.47)  侧向 {1:F2}(理论0.29)  垂向 {2:F2}(理论-0.05)",
                    rU, rV, rW));
                TestSuite.AssertTrue(System.Math.Abs(rU - 0.467) < 0.15,
                    string.Format("纵向自相关应符合 OU 理论 e^-0.761: 实测 {0}", rU));
                TestSuite.AssertTrue(System.Math.Abs(rV - 0.289) < 0.15,
                    string.Format("侧向自相关应符合 Lv=Lu 二阶理论: 实测 {0}", rV));
                TestSuite.AssertGreater(rV, rW + 0.1,
                    "侧向去相关应慢于垂向 (Lv=Lu > Lw=h)");
                TestSuite.AssertTrue(System.Math.Abs(rV - rU) < System.Math.Abs(rV - rW),
                    "侧向相关尺度应接近纵向量级, 而非垂向");
            });

            TestSuite.Add("紊流: 高度跨越参数刷新阈值的那一步输出连续 (无假阵风冲击)", () =>
            {
                // 回归: UpdateParameters 换 a=V/h 时若不缩放二阶通道状态 x2, 输出 y=x1+(√3/a)x2 中
                // 旧 x2 被 a 的变化倍数放大。贴地 h≈0.5 m (a=20) 稳定后高度跨过 15 m 阈值的下一步,
                // 修复前输出瞬跳: 爬到 17 m 约 10 m/s, 瞬移到 60 m 约 30 m/s; 修复后只随 σ 比例缩放 (<~1 m/s)。
                // 判据: 刷新前后相邻两步 (Δt=1/120 s) 的侧向/垂向输出差 < 3 m/s, 30 个种子全部满足。
                foreach (double newAlt in new[] { 17.0, 60.0 })
                {
                    double worstJump = 0;
                    for (int seed = 1; seed <= 30; seed++)
                    {
                        var turb = new DrydenTurbulence(20.0, DrydenTurbulence.TurbulenceLevel.Light, seed);
                        double t = 0;
                        Vec3 before = Vec3.Zero;
                        for (int i = 0; i < (int)(20.0 / Dt); i++)
                        {
                            t += Dt;
                            before = turb.WindAt(new Vec3(0, 0, -0.5), t);
                        }
                        t += Dt;
                        Vec3 after = turb.WindAt(new Vec3(0, 0, -newAlt), t);   // 触发 UpdateParameters
                        double jump = System.Math.Max(System.Math.Abs(after.Y - before.Y),
                                                      System.Math.Abs(after.Z - before.Z));
                        worstJump = System.Math.Max(worstJump, jump);
                    }
                    Console.WriteLine(string.Format(
                        "        0.5 m → {0:F0} m 刷新瞬间最大输出跳变 {1:F2} m/s (修复前约 {2})",
                        newAlt, worstJump, newAlt < 20 ? "10+" : "30+"));
                    TestSuite.AssertTrue(worstJump < 3.0,
                        string.Format("0.5→{0} m 参数刷新不应产生阵风冲击: 最大跳变 {1} m/s", newAlt, worstJump));
                }
            });

            TestSuite.Add("紊流: Dryden 幂等性 (RK4 重复调用安全)", () =>
            {
                var turb = new DrydenTurbulence(20.0, DrydenTurbulence.TurbulenceLevel.Light, seed: 3);
                var pos = new Vec3(0, 0, -50);
                Vec3 w1 = turb.WindAt(pos, 10.0);
                Vec3 w2 = turb.WindAt(pos, 10.0);   // 同时刻重复调用
                Vec3 w3 = turb.WindAt(pos, 10.0 + 1e-12);
                TestSuite.AssertClose(w1.X, w2.X, 1e-15, "重复调用应返回相同值");
                TestSuite.AssertClose(w1.Z, w2.Z, 1e-15, "重复调用应返回相同值");
                TestSuite.AssertClose(w1.X, w3.X, 1e-9, "微小时移不应显著推进状态");
            });

            TestSuite.Add("紊流: 强度随等级递增, 高度升高紊流减弱", () =>
            {
                var pos = new Vec3(0, 0, -100);
                double SigOf(DrydenTurbulence.TurbulenceLevel lv)
                {
                    var t = new DrydenTurbulence(20.0, lv, seed: 11);
                    double s2 = 0; int n = 8000; double tt = 0;
                    for (int i = 0; i < n; i++, tt += Dt) s2 += t.WindAt(pos, tt).Z * t.WindAt(pos, tt).Z;
                    return System.Math.Sqrt(s2 / n);
                }
                double sLight = SigOf(DrydenTurbulence.TurbulenceLevel.Light);
                double sSev = SigOf(DrydenTurbulence.TurbulenceLevel.Severe);
                TestSuite.AssertGreater(sSev, 2.0 * sLight, "强紊流应显著大于轻紊流");

                // 高度升高 → σw 不变 (0.1W20) 但 σu 降低; 检查低空 vs 高空纵向
                var tLow = new DrydenTurbulence(20.0, DrydenTurbulence.TurbulenceLevel.Moderate, seed: 5);
                var tHigh = new DrydenTurbulence(20.0, DrydenTurbulence.TurbulenceLevel.Moderate, seed: 5);
                var pLow = new Vec3(0, 0, -20); var pHigh = new Vec3(0, 0, -500);
                double S2(IWindModel m, Vec3 p, int comp)
                { double s2 = 0; int n = 8000; double tt = 0; for (int i = 0; i < n; i++, tt += Dt) { var v = m.WindAt(p, tt); s2 += comp == 0 ? v.X * v.X : v.Z * v.Z; } return System.Math.Sqrt(s2 / n); }
                double xLow = S2(tLow, pLow, 0), xHigh = S2(tHigh, pHigh, 0);
                Console.WriteLine(string.Format("        纵向σ: 低空20m {0:F2} vs 高空500m {1:F2} m/s", xLow, xHigh));
                TestSuite.AssertLess(xHigh, xLow, "高空纵向紊流应弱于低空");
            });

            // ================= 阵风与切变 =================
            TestSuite.Add("阵风: 1-cosine 波形正确", () =>
            {
                var gust = new DiscreteGust(new Vec3(0, 1, 0), 8.0, 5.0, 3.0);  // 东向侧风 8m/s
                var pos = new Vec3(0, 0, -100);
                TestSuite.AssertClose(gust.WindAt(pos, 4.999).Length, 0, 1e-9, "阵风前应为零");
                TestSuite.AssertClose(gust.WindAt(pos, 5.0).Y, 0, 1e-9, "起点为零");
                TestSuite.AssertClose(gust.WindAt(pos, 6.5).Y, 8.0, 1e-9, "中点峰值 = V_max");
                TestSuite.AssertClose(gust.WindAt(pos, 5.75).Y, 4.0, 1e-9, "上升段中点 = V_max/2");
                TestSuite.AssertClose(gust.WindAt(pos, 8.0).Y, 0, 1e-9, "终点回零");
                TestSuite.AssertClose(gust.WindAt(pos, 9.0).Length, 0, 1e-9, "之后保持零");
            });

            TestSuite.Add("切变: 幂律风速随高度变化", () =>
            {
                var shear = new PowerLawShearWind(new Vec3(5, 0, 0), 10.0, 0.2);
                TestSuite.AssertClose(shear.WindAt(new Vec3(0, 0, -10), 0).X, 5.0, 1e-12, "参考高度处风速不变");
                double v40 = shear.WindAt(new Vec3(0, 0, -40), 0).X;
                TestSuite.AssertClose(v40, 5.0 * System.Math.Pow(4.0, 0.2), 1e-9, "40m 处按幂律放大");
                TestSuite.AssertGreater(v40, 5.0, "高处风速应更大");
            });

            TestSuite.Add("稳定风: 顶风增加空速, 侧风产生侧滑", () =>
            {
                var s = new AircraftState();
                s.VelocityBody = new Vec3(20, 0, 0);
                double vHead = s.Airspeed(new Vec3(-5, 0, 0));
                TestSuite.AssertClose(vHead, 25.0, 1e-12, "顶风空速=25");
                double beta = s.Beta(new Vec3(0, 3, 0));   // 东向风 → 相对气流向西 → v_rel<0
                TestSuite.AssertTrue(System.Math.Abs(beta) > 0.05, "侧风应产生侧滑角");
            });

            // ================= 验收: 侧风阵风扰动与回稳 =================
            TestSuite.Add("验收: 侧风阵风 → 滚转/偏航扰动 → 操纵回稳", () =>
            {
                // 配平平飞 + 东向 8 m/s 1-cosine 侧风阵风 (t=5s, 3s 宽)
                var plane = new Aircraft(planeTemplate.Mass, new AeroModel(planeTemplate.Aero.Data),
                                         planeTemplate.Engine,
                                         new DiscreteGust(new Vec3(0, 1, 0), 8.0, 5.0, 3.0))
                { Gear = planeTemplate.Gear };
                var integ = new RigidBodyIntegrator(plane);
                var trimmer2 = new Trimmer(plane);

                TrimResult trim = trimmer2.TrimLevelFlight(20.0, 100.0);
                var s = trimmer2.BuildTrimState(trim, 100.0);
                var c = new ControlInputs { Elevator = trim.Elevator, Throttle = trim.Throttle };

                // 飞行员式横侧增稳 (副翼保持 + 偏航阻尼), 俯仰用配平保持
                const double kpRoll = 0.6, kdRoll = 0.15, krYaw = 0.8, tau = 0.04;
                double ailF = 0, rudF = 0;

                double maxBank = 0, maxBeta = 0, maxR = 0;
                double bankAtEnd = 99, betaAtEnd = 99, altDrift = 0;
                bool gustWindow = false;

                for (double t = 0; t <= 30.0; t += Dt)
                {
                    double[] e = s.Euler321();
                    double bank = e[0];
                    double beta = s.Beta(Vec3.Zero);
                    double p = s.AngularVelocityBody.X, r = s.AngularVelocityBody.Z;

                    if (t >= 5.0 && t <= 8.0) gustWindow = true;
                    if (t >= 5.0)   // 阵风开始后才统计扰动
                    {
                        maxBank = System.Math.Max(maxBank, System.Math.Abs(bank));
                        maxBeta = System.Math.Max(maxBeta, System.Math.Abs(beta));
                        maxR = System.Math.Max(maxR, System.Math.Abs(r));
                    }
                    if (t >= 25.0) { bankAtEnd = System.Math.Abs(bank); betaAtEnd = System.Math.Abs(beta); }
                    altDrift = System.Math.Max(altDrift, System.Math.Abs(s.Altitude - 100.0));

                    // 操纵: 回稳 (阵风后飞行员压坡度回平 + 蹬舵阻尼)
                    double ailCmd = Mathd.Clamp(kpRoll * bank + kdRoll * p, -0.3, 0.3);
                    double rudCmd = Mathd.Clamp(-krYaw * r, -0.3, 0.3);
                    ailF += (ailCmd - ailF) * Dt / tau;
                    rudF += (rudCmd - rudF) * Dt / tau;
                    c.Aileron = ailF; c.Rudder = rudF;

                    integ.Step(s, c, t, Dt);
                    if (double.IsNaN(s.Position.X)) { TestSuite.Fail("NaN t=" + t); return; }
                }

                Console.WriteLine(string.Format(
                    "        阵风扰动峰值: 滚转 {0:F1}°  侧滑 {1:F2}°  偏航率 {2:F2} rad/s", maxBank * Mathd.Rad2Deg, maxBeta * Mathd.Rad2Deg, maxR));
                Console.WriteLine(string.Format(
                    "        25s 后回稳状态: 滚转 {0:F2}°  侧滑 {1:F2}°  高度漂移 {2:F1} m",
                    bankAtEnd * Mathd.Rad2Deg, betaAtEnd * Mathd.Rad2Deg, altDrift));

                TestSuite.AssertTrue(gustWindow, "阵风窗口已进入");
                // 合理扰动: 足以察觉但不失控。
                // 注: TestUAV 上反效应弱 (Clβ≈-0.004, 数据事实), 侧风阵风的滚转
                // 扰动天然以"侧滑→偏航"为主、滚转为辅 —— 两者都出现且有限即合理。
                TestSuite.AssertGreater(maxBank, 0.15 * Mathd.Deg2Rad, "应产生 >0.15° 的滚转扰动");
                TestSuite.AssertLess(maxBank, 30.0 * Mathd.Deg2Rad, "滚转扰动应 <30° (合理范围)");
                TestSuite.AssertGreater(maxBeta, 5.0 * Mathd.Deg2Rad, "应产生 >5° 侧滑");
                TestSuite.AssertLess(maxBeta, 25.0 * Mathd.Deg2Rad, "侧滑扰动应 <25° (合理范围)");
                TestSuite.AssertGreater(maxR, 0.02, "应产生偏航率扰动");
                // 操纵回稳
                TestSuite.AssertLess(bankAtEnd, 1.0 * Mathd.Deg2Rad, "回稳后滚转 <1°");
                TestSuite.AssertLess(betaAtEnd, 1.0 * Mathd.Deg2Rad, "回稳后侧滑 <1°");
                TestSuite.AssertLess(altDrift, 15.0, "高度漂移 <15m");
                TestSuite.AssertTrue(!double.IsNaN(bankAtEnd), "无 NaN");
            });

            // ================= Dryden 紊流下飞行有界 =================
            TestSuite.Add("紊流: 中度 Dryden 紊流下平飞扰动有界且可控", () =>
            {
                var plane = new Aircraft(planeTemplate.Mass, new AeroModel(planeTemplate.Aero.Data),
                                         planeTemplate.Engine,
                                         new DrydenTurbulence(20.0, DrydenTurbulence.TurbulenceLevel.Moderate, seed: 21))
                { Gear = planeTemplate.Gear };
                var integ = new RigidBodyIntegrator(plane);
                var trimmer3 = new Trimmer(plane);

                TrimResult trim = trimmer3.TrimLevelFlight(20.0, 100.0);
                var s = trimmer3.BuildTrimState(trim, 100.0);
                var c = new ControlInputs { Elevator = trim.Elevator, Throttle = trim.Throttle };

                const double kpRoll = 0.6, kdRoll = 0.15, krYaw = 0.8, tau = 0.04;
                double ailF = 0, rudF = 0;
                double maxBank = 0, maxAltDrift = 0, maxSpdDev = 0;

                for (double t = 0; t <= 60.0; t += Dt)
                {
                    double[] e = s.Euler321();
                    maxBank = System.Math.Max(maxBank, System.Math.Abs(e[0]));
                    maxAltDrift = System.Math.Max(maxAltDrift, System.Math.Abs(s.Altitude - 100.0));
                    maxSpdDev = System.Math.Max(maxSpdDev, System.Math.Abs(s.Airspeed(Vec3.Zero) - 20.0));

                    double p = s.AngularVelocityBody.X, r = s.AngularVelocityBody.Z;
                    double ailCmd = Mathd.Clamp(kpRoll * e[0] + kdRoll * p, -0.3, 0.3);
                    double rudCmd = Mathd.Clamp(-krYaw * r, -0.3, 0.3);
                    ailF += (ailCmd - ailF) * Dt / tau;
                    rudF += (rudCmd - rudF) * Dt / tau;
                    c.Aileron = ailF; c.Rudder = rudF;

                    integ.Step(s, c, t, Dt);
                    if (double.IsNaN(s.Position.X)) { TestSuite.Fail("NaN t=" + t); return; }
                }
                Console.WriteLine(string.Format(
                    "        60s 中度紊流: 最大滚转 {0:F1}°, 高度偏差 {1:F1} m, 速度偏差 {2:F1} m/s",
                    maxBank * Mathd.Rad2Deg, maxAltDrift, maxSpdDev));

                TestSuite.AssertLess(maxBank, 20.0 * Mathd.Deg2Rad, "中度紊流下滚转扰动 <20°");
                TestSuite.AssertLess(maxAltDrift, 60.0, "高度偏差 <60m (开环不追高度, 慢漂移合理)");
                TestSuite.AssertLess(maxSpdDev, 8.0, "速度偏差 <8 m/s");
                TestSuite.AssertTrue(!double.IsNaN(maxBank), "无 NaN");
            });
        }
    }
}
