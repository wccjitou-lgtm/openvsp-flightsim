using FlightDynamics;
using FlightDynamics.Math;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 风场设置 (开发提示词 5.2/5.6 节: UI 可调环境风)。
    /// Inspector 中配置稳定风/切变/紊流, 运行时可改数值立即生效 (内部重建
    /// CompositeWind 注入 AircraftController)。所有风分量只在此叠加一次,
    /// 经核心库唯一的 V_rel 计算点进入气动 (5.6 节要求)。
    /// </summary>
    [RequireComponent(typeof(AircraftController))]
    public class WindSettings : MonoBehaviour
    {
        [Header("稳定风 (5.2)")]
        [Tooltip("风速 m/s")]
        public float WindSpeed = 0f;
        [Tooltip("风向 (来风方位角, 度; 0 = 北风)")]
        public float WindDirectionDeg = 0f;

        [Header("风切变 (5.3, 幂律)")]
        public bool EnableShear = false;
        [Tooltip("切变幂指数 (地面粗糙度 0.1~0.4)")]
        public float ShearExponent = 0.2f;
        public float ShearRefAltitude = 10f;

        [Header("Dryden 紊流 (5.4, MIL-F-8785C)")]
        public DrydenTurbulence.TurbulenceLevel Turbulence = DrydenTurbulence.TurbulenceLevel.Light;
        [Tooltip("固定随机种子 (0 = 每次随机)")]
        public int TurbulenceSeed = 42;

        [Header("离散阵风 (5.5, 1-cosine)")]
        [Tooltip("触发一次阵风 (运行时勾选/调用 TriggerGust)")]
        public bool TriggerGustOnStart = false;
        public float GustAmplitude = 8f;
        public float GustDuration = 3f;
        [Tooltip("阵风方向 (来风方位角, 度; 0 = 北风, 与 WindDirectionDeg 同约定)")]
        public float GustDirectionDeg = 90f;

        private AircraftController _controller;
        private readonly System.Collections.Generic.List<DiscreteGust> _gusts =
            new System.Collections.Generic.List<DiscreteGust>();

        [Header("风况实时调节 (HUD 面板/SetWeather 用)")]
        [Tooltip("允许风向缓慢连续摆动 (气象 veering, 无跳变)")]
        public bool DirectionVeer = false;
        public float VeerAmplitudeDeg = 18f;
        public float VeerPeriodS = 150f;
        [Tooltip("关闭后组合风场不含紊流 (纯稳定风)")]
        public bool EnableTurbulence = true;

        /// <summary>缓变稳定风模型 (字段可实时改, 无需重建)。</summary>
        private readonly SteadyWindModel _steady = new SteadyWindModel();
        /// <summary>持久 Dryden 实例 —— 调节风速/风向/等级时保持紊流状态连续不重置。</summary>
        private DrydenTurbulence _dryden;

        private void Awake()
        {
            _controller = GetComponent<AircraftController>();
            // 注意: Rebuild 需等 AircraftController.Awake 创建 Plane 之后 ——
            // 组件 Awake 顺序不保证, 故放 Start (所有 Awake 已完成)
        }

        private void Start()
        {
            var rng = new System.Random(TurbulenceSeed != 0 ? TurbulenceSeed : 42);
            _steady.VeerPhase = rng.NextDouble() * 2.0 * System.Math.PI;
            Rebuild();
            if (TriggerGustOnStart) TriggerGust(5f);
        }

        /// <summary>按当前 Inspector 设置完整重建风场 (含紊流重置)。</summary>
        public void Rebuild()
        {
            _dryden = MakeDryden(Turbulence);
            ApplyComposite("重建");
        }

        /// <summary>
        /// 实时调节风况 (HUD 风况面板调用): 稳定风部分与紊流等级即时生效,
        /// Dryden 状态保持连续 (仅等级变化时调整参数, 不重置随机过程)。
        /// </summary>
        public void SetWeather(float speedMps, float fromDeg, bool veer,
                               DrydenTurbulence.TurbulenceLevel level, bool enableTurb)
        {
            WindSpeed = speedMps;
            WindDirectionDeg = fromDeg;
            DirectionVeer = veer;
            Turbulence = level;
            EnableTurbulence = enableTurb;
            if (_dryden == null) _dryden = MakeDryden(level);
            else if (_dryden.W20 != W20Of(level)) _dryden.SetLevel(level);   // 保状态换强度
            ApplyComposite("实时调节");
        }

        private DrydenTurbulence MakeDryden(DrydenTurbulence.TurbulenceLevel level)
        {
            int seed = TurbulenceSeed != 0 ? TurbulenceSeed : Random.Range(1, int.MaxValue);
            return new DrydenTurbulence(20.0, level, seed,
                headingProvider: () => _controller != null && _controller.State != null
                    ? _controller.State.Euler321()[2] : 0.0);
        }

        private static double W20Of(DrydenTurbulence.TurbulenceLevel lv) => lv switch
        {
            DrydenTurbulence.TurbulenceLevel.Light => 7.7,
            DrydenTurbulence.TurbulenceLevel.Severe => 23.1,
            _ => 15.4,
        };

        /// <summary>按当前字段组装组合风场 (稳定风矢量/摆动实时读 _steady)。</summary>
        private void ApplyComposite(string reason)
        {
            _steady.Speed = WindSpeed;
            _steady.FromDeg = WindDirectionDeg;
            _steady.Veer = DirectionVeer;
            _steady.VeerAmplitudeDeg = VeerAmplitudeDeg;
            _steady.VeerPeriodS = VeerPeriodS;

            // 切变包裹的基准矢量随时间变化 (缓变时) —— 每次组装用当前时刻矢量
            Vec3 baseVec = _steady.VectorAt(Time.timeAsDouble);
            IWindModel steady = EnableShear
                ? (IWindModel)new PowerLawShearWind(baseVec, ShearRefAltitude, ShearExponent)
                : _steady;

            var composite = new CompositeWind().Add(steady);
            if (EnableTurbulence && _dryden != null) composite.Add(_dryden);
            foreach (var g in _gusts) composite.Add(g);

            if (_controller == null || _controller.Plane == null)
            {
                Debug.LogWarning("[WindSettings] 控制器/Plane 未就绪, 跳过 " + reason);
                return;
            }
            _controller.Plane.Wind = composite;
            Debug.Log(string.Format(
                "[WindSettings] 风场({0}): 稳定 {1:F1} m/s @ {2:F0}°{3} + 紊流 {4}",
                reason, WindSpeed, WindDirectionDeg,
                DirectionVeer ? string.Format(" (缓变 ±{0:F0}°/{1:F0}s)", VeerAmplitudeDeg, VeerPeriodS) : "",
                EnableTurbulence ? Turbulence.ToString() : "关"));
        }

        /// <summary>触发一次 1-cosine 阵风。</summary>
        /// <param name="delay">延迟 s</param>
        public void TriggerGust(float delay = 0f)
        {
            // 与 WindDirectionDeg 相同的"来风方位角"约定: 0=北风(吹向南)
            double dirRad = GustDirectionDeg * Mathf.Deg2Rad;
            _gusts.Add(new DiscreteGust(
                new Vec3(-System.Math.Cos(dirRad), -System.Math.Sin(dirRad), 0),
                GustAmplitude, Time.time + delay, GustDuration));
            Rebuild();
        }
    }
}
