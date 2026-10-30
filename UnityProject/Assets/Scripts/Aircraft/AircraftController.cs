using System;
using FlightDynamics;
using FlightDynamics.Math;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FlightSim
{
    /// <summary>
    /// 飞机 MonoBehaviour 包装层 (开发提示词 2 节架构):
    /// FixedUpdate 驱动纯 C# 核心库 (定步长, 与渲染帧率解耦),
    /// Update/LateUpdate 用插值把状态映射到 Transform。
    ///
    /// 坐标映射 (核心库 NED → Unity 世界, 集中在此处, 全项目唯一):
    ///   位置: unity(x,y,z) = (ned.y, -ned.z, ned.x)   // 东, 上, 北
    ///   姿态: Quaternion(-q.Y, q.Z, -q.X, q.W)
    ///     推导: NED 右手系(x北,y东,z地) → Unity 左手系(x东,y上,z北) 为
    ///     轴置换+手性翻转; 已用纯航向/纯俯仰特例数值验证。
    /// </summary>
    [DisallowMultipleComponent]
    public class AircraftController : MonoBehaviour
    {
        [Header("配置")]
        [Tooltip("AircraftConfig.json (相对 StreamingAssets)")]
        public string ConfigFileName = "AircraftConfig.json";
        [Tooltip("导入机型 config 绝对路径 (Phase 7, VspImports/.../AircraftConfig.json); 非空优先于 ConfigFileName")]
        public string ConfigAbsolutePath = "";
        [Tooltip("飞机可视模型 (z 前/y 上的 Unity 轴系, 由 stl_to_obj_unity.py 生成)")]
        public Renderer AircraftMesh;

        [Header("初始状态")]
        public float InitialHeadingDeg = 0f;
        public float InitialAirspeed = 0f;
        [Tooltip("初始 NED 位置 (北, 东, 下); 跑道起点一般为 (0,0,-0.33)")]
        public Vector3 InitialPositionNed = new Vector3(0f, 0f, -0.327f);

        [Header("遥测 (只读)")]
        public float Airspeed;
        /// <summary>最近一个物理步的环境风 (NED, 稳定风+紊流)。遥测直接读它, 不再另调 WindAt
        /// (Dryden 按时间戳推进, 外部用别的时钟调用会打乱滤波器)。</summary>
        public Vec3 LastWindNed { get; private set; }
        public float Altitude;
        /// <summary>离地高度 m (Altitude - 脚下地形高; 无地形登记时 = Altitude)。</summary>
        public float AltitudeAgl;
        public float PitchDeg;
        public float BankDeg;
        public float HeadingDeg;
        public bool OnGround;
        [Header("遥测 Phase 5 (HUD 用, 只读)")]
        public float VerticalSpeed;      // m/s, 正 = 上升
        public float GLoad;              // 法向过载 (比力法), 平飞/地面静止 ≈ 1
        public float Throttle;           // 0..1
        public float ElevatorDeg;        // 舵面实际指令偏角
        public float AileronDeg;
        public float RudderDeg;
        public float WindSpeed;          // m/s (飞机位置处的组合风)
        public float WindFromDeg;        // 来风方位角 0..360 (0 = 北风)

        // ---- 核心库对象 ----
        private Aircraft _plane;
        private RigidBodyIntegrator _integrator;
        private Trimmer _trimmer;
        private AircraftState _state;

        // 渲染插值 (物理 120Hz / 渲染帧率解耦)
        private AircraftState _renderPrev, _renderCurr;
        private double _physicsTime;

        // 输入提供者
        private IFlightInput _input;
        /// <summary>模式自驾等接管输入 (ModePilot 设置/清除); 非空时优先于 _input。</summary>
        public IFlightInput OverrideInput;
        private ControlSurfaceMapper _surfaces;

        public RigidBodyIntegrator Integrator { get { return _integrator; } }
        /// <summary>仅用于核心库单测与 ConsoleFlightSim --headless 自动配平演示;
        /// Unity 运行时的飞行配平走 KeyboardFlightInput 的 R/F 键, 不经过此对象。</summary>
        public Trimmer Trimmer { get { return _trimmer; } }
        public AircraftState State { get { return _state; } }
        public Aircraft Plane { get { return _plane; } }
        /// <summary>共享舵面映射 (按配置限位); 输入层应使用此实例而非自建。</summary>
        public ControlSurfaceMapper Surfaces { get { return _surfaces; } }
        public AeroResult LastAero;

        private void Awake()
        {
            // U7 兜底: config 损坏时 Parse 抛异常, 若任其冒出 _plane 为 null,
            // FixedUpdate 每步 NRE 刷屏 (8.7GB 日志事故模式)。捕获后禁用自身
            // 并只报一次错 — 场景里留下一架"死"飞机, 而不是拖死主线程。
            AircraftConfigLoader.LoadedAircraft loaded;
            try
            {
                loaded = !string.IsNullOrEmpty(ConfigAbsolutePath)
                    ? AircraftConfigLoader.LoadFromAbsolutePath(ConfigAbsolutePath)
                    : AircraftConfigLoader.LoadFromStreamingAssets(ConfigFileName);
                _plane = new Aircraft(loaded.Mass, new AeroModel(loaded.Aero), loaded.Engine, new CalmWind())
                {
                    Gear = loaded.Gear
                };
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[AircraftController] 配置加载失败, 整机禁用: " + ex.Message);
                // 整 GO 禁用 (而非仅本组件): HUD/键盘输入等兄弟组件引用 State/
                // Surfaces 为 null 会在 OnGUI/FixedUpdate 里继续 NRE 刷屏
                gameObject.SetActive(false);
                return;
            }
            // 场景登记了物理地形 (森林场景) 时新机直接继承: 工坊热替换会新建飞机,
            // 否则新机 Gear.Terrain 是默认平地, 会穿过山体。未登记 = 平地, 行为不变。
            if (TerrainRegistry.HasTerrain && _plane.Gear != null)
                _plane.Gear.Terrain = TerrainRegistry.Current;

            // 结构阻尼惯性自适应 (U06 防线 + U8 缩放): 默认 100/150/50
            // N·m/(rad/s) 是 14kg TestUAV (Ixx 0.509/Iyy 5.07/Izz 5.27) 的调参,
            // 对工坊超轻机 (Iyy 可低至 0.3) 是"扳手级"约束 — 抬轮迟缓到无法
            // 起飞。按惯量比例缩放后, 再钳稳定上限 c·h/I ≲ 2 (RK4 稳定域,
            // h = fixedDelta/子步), 防极端轻机地面数值自激 (U6 场景)。
            if (_plane.Gear != null)
            {
                double h = (1.0 / 120.0) / RigidBodyIntegrator.Substeps;
                var gp = _plane.Gear.Params;
                gp.StructDampRoll = System.Math.Min(
                    gp.StructDampRoll * _plane.Mass.Ixx / 0.509, 2.0 * _plane.Mass.Ixx / h);
                gp.StructDampPitch = System.Math.Min(
                    gp.StructDampPitch * _plane.Mass.Iyy / 5.07, 2.0 * _plane.Mass.Iyy / h);
                gp.StructDampYaw = System.Math.Min(
                    gp.StructDampYaw * _plane.Mass.Izz / 5.27, 2.0 * _plane.Mass.Izz / h);
            }
            _integrator = new RigidBodyIntegrator(_plane);
            _trimmer = new Trimmer(_plane);

            _state = new AircraftState();
            ApplyInitialState();

            _renderPrev = _state.Clone();
            _renderCurr = _state.Clone();

            _surfaces = new ControlSurfaceMapper(loaded.Aero, loaded.MaxDeflectionDeg);

            // 物理定步长 120Hz (开发提示词 4.4 节)。全局引擎参数, 用静态标志
            // 保证多实例/多场景下只设置一次 (P2-9)
            if (!_physicsRateSet)
            {
                Time.fixedDeltaTime = 1f / 120f;
                _physicsRateSet = true;
            }
        }

        private static bool _physicsRateSet;

        /// <summary>把 _state 设为初始状态 (Awake 与 ResetToInitial 共用)。</summary>
        private void ApplyInitialState()
        {
            _state.Position = new Vec3(InitialPositionNed.x, InitialPositionNed.y, InitialPositionNed.z);
            _state.Attitude = Quat.FromEuler321(0, 0, InitialHeadingDeg * Mathf.Deg2Rad);
            _state.VelocityBody = new Vec3(InitialAirspeed, 0, 0);
            _state.AngularVelocityBody = Vec3.Zero;
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (WorkshopUI.IsOpen) return;   // R13: 工坊输入隔离 (Backspace 不复位)
            if (kb != null && kb.backspaceKey.wasPressedThisFrame)
                ResetToInitial();
        }

        /// <summary>
        /// 复位到初始状态 (跑道起点等), 用于起飞/试飞后重新开始, Backspace 触发。
        /// 同时清空输入侧累积状态 (油门/配平/自动驾驶爬升标志) 与舵机滞后滤波,
        /// 否则复位后飞机瞬间归位但操纵量残留旧值, 会立刻带着旧油门/舵偏冲出去。
        /// 不重置 _physicsTime (单调递增) —— 风场/紊流按绝对时间戳推进
        /// (见 DrydenTurbulence), 重置会导致其内部 _lastTime 大于新时间,
        /// 复位后风场会冻结在重置前一刻的值直到物理时间追上为止。
        /// </summary>
        public void ResetToInitial()
        {
            ApplyInitialState();
            _renderPrev.CopyFrom(_state);
            _renderCurr.CopyFrom(_state);
            _plane.LastGroundContact = default(GroundContactResult);
            IsCrashed = false;
            CrashReason = "";
            _lastAirborneSink = 0;
            // 复位解除模式自驾 (交还手动输入)
            var pilot = GetComponent<ModePilot>();
            if (pilot != null) pilot.Disengage();

            _surfaces.ResetState();
            if (_input == null) _input = GetComponent<IFlightInput>();
            if (_input != null) _input.ResetState();

            var cam = Camera.main != null ? Camera.main.GetComponent<ChaseCamera>() : null;
            if (cam != null) cam.SnapToTarget();

            // 复位 = 新飞行段: 遥测记录换新文件 (旧文件自动落盘)。
            // 仅在记录中才换 —— 尊重用户 F9 手动关闭记录的选择。
            var logger = GetComponent<DataLogger>();
            if (logger != null && logger.IsRecording) logger.BeginFile();

            Debug.Log("[AircraftController] 已复位到初始状态 (Backspace)");
        }

        private void FixedUpdate()
        {
            // 坠毁后冻结物理 (DataLogger 仍独立采样冻结状态; Backspace 复位解除)
            if (IsCrashed || Frozen) return;

            // 惰性获取输入 (组件添加顺序无关; 无输入组件时回落键盘)。
            // 接口引用不触发 Unity 伪 null, 已销毁组件需显式探测后重解析
            // (测试/运行时替换输入组件的场景)。
            bool inputDead = _input is UnityEngine.Object dead && dead == null;
            if (_input == null || inputDead)
            {
                _input = GetComponent<IFlightInput>();
                if (_input == null) _input = gameObject.AddComponent<KeyboardFlightInput>();
            }
            // 模式自驾 (返航/降落/盘旋/自稳) 优先; 组件被销毁时自动回落
            bool overrideDead = OverrideInput is UnityEngine.Object od && od == null;
            if (overrideDead) OverrideInput = null;
            ControlInputs cmd = OverrideInput != null ? OverrideInput.Poll() : _input.Poll();

            // 场地边界防护 (靠近边界渐进接管副翼转向起飞点)
            if (_guard == null) _guard = GetComponent<FlightGuard>();
            if (_guard != null) _guard.Apply(ref cmd);

            // NED z 向下为正; 记录撞击前下沉率 (触地后弹簧减速, VS 已失真)。
            if (!_plane.LastGroundContact.AnyContact)
                _lastAirborneSink = _state.VelocityNed.Z;

            _renderPrev.CopyFrom(_state);
            _integrator.Step(_state, cmd, _physicsTime, Time.fixedDeltaTime);
            _physicsTime += Time.fixedDeltaTime;
            _renderCurr.CopyFrom(_state);

            // 遥测 (HUD 数据源, Phase 5): 全部与显示当步状态一致
            Vec3 wind = _plane.Wind != null
                ? _plane.Wind.WindAt(_state.Position, _physicsTime)
                : Vec3.Zero;
            LastWindNed = wind;
            Airspeed = (float)_state.Airspeed(wind);
            Altitude = (float)_state.Altitude;
            AltitudeAgl = (float)(_state.Altitude - TerrainRegistry.HeightAt(_state.Position.X, _state.Position.Y));
            double[] e = _state.Euler321();
            PitchDeg = (float)(e[1] * Mathd.Rad2Deg);
            BankDeg = (float)(e[0] * Mathd.Rad2Deg);
            HeadingDeg = (float)(e[2] * Mathd.Rad2Deg);
            OnGround = _plane.LastGroundContact.AnyContact;

            VerticalSpeed = (float)(-_state.VelocityNed.Z);

            // 过载 (比力计原理): n = -(F_非重力)_z / (m·g), 地面静止/平飞均 ≈ 1。
            // 顺带产出 LastAero (此前声明未赋值)。
            double mg = _plane.Mass.MassKg * AtmosphereModel.G0;
            Vec3 gravityBody = _state.Attitude.Conjugate.Rotate(new Vec3(0, 0, mg));
            Vec3 totalF = _plane.ComputeForce(_state, cmd, _physicsTime,
                out AeroResult aeroNow, out double thrustN);
            LastAero = aeroNow;
            GLoad = (float)(-(totalF - gravityBody).Z / mg);

            Throttle = (float)cmd.Throttle;
            ElevatorDeg = (float)(cmd.Elevator * Mathd.Rad2Deg);
            AileronDeg = (float)(cmd.Aileron * Mathd.Rad2Deg);
            RudderDeg = (float)(cmd.Rudder * Mathd.Rad2Deg);

            WindSpeed = (float)wind.Length;
            double windFrom = System.Math.Atan2(-wind.Y, -wind.X) * Mathd.Rad2Deg;
            WindFromDeg = (float)((windFrom + 360.0) % 360.0);

            DetectCrash();
        }

        // ================= 坠毁检测 (触地过猛 / 极端姿态) =================

        [Header("坠毁判定阈值")]
        [Tooltip("触地时空中下沉率超过此值判坠毁 (m/s)")]
        public float CrashSinkRate = 8f;
        [Tooltip("触地时滚转角绝对值超过此值判坠毁 (度)")]
        public float CrashBankDeg = 70f;
        [Tooltip("触地时俯仰角绝对值超过此值判坠毁 (度)")]
        public float CrashPitchDeg = 45f;

        /// <summary>已坠毁 (物理冻结; Backspace 复位解除)。</summary>
        public bool IsCrashed { get; private set; }

        /// <summary>坠毁原因 (HUD 坠毁横幅显示); 未坠毁时为空。</summary>
        public string CrashReason { get; private set; } = "";

        /// <summary>外部暂停物理 (场景切换载入期间, 由 SceneSwitcher 置位; 与坠毁不同, 不影响判定状态)。</summary>
        public bool Frozen { get; set; }

        private double _lastAirborneSink;
        private FlightGuard _guard;

        private void DetectCrash()
        {
            if (IsCrashed || !OnGround) return;

            // 水面是硬地板 (WaterFloorTerrain): 机体/起落架触到水面一律判坠海, 不看下沉率与姿态
            if (TerrainRegistry.IsWater(_state.Position.X, _state.Position.Y))
            {
                Crash("坠入水面");
                return;
            }

            if (_lastAirborneSink > CrashSinkRate)
            {
                Crash("触地过猛: 下沉率 " + _lastAirborneSink.ToString("F1") + " m/s");
                return;
            }
            if (Mathf.Abs(BankDeg) > CrashBankDeg)
            {
                Crash("触地姿态失控: 滚转 " + BankDeg.ToString("F0") + "°");
                return;
            }
            if (Mathf.Abs(PitchDeg) > CrashPitchDeg)
            {
                Crash("触地姿态失控: 俯仰 " + PitchDeg.ToString("F0") + "°");
            }
        }

        private void Crash(string reason)
        {
            IsCrashed = true;
            CrashReason = reason;
            Debug.Log("[AircraftController] 坠毁: " + reason + " — 按 Backspace 复位");
        }

        private void LateUpdate()
        {
            if (_renderPrev == null || _renderCurr == null) return;
            // 物理状态插值到渲染帧 (消除视觉抖动)
            double alpha = Time.fixedDeltaTime > 0 ? Time.timeAsDouble % Time.fixedDeltaTime / Time.fixedDeltaTime : 0;
            var s = Interpolate(_renderPrev, _renderCurr, alpha);
            ApplyToTransform(s);
        }

        private static AircraftState Interpolate(AircraftState a, AircraftState b, double t)
        {
            var s = new AircraftState();
            s.Position = a.Position + t * (b.Position - a.Position);
            s.VelocityBody = a.VelocityBody;
            s.AngularVelocityBody = a.AngularVelocityBody;
            // 四元数球面插的简化: 归一化线性插值 (姿态变化小, 误差可忽略)
            Quat qa = a.Attitude, qb = b.Attitude;
            double dot = qa.W * qb.W + qa.X * qb.X + qa.Y * qb.Y + qa.Z * qb.Z;
            if (dot < 0) { qb = new Quat(-qb.W, -qb.X, -qb.Y, -qb.Z); dot = -dot; }
            double k0 = 1 - t, k1 = t;
            s.Attitude = new Quat(k0 * qa.W + k1 * qb.W, k0 * qa.X + k1 * qb.X,
                                  k0 * qa.Y + k1 * qb.Y, k0 * qa.Z + k1 * qb.Z).Normalized();
            return s;
        }

        /// <summary>把核心库状态映射到 Unity Transform (NED→Unity, 见类注释)。</summary>
        private void ApplyToTransform(AircraftState s)
        {
            transform.position = new Vector3(
                (float)s.Position.Y,    // 东  → unity x
                (float)-s.Position.Z,   // 上  → unity y
                (float)s.Position.X);   // 北  → unity z

            Quat q = s.Attitude;
            transform.rotation = new Quaternion(
                (float)-q.Y,
                (float)q.Z,
                (float)-q.X,
                (float)q.W);
        }
    }
}
