using FlightDynamics;
using FlightDynamics.Math;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 场地边界防护 (自动返航): 以起飞点 (InitialPositionNed 水平分量) 为圆心,
    /// 水平距离进入警戒环 (BoundaryRadius - BoundaryMargin) 后, 渐进接管副翼,
    /// 把航向转向起飞点方向 —— 接管权重随距离从 0 (警戒环) 平滑升到 1 (边界),
    /// 飞行员在警戒环内拥有完全操纵权, 不会突兀抢杆。
    /// 由 AircraftController 在每个物理帧 Poll 之后、积分之前调用 Apply()。
    /// </summary>
    public class FlightGuard : MonoBehaviour
    {
        [Header("边界 (水平, 以起飞点为圆心)")]
        [Tooltip("场地半径 m (硬边界)")]
        public float BoundaryRadius = 1200f;
        [Tooltip("返航接管起始距离 = BoundaryRadius - Margin")]
        public float BoundaryMargin = 200f;

        [Header("返航控制律")]
        [Tooltip("最大返航坡度 rad")]
        public float MaxBankRad = 0.35f;
        public float BankGain = 0.6f;
        public float RollDampGain = 0.15f;
        [Tooltip("返航时最大副翼杆量 (比例)")]
        public float MaxAileronStick = 0.5f;

        [Header("返航时的地形避让 (有登记地形才生效; 平地场景行为不变)")]
        [Tooltip("返航接管期间, 前方地形高于当前高度 - 余量时先爬升 (转向照常进行)")]
        public bool TerrainAvoidance = true;
        [Tooltip("要求高出前方最高地形的余量 m")]
        public float TerrainClearanceM = 30f;
        [Tooltip("前视距离 m (沿航向及左右 30°/60° 扇面, 每 50 m 一点)")]
        public float LookAheadM = 400f;
        [Tooltip("低于失速速度估计的此倍数时不再拉杆并补满油门, 防止爬升中失速")]
        public float MinSpeedFactor = 1.35f;

        /// <summary>正在为避地形而爬升 (HUD 用)。</summary>
        public bool IsClimbingToClear { get; private set; }

        /// <summary>前视扇面内最高地形 m (无登记地形 = 0)。</summary>
        public float TerrainAhead { get; private set; }

        /// <summary>当前距起飞点水平距离 m (遥测/HUD 用)。</summary>
        public float DistanceToHome { get; private set; }

        /// <summary>是否正在自动返航 (接管权重 > 0)。</summary>
        public bool IsTurningBack { get; private set; }

        /// <summary>当前接管权重 0..1 (HUD/CSV 用)。</summary>
        public float Authority { get; private set; }

        private AircraftController _c;

        private void Awake() => _c = GetComponent<AircraftController>();

        /// <summary>每个物理帧调用: 按距离渐进改写副翼指令。
        /// ControlInputs 是 struct, 必须 ref 传入否则修改无法传回调用方。</summary>
        public void Apply(ref ControlInputs cmd)
        {
            var s = _c.State;
            Vector2 home = new Vector2(_c.InitialPositionNed.x, _c.InitialPositionNed.y);
            float dx = (float)s.Position.X - home.x;
            float dy = (float)s.Position.Y - home.y;
            DistanceToHome = Mathf.Sqrt(dx * dx + dy * dy);

            float warnR = BoundaryRadius - BoundaryMargin;
            // √ 曲线: 警戒环附近渐进介入 (飞行员几乎无感), 靠近硬边界迅速取得主导,
            // 避免与原输入 (如自动驾驶增稳) 的线性混合把转弯拉得过慢
            Authority = Mathf.Sqrt(Mathf.Clamp01(
                (DistanceToHome - warnR) / Mathf.Max(1f, BoundaryMargin)));
            IsTurningBack = Authority > 0f && !_c.IsCrashed;
            IsClimbingToClear = false;
            if (!IsTurningBack) return;

            // 起飞点方位 (从飞机指向起飞点) 与航向误差
            float homeBearing = Mathf.Atan2(-dy, -dx);          // NED: x 北, y 东
            double[] e = s.Euler321();
            float heading = (float)e[2];

            // 坡度指令 → 副翼 (共享 ControlLaws; 增益保留本组件可调字段,
            // MaxBankRad 是 rad, 共享接口按度传入后内部换算回来, 行为一致)
            float desiredBank = ControlLaws.BankFromHeadingErr(
                heading, homeBearing, MaxBankRad * Mathf.Rad2Deg);
            float ailCmd = (float)ControlLaws.RollStabilize(
                (float)e[0], desiredBank, (float)s.AngularVelocityBody.X,
                BankGain, RollDampGain);
            // cmd 已是实际舵偏: 在混合前转换一次符号, 不重复调用 mapper 的舵机滤波。
            float guardDeflection = Mathf.Clamp(
                (float)ControlLaws.SafeSign(_c.Plane.Aero.Data.Clda, "Clda(边界防护)") * ailCmd,
                -MaxAileronStick * (float)_c.Surfaces.MaxAileronRad,
                MaxAileronStick * (float)_c.Surfaces.MaxAileronRad);

            cmd.Aileron = Mathf.Lerp((float)cmd.Aileron, guardDeflection, Authority);

            if (TerrainAvoidance && TerrainRegistry.HasTerrain) AvoidTerrain(ref cmd, s, heading, (float)e[1]);
        }

        /// <summary>
        /// 返航接管期间的爬升避地形: 取航向正前方及左右 30°/60° 扇面内 LookAheadM 以内的最高地形,
        /// 高度低于"该地形 + TerrainClearanceM"时, 俯仰目标抬高 (与 ModePilot 同一条高度→俯仰曲线, 限 +12°),
        /// 升降舵按 √ 接管权重与原指令混合; 已低于"地形 + 余量/2"(紧迫) 时完全接管升降舵 (飞行员此刻往下推杆也无效)。
        /// 速度低于失速估计的 MinSpeedFactor 倍时俯仰目标压到 3° 以内并补满油门; 其余时候不动油门 (保持转弯半径)。
        /// 局限: 小型机爬升率只有 2~3 m/s, 前方是几百米高的山体时爬不过去 —— 本逻辑只对缓坡/低矮地形有效。
        /// </summary>
        private void AvoidTerrain(ref ControlInputs cmd, AircraftState s, float heading, float pitch)
        {
            double n = s.Position.X, east = s.Position.Y;
            float max = (float)TerrainRegistry.HeightAt(n, east);
            float[] fan = { 0f, -30f, 30f, -60f, 60f };
            foreach (float off in fan)
            {
                double a = heading + off * Mathf.Deg2Rad;
                double ca = System.Math.Cos(a), sa = System.Math.Sin(a);
                for (float d = 50f; d <= LookAheadM + 0.01f; d += 50f)
                    max = Mathf.Max(max, (float)TerrainRegistry.HeightAt(n + ca * d, east + sa * d));
            }
            TerrainAhead = max;

            float alt = (float)s.Altitude;
            float required = max + TerrainClearanceM;
            if (alt >= required) return;

            IsClimbingToClear = true;
            float pitchTarget = ControlLaws.AltToPitch(required, alt);
            double airspeed = s.Airspeed(Vec3.Zero);
            var plane = _c.Plane;
            double rho = AtmosphereModel.Density(0);
            double vStall = System.Math.Sqrt(2.0 * plane.Mass.MassKg * AtmosphereModel.G0 /
                                             (rho * plane.Aero.Data.Sref * 1.5));
            bool slow = airspeed < MinSpeedFactor * vStall;
            if (slow) pitchTarget = Mathf.Min(pitchTarget, 3f * Mathf.Deg2Rad);

            double eCmd = ControlLaws.PitchStabilize(pitchTarget, pitch, s.AngularVelocityBody.Y);
            double maxE = _c.Surfaces.MaxElevatorRad;
            double guardElev = ControlLaws.SafeSign(plane.Aero.Data.Cmde, "Cmde(边界防护)")
                               * System.Math.Max(-0.8 * maxE, System.Math.Min(0.8 * maxE, eCmd));
            bool urgent = alt < max + TerrainClearanceM * 0.5f;
            float w = urgent ? 1f : Authority;
            cmd.Elevator += (guardElev - cmd.Elevator) * w;
            // 不加油门: 满油门会把空速推到 39 m/s, 20° 坡度的转弯半径从 ~130 m 涨到 ~430 m, 反而撞上谷壁; 只在偏慢时补油
            if (slow) cmd.Throttle += (1.0 - cmd.Throttle) * w;
        }
    }
}
