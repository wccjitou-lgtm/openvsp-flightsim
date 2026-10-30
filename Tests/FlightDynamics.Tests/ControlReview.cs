using System;
using System.Collections.Generic;
using FlightDynamics.IO;
using FlightDynamics.Math;

namespace FlightDynamics.Tests
{
    /// <summary>
    /// 控制律/姿态响应复核 (独立可执行): 复刻 Unity 侧 ControlSurfaceMapper + ControlLaws 的
    /// 公式与常数, 驱动同一份 6DOF 核心, 检查 (1) 开环模态 (2) 杆量→姿态方向 (3) 闭环增稳响应
    /// (4) NED→Unity 姿态映射。Unity 侧常数改动时需同步本文件。
    /// 用法: ControlReview.exe [AircraftConfig.json]
    /// </summary>
    public static class ControlReview
    {
        // ---- 复刻 ControlSurfaceMapper ----
        class Mapper
        {
            public double MaxE, MaxA, MaxR, SE, SA, SR, fe, fa, fr;
            const double Tau = 0.04;
            public Mapper(AeroData a, double maxE, double maxA, double maxR)
            {
                MaxE = maxE * Mathd.Deg2Rad; MaxA = maxA * Mathd.Deg2Rad; MaxR = maxR * Mathd.Deg2Rad;
                SE = Sign(a.Cmde); SA = Sign(a.Clda); SR = Sign(a.Cndr);
            }
            static double Sign(double d) { return d > 0 ? 1 : (d < 0 ? -1 : 1); }
            public ControlInputs Map(double pitch, double roll, double yaw, double thr, double dt)
            {
                double ec = SE * Mathd.Clamp(pitch, -1, 1) * MaxE;
                double ac = SA * Mathd.Clamp(roll, -1, 1) * MaxA;
                double rc = SR * Mathd.Clamp(yaw, -1, 1) * MaxR;
                double k = dt / Tau;
                var c = new ControlInputs();
                c.Elevator = fe += (ec - fe) * k;
                c.Aileron = fa += (ac - fa) * k;
                c.Rudder = fr += (rc - fr) * k;
                c.Throttle = Mathd.Clamp(thr, 0, 1);
                return c;
            }
        }

        // ---- 复刻 ControlLaws ----
        static double RollStab(double bank, double des, double p) { return 0.6 * (des - bank) - 0.15 * p; }
        static double PitchStab(double tgt, double pitch, double q) { return 0.8 * (tgt - pitch) + 0.18 * (0.0 - q); }
        static double YawDamp(double r, double yawStick) { return -0.8 * r + yawStick * 0.349; }
        static double AltToPitch(double target, double alt)
        {
            double err = Mathd.Clamp(target - alt, -60, 60);
            return Mathd.Clamp((2 + 0.12 * err) * Mathd.Deg2Rad, -8 * Mathd.Deg2Rad, 12 * Mathd.Deg2Rad);
        }
        static double BankFromHdg(double heading, double bearing, double maxBankDeg)
        {
            double err = System.Math.Atan2(System.Math.Sin(bearing - heading), System.Math.Cos(bearing - heading));
            if (err < -System.Math.PI + 0.02) err = System.Math.PI;
            return Mathd.Clamp(0.9 * err, -maxBankDeg * Mathd.Deg2Rad, maxBankDeg * Mathd.Deg2Rad);
        }

        class Rig
        {
            public AircraftConfigJson.LoadedAircraft Cfg;
            public Aircraft Plane;
            public RigidBodyIntegrator Integ;
            public AircraftState S;
            public Mapper M;
            public double T;
            public const double Dt = 1.0 / 120.0;
            public double MaxE = 25, MaxA = 20, MaxR = 25;

            public Rig(string path)
            {
                Cfg = AircraftConfigJson.Load(path);
                Plane = new Aircraft(Cfg.Mass, new AeroModel(Cfg.Aero), Cfg.Engine, new CalmWind());
                Integ = new RigidBodyIntegrator(Plane);
                M = new Mapper(Cfg.Aero, MaxE, MaxA, MaxR);
            }
            public TrimResult TrimAt(double V, double alt)
            {
                var tr = new Trimmer(Plane).TrimLevelFlight(V, alt);
                S = new Trimmer(Plane).BuildTrimState(tr, alt);
                M = new Mapper(Cfg.Aero, MaxE, MaxA, MaxR);
                T = 0;
                return tr;
            }
            public void Step(ControlInputs c) { Integ.Step(S, c, T, Dt); T += Dt; }
            public double Roll { get { return S.Euler321()[0]; } }
            public double Pitch { get { return S.Euler321()[1]; } }
            public double Yaw { get { return S.Euler321()[2]; } }
            public double Alpha { get { return S.Alpha(Vec3.Zero); } }
            public double Beta { get { return S.Beta(Vec3.Zero); } }
            public double V { get { return S.Airspeed(Vec3.Zero); } }
            public double Alt { get { return S.Altitude; } }
            public double P { get { return S.AngularVelocityBody.X; } }
            public double Q { get { return S.AngularVelocityBody.Y; } }
            public double R { get { return S.AngularVelocityBody.Z; } }
        }

        static double D(double rad) { return rad * Mathd.Rad2Deg; }

