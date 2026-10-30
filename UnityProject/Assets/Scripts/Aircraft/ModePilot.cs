using FlightDynamics;
using FlightDynamics.Math;
using UnityEngine;

namespace FlightSim
{
    public enum FlightMode { Manual, Rtl, Land, Loiter, Stab }

    /// <summary>
    /// 飞行模式自驾 (返航/降落/盘旋/自稳): 地面站式一键模式切换。
    /// 通过 AircraftController.OverrideInput 接管当前输入 (键盘/演示自驾),
    /// 解除后交还原输入。控制律统一走共享 ControlLaws (与 AutoPilotInput/
    /// FlightGuard 同一套已验证符号约定)。
    /// 模式细节:
    ///   RTL  返航: 转向起飞点飞回 (高度保持 max(接管高度,60m)), 距家 <100m 转为
    ///              到家盘旋 (22° 坡度, 半径 ~100m)。
    ///   LAND 降落: 有跑道 (场景含 RunwayEnvironment): 导航至入口上空 60m →
    ///              沿 3.5° 下滑道对准跑道 → 3m 拉平 → 接地刹车停住;
    ///              无跑道 (山地等): 以 1.5 m/s 盘旋下降到地面 (平缓接地不触发坠毁)。
    ///   LOITER 盘旋: 以 22° 坡度绕接管点 ~100m 半径平飞。
    ///   STAB  自稳: 机翼回平 + 航向软保持 (±10°) + 高度保持, 油门保持接管值。
    /// HUD 按钮/数字键 1-4 切换; 再按同一键或 0 解除; Backspace 复位自动解除。
    /// </summary>
    public class ModePilot : MonoBehaviour, IFlightInput
    {
        public FlightMode Mode { get; private set; } = FlightMode.Manual;   // Manual = 未接管
        public string Phase { get; private set; } = "";                     // HUD 副标题

        [Header("模式参数")]
        public float MaxBankDeg = 30f;    // 航向机动坡度上限 (本机转弯率有限, 取大些)
        public float LoiterBankDeg = 24f;
        public float AltHoldMinM = 60f;        // RTL 最低保持高度
        public float RtlArriveRadiusM = 100f;
        public float LandGlideslopeDeg = 3.5f;
        public float LandNavAltM = 60f;
        public float LandDescentMps = 1.5f;    // 无跑道盘旋下降率
        [Tooltip("读跑道几何的降落: 五边目标空速 m/s。低于它才补油, 高于则怠速 (滑翔机型零油门自然进近速度偏高, 不能再加油)")]
        public float LandApproachSpeedMps = 20f;
        public float CruiseThrottle = 0.45f;

        private AircraftController _c;
        private bool _rtlArrived;
        private int _landPhase;                // 0 导航 1 五边 2 拉平 3 滑跑
        private float _engageAlt, _engageThrottle, _engageTime;
        private Vector2 _loiterCenter;         // NED (北, 东)
        private GameObject _runwayGo;          // Engage 时缓存 (原先每帧 GameObject.Find)
        private RunwayGeometry _runwayGeom;    // 有 RunwayEnvironment 组件时的真实几何; 否则 null → 旧版硬编码数值

        private void Awake() => _c = GetComponent<AircraftController>();

        // ================= 模式切换 =================

        /// <summary>切换模式 (再次按同一模式 = 解除回手动)。</summary>
        public void Engage(FlightMode m)
        {
            if (m == FlightMode.Manual || Mode == m) { Disengage(); return; }
            Mode = m;
            _rtlArrived = false;
            _landPhase = 0;
            _engageAlt = Mathf.Max((float)_c.State.Altitude, 5f);
            _engageThrottle = Mathf.Clamp(_c.Throttle, 0.15f, 0.7f);
            _engageTime = Time.time;
            _loiterCenter = Home();
            _stickThrottle = Mathf.Clamp((float)_c.Throttle, 0f, 1f);   // U13: STAB 油门从当前值起手动累积
            _stabRollWasActive = _stabPitchWasActive = false;
            _runwayGo = GameObject.Find("RunwayEnvironment");
            var runwayEnv = _runwayGo != null ? _runwayGo.GetComponent<RunwayEnvironment>() : null;
            _runwayGeom = runwayEnv != null ? runwayEnv.Geometry : null;
            if (m == FlightMode.Stab) CaptureStabHeading();
            _c.OverrideInput = this;
            Debug.Log("[ModePilot] 接管 → " + ModeName(m));
        }

