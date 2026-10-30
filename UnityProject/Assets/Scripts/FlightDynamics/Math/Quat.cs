using System;

namespace FlightDynamics.Math
{
    /// <summary>
    /// 四元数 (Hamilton 积, W 为标量分量)。约定: 表示机体到 NED 的姿态旋转,
    /// 即 v_NED = q.Rotate(v_body)。避免欧拉角万向锁, 仅显示时转欧拉角。
    /// </summary>
    public struct Quat
    {
        public double W, X, Y, Z;

        public Quat(double w, double x, double y, double z) { W = w; X = x; Y = y; Z = z; }

        public static readonly Quat Identity = new Quat(1, 0, 0, 0);

        /// <summary>Hamilton 四元数乘积 (this ⊗ b)。</summary>
        public static Quat operator *(Quat a, Quat b)
        {
            return new Quat(
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W);
        }

        public static Quat operator *(Quat a, double s)
        {
            return new Quat(a.W * s, a.X * s, a.Y * s, a.Z * s);
        }

        public static Quat operator *(double s, Quat a) { return a * s; }

        public static Quat operator +(Quat a, Quat b)
        {
            return new Quat(a.W + b.W, a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }

        public Quat Conjugate { get { return new Quat(W, -X, -Y, -Z); } }

        public double Norm { get { return System.Math.Sqrt(W * W + X * X + Y * Y + Z * Z); } }

        public Quat Normalized()
        {
            double n = Norm;
            if (n < 1e-300) return Identity;
            return new Quat(W / n, X / n, Y / n, Z / n);
        }

        /// <summary>用四元数旋转向量: v' = q v q*。</summary>
        public Vec3 Rotate(Vec3 v)
        {
            // 优化展开形式 (避免完整四元数乘法):
            // t = 2 * (q_vec × v); v' = v + w*t + (q_vec × t)
            Vec3 qv = new Vec3(X, Y, Z);
            Vec3 t = 2.0 * qv.Cross(v);
            return v + W * t + qv.Cross(t);
        }

        /// <summary>绕机体轴旋转增量 (右乘): 返回 this ⊗ delta。</summary>
        public Quat MultiplyBodyRate(Quat delta) { return this * delta; }

        /// <summary>从轴角构造 (轴为单位向量, 角为弧度)。</summary>
        public static Quat FromAxisAngle(Vec3 axis, double angleRad)
        {
            Vec3 a = axis.Normalized();
            double h = 0.5 * angleRad;
            double s = System.Math.Sin(h);
            return new Quat(System.Math.Cos(h), a.X * s, a.Y * s, a.Z * s);
        }

        /// <summary>
        /// 从欧拉角构造 (NED 3-2-1 序: 先偏航 psi, 再俯仰 theta, 再滚转 phi)。
        /// v_NED = R(psi,theta,phi) v_body。
        /// </summary>
        public static Quat FromEuler321(double rollRad, double pitchRad, double yawRad)
        {
            double cr = System.Math.Cos(rollRad * 0.5), sr = System.Math.Sin(rollRad * 0.5);
            double cp = System.Math.Cos(pitchRad * 0.5), sp = System.Math.Sin(pitchRad * 0.5);
            double cy = System.Math.Cos(yawRad * 0.5), sy = System.Math.Sin(yawRad * 0.5);

            return new Quat(
                cr * cp * cy + sr * sp * sy,
                sr * cp * cy - cr * sp * sy,
                cr * sp * cy + sr * cp * sy,
                cr * cp * sy - sr * sp * cy);
        }

        /// <summary>转欧拉角 (NED 3-2-1)。返回 [roll, pitch, yaw] (rad)。</summary>
        public double[] ToEuler321()
        {
            // 标准 ZYX (yaw-pitch-roll) 提取, 与 FromEuler321 互逆
            double sinPitch = 2.0 * (W * Y - Z * X);
            sinPitch = Mathd.Clamp(sinPitch, -1.0, 1.0);
            double pitch = System.Math.Asin(sinPitch);
            double roll = System.Math.Atan2(2.0 * (W * X + Y * Z), 1.0 - 2.0 * (X * X + Y * Y));
            double yaw = System.Math.Atan2(2.0 * (W * Z + X * Y), 1.0 - 2.0 * (Y * Y + Z * Z));
            return new double[] { roll, pitch, yaw };
        }
    }
}