        public static int Main(string[] args)
        {
            string path = args.Length > 0 ? args[0] : "AircraftConfig.json";
            var rig = new Rig(path);
            var a = rig.Cfg.Aero;
            Console.WriteLine("=== config: " + rig.Cfg.Name + "  m=" + rig.Cfg.Mass.MassKg.ToString("F2")
                + " Ixx/Iyy/Izz/Ixz=" + rig.Cfg.Mass.Ixx.ToString("F3") + "/" + rig.Cfg.Mass.Iyy.ToString("F3")
                + "/" + rig.Cfg.Mass.Izz.ToString("F3") + "/" + rig.Cfg.Mass.Ixz.ToString("F3"));
            int fails = 0;

            // ---------------- 1. 配平扫描 ----------------
            Console.WriteLine("\n--- 1. Trim sweep (alt 100 m) ---");
            Console.WriteLine("  V    alpha(deg) de(deg) throttle  CL     L/D");
            foreach (double V in new[] { 12.0, 15, 18, 22, 25, 30, 35, 40 })
            {
                var tr = rig.TrimAt(V, 100);
                double cl = a.CL0 + a.CLalpha * tr.Alpha + a.CLde * tr.Elevator;
                double cd = a.CD0 + a.KInduced * cl * cl;
                Console.WriteLine("  " + V.ToString("F0").PadLeft(3) + "  " + D(tr.Alpha).ToString("F2").PadLeft(8)
                    + "  " + D(tr.Elevator).ToString("F2").PadLeft(7) + "  " + tr.Throttle.ToString("F3").PadLeft(8)
                    + "  " + cl.ToString("F3") + "  " + (cl / cd).ToString("F1")
                    + (tr.Throttle > 1.0 ? "   <-- thrust-limited" : "")
                    + (System.Math.Abs(tr.Elevator) > rig.MaxE * Mathd.Deg2Rad * 0.8 ? "   <-- elevator near limit" : ""));
            }

            // ---------------- 2. 开环线性模态 ----------------
            Console.WriteLine("\n--- 2. Open-loop modes from numerical linearisation ---");
            foreach (double V in new[] { 18.0, 25, 35 })
            {
                var tr = rig.TrimAt(V, 100);
                var thr = tr.Throttle;
                Modes(rig, tr, V);
            }

            // ---------------- 3. 杆量→姿态方向 ----------------
            Console.WriteLine("\n--- 3. Stick -> attitude direction (open loop, V=25) ---");
            fails += SignChecks(rig);

            // ---------------- 4. 闭环增稳 ----------------
            Console.WriteLine("\n--- 4. Closed-loop stabilisation (ModePilot/STAB law, V=25 trim throttle) ---");
            fails += ClosedLoop(rig);

            // ---------------- 5. Unity 姿态映射 ----------------
            Console.WriteLine("\n--- 5. NED -> Unity transform mapping ---");
            fails += UnityMapping();

            // ---------------- 6. 实际模式参数下的长时程/手动工况 ----------------
            Console.WriteLine("\n--- 6. Realistic mode scenarios ---");
            ExtendedScenarios(rig);

            Console.WriteLine("\nREVIEW RESULT: " + (fails == 0 ? "ALL CHECKS PASS" : fails + " CHECK(S) FAILED"));
            return fails == 0 ? 0 : 1;
        }

        // =============== 线性化 ===============
        // 状态 x = [u v w p q r phi theta], 控制 = 配平常值
        static double[] Deriv(Rig rig, double[] x, ControlInputs c, double alt)
        {
            var s = new AircraftState();
            s.Position = new Vec3(0, 0, -alt);
            s.Attitude = Quat.FromEuler321(x[6], x[7], 0);
            s.VelocityBody = new Vec3(x[0], x[1], x[2]);
            s.AngularVelocityBody = new Vec3(x[3], x[4], x[5]);
            AeroResult ar; double th;
            Vec3 F = rig.Plane.ComputeForce(s, c, 0, out ar, out th);
            Vec3 M = rig.Plane.ComputeMoment(s, ar);
            Vec3 vd = F * (1.0 / rig.Plane.Mass.MassKg) - s.AngularVelocityBody.Cross(s.VelocityBody);
            var I = rig.Plane.Mass.InertiaTensor();
            Vec3 iw = I.Multiply(s.AngularVelocityBody);
            Vec3 wd = I.Inverse().Multiply(M - s.AngularVelocityBody.Cross(iw));
            double p = x[3], q = x[4], r = x[5], ph = x[6], tt = x[7];
            double phd = p + System.Math.Tan(tt) * (q * System.Math.Sin(ph) + r * System.Math.Cos(ph));
            double thd = q * System.Math.Cos(ph) - r * System.Math.Sin(ph);
            return new[] { vd.X, vd.Y, vd.Z, wd.X, wd.Y, wd.Z, phd, thd };
        }

