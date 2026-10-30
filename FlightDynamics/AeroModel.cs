using System;
using FlightDynamics.Math;

namespace FlightDynamics
{
    /// <summary>
    /// 气动/质量/参考量数据 (来自 AircraftConfig.json, 见开发提示词 3.4 节)。
    /// 导数符号约定 (meta.signConventions, 由数据管线写入):
    ///   - 角度类导数 per rad; 角速度类导数按无量纲率 q̂=q·cref/(2V), p̂/r̂ 用 bref
    ///   - 系数为标准稳定性系数 (CL 抬升为正, Cm 抬头为正, Cl 右翼下沉为正, Cn 机头右偏为正)
    ///   - 舵面符号: 升降舵正偏 = 后缘向下 (标准约定, ΔCL>0, ΔCm<0 低头;
    ///     实际数据 CLde>0, Cmde<0, 以数据文件 meta.signConventions 为准)
    /// </summary>
    public class AeroData
    {
        // 参考量
        public double Sref = 1.0;       // m^2
        public double Bref = 1.0;       // m (展长)
        public double Cref = 1.0;       // m (平均气动弦长)

        // 纵向
        public double CL0 = 0, CLalpha = 0, CLq = 0;
        public double CD0 = 0, KInduced = 0;
        public double Cm0 = 0, Cmalpha = 0, Cmq = 0;

        // 横侧向
        public double CYbeta = 0, Clbeta = 0, Cnbeta = 0;
        public double CYp = 0, Clp = 0, Cnp = 0;
        public double CYr = 0, Clr = 0, Cnr = 0;

        // 操纵导数
        public double CLde = 0, Cmde = 0;             // 升降舵
        public double CYda = 0, Clda = 0, Cnda = 0;   // 副翼
        public double CYdr = 0, Cldr = 0, Cndr = 0;   // 方向舵
    }

    /// <summary>质量属性。惯量张量为标准机体轴 (x 前, y 右, z 下), 原点在重心。</summary>
    public class MassData
    {
        public double MassKg = 1.0;
        public double Ixx = 1, Iyy = 1, Izz = 1;
        public double Ixy = 0, Ixz = 0, Iyz = 0;

        public Matrix3 InertiaTensor()
        {
            return Matrix3.FromInertia(Ixx, Iyy, Izz, Ixy, Ixz, Iyz);
        }
    }

    /// <summary>简化推力模型参数 (开发提示词 4.5 节)。</summary>
    public class EngineData
    {
        public double MaxThrustN = 1.0;
        public double Vref = 60.0; // 推力衰减参考空速 m/s

        /// <summary>T = maxThrust * throttle * f(V), f(V) = 1 - clamp(V/Vref, 0, 0.5)。</summary>
        public double Thrust(double throttle, double airspeed)
        {
            double f = 1.0 - Mathd.Clamp(airspeed / Vref, 0.0, 0.5);
            return MaxThrustN * Mathd.Clamp(throttle, 0.0, 1.0) * f;
        }
    }

    /// <summary>一次气动计算的完整结果 (力/力矩/系数, 便于调试与遥测)。</summary>
    public struct AeroResult
    {
        public double Alpha, Beta, Airspeed, DynPressure;
        public double CL, CD, CY, Cl, Cm, Cn;
        public Vec3 ForceBody;    // N, 机体轴
        public Vec3 MomentBody;   // N*m, 机体轴 (关于重心)
    }

    /// <summary>
    /// 线性化稳定性导数气动模型 (开发提示词 4.2/4.3 节)。
    /// 仅小攻角/常规飞行状态有效 (见开发提示词 11 节范围限定)。
    /// </summary>
    public class AeroModel
    {
        public readonly AeroData Data;

        public AeroModel(AeroData data) { Data = data; }

