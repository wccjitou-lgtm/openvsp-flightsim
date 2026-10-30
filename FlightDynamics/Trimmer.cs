using System;
using FlightDynamics.Math;

namespace FlightDynamics
{
    /// <summary>稳态水平直线飞行配平结果。</summary>
    public struct TrimResult
    {
        public double Alpha;        // 配平攻角 rad
        public double Elevator;     // 配平升降舵偏角 rad
        public double Throttle;     // 配平油门 0..1
        public double PitchAttitude;// 俯仰姿态角 rad (= alpha, 因为 gamma=0)
        public double Airspeed;     // m/s
        public double ResidualCm;   // 配平后的纵向残余力矩系数 (应接近 0)
    }

    /// <summary>
    /// 稳态配平器: 给定空速与高度, 求解 (alpha, 升降舵, 油门) 使
    /// 升力=重力、推力=阻力、俯仰力矩=0 (定常平飞, 开发提示词 Phase 1 验收)。
    /// 假设 beta=0, 角速度=0 (定常), 推力沿飞行方向。
    /// </summary>
    public class Trimmer
    {
        private readonly Aircraft _plane;

        public Trimmer(Aircraft plane) { _plane = plane; }

        /// <summary>迭代求解定常平飞配平。</summary>
        /// <param name="airspeed">目标空速 m/s</param>
        /// <param name="altitudeM">几何高度 m</param>
        public TrimResult TrimLevelFlight(double airspeed, double altitudeM)
        {
            AeroData a = _plane.Aero.Data;
            double rho = AtmosphereModel.Density(altitudeM);
            double qbar = 0.5 * rho * airspeed * airspeed;
            double weight = _plane.Mass.MassKg * AtmosphereModel.G0;

            // 初值 (忽略推力垂直分量的经典解)
            double elevator = 0;
            double alpha = (weight / (qbar * a.Sref) - a.CL0) / a.CLalpha;

            // 迭代: 推力沿机体 x 轴 (与速度差一个 alpha 角), 精确平衡:
            //   沿速度方向: T·cosα = D
            //   垂直速度方向: L + T·sinα = W
            //   力矩: Cm(α, δe) = 0
            const int MaxIter = 200;
            double throttle = 1;
            for (int i = 0; i < MaxIter; i++)
            {
                // 当前升力系数与阻力
                double cl = a.CL0 + a.CLalpha * alpha + a.CLde * elevator;
                double cd = a.CD0 + a.KInduced * cl * cl;
                double drag = qbar * a.Sref * cd;

                // 推力 = 阻力 / cosα
                double thrustReq = drag / System.Math.Max(System.Math.Cos(alpha), 0.5);
                double thrustAvail = _plane.Engine.Thrust(1.0, airspeed);
                throttle = thrustAvail > 1e-9 ? thrustReq / thrustAvail : 1.0;

                // 需要的升力系数 (扣除推力垂直分量)
                double clReq = (weight - thrustReq * System.Math.Sin(alpha)) / (qbar * a.Sref);

                // 力矩平衡求升降舵
                elevator = -(a.Cm0 + a.Cmalpha * alpha) / a.Cmde;
                // 升力平衡求攻角
                double alphaNew = (clReq - a.CL0 - a.CLde * elevator) / a.CLalpha;

                if (System.Math.Abs(alphaNew - alpha) < 1e-13)
                {
                    alpha = alphaNew;
                    break;
                }
                alpha = 0.5 * alpha + 0.5 * alphaNew; // 松弛
            }

            // 残余力矩系数 (校验)
            double cmResidual = a.Cm0 + a.Cmalpha * alpha + a.Cmde * elevator;

            var r = new TrimResult();
            r.Alpha = alpha;
            r.Elevator = elevator;
            r.Throttle = Mathd.Clamp(throttle, 0.0, 1.5); // 允许略超 1 以暴露推力不足
            r.PitchAttitude = alpha;      // gamma = 0 → theta = alpha (beta=0 无风)
            r.Airspeed = airspeed;
            r.ResidualCm = cmResidual;
            return r;
        }

        /// <summary>按配平结果构造初始飞行状态 (高度 altitudeM, 航向 0 = 北)。</summary>
        public AircraftState BuildTrimState(TrimResult trim, double altitudeM)
        {
            var s = new AircraftState();
            s.Position = new Vec3(0, 0, -altitudeM);
            s.Attitude = Quat.FromEuler321(0, trim.PitchAttitude, 0);
            double V = trim.Airspeed;
            s.VelocityBody = new Vec3(V * System.Math.Cos(trim.Alpha), 0, V * System.Math.Sin(trim.Alpha));
            s.AngularVelocityBody = Vec3.Zero;
            return s;
        }
    }
}
