using System;

namespace FlightDynamics.Math
{
    public static class Mathd
    {
        public const double Deg2Rad = System.Math.PI / 180.0;
        public const double Rad2Deg = 180.0 / System.Math.PI;

        public static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }

    /// <summary>3x3 矩阵 (double), 主要用于惯量张量及其求逆。</summary>
    public struct Matrix3
    {
        // 行主序
        public double M00, M01, M02;
        public double M10, M11, M12;
        public double M20, M21, M22;

        public static Matrix3 FromDiagonal(double a, double b, double c)
        {
            var m = new Matrix3();
            m.M00 = a; m.M11 = b; m.M22 = c;
            return m;
        }

        /// <summary>对称惯量张量: [Ixx, Iyy, Izz] 对角 + [Ixy, Ixz, Iyz] 乘积项。</summary>
        public static Matrix3 FromInertia(double ixx, double iyy, double izz,
                                          double ixy, double ixz, double iyz)
        {
            var m = new Matrix3();
            m.M00 = ixx; m.M01 = -ixy; m.M02 = -ixz;
            m.M10 = -ixy; m.M11 = iyy; m.M12 = -iyz;
            m.M20 = -ixz; m.M21 = -iyz; m.M22 = izz;
            return m;
        }

        public Vec3 Multiply(Vec3 v)
        {
            return new Vec3(
                M00 * v.X + M01 * v.Y + M02 * v.Z,
                M10 * v.X + M11 * v.Y + M12 * v.Z,
                M20 * v.X + M21 * v.Y + M22 * v.Z);
        }

        public double Determinant()
        {
            return M00 * (M11 * M22 - M12 * M21)
                 - M01 * (M10 * M22 - M12 * M20)
                 + M02 * (M10 * M21 - M11 * M20);
        }

        /// <summary>解析法求逆。奇异矩阵抛 InvalidOperationException。</summary>
        public Matrix3 Inverse()
        {
            double det = Determinant();
            if (System.Math.Abs(det) < 1e-300)
                throw new InvalidOperationException("矩阵奇异, 无法求逆 (惯量张量退化?)");

            double invDet = 1.0 / det;
            var r = new Matrix3();
            r.M00 = (M11 * M22 - M12 * M21) * invDet;
            r.M01 = (M02 * M21 - M01 * M22) * invDet;
            r.M02 = (M01 * M12 - M02 * M11) * invDet;
            r.M10 = (M12 * M20 - M10 * M22) * invDet;
            r.M11 = (M00 * M22 - M02 * M20) * invDet;
            r.M12 = (M02 * M10 - M00 * M12) * invDet;
            r.M20 = (M10 * M21 - M11 * M20) * invDet;
            r.M21 = (M01 * M20 - M00 * M21) * invDet;
            r.M22 = (M00 * M11 - M01 * M10) * invDet;
            return r;
        }
    }
}
