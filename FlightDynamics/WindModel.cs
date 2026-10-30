using System;
using FlightDynamics.Math;

namespace FlightDynamics
{
    /// <summary>风模型接口: 返回指定位置/时刻的风矢量 (NED, m/s)。</summary>
    public interface IWindModel
    {
        Vec3 WindAt(Vec3 positionNed, double time);
    }

    /// <summary>无风。</summary>
    public class CalmWind : IWindModel
    {
        public Vec3 WindAt(Vec3 positionNed, double time) { return Vec3.Zero; }
    }

    /// <summary>恒定风 (稳定风, 开发提示词 5.2 节)。</summary>
    public class ConstantWind : IWindModel
    {
        public readonly Vec3 WindNed;
        public ConstantWind(Vec3 windNed) { WindNed = windNed; }
        public Vec3 WindAt(Vec3 positionNed, double time) { return WindNed; }
    }

    /// <summary>
    /// 复合风场: 多个风模型线性叠加 (稳定风 + 切变 + 紊流 + 阵风)。
    /// Phase 3 将补充 Dryden 紊流与 1-cosine 阵风等子模型。
    /// </summary>
    public class CompositeWind : IWindModel
    {
        private readonly System.Collections.Generic.List<IWindModel> _models =
            new System.Collections.Generic.List<IWindModel>();

        public CompositeWind Add(IWindModel model)
        {
            _models.Add(model);
            return this;
        }

        public Vec3 WindAt(Vec3 positionNed, double time)
        {
            Vec3 total = Vec3.Zero;
            for (int i = 0; i < _models.Count; i++)
                total = total + _models[i].WindAt(positionNed, time);
            return total;
        }
    }

    /// <summary>
    /// 强切变层 (风场测试场景, 开发提示词 5.7 节):
    /// 在 [bottomAlt, topAlt] 高度带内风速从 bottomWind 线性过渡到 topWind,
    /// 带外分别保持两端值 —— 产生可量化的强切变 (ΔV/Δh)。
    /// </summary>
    public class ShearLayerWind : IWindModel
    {
        private readonly Vec3 _bottomWind, _topWind;
        private readonly double _bottomAlt, _topAlt;

        public ShearLayerWind(Vec3 bottomWind, Vec3 topWind, double bottomAltM, double topAltM)
        {
            _bottomWind = bottomWind;
            _topWind = topWind;
            _bottomAlt = bottomAltM;
            _topAlt = System.Math.Max(topAltM, bottomAltM + 0.1);
        }

        public Vec3 WindAt(Vec3 positionNed, double time)
        {
            double h = -positionNed.Z;
            if (h <= _bottomAlt) return _bottomWind;
            if (h >= _topAlt) return _topWind;
            double t = (h - _bottomAlt) / (_topAlt - _bottomAlt);
            return _bottomWind + t * (_topWind - _bottomWind);
        }
    }

    /// <summary>
    /// 空间门控风 (风场测试场景): 只在矩形区域 (NED north/east 范围 + 高度带) 内
    /// 生效子风模型 (如强紊流区), 区域外无风 —— 用于测试穿越紊流区的瞬态响应。
    /// 边界按硬切换 (足够本文档用途; 平滑过渡可外接 ShearLayerWind 组合)。
    /// </summary>
    public class SpatialGateWind : IWindModel
    {
        private readonly IWindModel _inner;
        private readonly double _northMin, _northMax, _eastMin, _eastMax, _altMin, _altMax;

        public SpatialGateWind(IWindModel inner,
            double northMin, double northMax, double eastMin, double eastMax,
            double altMin = 0.0, double altMax = 1e9)
        {
            _inner = inner;
            _northMin = northMin; _northMax = northMax;
            _eastMin = eastMin; _eastMax = eastMax;
            _altMin = altMin; _altMax = altMax;
        }

        public Vec3 WindAt(Vec3 positionNed, double time)
        {
            double n = positionNed.X, e = positionNed.Y, h = -positionNed.Z;
            bool inside = n >= _northMin && n <= _northMax
                       && e >= _eastMin && e <= _eastMax
                       && h >= _altMin && h <= _altMax;
            return inside ? _inner.WindAt(positionNed, time) : Vec3.Zero;
        }
    }

    /// <summary>
    /// 幂律风切变 (开发提示词 5.3 节): V(h) = V_ref * (h/h_ref)^alpha。
    /// 方向与参考风一致, 仅模长随高度变化。
    /// </summary>
    public class PowerLawShearWind : IWindModel
    {
        private readonly Vec3 _refWind;
        private readonly double _refAltitude;
        private readonly double _exponent;

        /// <param name="refWind">参考高度处的风矢量 (NED)</param>
        /// <param name="refAltitudeM">参考高度 (m)</param>
        /// <param name="alphaExp">幂指数 (地面粗糙度 0.1~0.4)</param>
        public PowerLawShearWind(Vec3 refWind, double refAltitudeM, double alphaExp)
        {
            _refWind = refWind;
            _refAltitude = System.Math.Max(refAltitudeM, 1e-3);
            _exponent = alphaExp;
        }

        public Vec3 WindAt(Vec3 positionNed, double time)
        {
            double h = System.Math.Max(-positionNed.Z, 1e-3); // 高度不低于 1mm, 避免奇异
            double scale = System.Math.Pow(h / _refAltitude, _exponent);
            return _refWind * scale;
        }
    }
}