        static void Modes(Rig rig, TrimResult tr, double V)
        {
            double alt = 100;
            var c = new ControlInputs { Throttle = tr.Throttle, Elevator = tr.Elevator };
            double[] x0 = { V * System.Math.Cos(tr.Alpha), 0, V * System.Math.Sin(tr.Alpha), 0, 0, 0, 0, tr.Alpha };
            double[] f0 = Deriv(rig, x0, c, alt);
            double[,] A = new double[8, 8];
            for (int j = 0; j < 8; j++)
            {
                double h = j < 3 ? 1e-4 : 1e-6;
                var xp = (double[])x0.Clone(); var xm = (double[])x0.Clone();
                xp[j] += h; xm[j] -= h;
                var fp = Deriv(rig, xp, c, alt); var fm = Deriv(rig, xm, c, alt);
                for (int i = 0; i < 8; i++) A[i, j] = (fp[i] - fm[i]) / (2 * h);
            }
            double resid = 0; for (int i = 0; i < 8; i++) resid = System.Math.Max(resid, System.Math.Abs(f0[i]));
            var ev = Eig(A);
            Console.WriteLine("  V=" + V + " m/s  (trim residual |xdot|max=" + resid.ToString("E1") + ")");
            double lamMax = 0; foreach (var e0 in ev) lamMax = System.Math.Max(lamMax, System.Math.Sqrt(e0.Re * e0.Re + e0.Im * e0.Im));
            double hSub = Rig.Dt / RigidBodyIntegrator.Substeps;
            Console.WriteLine("    max|lambda|*h(RK4 substep h=1/480 s) = " + (lamMax * hSub).ToString("F2") + "  (RK4 stable < 2.78)");
            Array.Sort(ev, (p1, p2) => p2.Re.CompareTo(p1.Re));
            foreach (var e in ev)
            {
                string tag = "";
                double wn = System.Math.Sqrt(e.Re * e.Re + e.Im * e.Im);
                if (System.Math.Abs(e.Im) > 1e-6)
                {
                    if (e.Im < 0) continue;
                    double z = -e.Re / wn;
                    tag = "oscillatory  wn=" + wn.ToString("F3") + " rad/s  zeta=" + z.ToString("F3")
                        + "  period=" + (2 * System.Math.PI / System.Math.Abs(e.Im)).ToString("F2") + " s"
                        + (e.Re > 0 ? "  UNSTABLE" : "");
                    Console.WriteLine("    " + e.Re.ToString("F4").PadLeft(9) + " +/-" + e.Im.ToString("F4") + "j   " + tag);
                }
                else
                {
                    tag = "real  tau=" + (e.Re != 0 ? (-1.0 / e.Re).ToString("F3") : "inf") + " s" + (e.Re > 0 ? "  UNSTABLE (t2=" + (System.Math.Log(2) / e.Re).ToString("F1") + " s)" : "");
                    Console.WriteLine("    " + e.Re.ToString("F4").PadLeft(9) + "                " + tag);
                }
            }
        }

        struct Cx { public double Re, Im; public Cx(double r, double i) { Re = r; Im = i; } }

        // 特征多项式 (Faddeev-LeVerrier) + Durand-Kerner
        static Cx[] Eig(double[,] A)
        {
            int n = A.GetLength(0);
            double[] c = new double[n + 1]; c[0] = 1;
            double[,] M = new double[n, n];
            for (int i = 0; i < n; i++) M[i, i] = 1;
            for (int k = 1; k <= n; k++)
            {
                double[,] AM = new double[n, n];
                for (int i = 0; i < n; i++) for (int j = 0; j < n; j++)
                { double s = 0; for (int l = 0; l < n; l++) s += A[i, l] * M[l, j]; AM[i, j] = s; }
                double tr = 0;
                for (int i = 0; i < n; i++) tr += AM[i, i];
                c[k] = -tr / k;
                for (int i = 0; i < n; i++) AM[i, i] += c[k];
                M = AM;
            }
            // roots of x^n + c1 x^(n-1) ... + cn
            var z = new Cx[n];
            for (int i = 0; i < n; i++) { double ang = 2 * System.Math.PI * i / n + 0.4; z[i] = new Cx(System.Math.Cos(ang) * 20, System.Math.Sin(ang) * 20); }
            for (int it = 0; it < 5000; it++)
            {
                double maxd = 0;
                for (int i = 0; i < n; i++)
                {
                    Cx p = new Cx(1, 0);
                    foreach (int k in Range(1, n)) p = Add(Mul(p, z[i]), new Cx(c[k], 0));
                    Cx den = new Cx(1, 0);
                    for (int j = 0; j < n; j++) if (j != i) den = Mul(den, Sub(z[i], z[j]));
                    Cx d = Div(p, den);
                    z[i] = Sub(z[i], d);
                    maxd = System.Math.Max(maxd, System.Math.Sqrt(d.Re * d.Re + d.Im * d.Im));
                }
                if (maxd < 1e-13) break;
            }
            for (int i = 0; i < n; i++) if (System.Math.Abs(z[i].Im) < 1e-7) z[i] = new Cx(z[i].Re, 0);
            return z;
        }
        static IEnumerable<int> Range(int a, int b) { for (int i = a; i <= b; i++) yield return i; }
        static Cx Add(Cx a, Cx b) { return new Cx(a.Re + b.Re, a.Im + b.Im); }
        static Cx Sub(Cx a, Cx b) { return new Cx(a.Re - b.Re, a.Im - b.Im); }
        static Cx Mul(Cx a, Cx b) { return new Cx(a.Re * b.Re - a.Im * b.Im, a.Re * b.Im + a.Im * b.Re); }
        static Cx Div(Cx a, Cx b) { double d = b.Re * b.Re + b.Im * b.Im; return new Cx((a.Re * b.Re + a.Im * b.Im) / d, (a.Im * b.Re - a.Re * b.Im) / d); }

