using UnityEngine;

namespace FlightSim
{
    /// <summary>一次环境构建产出的对象引用 (SceneSwitcher 等待就绪、测试断言用)。</summary>
    public sealed class EnvironmentHandles
    {
        public GameObject Root;
        public RunwayEnvironment Runway;
        public TerrainFromHeightmap Terrain;
        public ScatterEnvironment Scatter;
        public WildlifeEnvironment Wildlife;   // profile 不要动物时为 null
        public BirdFlock Birds;                // profile 不要飞鸟时为 null
        public IslandProps Props;              // profile 无附加物时为 null
        public SeaWater Sea;                   // profile 无海时为 null
    }

    /// <summary>
    /// 按 SceneProfile 建出随场景变化的全部对象, 统一挂在 "EnvironmentRoot" 下。
    /// 编辑期场景生成 (FlightSimSceneSetup) 与运行时切换 (SceneSwitcher) 共用本方法;
    /// 切换 = 销毁整个 Root 再 Build, 不需要给各环境组件逐个补 Clear。
    /// 子对象名沿用旧场景 (RunwayEnvironment/ForestTerrain/ScatterEnvironment/...),
    /// ModePilot 与快照工具按名字 Find 不受层级影响。
    /// 组件的 Start 在下一帧才跑 (内容在 Start 里建), 就绪判断见 SceneSwitcher。
    /// </summary>
    public static class EnvironmentBuilder
    {
        public const string RootName = "EnvironmentRoot";

        public static EnvironmentHandles Build(SceneProfile p)
        {
            var h = new EnvironmentHandles { Root = new GameObject(RootName) };

            h.Runway = Child(h.Root, "RunwayEnvironment").AddComponent<RunwayEnvironment>();
            h.Runway.BuildGroundPlane = false;   // 地面由 Terrain 提供 (机场台地 = y 0)
            h.Runway.TreeCount = p.RunwayTreeCount;

            if (!string.IsNullOrEmpty(p.TerrainRaw))
            {
                h.Terrain = Child(h.Root, p.TerrainObjectName).AddComponent<TerrainFromHeightmap>();
                h.Terrain.RawFileName = p.TerrainRaw;
                h.Terrain.MetaFileName = p.TerrainMeta;
            }

            if (p.Sea && h.Terrain != null)
            {
                h.Sea = Child(h.Root, "SeaWater").AddComponent<SeaWater>();
                h.Sea.Terrain = h.Terrain;
                h.Sea.WindSpeedMps = p.WindSpeedMps;
                h.Sea.WindFromDeg = p.WindFromDeg;
            }

            Rect airport = h.Runway.AirportBounds;

            h.Scatter = Child(h.Root, "ScatterEnvironment").AddComponent<ScatterEnvironment>();
            h.Scatter.AreaRadius = p.BoundaryRadius;
            h.Scatter.TreeCount = p.TreeCount;
            h.Scatter.RockCount = p.RockCount;
            h.Scatter.BushCount = p.BushCount;
            h.Scatter.PalmCount = p.PalmCount;
            h.Scatter.ConiferCount = p.ConiferCount;
            ApplyClearZone(airport, out h.Scatter.UseClearZone, out h.Scatter.ClearCenter, out h.Scatter.ClearHalfSize);
            if (p.ScatterAboveSeaOnly) h.Scatter.TerrainRef = h.Terrain;

            if (p.Props == "island")
            {
                h.Props = Child(h.Root, "IslandProps").AddComponent<IslandProps>();
                h.Props.Terrain = h.Terrain;
            }

            if (p.Wildlife)
            {
                h.Wildlife = Child(h.Root, "WildlifeEnvironment").AddComponent<WildlifeEnvironment>();
                h.Wildlife.AreaRadius = p.BoundaryRadius;
                ApplyClearZone(airport, out h.Wildlife.UseClearZone, out h.Wildlife.ClearCenter, out h.Wildlife.ClearHalfSize);
            }

            if (p.BirdFlocks > 0)
            {
                h.Birds = Child(h.Root, "BirdFlock").AddComponent<BirdFlock>();
                h.Birds.FlockCount = p.BirdFlocks;
                h.Birds.AreaRadius = p.BoundaryRadius;
            }
            return h;
        }

        /// <summary>净空 = 机场占地矩形 (跑道+滑行道+停机坪+建筑+两端缓冲), 同时覆盖跑道自种树带。</summary>
        private static void ApplyClearZone(Rect airport, out bool use, out Vector2 center, out Vector2 halfSize)
        {
            use = true;
            center = airport.center;
            halfSize = airport.size * 0.5f;
        }

        private static GameObject Child(GameObject root, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            return go;
        }
    }
}