        public void Disengage()
        {
            if (Mode == FlightMode.Manual) return;
            Debug.Log("[ModePilot] 解除 " + ModeName(Mode) + " → 手动");
            Mode = FlightMode.Manual;
            Phase = "";
            if (_c.OverrideInput == this) _c.OverrideInput = null;
        }

        public void ResetState() => Disengage();

        public static string ModeName(FlightMode m) => m switch
        {
            FlightMode.Rtl => "返航",
            FlightMode.Land => "降落",
            FlightMode.Loiter => "盘旋",
            FlightMode.Stab => "自稳",
            _ => "手动",
        };

        // ================= 主控制回路 (仅接管时被轮询) =================

        public ControlInputs Poll()
        {
            var s = _c.State;
            double[] e = s.Euler321();
            float pitch = (float)e[1], bank = (float)e[0], heading = (float)e[2];
            float alt = (float)s.Altitude;
            float p = (float)s.AngularVelocityBody.X, q = (float)s.AngularVelocityBody.Y;
            float r = (float)s.AngularVelocityBody.Z;

            float desiredBank = 0f, pitchTarget = 2f * Mathf.Deg2Rad,
                  throttle = CruiseThrottle, yawStick = 0f, brake = 0f;

            switch (Mode)
            {
                case FlightMode.Rtl:
                {
                    float dist = HorizDist(s, _loiterCenter);
                    if (!_rtlArrived)
                    {
                        Phase = "飞向起飞点";
                        desiredBank = ControlLaws.BankFromHeadingErr(heading, BearingTo(s, _loiterCenter), MaxBankDeg);
                        if (dist < RtlArriveRadiusM) { _rtlArrived = true; Debug.Log("[ModePilot] RTL 到达, 转到家盘旋"); }
                    }
                    if (_rtlArrived) { Phase = "到家盘旋"; desiredBank = LoiterBankDeg * Mathf.Deg2Rad; }
                    pitchTarget = AltToPitch(ClearAlt(Mathf.Max(_engageAlt, AltHoldMinM), TerrainMaxAround(s, heading), AltHoldMinM), alt);
                    break;
                }

                case FlightMode.Land:
                {
                    bool hasRunway = _runwayGo != null;
                    if (!hasRunway)
                    {
                        // 通用: 盘旋下降
                        Phase = "盘旋下降";
                        desiredBank = LoiterBankDeg * Mathf.Deg2Rad;
                        float target = _engageAlt - LandDescentMps * (Time.time - _engageTime);
                        pitchTarget = AltToPitch(Mathf.Max(target, 0.5f + GroundHere(s)), alt);
                        throttle = 0f;
                        break;
                    }

                    if (_runwayGeom != null)
                    {
                        LandOnRunway(s, heading, alt, ref desiredBank, ref pitchTarget,
                                     ref throttle, ref yawStick, ref brake);
                        break;
                    }

                    // 跑道型 (无几何数据的旧路径, 测试里的空 RunwayEnvironment GO 走这里,
                    // 数值保持原样): 入口 (跑道起点南侧 80m 上空) → 五边 → 拉平 → 滑跑
                    Vector2 threshold = Home() + new Vector2(-80f, 0f);   // NED 北向 -80
                    float distTh = HorizDist(s, threshold);
                    if (_landPhase == 0)
                    {
                        Phase = "导航至入口";
                        desiredBank = ControlLaws.BankFromHeadingErr(heading, BearingTo(s, threshold), MaxBankDeg);
                        pitchTarget = AltToPitch(ClearAlt(Mathf.Max(_engageAlt, LandNavAltM), TerrainMaxAround(s, heading), 40f), alt);
                        if (distTh < 70f && Mathf.Abs(WrapDeg(heading * Mathf.Rad2Deg)) < 20f)
                        { _landPhase = 1; Debug.Log("[ModePilot] LAND 五边开始"); }
                    }
                    else if (_landPhase == 1)
                    {
                        Phase = "五边下滑";
                        // 侧向: 跑道朝北假设下, 横向偏差 = 相对起飞点的东向偏移
                        // (起飞点不一定在原点, 用绝对坐标会读出系统性假横偏)
                        Vector2 home = Home();
                        float cross = (float)(s.Position.Y - home.y);
                        desiredBank = Mathf.Clamp(-cross * 0.15f, -10f, 10f) * Mathf.Deg2Rad
                                    + ControlLaws.BankFromHeadingErr(heading, 0f, 12f);
                        desiredBank = Mathf.Clamp(desiredBank, -MaxBankDeg * Mathf.Deg2Rad, MaxBankDeg * Mathf.Deg2Rad);
                        // 接地点在起飞点北 40m (同样相对 Home 计算)
                        float distTdn = Mathf.Max((float)(s.Position.X - home.x) - 40f, 0f);
                        float glideAlt = 3f + Mathf.Tan(LandGlideslopeDeg * Mathf.Deg2Rad) * distTdn;
                        pitchTarget = AltToPitch(Mathf.Min(glideAlt, LandNavAltM), alt);
                        throttle = 0f;
                        if (alt < 3f) { _landPhase = 2; Debug.Log("[ModePilot] LAND 拉平"); }
                    }
                    else if (_landPhase == 2)
                    {
                        Phase = "拉平";
                        pitchTarget = 6f * Mathf.Deg2Rad;
                        desiredBank = ControlLaws.BankFromHeadingErr(heading, 0f, 8f);
                        throttle = 0f;
                        if (_c.OnGround) { _landPhase = 3; Debug.Log("[ModePilot] LAND 接地, 滑跑刹车"); }
                    }
                    else
                    {
                        Phase = "滑跑";
                        pitchTarget = 0f;
                        yawStick = Mathf.Clamp(HeadingErr(heading, 0f) * 1.2f, -0.5f, 0.5f);
                        brake = 1f;
                    }
                    break;
                }

                case FlightMode.Loiter:
                {
                    Phase = "盘旋";
                    desiredBank = LoiterBankDeg * Mathf.Deg2Rad;
                    pitchTarget = AltToPitch(ClearAlt(_engageAlt, TerrainMaxAround(s, heading), 30f), alt);
                    break;
                }

                case FlightMode.Stab:
                {
                    // U13: 自稳=姿态增稳 (标准增稳语义), 杆量穿透 — 之前是全接管
                    // (航向+高度+油门锁死, 杆量被忽略), 用户反馈"点了增稳就无法操控"。
                    // 现在: 横滚杆 ±1 → ±30° 坡度; 俯仰杆 ±1 → 2°基线 ±12°; 杆回中
                    // (死区 0.08) → 航向/高度保持; 油门保持手动; 方向舵脚蹬照旧。
                    Phase = "自稳";
                    var stick = ReadStabStick(Time.fixedDeltaTime);
                    _stabFeedStick = new Vector2(stick.x, stick.y);   // U15 前馈
                    bool rollActive = Mathf.Abs(stick.x) > 0.08f;
                    bool pitchActive = Mathf.Abs(stick.y) > 0.08f;
                    if (rollActive)
                        desiredBank = ControlLaws.StabStickToAttitude(stick.x, 0f).x;
                    else
                    {
                        if (_stabRollWasActive) _stabHeading = heading;   // 松杆瞬间改捕当前航向
                        desiredBank = ControlLaws.BankFromHeadingErr(heading, _stabHeading, 10f);
                    }
                    _stabRollWasActive = rollActive;
                    if (pitchActive)
                        pitchTarget = ControlLaws.StabStickToAttitude(0f, stick.y).y;
                    else
                    {
                        if (_stabPitchWasActive) _engageAlt = Mathf.Max(alt, 30f);   // 松杆保持当前高度
                        pitchTarget = AltToPitch(ClearAlt(Mathf.Max(_engageAlt, 30f), TerrainMaxAround(s, heading), 30f), alt);
                    }
                    _stabPitchWasActive = pitchActive;
                    throttle = _stickThrottle;
                    yawStick = Mathf.Clamp(stick.z, -0.5f, 0.5f);   // 脚蹬穿透 (U13)
                    break;
                }
            }

            // 控制律输出正力矩方向的等效舵量; 归一化后由 mapper 统一转换舵偏符号。
            // U15: STAB 用「增稳+杆量前馈」混合 (满杆可达 ~90% 舵量, 原先误差
            // 包线只能给 ~45%), 且钳制放宽到 ±0.9/±0.7; 其余自驾模式保持原值。
            double eCmd, aCmd;
            double eClamp = 0.8, aClamp = 0.5;
            var surf = _c.Surfaces;
            if (Mode == FlightMode.Stab)
            {
                eCmd = ControlLaws.StabElevator(pitchTarget, pitch, q, _stabFeedStick.y, surf.MaxElevatorRad);
                aCmd = ControlLaws.StabAileron(bank, desiredBank, p, _stabFeedStick.x, surf.MaxAileronRad);
                eClamp = 0.9; aClamp = 0.7;
            }
            else
            {
                eCmd = ControlLaws.PitchStabilize(pitchTarget, pitch, q);
                aCmd = ControlLaws.RollStabilize(bank, desiredBank, p);
            }
            double rCmd = ControlLaws.YawDamp(r, yawStick);   // 偏航阻尼 + 地面航向保持
            return surf.Map(
                Mathd.Clamp(eCmd / surf.MaxElevatorRad, -eClamp, eClamp),
                Mathd.Clamp(aCmd / surf.MaxAileronRad, -aClamp, aClamp),
                Mathd.Clamp(rCmd / surf.MaxRudderRad, -0.5, 0.5),
                throttle, brake, Time.fixedDeltaTime);
        }