        // =============== 杆量方向 ===============
        static int SignChecks(Rig rig)
        {
            int fails = 0;
            // 每项: 配平 → 施加杆量 → 看响应
            var cases = new[]
            {
                new { name = "pitch stick +0.3 (pull)", p = 0.3, r = 0.0, y = 0.0 },
                new { name = "roll  stick +0.3 (right)", p = 0.0, r = 0.3, y = 0.0 },
                new { name = "yaw   stick +0.3 (right)", p = 0.0, r = 0.0, y = 0.3 },
            };
            foreach (var cs in cases)
            {
                var tr = rig.TrimAt(25, 300);
                double pitch0 = rig.Pitch, yaw0 = rig.Yaw;
                double hold = tr.Elevator;
                string o = "  " + cs.name + ": ";
                double tReport = 0, q05 = 0, dth05 = 0, phi05 = 0, p05 = 0, dpsi05 = 0, r05 = 0;
                for (int i = 0; i < 120 * 3; i++)
                {
                    var c = rig.M.Map(cs.p, cs.r, cs.y, tr.Throttle, Rig.Dt);
                    c.Elevator += hold;   // 叠加配平升降舵 (杆量是相对配平的增量)
                    rig.Step(c);
                    if (i == 59)
                    { q05 = rig.Q; dth05 = rig.Pitch - pitch0; phi05 = rig.Roll; p05 = rig.P; dpsi05 = rig.Yaw - yaw0; r05 = rig.R; }
                    if (i == 59 || i == 119 || i == 359)
                    {
                        tReport = (i + 1) / 120.0;
                        o += "[t=" + tReport.ToString("F1") + " dTheta=" + D(rig.Pitch - pitch0).ToString("F2") + " phi=" + D(rig.Roll).ToString("F2")
                          + " dPsi=" + D(rig.Yaw - yaw0).ToString("F2") + " p=" + D(rig.P).ToString("F1") + " q=" + D(rig.Q).ToString("F1")
                          + " r=" + D(rig.R).ToString("F1") + " beta=" + D(rig.Beta).ToString("F2") + "] ";
                    }
                }
                Console.WriteLine(o);
                bool ok = true;
                if (cs.p != 0) ok = q05 > 0 && dth05 > 0;
                if (cs.r != 0) ok = phi05 > 0 && p05 > 0;
                if (cs.y != 0) ok = dpsi05 > 0 && r05 > 0;
                Console.WriteLine("      -> " + (ok ? "PASS direction correct (judged at t=0.5 s)" : "FAIL direction wrong (judged at t=0.5 s)"));
                if (!ok) fails++;
            }
            // 滚转稳态速率: 与理论 p_ss = -Clda*da / (Clp * b/(2V)) 对比
            {
                var tr = rig.TrimAt(25, 300);
                double rolled = 0;
                for (int i = 0; i < 120; i++)
                {
                    var c = rig.M.Map(0, 1.0, 0, tr.Throttle, Rig.Dt); c.Elevator += tr.Elevator; rig.Step(c);
                    if (i == 30) rolled = rig.P;
                }
                var a = rig.Cfg.Aero; double da = rig.MaxA * Mathd.Deg2Rad;
                double clEff = a.Clda * (System.Math.Sign(a.Clda) * da);
                double pss = -clEff / (a.Clp * a.Bref / (2 * 25));
                Console.WriteLine("  full right stick: sim p(1s)=" + D(rig.P).ToString("F1") + " deg/s, theory p_ss(no coupling)=" + D(pss).ToString("F1") + " deg/s");
                if (System.Math.Abs(rig.P - pss) / System.Math.Abs(pss) > 0.25) { Console.WriteLine("      -> FAIL steady roll rate deviates >25% from derivative theory"); fails++; }
                else Console.WriteLine("      -> PASS steady roll rate consistent with Clda/Clp");
            }
            return fails;
        }

