using FlightDynamics;

namespace FlightSim
{
    /// <summary>
    /// 给任意地形模型加垂直偏移 (森林场景 S2): 生成器的高度基准是"图内海拔"
    /// (机场台地 = 62 m), 而场景约定跑道面 = Unity y 0, 所以物理侧要减去台地高度。
    /// 新增类, 不改核心库 HeightmapTerrain 的行为。
    /// </summary>
    public sealed class OffsetTerrain : ITerrainModel
    {
        private readonly ITerrainModel _inner;
        private readonly double _offset;

        public OffsetTerrain(ITerrainModel inner, double offsetM)
        {
            _inner = inner;
            _offset = offsetM;
        }

        public double HeightAt(double north, double east)
        {
            return _inner.HeightAt(north, east) - _offset;
        }
    }

    /// <summary>
    /// 水面即地板 (海岛 B2): 高度 = max(海床, 海面)。飞机的起落架/机体触到水面就是"着地",
    /// 不会穿过海面落到海底; <see cref="IsWater"/> 判断该点是否被水覆盖 (海床低于海面)。
    /// 新增类, 不改核心库; 高度基准与 <see cref="OffsetTerrain"/> 一致 (跑道面 = 0)。
    /// </summary>
    public sealed class WaterFloorTerrain : ITerrainModel
    {
        public ITerrainModel Bed { get; private set; }
        public double SeaLevelY { get; private set; }

        public WaterFloorTerrain(ITerrainModel bed, double seaLevelY)
        {
            Bed = bed;
            SeaLevelY = seaLevelY;
        }

        public double HeightAt(double north, double east)
        {
            return System.Math.Max(Bed.HeightAt(north, east), SeaLevelY);
        }

        public bool IsWater(double north, double east)
        {
            return Bed.HeightAt(north, east) < SeaLevelY;
        }
    }

    /// <summary>
    /// 当前场景的物理地形 (静态登记)。TerrainFromHeightmap 加载后登记;
    /// AircraftController.Awake 读取, 所以工坊热替换新建的飞机也自带地形 (否则新机
    /// Gear.Terrain 默认平地, 会穿过山体); ModePilot 的净空高度也查这里。
    /// 未登记时返回平地, 与旧行为一致。
    /// </summary>
    public static class TerrainRegistry
    {
        private static ITerrainModel _current;

        public static ITerrainModel Current { get { return _current ?? FlatTerrain.Default; } }
        public static bool HasTerrain { get { return _current != null; } }

        public static void Set(ITerrainModel terrain) { _current = terrain; }
        public static void Clear() { _current = null; }

        /// <summary>NED (北, 东) 处是否被水覆盖。登记的地形没有水面 (森林/平地) 时恒 false。</summary>
        public static bool IsWater(double north, double east)
        {
            var w = _current as WaterFloorTerrain;
            return w != null && w.IsWater(north, east);
        }

        /// <summary>NED (北, 东) 处地形高度 (m, 与 AircraftState.Altitude 同基准)。</summary>
        public static double HeightAt(double north, double east)
        {
            return Current.HeightAt(north, east);
        }
    }
}
