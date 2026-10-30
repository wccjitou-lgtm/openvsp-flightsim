using System;

namespace FlightDynamics.Math
{
    /// <summary>三维向量 (double 精度, 飞行力学用, 与 UnityEngine.Vector3 无关)。</summary>
    public struct Vec3
    {
        public double X, Y, Z;

        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);

        public static Vec3 operator +(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vec3 operator -(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vec3 operator -(Vec3 a) { return new Vec3(-a.X, -a.Y, -a.Z); }
        public static Vec3 operator *(Vec3 a, double s) { return new Vec3(a.X * s, a.Y * s, a.Z * s); }
        public static Vec3 operator *(double s, Vec3 a) { return a * s; }
        public static Vec3 operator /(Vec3 a, double s) { return new Vec3(a.X / s, a.Y / s, a.Z / s); }

        public double Dot(Vec3 b) { return X * b.X + Y * b.Y + Z * b.Z; }

        public Vec3 Cross(Vec3 b)
        {
            return new Vec3(
                Y * b.Z - Z * b.Y,
                Z * b.X - X * b.Z,
                X * b.Y - Y * b.X);
        }

        public double LengthSq { get { return X * X + Y * Y + Z * Z; } }
        public double Length { get { return System.Math.Sqrt(LengthSq); } }

        public Vec3 Normalized()
        {
            double len = Length;
            if (len < 1e-300) return Zero;
            return this / len;
        }

        public override string ToString() { return string.Format("({0:F6}, {1:F6}, {2:F6})", X, Y, Z); }
    }
}