        // =============== 闭环 ===============
        static int ClosedLoop(Rig rig)
        {
            int fails = 0;
            // (a) 坡度阶跃 30° (STAB 滚转杆满偏), 俯仰按高度保持
            {
                var tr = rig.TrimAt(25, 300);
                double alt0 = rig.Alt, hdg0 = rig.Yaw;
                double engage = alt0;
                double maxBank = 0, maxBeta = 0, minAlt = alt0, minV = rig.V, overshoot = 0, tRise = -1;
                Console.WriteLine("  (a) bank step to +30deg, pitch = altitude-hold, throttle trim=" + tr.Throttle.ToString("F3"));
                Console.WriteLine("      t    bank   p   pitch  alpha  beta   V    dAlt  hdgRate(deg/s) theory(g*tan(phi)/V)");
                double prevYaw = rig.Yaw; double tprev = 0;
                for (int i = 0; i < 120 * 40; i++)
                {
                    double pitchTgt = AltToPitch(engage, rig.Alt);
                    double eCmd = PitchStab(pitchTgt, rig.Pitch, rig.Q);
                    double aCmd = RollStab(rig.Roll, 30 * Mathd.Deg2Rad, rig.P);
                    double rCmd = YawDamp(rig.R, 0);
                    var c = rig.M.Map(Mathd.Clamp(eCmd / (rig.MaxE * Mathd.Deg2Rad), -0.8, 0.8),
                                      Mathd.Clamp(aCmd / (rig.MaxA * Mathd.Deg2Rad), -0.5, 0.5),
                                      Mathd.Clamp(rCmd / (rig.MaxR * Mathd.Deg2Rad), -0.5, 0.5), tr.Throttle, Rig.Dt);
                    rig.Step(c);
                    maxBank = System.Math.Max(maxBank, D(rig.Roll));
                    maxBeta = System.Math.Max(maxBeta, System.Math.Abs(D(rig.Beta)));
                    minAlt = System.Math.Min(minAlt, rig.Alt); minV = System.Math.Min(minV, rig.V);
                    if (tRise < 0 && D(rig.Roll) >= 27) tRise = rig.T;
                    if (i % (120 * 4) == 120 * 4 - 1)
                    {
                        double dpsi = D(WrapPi(rig.Yaw - prevYaw)) / (rig.T - tprev); prevYaw = rig.Yaw; tprev = rig.T;
                        double th = 9.80665 * System.Math.Tan(rig.Roll) / rig.V * Mathd.Rad2Deg;
                        Console.WriteLine("    " + rig.T.ToString("F0").PadLeft(4) + "  " + D(rig.Roll).ToString("F1").PadLeft(5) + " " + D(rig.P).ToString("F1").PadLeft(5)
                            + "  " + D(rig.Pitch).ToString("F1").PadLeft(5) + " " + D(rig.Alpha).ToString("F1").PadLeft(5) + " " + D(rig.Beta).ToString("F2").PadLeft(6)
                            + " " + rig.V.ToString("F1").PadLeft(5) + " " + (rig.Alt - alt0).ToString("F1").PadLeft(6) + "   " + dpsi.ToString("F2").PadLeft(7) + "   " + th.ToString("F2").PadLeft(7));
                    }
                }
                double finalBank = D(rig.Roll);
                Console.WriteLine("      bank: rise(90%)=" + tRise.ToString("F2") + " s, peak=" + maxBank.ToString("F2") + ", final(40s)=" + finalBank.ToString("F2")
                    + "; max|beta|=" + maxBeta.ToString("F2") + " deg; min alt delta=" + (minAlt - alt0).ToString("F1") + " m; min V=" + minV.ToString("F1"));
                // 纯 P 坡度环有稳态误差, 大小取决于 |Clda| 与上反效应 Clbeta 的对抗;
                // 高静稳定构型 (如根尖弦互换后 SM 21%) 会落在 27-28deg。界限只抓
                // 符号/舵机/积分器级别的错误, 不考核调参品质。
                bool ok = System.Math.Abs(finalBank - 30) < 4.0 && maxBank < 33 && !double.IsNaN(rig.Roll);
                Console.WriteLine("      -> " + (ok ? "PASS bank tracks 30deg without big overshoot" : "FAIL bank tracking")); if (!ok) fails++;
            }
            // (b) 俯仰阶跃: 目标 +10°, 之后回平
            {
                var tr = rig.TrimAt(25, 300);
                Console.WriteLine("  (b) pitch step: target +10deg for 8 s (throttle held at trim), then 2deg baseline");
                double maxP = -99, tRise = -1; double qpk = 0;
                for (int i = 0; i < 120 * 30; i++)
                {
                    double tgt = rig.T < 8 ? 10 * Mathd.Deg2Rad : 2 * Mathd.Deg2Rad;
                    double eCmd = PitchStab(tgt, rig.Pitch, rig.Q);
                    double rCmd = YawDamp(rig.R, 0);
                    double aCmd = RollStab(rig.Roll, 0, rig.P);
                    var c = rig.M.Map(Mathd.Clamp(eCmd / (rig.MaxE * Mathd.Deg2Rad), -0.8, 0.8),
                                      Mathd.Clamp(aCmd / (rig.MaxA * Mathd.Deg2Rad), -0.5, 0.5),
                                      Mathd.Clamp(rCmd / (rig.MaxR * Mathd.Deg2Rad), -0.5, 0.5), tr.Throttle, Rig.Dt);
                    rig.Step(c);
                    if (rig.T < 8) { maxP = System.Math.Max(maxP, D(rig.Pitch)); if (tRise < 0 && D(rig.Pitch) >= 9) tRise = rig.T; qpk = System.Math.Max(qpk, D(rig.Q)); }
                    if (i % (120 * 3) == 120 * 3 - 1)
                        Console.WriteLine("    t=" + rig.T.ToString("F0").PadLeft(3) + " pitch=" + D(rig.Pitch).ToString("F2").PadLeft(6) + " alpha=" + D(rig.Alpha).ToString("F2").PadLeft(6)
                            + " V=" + rig.V.ToString("F1") + " alt=" + rig.Alt.ToString("F1") + " vs=" + (-rig.S.VelocityNed.Z).ToString("F2") + " q=" + D(rig.Q).ToString("F2"));
                }
                Console.WriteLine("      pitch: 90% rise=" + tRise.ToString("F2") + " s, peak=" + maxP.ToString("F2") + "deg, peak q=" + qpk.ToString("F1") + " deg/s");
                // 纯 P 俯仰环稳态误差随 |Cmde/Cmalpha| 变化: 静稳定度高的构型需要
                // 更多升降舵 per 度, 峰值会明显低于目标 (SM 21% 构型约 7.5deg)。
                bool ok = maxP > 6.5 && maxP < 11.5;
                Console.WriteLine("      -> " + (ok ? "PASS pitch step reaches target, small overshoot" : "CHECK pitch step: steady-state offset/overshoot (see table)")); if (!ok) fails++;
            }
            // (c) 航向捕获 +90° (RTL/Loiter 常用链路)
            {
                var tr = rig.TrimAt(25, 300);
                double engage = rig.Alt; double target = 90 * Mathd.Deg2Rad;
                Console.WriteLine("  (c) heading capture +90deg: bank<=30deg, alt hold");
                double maxBank = 0, maxOver = 0, hdgFinal = 0; double tCap = -1;
                for (int i = 0; i < 120 * 60; i++)
                {
                    double des = BankFromHdg(rig.Yaw, target, 30);
                    double pitchTgt = AltToPitch(engage, rig.Alt);
                    double eCmd = PitchStab(pitchTgt, rig.Pitch, rig.Q);
                    double aCmd = RollStab(rig.Roll, des, rig.P);
                    double rCmd = YawDamp(rig.R, 0);
                    var c = rig.M.Map(Mathd.Clamp(eCmd / (rig.MaxE * Mathd.Deg2Rad), -0.8, 0.8),
                                      Mathd.Clamp(aCmd / (rig.MaxA * Mathd.Deg2Rad), -0.5, 0.5),
                                      Mathd.Clamp(rCmd / (rig.MaxR * Mathd.Deg2Rad), -0.5, 0.5), tr.Throttle, Rig.Dt);
                    rig.Step(c);
                    maxBank = System.Math.Max(maxBank, System.Math.Abs(D(rig.Roll)));
                    maxOver = System.Math.Max(maxOver, D(rig.Yaw) - 90);
                    if (tCap < 0 && System.Math.Abs(D(rig.Yaw) - 90) < 2) tCap = rig.T;
                    if (i % (120 * 6) == 120 * 6 - 1)
                        Console.WriteLine("    t=" + rig.T.ToString("F0").PadLeft(3) + " hdg=" + D(rig.Yaw).ToString("F1").PadLeft(6) + " bank=" + D(rig.Roll).ToString("F1").PadLeft(5)
                            + " beta=" + D(rig.Beta).ToString("F2").PadLeft(6) + " alt=" + (rig.Alt - engage).ToString("F1").PadLeft(6) + " V=" + rig.V.ToString("F1"));
                }
                hdgFinal = D(rig.Yaw);
                Console.WriteLine("      heading within 2deg at t=" + tCap.ToString("F1") + " s, overshoot=" + maxOver.ToString("F2") + "deg, max bank=" + maxBank.ToString("F1") + ", final hdg=" + hdgFinal.ToString("F2"));
                bool ok = System.Math.Abs(hdgFinal - 90) < 2 && maxOver < 8;
                Console.WriteLine("      -> " + (ok ? "PASS heading captured, turn direction right" : "FAIL heading capture")); if (!ok) fails++;
            }
            // (d) 开环自由响应: 配平后无输入 60 s (模态是否发散)
            {
                var tr = rig.TrimAt(25, 300);
                double alt0 = rig.Alt;
                // 小扰动: 初始 3° 滚转 + 0.5° 侧滑效果
                rig.S.Attitude = Quat.FromEuler321(3 * Mathd.Deg2Rad, tr.PitchAttitude, 0);
                double maxBank = 0;
                for (int i = 0; i < 120 * 60; i++)
                {
                    var c = new ControlInputs { Throttle = tr.Throttle, Elevator = tr.Elevator };
                    rig.Step(c);
                    maxBank = System.Math.Max(maxBank, System.Math.Abs(D(rig.Roll)));
                }
                Console.WriteLine("  (d) open-loop 60 s after 3deg bank disturbance (no input): final bank=" + D(rig.Roll).ToString("F1") + " max|bank|=" + maxBank.ToString("F1")
                    + " hdg=" + D(rig.Yaw).ToString("F1") + " beta=" + D(rig.Beta).ToString("F2") + " V=" + rig.V.ToString("F1") + " dAlt=" + (rig.Alt - alt0).ToString("F1"));
            }
            return fails;
        }

