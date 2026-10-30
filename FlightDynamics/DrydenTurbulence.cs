using System;
using FlightDynamics.Math;

namespace FlightDynamics
{
    /// <summary>
    /// Dryden 大气紊流模型 (MIL-F-8785C 军标低空部分; 开发提示词 5.4 节)。
    ///
    /// 三通道成形滤波器 (V = 名义空速, a = V/L):
    ///   纵向 (沿航向) 一阶:  H_u(s) = σu·√(2/(π·a)) / (1 + s/a)          [OU 过程]
    ///   侧向/垂向 二阶:      H(s)   = σ·√(1/a)·(1 + √3·s/a) / (1 + s/a)²
    ///
    /// 二阶通道状态空间 (白噪声 n, 单位强度):
    ///   x1' = x2;  x2' = -a²x1 - 2a·x2 + b·n;  输出 y = x1 + (√3/a)·x2
    ///   由 Lyapunov 方程 AP+PA'+BB'=0 解得 Var(y) = b²/a³ → b = σ·a^1.5
    ///   (σ 即通道目标标准差, 无经验校准系数)。
    ///
    /// MIL-F-8785C 低空参数 (SI, h 为几何高度):
    ///   σw  = 0.1·W20;  Lw = h
    ///   Lu = h/(0.177+0.000823·h_ft)^1.2;  σu = σv = σw/(0.177+0.000823·h_ft)^0.4
    ///   (W20 = 6.1m 高参考风速: 轻 7.7 / 中 15.4 / 强 23.1 m/s)
    ///
    /// 离散化: 一阶通道精确 OU 离散 (方差精确); 二阶通道 A_d 重根闭式矩阵指数,
    /// 噪声协方差 Q_d = b²∫e^{As}BB'e^{A's}ds 数值积分 + Cholesky 下三角注入。
    ///
    /// 输出: 三通道 (沿航向/侧向/垂向) 按当前航向旋转到 NED。
    /// 状态以绝对时间戳推进, 同时刻重复调用返回缓存 —— RK4 每步 4 次调用安全幂等。
    /// </summary>
    public class DrydenTurbulence : IWindModel
    {
        public enum TurbulenceLevel { Light = 0, Moderate = 1, Severe = 2 }

        private readonly double _airspeed;
        private readonly Func<double> _headingProvider;
        private readonly Random _rng;

        private double _u;                       // 一阶通道状态 (沿航向)
        private readonly double[] _v2 = new double[2];  // 侧向二阶
        private readonly double[] _w2 = new double[2];  // 垂向二阶

        private double _lastTime = double.NegativeInfinity;
        private Vec3 _cached = Vec3.Zero;
        private double _lastAltitude = -999;

        // 当前成形参数
        private double _aU, _aW;                // V/Lu, V/Lw
        private double _sigmaU, _sigmaW;
        private double[] _cholV, _cholW;         // 二阶噪声 Cholesky (b=1 基准)
        private double _noiseDtUsed = -1;        // Cholesky 对应的步长

        public DrydenTurbulence(double airspeed, TurbulenceLevel level, int seed = 42,
                                Func<double> headingProvider = null)
        {
            _airspeed = System.Math.Max(airspeed, 1.0);
            _headingProvider = headingProvider;
            _rng = new Random(seed);
            SetLevel(level);
            UpdateParameters(100.0);   // 初始默认高度 100m
        }

        /// <summary>参考风速 W20 (m/s)。可运行时修改以调整紊流强度。</summary>
        public double W20 { get; private set; }

        public void SetLevel(TurbulenceLevel level)
        {
            W20 = level switch
            {
                TurbulenceLevel.Light => 7.7,
                TurbulenceLevel.Moderate => 15.4,
                _ => 23.1,
            };
        }

        public Vec3 WindAt(Vec3 positionNed, double time)
        {
            double h = System.Math.Max(-positionNed.Z, 1.0);
            double dt = time - _lastTime;
            if (dt <= 1e-9) return _cached;

            if (System.Math.Abs(h - _lastAltitude) > 15.0)
            {
                UpdateParameters(h);
                _lastAltitude = h;
            }

            if (dt > 0.5) dt = 0.5;   // 初始化大步限幅

            // ---- 一阶 OU 通道 (沿航向) ----
            double phi = System.Math.Exp(-_aU * dt);
            _u = phi * _u + _sigmaU * System.Math.Sqrt(1 - phi * phi) * Gaussian();

            // ---- 二阶通道 (侧向/垂向) ----
            EnsureCholesky(dt);
            // MIL-F-8785C 低空: Lv = Lu (侧向与纵向同尺度), Lw = h (垂向单独)
            double v = StepSecondOrder(_v2, _sigmaU, _aU, _cholV, dt);
            double w = StepSecondOrder(_w2, _sigmaW, _aW, _cholW, dt);

            // ---- 旋转到 NED ----
            double heading = _headingProvider != null ? _headingProvider() : 0.0;
            double ch = System.Math.Cos(heading), sh = System.Math.Sin(heading);
            // 航向方向 (N,E) = (cosψ, sinψ); 侧向 = (-sinψ, cosψ)
            _cached = new Vec3(ch * _u - sh * v, sh * _u + ch * v, w);
            _lastTime = time;
            return _cached;
        }

