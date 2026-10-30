using System;
using System.Threading;
using FlightDynamics;
using FlightDynamics.IO;
using FlightDynamics.Math;

namespace FlightSim.Console
{
    /// <summary>
    /// 交互式控制台飞行模拟器 —— 用 Phase 0-2 的真实物理核心 (与 Unity 层同一套代码):
    ///   120Hz 定步长 RK4 6DOF + 线性气动 + 弹簧-阻尼起落架。
    ///
    /// 键位 (与 Unity 层一致):
    ///   W/S      俯仰 (W=拉杆抬头)      A/D      滚转
    ///   Q/E      方向舵 (地面=前轮转向)  ↑/↓ 或 +/-  油门增减
    ///   B        刹车 (按住)             R/F      配平增减
    ///   X        重置到跑道起点          Esc      退出
    /// </summary>
    public static class ConsoleFlightSim
    {
        const double Dt = 1.0 / 120.0;
        const double DisplayInterval = 0.08;   // HUD 刷新 ~12Hz

        static Aircraft _plane;
        static RigidBodyIntegrator _integ;
        static Trimmer _trimmer;
        static AircraftState _s;

        static double _pitchIn, _rollIn, _yawIn;      // 杆量 -1..1
        static double _throttle, _brake, _trimDeg;
        static double _simTime;
        static bool _crashed;

        // ---- 风设置 (F2 菜单调节) ----
        static double _windSpeed;          // 稳定风 m/s
        static double _windDirDeg;         // 风向 (方位角)
        static int _turbLevel;             // 0 无 1 轻 2 中 3 强
        static bool _shearOn;
        static readonly System.Collections.Generic.List<DiscreteGust> _gusts =
            new System.Collections.Generic.List<DiscreteGust>();
        static double _hudWindMag;

        // 飞行统计
        static double _maxAlt, _maxSpd, _tdSink = -1, _tdBank, _tdPitch, _tdDist;
        static bool _airborneOnce, _landed;