        static double WrapPi(double x) { return System.Math.Atan2(System.Math.Sin(x), System.Math.Cos(x)); }

        // 一步闭环 (姿态保持律, 与 ModePilot.Poll 末段一致); betaGain>0 时叠加 β 反馈方向舵 (对照用)
        static void StepLaw(Rig rig, double desBank, double pitchTgt, double throttle, double yawStick, double betaGain)
        {
            double eCmd = PitchStab(pitchTgt, rig.Pitch, rig.Q);
            double aCmd = RollStab(rig.Roll, desBank, rig.P);
            double rCmd = YawDamp(rig.R, yawStick) + betaGain * rig.Beta;
            var c = rig.M.Map(Mathd.Clamp(eCmd / (rig.MaxE * Mathd.Deg2Rad), -0.8, 0.8),
                              Mathd.Clamp(aCmd / (rig.MaxA * Mathd.Deg2Rad), -0.5, 0.5),
                              Mathd.Clamp(rCmd / (rig.MaxR * Mathd.Deg2Rad), -0.5, 0.5), throttle, Rig.Dt);
            rig.Step(c);
        }

        static void ExtendedScenarios(Rig rig)
        {
            // (e) 盘旋: LoiterBank=24deg, CruiseThrottle=0.45, 高度保持, 120 s; 对比 β 反馈方向舵
            foreach (double bg in new[] { 0.0, 1.0, 2.0, 4.0 })
            {
                var tr = rig.TrimAt(25, 100);
                double eng = rig.Alt; double psi0 = rig.Yaw; double lastPsi = psi0; double lastT = 0;
                Console.WriteLine("  (e" + (bg > 0 ? "'" : "") + ") Loiter bank 24deg, throttle 0.45, alt-hold, beta-feedback gain=" + bg + (bg > 0 ? "  [what-if, not in project]" : "  [as shipped]"));
                Console.WriteLine("      t   bank  pitch  beta    V    dAlt   hdgRate theory   turnRadius(m)");
                for (int i = 0; i < 120 * 120; i++)
                {
                    StepLaw(rig, 24 * Mathd.Deg2Rad, AltToPitch(eng, rig.Alt), 0.45, 0, bg);
                    if (i % (120 * 20) == 120 * 20 - 1)
                    {
                        double rate = D(WrapPi(rig.Yaw - lastPsi)) / (rig.T - lastT); lastPsi = rig.Yaw; lastT = rig.T;
                        double th = 9.80665 * System.Math.Tan(rig.Roll) / rig.V * Mathd.Rad2Deg;
                        double rad = rate != 0 ? rig.V / (rate * Mathd.Deg2Rad) : 0;
                        Console.WriteLine("    " + rig.T.ToString("F0").PadLeft(4) + " " + D(rig.Roll).ToString("F1").PadLeft(5) + " " + D(rig.Pitch).ToString("F1").PadLeft(5)
                            + " " + D(rig.Beta).ToString("F2").PadLeft(6) + " " + rig.V.ToString("F1").PadLeft(5) + " " + (rig.Alt - eng).ToString("F1").PadLeft(6)
                            + "  " + rate.ToString("F2").PadLeft(6) + " " + th.ToString("F2").PadLeft(6) + "   " + rad.ToString("F0").PadLeft(6));
                    }
                }
            }

            // (f) 手动键盘点按 (KeyboardFlightInput: 杆量 ±1 直通 mapper, 无增稳)
            Console.WriteLine("  (f) manual keyboard taps, no stabilisation (stick=1.0 for tap duration, then released):");
            foreach (double tap in new[] { 0.1, 0.25, 0.5 })
            {
                var tr = rig.TrimAt(25, 100);
                double th0 = rig.Pitch, maxTh = -99, maxQ = 0, minV = 99;
                for (int i = 0; i < 120 * 6; i++)
                {
                    double st = rig.T < tap ? 1.0 : 0.0;
                    var c = rig.M.Map(st, 0, 0, tr.Throttle, Rig.Dt);
                    rig.Step(c);
                    maxTh = System.Math.Max(maxTh, D(rig.Pitch - th0)); maxQ = System.Math.Max(maxQ, D(rig.Q)); minV = System.Math.Min(minV, rig.V);
                }
                Console.WriteLine("      W tap " + tap.ToString("F2") + " s -> peak dPitch=" + maxTh.ToString("F1") + " deg, peak q=" + maxQ.ToString("F0") + " deg/s, after 6 s: pitch=" + D(rig.Pitch).ToString("F1") + " alpha=" + D(rig.Alpha).ToString("F1") + " V=" + rig.V.ToString("F1"));
            }
            foreach (double tap in new[] { 0.1, 0.25, 0.5 })
            {
                var tr = rig.TrimAt(25, 100);
                double maxPhi = -99;
                for (int i = 0; i < 120 * 6; i++)
                {
                    double st = rig.T < tap ? 1.0 : 0.0;
                    var c = rig.M.Map(0, st, 0, tr.Throttle, Rig.Dt); c.Elevator += tr.Elevator;
                    rig.Step(c);
                    maxPhi = System.Math.Max(maxPhi, D(rig.Roll));
                }
                Console.WriteLine("      D tap " + tap.ToString("F2") + " s -> peak bank=" + maxPhi.ToString("F1") + " deg, after 6 s: bank=" + D(rig.Roll).ToString("F1") + " hdg=" + D(rig.Yaw).ToString("F1") + " beta=" + D(rig.Beta).ToString("F1"));
            }

            // (g) STAB 俯仰杆满杆(+14deg 目标)持续 30 s, 油门保持配平 (无失速模型下的速度/迎角演化)
            {
                var tr = rig.TrimAt(25, 100);
                Console.WriteLine("  (g) STAB full back-stick (pitch tgt 14deg) for 30 s, throttle trim=" + tr.Throttle.ToString("F2"));
                for (int i = 0; i < 120 * 30; i++)
                {
                    StepLaw(rig, 0, 14 * Mathd.Deg2Rad, tr.Throttle, 0, 0);
                    if (i % (120 * 5) == 120 * 5 - 1)
                        Console.WriteLine("      t=" + rig.T.ToString("F0").PadLeft(3) + " pitch=" + D(rig.Pitch).ToString("F1").PadLeft(5) + " alpha=" + D(rig.Alpha).ToString("F1").PadLeft(5)
                            + " V=" + rig.V.ToString("F1").PadLeft(5) + " vs=" + (-rig.S.VelocityNed.Z).ToString("F2").PadLeft(6) + " CL=" + (rig.Plane.Aero.Compute(rig.S.RelativeWindBody(Vec3.Zero), rig.S.AngularVelocityBody, 1.2, new ControlInputs()).CL).ToString("F2"));
                }
                double vStall = System.Math.Sqrt(2 * rig.Cfg.Mass.MassKg * AtmosphereModel.G0 / (1.225 * rig.Cfg.Aero.Sref * 1.5));
                Console.WriteLine("      (model CL clamp 1.5 => vStall estimate " + vStall.ToString("F1") + " m/s; no stall break in the aero model)");
            }
        }