        private void UpdateParameters(double h)
        {
            double oldSigmaU = _sigmaU, oldSigmaW = _sigmaW, oldAU = _aU, oldAW = _aW;

            double hFt = h / 0.3048;
            double denom = 0.177 + 0.000823 * hFt;
            _sigmaW = 0.1 * W20;
            _sigmaU = _sigmaW / System.Math.Pow(denom, 0.4);
            _aW = _airspeed / System.Math.Max(h, 1.0);
            _aU = _airspeed / (h / System.Math.Pow(denom, 1.2));
            _noiseDtUsed = -1;   // 强制重算 Cholesky

            // 换挡时状态必须随参数重新缩放: 二阶通道 x1 ~ σ/2, x2 ~ σ·a/2, 输出 y = x1 + (√3/a)·x2。
            // 只换 a 不缩放 x2, 则 a 骤降 (贴地→爬升, a=V/h 可差数十倍) 时 √3/a 把旧 x2 放大同样倍数,
            // 输出瞬间冲出 10~70 m/s 的假阵风 (起飞爬升每过 15 m 一次)。
            // 按"标准化状态不变"缩放: 输出等于旧输出乘 σ 比, 连续。首次调用旧参数为 0, 跳过。
            if (oldSigmaU > 0 && oldSigmaW > 0 && oldAU > 0 && oldAW > 0)
            {
                double ku = _sigmaU / oldSigmaU, kw = _sigmaW / oldSigmaW;
                _u *= ku;
                _v2[0] *= ku;
                _v2[1] *= ku * _aU / oldAU;
                _w2[0] *= kw;
                _w2[1] *= kw * _aW / oldAW;
            }
        }

        /// <summary>推进二阶通道, 返回输出 y = x1 + (√3/a)x2。噪声理论强度 b=σ·a^1.5。
        /// a 与 chol 必须与该通道的成形参数一致 (侧向 _aU/_cholV, 垂向 _aW/_cholW)。</summary>
        private double StepSecondOrder(double[] x, double sigma, double a, double[] chol, double dt)
        {
            double ex = System.Math.Exp(-a * dt);
            double ad11 = (1 + a * dt) * ex, ad12 = dt * ex;
            double ad21 = -a * a * dt * ex, ad22 = (1 - a * dt) * ex;

            double b = sigma * System.Math.Pow(a, 1.5);
            double n1 = Gaussian(), n2 = Gaussian();
            double w1 = b * (chol[0] * n1);
            double w2 = b * (chol[1] * n1 + chol[2] * n2);

            double x1 = ad11 * x[0] + ad12 * x[1] + w1;
            double x2 = ad21 * x[0] + ad22 * x[1] + w2;
            x[0] = x1; x[1] = x2;
            return x1 + System.Math.Sqrt(3.0) / a * x2;
        }

        private void EnsureCholesky(double dt)
        {
            if (System.Math.Abs(dt - _noiseDtUsed) < 1e-12) return;
            _cholV = CholQd(_aU, dt);   // 侧向: a = V/Lv = V/Lu
            _cholW = CholQd(_aW, dt);   // 垂向: a = V/Lw = V/h
            _noiseDtUsed = dt;
        }

        /// <summary>Q_d = ∫₀^Δ e^{As}BB'e^{A's}ds (B=(0,1)) 的 Cholesky 下三角。</summary>
        private static double[] CholQd(double a, double dt)
        {
            int steps = 128;
            double ds = dt / steps;
            double q11 = 0, q12 = 0, q22 = 0;
            for (int i = 0; i < steps; i++)
            {
                double s = (i + 0.5) * ds;
                double ex = System.Math.Exp(-a * s);
                double f1 = s * ex, f2 = (1 - a * s) * ex;
                q11 += f1 * f1 * ds; q12 += f1 * f2 * ds; q22 += f2 * f2 * ds;
            }
            double l11 = System.Math.Sqrt(System.Math.Max(q11, 1e-300));
            double l21 = q12 / l11;
            double l22 = System.Math.Sqrt(System.Math.Max(q22 - l21 * l21, 1e-300));
            return new double[] { l11, l21, l22 };
        }

        private double Gaussian()
        {
            double u1 = 1.0 - _rng.NextDouble();
            double u2 = _rng.NextDouble();
            return System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2.0 * System.Math.PI * u2);
        }
    }

    /// <summary>
    /// 离散阵风 (1-cosine, 开发提示词 5.5 节)。
    /// 提示词的半周期公式 V(t)=(V_max/2)(1-cos(πt/T)) 在 t=T 处达到峰值后回零
    /// 会产生突跳; 这里采用其对称全周期形式 (两个半周期拼接, 起点/终点都为 0):
    ///   V_gust(t) = (V_max/2)·(1 - cos(2π·t/T)),  0 ≤ t ≤ T, 峰值在 T/2。
    /// 方向为 NED 矢量 (自动归一化)。无状态, RK4 幂等安全。
    /// </summary>
    public class DiscreteGust : IWindModel
    {
        public readonly Vec3 Direction;
        public readonly double Amplitude;   // m/s
        public readonly double StartTime;
        public readonly double Duration;    // s

        public DiscreteGust(Vec3 directionNed, double amplitude, double startTime, double duration)
        {
            Direction = directionNed.Normalized();
            Amplitude = amplitude;
            StartTime = startTime;
            Duration = System.Math.Max(duration, 1e-3);
        }

        public Vec3 WindAt(Vec3 positionNed, double time)
        {
            double t = time - StartTime;
            if (t < 0 || t > Duration) return Vec3.Zero;
            return Direction * (0.5 * Amplitude * (1.0 - System.Math.Cos(2.0 * System.Math.PI * t / Duration)));
        }
    }
}
