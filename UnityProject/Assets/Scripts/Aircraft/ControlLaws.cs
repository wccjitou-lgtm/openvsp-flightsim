using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 共享增稳控制律 (AutoPilotInput 起飞演示 / ModePilot 模式自驾 / FlightGuard
    /// 边界返航 三处共用的同一套已验证公式; 2026-09-28 从三处重复实现提取)。
    /// 输出为正主轴力矩方向的等效舵量 (rad), 不含机型操纵导数符号;
    /// 输入层归一化后交给 ControlSurfaceMapper, 直接写舵偏的防护层只转换一次符号。
    /// 调增益属于行为变更, 必须与重构分开提交并跑全量回归。
    /// </summary>
    public static class ControlLaws
    {
        /// <summary>滚转增稳: 坡度误差 P + 滚转率阻尼 → 右滚为正的等效舵量 (rad)。
        /// FlightGuard 保留可调增益字段, 其余调用点用默认值。</summary>
        public static double RollStabilize(double bank, double desiredBank, double p,
            double bankGain = 0.6, double rollDamp = 0.15)
            => bankGain * (desiredBank - bank) - rollDamp * p;

        /// <summary>俯仰增稳: 俯仰误差 P + 俯仰率阻尼 → 抬头为正的等效舵量 (rad)。</summary>
        public static double PitchStabilize(double pitchTarget, double pitch, double q)
            => 0.8 * (pitchTarget - pitch) + 0.18 * (0.0 - q);

        /// <summary>高度保持 → 俯仰目标 (2° 基线 + 高度误差比例, 限 ±12°; ModePilot 与 FlightGuard 的爬升共用)。</summary>
        public static float AltToPitch(float targetAlt, float alt)
        {
            float err = Mathf.Clamp(targetAlt - alt, -60f, 60f);
            return Mathf.Clamp((2f + 0.12f * err) * Mathf.Deg2Rad, -8f * Mathf.Deg2Rad, 12f * Mathf.Deg2Rad);
        }

        /// <summary>偏航阻尼器 (+ 可选方向舵脚蹬量, 地面航向保持用)。</summary>
        public static double YawDamp(double r, double yawStick = 0.0)
            => -0.8 * r + yawStick * 0.349;

        /// <summary>航向误差 (rad, wrap ±π) → 期望坡度 (rad), 限 ±maxBankDeg。
        /// 目标恰在正后方 (±π, 浮点 sin(π) 符号抖动会随机左/右转) 时固定右转。</summary>
        public static float BankFromHeadingErr(float heading, float bearing, float maxBankDeg)
        {
            float err = Mathf.Atan2(Mathf.Sin(bearing - heading), Mathf.Cos(bearing - heading));
            if (err < -Mathf.PI + 0.02f) err = Mathf.PI;
            return Mathf.Clamp(0.9f * err, -maxBankDeg * Mathf.Deg2Rad, maxBankDeg * Mathf.Deg2Rad);
        }

        /// <summary>STAB 杆量 → 姿态指令 (U13/U15): 横滚杆 ±1 → ±30° 坡度;
        /// 俯仰杆 ±1 → 2° 基线 ±16° (U15: 原 ±12° 时满杆误差只能产生 ~45%
        /// 舵量, 用户实测"杆量最大只有 50%")。杆中立时上层回落航向/高度保持。</summary>
        public static Vector2 StabStickToAttitude(float rollStick, float pitchStick)
            => new Vector2(rollStick * 30f * Mathf.Deg2Rad,
                           (2f + pitchStick * 16f) * Mathf.Deg2Rad);

        /// <summary>STAB 俯仰混合 (U15): 姿态误差增稳 + 杆量前馈 (35% 满舵)。
        /// 前馈保证满杆时舵量不被姿态误差包线限在 ~45%。</summary>
        public static double StabElevator(double pitchTarget, double pitch, double q,
                                         double pitchStick, double maxElevRad)
            => PitchStabilize(pitchTarget, pitch, q) + 0.35 * pitchStick * maxElevRad;

        /// <summary>STAB 横滚混合 (U15): 坡度误差增稳 + 杆量前馈 (25% 满舵)。</summary>
        public static double StabAileron(double bank, double desiredBank, double p,
                                        double rollStick, double maxAilRad)
            => RollStabilize(bank, desiredBank, p) + 0.25 * rollStick * maxAilRad;

        /// <summary>操纵导数符号 (退化保护): 导入机型的操纵导数可能恰好算出 0,
        /// 此时 Math.Sign 返回 0 会让对应舵面/边界防护通道永久失效且无任何提示。
        /// 退化时记录一次警告并退回 +1, 而不是让该轴静默失效。axisName 仅用于日志。</summary>
        public static double SafeSign(double derivative, string axisName)
        {
            int sign = System.Math.Sign(derivative);
            if (sign != 0) return sign;
            Debug.LogWarning($"[ControlLaws] {axisName} 操纵导数为 0 (退化), 该通道符号退回默认 +1");
            return 1.0;
        }
    }
}