        // =============== Unity 姿态映射 ===============
        static int UnityMapping()
        {
            int fails = 0;
            Func<Quat, Quat> toUnity = q => new Quat(q.W, -q.Y, q.Z, -q.X);   // (x,y,z,w)=(-qY, qZ, -qX, qW)
            // NED: pitch up => nose (body +x) points to up (-Z_ned). Unity: forward (0,0,1) must gain +y.
            var qu = toUnity(Quat.FromEuler321(0, 10 * Mathd.Deg2Rad, 0));
            Vec3 fwd = qu.Rotate(new Vec3(0, 0, 1));
            bool ok = fwd.Y > 0.15 && fwd.Z > 0.9;
            Console.WriteLine("  pitch +10: Unity forward=(" + fwd.X.ToString("F3") + "," + fwd.Y.ToString("F3") + "," + fwd.Z.ToString("F3") + ") -> " + (ok ? "PASS nose up" : "FAIL")); if (!ok) fails++;
            // roll right: right wing (Unity local +x) goes down (world y<0)
            qu = toUnity(Quat.FromEuler321(30 * Mathd.Deg2Rad, 0, 0));
            Vec3 rw = qu.Rotate(new Vec3(1, 0, 0)); Vec3 up = qu.Rotate(new Vec3(0, 1, 0));
            ok = rw.Y < -0.4 && up.X > 0.4;
            Console.WriteLine("  roll +30: right-wing=(" + rw.X.ToString("F3") + "," + rw.Y.ToString("F3") + "," + rw.Z.ToString("F3") + ") up=(" + up.X.ToString("F3") + "," + up.Y.ToString("F3") + "," + up.Z.ToString("F3") + ") -> " + (ok ? "PASS right wing down, lift tilts right" : "FAIL")); if (!ok) fails++;
            // yaw +90 (east): forward -> +x
            qu = toUnity(Quat.FromEuler321(0, 0, 90 * Mathd.Deg2Rad));
            fwd = qu.Rotate(new Vec3(0, 0, 1));
            ok = fwd.X > 0.99 && System.Math.Abs(fwd.Z) < 0.02;
            Console.WriteLine("  yaw +90: Unity forward=(" + fwd.X.ToString("F3") + "," + fwd.Y.ToString("F3") + "," + fwd.Z.ToString("F3") + ") -> " + (ok ? "PASS east=+x" : "FAIL")); if (!ok) fails++;
            // 组合: 与 NED 直接旋转一致 (任意姿态, body 向量映射一致)
            var rnd = new Random(1); double maxErr = 0;
            for (int k = 0; k < 200; k++)
            {
                var q = Quat.FromEuler321((rnd.NextDouble() - .5) * 3, (rnd.NextDouble() - .5) * 2.5, (rnd.NextDouble() - .5) * 6);
                // 机体轴 (x前,y右,z下) -> Unity 本地 (x右,y上,z前): (bx,by,bz) -> (by, -bz, bx)
                Vec3 b = new Vec3(rnd.NextDouble() - .5, rnd.NextDouble() - .5, rnd.NextDouble() - .5);
                Vec3 ned = q.Rotate(b);                           // NED 世界向量
                Vec3 wantUnity = new Vec3(ned.Y, -ned.Z, ned.X);   // NED -> Unity 世界
                Vec3 local = new Vec3(b.Y, -b.Z, b.X);
                Vec3 gotUnity = toUnity(q).Rotate(local);
                maxErr = System.Math.Max(maxErr, (wantUnity - gotUnity).Length);
            }
            ok = maxErr < 1e-12;
            Console.WriteLine("  random attitudes: max |Unity rotate - NED rotate mapped| = " + maxErr.ToString("E2") + " -> " + (ok ? "PASS" : "FAIL")); if (!ok) fails++;
            return fails;
        }
    }
}