        /// <summary>
        /// 计算气动力/力矩。
        /// </summary>
        /// <param name="vRelBody">相对气流速度在机体轴的投影 (m/s)</param>
        /// <param name="rates">机体角速度 (p,q,r) rad/s</param>
        /// <param name="rho">当地空气密度 kg/m^3</param>
        /// <param name="controls">舵面偏角 rad</param>
        public AeroResult Compute(Vec3 vRelBody, Vec3 rates, double rho, ControlInputs controls)
        {
            var r = new AeroResult();
            double V = vRelBody.Length;
            r.Airspeed = V;
            r.Alpha = System.Math.Atan2(vRelBody.Z, vRelBody.X);
            double betaSin = V > 1e-9 ? Mathd.Clamp(vRelBody.Y / V, -1.0, 1.0) : 0;
            r.Beta = System.Math.Asin(betaSin);

            // 数值稳健性处理 (§11 线性范围外的工程 clamp, 非失速模型):
            //  - 气动力用的有效攻角/侧滑限制在 ±25°, 防止极端姿态下线性外推爆炸
            //  - 升力系数限幅, 无量纲率限幅, 防止瞬时低速时的数值发散
            double alpha = Mathd.Clamp(r.Alpha, -0.4363, 0.4363);   // ±25°
            double beta = Mathd.Clamp(r.Beta, -0.4363, 0.4363);

            double vSafe = System.Math.Max(V, 3.0);  // 无量纲率用 3 m/s 下限, 防低速除零放大
            double qhat = Mathd.Clamp(rates.Y * Data.Cref / (2.0 * vSafe), -1.0, 1.0);
            double phat = Mathd.Clamp(rates.X * Data.Bref / (2.0 * vSafe), -1.0, 1.0);
            double rhat = Mathd.Clamp(rates.Z * Data.Bref / (2.0 * vSafe), -1.0, 1.0);

            // 系数 (4.3 节)
            r.CL = Mathd.Clamp(
                Data.CL0 + Data.CLalpha * alpha + Data.CLq * qhat + Data.CLde * controls.Elevator,
                -1.2, 1.5);
            r.CD = Data.CD0 + Data.KInduced * r.CL * r.CL;
            r.CY = Data.CYbeta * beta + Data.CYp * phat + Data.CYr * rhat
                 + Data.CYda * controls.Aileron + Data.CYdr * controls.Rudder;
            r.Cl = Data.Clbeta * beta + Data.Clp * phat + Data.Clr * rhat
                 + Data.Clda * controls.Aileron + Data.Cldr * controls.Rudder;
            r.Cm = Data.Cm0 + Data.Cmalpha * alpha + Data.Cmq * qhat + Data.Cmde * controls.Elevator;
            r.Cn = Data.Cnbeta * beta + Data.Cnp * phat + Data.Cnr * rhat
                 + Data.Cnda * controls.Aileron + Data.Cndr * controls.Rudder;

            r.DynPressure = 0.5 * rho * V * V;
            double qS = r.DynPressure * Data.Sref;

            // 风轴系到机体轴系的力变换 (推导见项目文档):
            //   v̂ = (cosα cosβ, sinβ, sinα cosβ)   相对气流方向
            //   n̂ = (sinα, 0, -cosα)               升力方向 (⊥ v̂, 体轴 z 负向为上)
            //   ŝ = (-sinβ cosα, cosβ, -sinα sinβ) 侧力方向 (≈ +y)
            // F = qS * ( -CD·v̂ + CY·ŝ + CL·n̂ )
            double ca = System.Math.Cos(alpha), sa = System.Math.Sin(alpha);
            double cb = System.Math.Cos(beta), sb = System.Math.Sin(beta);
            Vec3 vHat = new Vec3(ca * cb, sb, sa * cb);
            Vec3 nHat = new Vec3(sa, 0.0, -ca);
            Vec3 sHat = new Vec3(-sb * ca, cb, -sa * sb);

            r.ForceBody = qS * (-r.CD * vHat + r.CY * sHat + r.CL * nHat);
            r.MomentBody = qS * new Vec3(Data.Bref * r.Cl, Data.Cref * r.Cm, Data.Bref * r.Cn);
            return r;
        }
    }
}