        private float _stabHeading;
        private bool _stabRollWasActive, _stabPitchWasActive;
        private float _stickThrottle;   // STAB 手动油门累积 (接管时从当前油门起算)
        private Vector2 _stabFeedStick;   // U15: STAB 杆量前馈 (x=横滚 y=俯仰)

        /// <summary>STAB 杆量读取 (U13): 与 KeyboardFlightInput 同键位 (W/S/A/D·方向键
        /// + Shift/Ctrl 油门 + Q/E 脚蹬), 返回原始杆量 (x=横滚 y=俯仰 z=方向, ±1);
        /// 工坊打开期间隔离 (R13 同款)。油门在内部累积 (0..1)。</summary>
        private Vector3 ReadStabStick(float dt)
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (WorkshopUI.IsOpen || kb == null) return Vector3.zero;
            float roll = 0f, pitch = 0f, yaw = 0f;
            // 俯仰与手动输入同为遥控器 (Mode 2) 习惯: S/↓ 拉杆抬头, W/↑ 低头
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) pitch += 1f;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) pitch -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) roll += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) roll -= 1f;
            if (kb.eKey.isPressed) yaw += 1f;   // E=右舵 (与 KeyboardFlightInput 同号)
            if (kb.qKey.isPressed) yaw -= 1f;
            if (kb.leftShiftKey.isPressed) _stickThrottle += 0.5f * dt;   // 与 KeyboardFlightInput.ThrottleRate 一致
            if (kb.leftCtrlKey.isPressed) _stickThrottle -= 0.5f * dt;
            _stickThrottle = Mathf.Clamp01(_stickThrottle);
            return new Vector3(roll, pitch, yaw);
        }

        // ================= 有跑道几何的降落 (S1) =================

        /// <summary>
        /// 读取 RunwayGeometry 的降落: 导航至进近定位点 (瞄准点前 D 米中线) → 五边沿
        /// 下滑道对准 (横偏按跑道中线、高度相对跑道标高) → 3m 拉平 → 接地刹车滚跑。
        /// 阶段名与旧路径一致 (HUD/测试依赖)。仅在 _runwayGeom 非空时调用。
        /// </summary>
        private void LandOnRunway(AircraftState s, float heading, float alt,
            ref float desiredBank, ref float pitchTarget, ref float throttle,
            ref float yawStick, ref float brake)
        {
            var g = _runwayGeom;
            float gs = LandGlideslopeDeg * Mathf.Deg2Rad;
            const float flareAgl = 3f;
            Vector2 pos = new Vector2((float)s.Position.X, (float)s.Position.Y);
            g.AlongCross(pos, out float along, out float cross);
            float agl = alt - g.ElevationM;
            float toGo = g.TouchdownDistM - along;                       // 距瞄准点的沿跑道剩余距离
            float fixDist = Mathf.Clamp((LandNavAltM - flareAgl) / Mathf.Tan(gs), 300f, 1100f);
            Vector2 fix = g.PointAlong(g.TouchdownDistM - fixDist);      // 进近定位点
            float hdgErrRad = HeadingErr(heading, g.HeadingRad);

            if (_landPhase == 0)
            {
                Phase = "导航至入口";
                desiredBank = ControlLaws.BankFromHeadingErr(heading, BearingTo(s, fix), MaxBankDeg);
                pitchTarget = AltToPitch(ClearAlt(Mathf.Max(_engageAlt, LandNavAltM + g.ElevationM), TerrainMaxAround(s, heading), 40f), alt);
                bool nearFix = HorizDist(s, fix) < 90f;
                bool pastFixAligned = toGo < fixDist && Mathf.Abs(cross) < 60f;
                if ((nearFix || pastFixAligned) && Mathf.Abs(hdgErrRad) < 25f * Mathf.Deg2Rad)
                { _landPhase = 1; Debug.Log("[ModePilot] LAND 五边开始 (读跑道几何)"); }
            }
            else if (_landPhase == 1)
            {
                Phase = "五边下滑";
                desiredBank = Mathf.Clamp(-cross * 0.15f, -10f, 10f) * Mathf.Deg2Rad
                            + ControlLaws.BankFromHeadingErr(heading, g.HeadingRad, 12f);
                desiredBank = Mathf.Clamp(desiredBank, -MaxBankDeg * Mathf.Deg2Rad, MaxBankDeg * Mathf.Deg2Rad);
                // 下滑道: 瞄准点处高 flareAgl, 向远处按下滑角抬升; 起始段不高于 LandNavAltM
                float glideAgl = Mathf.Min(flareAgl + Mathf.Tan(gs) * Mathf.Max(toGo, 0f), LandNavAltM);
                float err = Mathf.Clamp(g.ElevationM + glideAgl - alt, -40f, 40f);   // 正 = 在下滑道下方
                // 俯仰管下滑道 (基线 - 下滑角 + 高度误差比例); 油门只管速度:
                // 低于目标进近速度才补油, 否则怠速 —— 低阻机型加油门只会越飘越快
                float pitchDeg = 2f - LandGlideslopeDeg + 0.12f * err;
                pitchTarget = Mathf.Clamp(pitchDeg, -8f, 12f) * Mathf.Deg2Rad;
                throttle = Mathf.Clamp(0.05f * (LandApproachSpeedMps - _c.Airspeed), 0f, 0.6f);
                if (agl < flareAgl) { _landPhase = 2; Debug.Log("[ModePilot] LAND 拉平"); }
            }
            else if (_landPhase == 2)
            {
                Phase = "拉平";
                // 按目标下沉率闭环 (指数拉平: 下沉率目标正比于离地高度, 最小 0.5 m/s):
                // 固定大俯仰会爆升 (实测 3m→13m 再飘数百米), 闭环只补当下需要的升力
                float vsTarget = -Mathf.Max(FlareMinSinkMps, 0.25f * Mathf.Max(agl, 0f));
                float flarePitchDeg = 1f + 1.5f * (vsTarget - _c.VerticalSpeed);
                pitchTarget = Mathf.Clamp(flarePitchDeg, -2f, 7f) * Mathf.Deg2Rad;
                desiredBank = ControlLaws.BankFromHeadingErr(heading, g.HeadingRad, 8f);
                throttle = 0f;
                if (_c.OnGround) { _landPhase = 3; Debug.Log("[ModePilot] LAND 接地, 滑跑刹车"); }
            }
            else
            {
                Phase = "滑跑";
                pitchTarget = 0f;
                yawStick = Mathf.Clamp(hdgErrRad * 1.2f, -0.5f, 0.5f);
                throttle = 0f;
                brake = 1f;
            }
        }

        private const float FlareMinSinkMps = 0.5f;

        // ================= 工具 =================

        private Vector2 Home() => new Vector2(_c.InitialPositionNed.x, _c.InitialPositionNed.y);

        // ================= 地形净空 (S2) =================

        /// <summary>脚下地形高 m (无登记地形 = 0, 与旧平地行为一致)。</summary>
        private static float GroundHere(AircraftState s)
        {
            return TerrainRegistry.HasTerrain
                ? (float)TerrainRegistry.HeightAt(s.Position.X, s.Position.Y) : 0f;
        }

        /// <summary>
        /// 近处地形最高点 m: 当前位置 + 航向前方 100/200/300/400m + 半径 120m 一圈 8 点
        /// (盘旋半径约 100m)。无登记地形返回 0。采样点有限, 不是完整航迹扫描 ——
        /// 窄山脊可能漏掉, 余量 (margin) 要给够。
        /// </summary>
        private static float TerrainMaxAround(AircraftState s, float heading)
        {
            if (!TerrainRegistry.HasTerrain) return 0f;
            double n = s.Position.X, e = s.Position.Y;
            double max = TerrainRegistry.HeightAt(n, e);
            for (int i = 1; i <= 4; i++)
            {
                double d = 100.0 * i;
                max = System.Math.Max(max, TerrainRegistry.HeightAt(
                    n + System.Math.Cos(heading) * d, e + System.Math.Sin(heading) * d));
            }
            for (int k = 0; k < 8; k++)
            {
                double a = k * System.Math.PI / 4.0;
                max = System.Math.Max(max, TerrainRegistry.HeightAt(
                    n + System.Math.Cos(a) * 120.0, e + System.Math.Sin(a) * 120.0));
            }
            return (float)max;
        }

        /// <summary>目标高度与"近处地形最高点 + 余量"取大; 无登记地形时原样返回 (行为不变)。</summary>
        private static float ClearAlt(float target, float terrainMax, float margin)
        {
            return TerrainRegistry.HasTerrain ? Mathf.Max(target, terrainMax + margin) : target;
        }

        private static float HorizDist(AircraftState s, Vector2 p)
        {
            float dx = (float)s.Position.X - p.x, dy = (float)s.Position.Y - p.y;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private static float BearingTo(AircraftState s, Vector2 p) =>
            Mathf.Atan2(p.y - (float)s.Position.Y, p.x - (float)s.Position.X);

        /// <summary>航向误差 (rad, wrap ±π)。</summary>
        private static float HeadingErr(float heading, float target)
        {
            float e = target - heading;
            return Mathf.Atan2(Mathf.Sin(e), Mathf.Cos(e));
        }

        private static float WrapDeg(float deg)
        {
            while (deg > 180f) deg -= 360f;
            while (deg < -180f) deg += 360f;
            return deg;
        }

        /// <summary>高度保持 → 俯仰目标 (2° 基线 + 高度误差比例, 限 ±12°)。</summary>
        private static float AltToPitch(float targetAlt, float alt) => ControlLaws.AltToPitch(targetAlt, alt);

        // Engage 时捕获自稳航向
        private void CaptureStabHeading()
        {
            _stabHeading = (float)_c.State.Euler321()[2];
        }
    }
}
