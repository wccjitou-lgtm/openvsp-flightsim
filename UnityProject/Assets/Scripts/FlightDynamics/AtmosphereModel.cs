using System;

namespace FlightDynamics
{
    /// <summary>
    /// ISA 国际标准大气 (开发提示词 5.1 节)。
    /// 分层: 对流层 0~11 km (L=6.5 K/km), 平流层下段 11~20 km (等温 216.65 K)。
    /// </summary>
    public static class AtmosphereModel
    {
        public const double T0 = 288.15;        // 海平面温度 K
        public const double P0 = 101325.0;      // 海平面压强 Pa
        public const double Rho0 = 1.225;       // 海平面密度 kg/m^3
        public const double LapseRate = 0.0065; // 对流层温度递减率 K/m
        public const double Tropopause = 11000.0; // 对流层顶 m
        public const double T11 = T0 - LapseRate * Tropopause; // 216.65 K
        public const double GasConstant = 287.05287; // 空气气体常数 J/(kg*K)
        public const double G0 = 9.80665;       // 标准重力加速度 m/s^2

        /// <summary>
        /// 场地标高偏置 (m): 查询大气时用 altitudeM + AltitudeOffsetM。高原机场 (场景里跑道面 = 高度 0) 设成
        /// 机场海拔, 密度/压强/温度随之按 ISA 的该海拔取值。默认 0 = 海平面, 与旧行为逐位一致。
        /// 场景切换时设置, 离开该场景必须复位为 0 (静态状态)。
        /// </summary>
        public static double AltitudeOffsetM = 0.0;

        /// <summary>温度 (K)。</summary>
        public static double Temperature(double altitudeM)
        {
            altitudeM += AltitudeOffsetM;
            if (altitudeM < Tropopause)
                return T0 - LapseRate * altitudeM;
            return T11; // 20 km 以下近似等温
        }

        /// <summary>压强 (Pa)。</summary>
        public static double Pressure(double altitudeM)
        {
            altitudeM += AltitudeOffsetM;
            if (altitudeM < Tropopause)
            {
                double theta = (T0 - LapseRate * altitudeM) / T0;
                return P0 * System.Math.Pow(theta, G0 / (LapseRate * GasConstant));
            }
            // 平流层下段: 从对流层顶继续 (指数衰减)
            double p11 = P0 * System.Math.Pow(T11 / T0, G0 / (LapseRate * GasConstant));
            return p11 * System.Math.Exp(-G0 * (altitudeM - Tropopause) / (GasConstant * T11));
        }

        /// <summary>密度 (kg/m^3)。</summary>
        public static double Density(double altitudeM)
        {
            // Pressure/Temperature 内部各自加偏置, 这里传原始高度即可
            return Pressure(altitudeM) / (GasConstant * Temperature(altitudeM));
        }
    }
}
