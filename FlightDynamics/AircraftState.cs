using System;
using FlightDynamics.Math;

namespace FlightDynamics
{
    /// <summary>操纵面输入 (全部为实际偏角, 弧度)。</summary>
    public struct ControlInputs
    {
        public double Elevator;   // 升降舵偏角 rad (符号约定见 AeroData)
        public double Aileron;    // 副翼偏角 rad
        public double Rudder;     // 方向舵偏角 rad (在地面兼作前轮转向)
        public double Throttle;   // 油门 0..1
        public double Brake;      // 刹车 0..1 (机轮, Phase 2)

        public static ControlInputs Zero { get { return new ControlInputs(); } }
    }

    /// <summary>
    /// 6DOF 飞行状态。机体轴系: x 前, y 右, z 下; 地球轴系: NED (北东地)。
    /// 状态: 位置 NED, 姿态四元数 (体→NED), 机体线速度 (u,v,w), 机体角速度 (p,q,r)。
    /// </summary>
    public class AircraftState
    {
        /// <summary>位置 (NED, m; Z 负值代表在地平线上方, 高度 = -Z)。</summary>
        public Vec3 Position = Vec3.Zero;

        /// <summary>姿态 (体→NED), v_NED = Attitude.Rotate(v_body)。</summary>
        public Quat Attitude = Quat.Identity;

        /// <summary>质心速度 (机体轴系, m/s)。</summary>
        public Vec3 VelocityBody = Vec3.Zero;

        /// <summary>角速度 (机体轴系, rad/s): X=滚转率 p, Y=俯仰率 q, Z=偏航率 r。</summary>
        public Vec3 AngularVelocityBody = Vec3.Zero;

        public AircraftState Clone()
        {
            var s = new AircraftState();
            s.Position = Position;
            s.Attitude = Attitude;
            s.VelocityBody = VelocityBody;
            s.AngularVelocityBody = AngularVelocityBody;
            return s;
        }

        public void CopyFrom(AircraftState src)
        {
            Position = src.Position;
            Attitude = src.Attitude;
            VelocityBody = src.VelocityBody;
            AngularVelocityBody = src.AngularVelocityBody;
        }

        /// <summary>地速 (NED, m/s)。</summary>
        public Vec3 VelocityNed { get { return Attitude.Rotate(VelocityBody); } }

        /// <summary>几何高度 (m)。</summary>
        public double Altitude { get { return -Position.Z; } }

        // ---- 相对气流 (风的介入点, 开发提示词 4.2 节; 全项目仅此处叠加风) ----

        /// <summary>相对气流速度 = 地速(NED) - 风(NED), 再投影到机体轴 (m/s)。</summary>
        public Vec3 RelativeWindBody(Vec3 windNed)
        {
            Vec3 vRelNed = VelocityNed - windNed;
            return Attitude.Conjugate.Rotate(vRelNed);
        }

        /// <summary>空速 (m/s)。</summary>
        public double Airspeed(Vec3 windNed)
        {
            return RelativeWindBody(windNed).Length;
        }

        /// <summary>攻角 alpha = atan2(w_rel, u_rel) (rad)。</summary>
        public double Alpha(Vec3 windNed)
        {
            Vec3 v = RelativeWindBody(windNed);
            return System.Math.Atan2(v.Z, v.X);
        }

        /// <summary>侧滑角 beta = asin(v_rel / V) (rad)。</summary>
        public double Beta(Vec3 windNed)
        {
            Vec3 v = RelativeWindBody(windNed);
            double speed = v.Length;
            if (speed < 1e-9) return 0;
            return System.Math.Asin(Mathd.Clamp(v.Y / speed, -1.0, 1.0));
        }

        /// <summary>欧拉角 [滚转, 俯仰, 偏航] (rad), 仅用于显示/HUD。</summary>
        public double[] Euler321() { return Attitude.ToEuler321(); }
    }
}