        public static void Main(string[] args)
        {
            try { System.Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            bool headless = args.Length >= 2 && args[0] == "--headless";
            string cfgPath = (!headless && args.Length > 0) ? args[0] : "AircraftConfig.json";
            if (!System.IO.File.Exists(cfgPath))
            {
                System.Console.WriteLine("找不到 " + cfgPath + " (在本 exe 同目录放 AircraftConfig.json)");
                return;
            }

            var cfg = AircraftConfigJson.Load(cfgPath);
            _plane = new Aircraft(cfg.Mass, new AeroModel(cfg.Aero), cfg.Engine, new CalmWind())
            { Gear = cfg.Gear };
            _integ = new RigidBodyIntegrator(_plane);
            _trimmer = new Trimmer(_plane);

            RebuildWind();

            // --headless N: 自动演示 N 秒 (满油门起飞 -> 爬升), 无渲染无键盘
            if (headless)
            {
                RunHeadless(double.Parse(args[1]));
                return;
            }

            Reset();
            System.Console.CursorVisible = false;
            System.Console.Clear();
            System.Console.WriteLine("正在启动 TestUAV 控制台飞行模拟器 (物理核心 120Hz)...");
            Thread.Sleep(600);
            System.Console.Clear();

            double lastDisplay = -1;
            var controls = ControlInputs.Zero;

            while (true)
            {
                // ---- 输入 ----
                while (System.Console.KeyAvailable)
                {
                    var key = System.Console.ReadKey(true);
                    switch (key.Key)
                    {
                        // 增量杆量: 连按累积, 松手自动缓慢回中 (下方回中律)
                        case ConsoleKey.W: _pitchIn = Math.Min(1.2, _pitchIn + 0.18); break;
                        case ConsoleKey.S: _pitchIn = Math.Max(-1.2, _pitchIn - 0.18); break;
                        case ConsoleKey.A: _rollIn = Math.Max(-1.2, _rollIn - 0.22); break;
                        case ConsoleKey.D: _rollIn = Math.Min(1.2, _rollIn + 0.22); break;
                        case ConsoleKey.Q: _yawIn = Math.Max(-1, _yawIn - 0.25); break;
                        case ConsoleKey.E: _yawIn = Math.Min(1, _yawIn + 0.25); break;
                        case ConsoleKey.UpArrow: _throttle = Math.Min(1, _throttle + 0.06); break;
                        case ConsoleKey.DownArrow: _throttle = Math.Max(0, _throttle - 0.06); break;
                        case ConsoleKey.R: _trimDeg += 0.5; break;
                        case ConsoleKey.F: _trimDeg -= 0.5; break;
                        case ConsoleKey.B: _brake = 1; break;
                        case ConsoleKey.X: Reset(); break;
                        case ConsoleKey.F2: WindSettingsMenu(); break;
                        case ConsoleKey.Escape: Cleanup(); return;
                    }
                }

                // ---- 杆量自动回中 (模拟松杆, 速率 0.8/s; 油门保持) ----
                double center = 0.8 * Dt;
                if (_pitchIn > 0) _pitchIn = Math.Max(0, _pitchIn - center);
                else _pitchIn = Math.Min(0, _pitchIn + center);
                if (_rollIn > 0) _rollIn = Math.Max(0, _rollIn - 1.2 * Dt);
                else _rollIn = Math.Min(0, _rollIn + 1.2 * Dt);
                if (_yawIn > 0) _yawIn = Math.Max(0, _yawIn - 1.5 * Dt);
                else _yawIn = Math.Min(0, _yawIn + 1.5 * Dt);
                _brake = Math.Max(0, _brake - 4 * Dt);   // 刹车也需按住 (连按维持)

                // ---- 控制律: 杆量 → 舵面 (最大偏角 + 40ms 舵机滞后) ----
                double maxE = 25 * Mathd.Deg2Rad, maxA = 20 * Mathd.Deg2Rad, maxR = 25 * Mathd.Deg2Rad;
                double trimE = -(_trimDeg / 25.0) * maxE;      // 配平 (拉杆为正 = 负偏角)
                double eCmd = Mathd.Clamp(-( _pitchIn) * maxE + trimE, -maxE, maxE);
                double aCmd = Mathd.Clamp(_rollIn * maxA, -maxA, maxA);
                double rCmd = Mathd.Clamp(_yawIn * maxR, -maxR, maxR);
                double k = Dt / 0.04;
                controls.Elevator += (eCmd - controls.Elevator) * k;
                controls.Aileron += (aCmd - controls.Aileron) * k;
                controls.Rudder += (rCmd - controls.Rudder) * k;
                controls.Throttle = _throttle;
                controls.Brake = _brake;

                // ---- 物理步进 ----
                _integ.Step(_s, controls, _simTime, Dt);
                _simTime += Dt;

                // ---- 统计与坠毁判定 ----
                double alt = _s.Altitude, spd = _s.Airspeed(Vec3.Zero);
                double[] e = _s.Euler321();
                _maxAlt = Math.Max(_maxAlt, alt);
                _maxSpd = Math.Max(_maxSpd, spd);
                bool onGround = _plane.LastGroundContact.AnyContact;
                if (alt > 2.0) _airborneOnce = true;
                if (_airborneOnce && onGround && alt < 0.45 && _tdSink < 0)
                {
                    _tdSink = _s.VelocityNed.Z; _tdBank = e[0]; _tdPitch = e[1];
                    _tdDist = _s.Position.X;
                    if (_tdSink > 4.0 || Math.Abs(e[0]) > 45 * Mathd.Deg2Rad) _crashed = true;
                    _landed = true;
                }
                if (!_plane.LastGroundContact.AnyContact && alt > 1.0) { _tdSink = -1; _landed = false; }

                // ---- 显示节流 ----
                if (_simTime - lastDisplay >= DisplayInterval)
                {
                    lastDisplay = _simTime;
                    Render(e, alt, spd, onGround, controls);
                }
                // 实时对齐 (~120Hz)
                Thread.Sleep(4);
            }
        }

        static void Reset()
        {
            _s = new AircraftState();
            _s.Position = new Vec3(0, 0, -0.327);
            _s.Attitude = Quat.Identity;
            _simTime = 0; _throttle = 0; _trimDeg = 0; _crashed = false;
            _gusts.Clear(); RebuildWind();
            _maxAlt = 0; _maxSpd = 0; _tdSink = -1; _airborneOnce = false; _landed = false;
        }

        static void Cleanup()
        {
            System.Console.CursorVisible = true;
            System.Console.SetCursorPosition(0, 20);
            System.Console.WriteLine("再见!");
        }

        static void Render(double[] e, double alt, double spd, bool onGround, ControlInputs c)
        {
            double vz = _s.VelocityNed.Z;
            _hudWindMag = _plane.Wind.WindAt(_s.Position, _simTime).Length;
            string Action;
            if (_crashed) Action = "*** 坠毁! 按 X 重置 ***";
            else if (_landed && spd < 0.5) Action = "*** 着陆成功! 已停稳. 按 X 再来一次 ***";
            else if (onGround) Action = _airborneOnce ? "滑跑 (已着陆)" : "地面滑跑: 按住 ↑ 加满油门, >15m/s 按 W 拉杆";
            else Action = "空中";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("==========================================================================");
            sb.AppendLine("  TestUAV 控制台飞行模拟器   [OpenVSP/VSPAERO 数据 + 6DOF RK4 物理核心]  ");
            sb.AppendLine("==========================================================================");
            sb.AppendLine(string.Format(
                " 空速 {0,6:F1} m/s   高度 {1,7:F1} m   垂速 {2,6:F2} m/s   油门 {3,3:F0}%",
                spd, alt, vz, _throttle * 100));
            sb.AppendLine(string.Format(
                " 俯仰 {0,6:F1}°   滚转 {1,6:F1}°   航向 {2,6:F1}°   {3}",
                e[1] * Mathd.Rad2Deg, e[0] * Mathd.Rad2Deg, ((e[2] * Mathd.Rad2Deg) % 360 + 360) % 360,
                onGround ? "[地面]" : "[空中]"));
            sb.AppendLine(string.Format(
                " 位置: 北 {0,7:F0} m   东 {1,6:F0} m      舵面: 升降 {2,5:F1}°  副翼 {3,5:F1}°  方向 {4,5:F1}°",
                _s.Position.X, _s.Position.Y,
                c.Elevator * Mathd.Rad2Deg, c.Aileron * Mathd.Rad2Deg, c.Rudder * Mathd.Rad2Deg));
            sb.AppendLine(string.Format(" 配平 {0,5:F1}°   刹车 {1}   时间 {2,5:F0} s   最高 {3:F0}m / {4:F0}m/s",
                _trimDeg, _brake > 0.5 ? "开" : "关", _simTime, _maxAlt, _maxSpd));
            sb.AppendLine("--------------------------------------------------------------------------");
            sb.Append(AttitudeIndicator(e[0], e[1]));
            sb.AppendLine("--------------------------------------------------------------------------");
            if (_airborneOnce && _landed && _tdSink > 0)
                sb.AppendLine(string.Format(
                    " 着陆统计: 下沉 {0:F2} m/s   接地滚转 {1:F1}°   接地俯仰 {2:F1}°   距起飞点 {3:F0} m",
                    _tdSink, _tdBank * Mathd.Rad2Deg, _tdPitch * Mathd.Rad2Deg, _tdDist));
            sb.AppendLine(" " + Action);
            sb.AppendLine(string.Format(
                " 风: 稳定 {0:F0} m/s @ {1:F0}°  紊流 {2}  切变 {3}  实测 {4:F1} m/s   [F2 风设置]",
                _windSpeed, _windDirDeg,
                new[] { "无", "轻", "中", "强" }[_turbLevel], _shearOn ? "开" : "关", _hudWindMag));
            sb.AppendLine(" W/S俯仰 A/D滚转 Q/E方向舵 ↑↓油门 B刹车 R/F配平 X重置 F2风设置 Esc退出");

            System.Console.SetCursorPosition(0, 0);
            System.Console.Write(sb.ToString());
        }

        /// <summary>自动演示: 满油门滑跑, 15m/s 拉杆, 爬升到目标高度改平。</summary>
        static void RunHeadless(double seconds)
        {
            Reset();
            _throttle = 1.0;
            var c = ControlInputs.Zero;
            double maxE = 25 * Mathd.Deg2Rad;
            bool climbed = false;
            for (double t = 0; t < seconds; t += Dt)
            {
                double spd = _s.Airspeed(Vec3.Zero);
                double alt = _s.Altitude;
                double pitch = _s.Euler321()[1];
                double p = _s.AngularVelocityBody.X;

                double pitchTarget = 0;
                if (alt > 30) { climbed = true; }
                if (climbed) pitchTarget = 2.0 * Mathd.Deg2Rad;        // 改平巡航
                else if (spd > 15) pitchTarget = 9.0 * Mathd.Deg2Rad;   // 起飞爬升

                double eCmd = -0.8 * (pitchTarget - pitch) - 0.15 * _s.AngularVelocityBody.Y;
                c.Elevator += (Mathd.Clamp(eCmd, -maxE, maxE) - c.Elevator) * Dt / 0.04;
                c.Aileron += ((0.6 * _s.Euler321()[0] + 0.15 * p) - c.Aileron) * Dt / 0.04;
                c.Throttle = 1.0;
                if (climbed) c.Throttle = 0.25;

                _integ.Step(_s, c, t, Dt);
                if (double.IsNaN(_s.Position.X)) { System.Console.WriteLine("NaN! t=" + t); return; }
            }
            double[] e = _s.Euler321();
            System.Console.WriteLine("=== 自动演示 " + seconds + "s ===");
            System.Console.WriteLine(string.Format(
                "高度 {0:F1} m   空速 {1:F1} m/s   俯仰 {2:F1}°   滚转 {3:F2}°   北移 {4:F0} m",
                _s.Altitude, _s.Airspeed(Vec3.Zero), e[1] * Mathd.Rad2Deg, e[0] * Mathd.Rad2Deg, _s.Position.X));
            System.Console.WriteLine(climbed ? "状态: 已爬升并改平巡航 (物理核心工作正常)" : "状态: 起飞爬升中");
        }

        /// <summary>重建组合风场 (稳定风 + 切变 + Dryden + 阵风列表)。</summary>
        static void RebuildWind()
        {
            double dirRad = _windDirDeg * Mathd.Deg2Rad;
            // 风矢量 = 气团移动方向 = 风向方位的反方向
            Vec3 baseWind = new Vec3(-System.Math.Cos(dirRad), -System.Math.Sin(dirRad), 0) * _windSpeed;
            IWindModel steady = _shearOn
                ? (IWindModel)new PowerLawShearWind(baseWind, 10.0, 0.2)
                : new ConstantWind(baseWind);

            var composite = new CompositeWind().Add(steady);
            if (_turbLevel > 0)
            {
                composite.Add(new DrydenTurbulence(20.0,
                    (DrydenTurbulence.TurbulenceLevel)(_turbLevel - 1), seed: 99,
                    headingProvider: () => _s != null ? _s.Euler321()[2] : 0.0));
            }
            foreach (var g in _gusts) composite.Add(g);
            _plane.Wind = composite;
        }

        /// <summary>F2 风设置菜单 (物理暂停, 按数字键调节)。</summary>
        static void WindSettingsMenu()
        {
            while (true)
            {
                System.Console.SetCursorPosition(0, 21);
                System.Console.WriteLine("---- 风设置 (按数字键调节) ------------------------------");
                System.Console.WriteLine(string.Format(
                    " 1 风速+2m/s [{0:F0}]   2 风向+30° [{1:F0}°]   3 紊流 [{2}]",
                    _windSpeed, _windDirDeg, new[] { "无", "轻", "中", "强" }[_turbLevel]));
                System.Console.WriteLine(string.Format(
                    " 4 切变 [{0}]           5 侧风阵风(8m/s)      6 顶风阵风(8m/s)",
                    _shearOn ? "开" : "关"));
                System.Console.WriteLine(" 7 清空阵风             F2/Esc 返回飞行");
                var key = System.Console.ReadKey(true).Key;
                switch (key)
                {
                    case ConsoleKey.D1: _windSpeed = System.Math.Min(25, _windSpeed + 2); RebuildWind(); break;
                    case ConsoleKey.D2: _windDirDeg = (_windDirDeg + 30) % 360; RebuildWind(); break;
                    case ConsoleKey.D3: _turbLevel = (_turbLevel + 1) % 4; RebuildWind(); break;
                    case ConsoleKey.D4: _shearOn = !_shearOn; RebuildWind(); break;
                    case ConsoleKey.D5:
                        _gusts.Add(new DiscreteGust(new Vec3(0, 1, 0), 8.0, _simTime + 0.5, 3.0));
                        RebuildWind(); return;
                    case ConsoleKey.D6:
                        _gusts.Add(new DiscreteGust(new Vec3(1, 0, 0), 8.0, _simTime + 0.5, 3.0));
                        RebuildWind(); return;
                    case ConsoleKey.D7: _gusts.Clear(); RebuildWind(); break;
                    case ConsoleKey.F2:
                    case ConsoleKey.Escape: return;
                }
            }
        }

        /// <summary>ASCII 姿态仪: 地平线随滚转/俯仰移动。</summary>
        static string AttitudeIndicator(double roll, double pitch)
        {
            var sb = new System.Text.StringBuilder(" ");
            for (int i = 0; i < 4; i++)
            {
                int width = 68;
                // 每行代表俯仰刻度: 中心行 0°, 上下 ±5°/行
                double linePitch = (2 - i) * 5.0 * Mathd.Deg2Rad;   // i=0: +10°, 2: 0°, 3: -5°
                double shift = Math.Tan(roll) * (pitch - linePitch) * 12.0; // 滚转+俯仰 → 地平线位移
                int center = width / 2 + (int)Math.Round(Mathd.Clamp(shift, -30, 30));
                for (int x = 0; x < width; x++)
                {
                    double colAngle = (x - width / 2) * 1.2 * Mathd.Deg2Rad; // 左右展开角
                    bool sky = Math.Tan(roll) * Math.Tan(colAngle) + (pitch - linePitch) * 8.0 > 0;
                    if (Math.Abs(x - center) < 2 && Math.Abs(colAngle) < 0.35) sb.Append(i == 2 ? '═' : '─');
                    else sb.Append(sky ? ' ' : '▒');
                }
                sb.AppendLine();
                sb.Append(" ");
            }
            sb.AppendLine("                        [ 机身 ▲ 朝上 ]");
            return sb.ToString();
        }
    }
}
