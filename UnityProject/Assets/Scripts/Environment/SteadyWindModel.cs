using System;
using FlightDynamics;
using FlightDynamics.Math;

namespace FlightSim
{
    /// <summary>
    /// 可缓变的稳定风 (来风方位角约定, 与 WindSettings 一致):
    ///   WindAt = -Speed·(cos(dir), sin(dir), 0), dir = FromDeg + Veer 摆动。
    /// Veer 开启时方向按正弦缓慢连续摆动 (气象风摆/veering), 无任何跳变;
    /// 关闭时方向恒定。纯 C# 逻辑 (无 Unity 依赖), 状态只依赖绝对时间,
    /// 同一时刻重复查询幂等 (RK4 安全)。
    /// </summary>
    public class SteadyWindModel : IWindModel
    {
        public double Speed;          // m/s
        public double FromDeg;        // 来风方位角 0=北风
        public bool Veer;             // 方向缓变开关
        public double VeerAmplitudeDeg = 18.0;
        public double VeerPeriodS = 150.0;
        public double VeerPhase;      // 随机相位 (种子化)

        public SteadyWindModel(double speed = 0, double fromDeg = 0)
        {
            Speed = speed;
            FromDeg = fromDeg;
        }

        /// <summary>当前时刻的来风方位角 (度, 含摆动)。</summary>
        public double DirectionAt(double time) => Veer
            ? FromDeg + VeerAmplitudeDeg * Math.Sin(2.0 * Math.PI * time / VeerPeriodS + VeerPhase)
            : FromDeg;

        /// <summary>当前时刻的风矢量 (NED)。</summary>
        public Vec3 VectorAt(double time)
        {
            double rad = DirectionAt(time) * Math.PI / 180.0;
            return new Vec3(-Math.Cos(rad), -Math.Sin(rad), 0.0) * Speed;
        }

        public Vec3 WindAt(Vec3 positionNed, double time) => VectorAt(time);
    }
}
