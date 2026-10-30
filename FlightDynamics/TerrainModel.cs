using System;
using System.IO;
using FlightDynamics.Math;

namespace FlightDynamics
{
    /// <summary>
    /// 地形模型接口 (Phase 4, 开发提示词 5.7 节)。
    /// 坐标: NED 平面坐标 (north, east 米), 返回地形海拔高度 (米, 向上为正)。
    /// 地形上表面在 NED 中位于 z = -HeightAt(north, east)。
    /// </summary>
    public interface ITerrainModel
    {
        double HeightAt(double north, double east);
    }

    /// <summary>平坦地形 (z=0 平面, Phase 1-3 的默认行为)。</summary>
    public class FlatTerrain : ITerrainModel
    {
        public static readonly FlatTerrain Default = new FlatTerrain();
        public double HeightAt(double north, double east) { return 0.0; }
    }

    /// <summary>
    /// 高度图地形: 读取 Unity Terrain 标准 16-bit RAW (big-endian uint16,
    /// 行主序北→南), 双线性插值查询。地形中心对准 NED 原点, 边界外取最近
    /// 边界值 (避免悬崖)。法向按垂直近似 (坡度 &lt;30° 时误差可忽略)。
    ///
    /// 生成工具: Tools/terrain_tools.py (fBm 合成 或 SRTM HGT 真实 DEM)。
    /// </summary>
    public class HeightmapTerrain : ITerrainModel
    {
        private readonly ushort[] _heights;
        private readonly int _size;
        private readonly double _worldSize;
        private readonly double _heightScale;
        private readonly double _half;
        private readonly double _cell;   // 格距 (米)

        public int SizePx { get { return _size; } }
        public double WorldSizeM { get { return _worldSize; } }
        public double HeightScaleM { get { return _heightScale; } }

        /// <param name="raw16BigEndian">RAW 字节数组 (big-endian uint16 行主序)</param>
        /// <param name="sizePx">边长像素 (如 513)</param>
        /// <param name="worldSizeM">世界边长 (米)</param>
        /// <param name="heightScaleM">0..65535 映射到 0..heightScale 米</param>
        public HeightmapTerrain(byte[] raw16BigEndian, int sizePx, double worldSizeM, double heightScaleM)
        {
            if (raw16BigEndian == null || raw16BigEndian.Length < sizePx * sizePx * 2)
                throw new ArgumentException("RAW 数据不足");
            _size = sizePx;
            _worldSize = worldSizeM;
            _heightScale = heightScaleM;
            _half = worldSizeM / 2.0;
            _cell = worldSizeM / (sizePx - 1);
            _heights = new ushort[sizePx * sizePx];
            for (int i = 0; i < _heights.Length; i++)
                _heights[i] = (ushort)((raw16BigEndian[2 * i] << 8) | raw16BigEndian[2 * i + 1]);
        }

        public static HeightmapTerrain FromFile(string rawPath, int sizePx, double worldSizeM, double heightScaleM)
        {
            byte[] data = File.ReadAllBytes(rawPath);
            return new HeightmapTerrain(data, sizePx, worldSizeM, heightScaleM);
        }

        public double HeightAt(double north, double east)
        {
            // NED -> 像素坐标: 行 0 = 最北 (north = +half), 列 0 = 最西 (east = -half)
            double u = (_half - north) / _cell;   // 行
            double v = (east + _half) / _cell;    // 列

            // 边界 clamp
            if (u < 0) u = 0; if (u > _size - 1) u = _size - 1;
            if (v < 0) v = 0; if (v > _size - 1) v = _size - 1;

            int u0 = (int)u, v0 = (int)v;
            int u1 = System.Math.Min(u0 + 1, _size - 1);
            int v1 = System.Math.Min(v0 + 1, _size - 1);
            double fu = u - u0, fv = v - v0;

            double h00 = Norm(_heights[u0 * _size + v0]);
            double h10 = Norm(_heights[u1 * _size + v0]);
            double h01 = Norm(_heights[u0 * _size + v1]);
            double h11 = Norm(_heights[u1 * _size + v1]);

            double top = h00 + fu * (h10 - h00);
            double bot = h01 + fu * (h11 - h01);
            return (top + fv * (bot - top)) * _heightScale;
        }

        private static double Norm(ushort v) { return v / 65535.0; }
    }
}
