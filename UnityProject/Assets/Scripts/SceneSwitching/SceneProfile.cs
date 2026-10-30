using System.Collections.Generic;
using FlightDynamics;

namespace FlightSim
{
    /// <summary>
    /// 飞行场地配置 (纯数据): 地形 + 机场 + 生态 + 默认风 + 边界。
    /// EnvironmentBuilder 按它建环境, SceneSwitcher 按它复位飞机/风/边界。
    /// 数值来源: 森林 = 原 FlightSimSceneSetup 的硬编码 (S2 定稿值), 行为不变。
    /// </summary>
    public sealed class SceneProfile
    {
        public string Id;
        public string DisplayName;
        /// <summary>false: 按钮灰显, SwitchTo 拒绝 (资产尚未做好)。</summary>
        public bool Ready;

        // ---- 地形 (StreamingAssets 相对路径) ----
        public string TerrainObjectName = "ForestTerrain";
        public string TerrainRaw = "";
        public string TerrainMeta = "";

        // ---- 高原 ----
        /// <summary>机场台地 (跑道面) 的真实海拔 m。切换时写入 AtmosphereModel.AltitudeOffsetM (密度/压强/温度按该海拔取); 0 = 海平面。</summary>
        public float PlatformMslM;

        // ---- 场地 ----
        public float BoundaryRadius = 1800f;
        /// <summary>飞机起点: 跑道入口内多少米 (中线上)。</summary>
        public float StartAlongRunwayM = 12f;

        // ---- 生态 ----
        public int TreeCount, RockCount, BushCount;
        /// <summary>棕榈 (海岛; 仅在有海的地形上生效)。</summary>
        public int PalmCount;
        /// <summary>针叶树 (雪谷): 只种在相对台地 -10~210 m 的缓坡上。</summary>
        public int ConiferCount;
        /// <summary>跑道西侧自带的带状树数量 (RunwayEnvironment.TreeCount, 默认 40 = 原行为)。</summary>
        public int RunwayTreeCount = 40;
        public bool Wildlife;
        public int BirdFlocks;
        /// <summary>撒布只在海面以上的陆地 (海岛)。</summary>
        public bool ScatterAboveSeaOnly;
        /// <summary>附加物: "" 无; "island" = 灯塔 + 码头。</summary>
        public string Props = "";

        // ---- 海与天空 ----
        /// <summary>建海面 (地形 meta 有海才生效): 水面渲染 + 物理水面地板由地形登记负责。</summary>
        public bool Sea;
        /// <summary>Resources 下的 VolumeProfile 路径 (如 "Rendering/IslandVolumeProfile"); 空 = 沿用场景自带的全局 Volume Profile。</summary>
        public string VolumeProfileResource = "";

        // ---- 太阳 (NaN = 沿用场景自带) ----
        /// <summary>太阳俯角/朝向 (度, Quaternion.Euler(pitch, yaw, 0): 光线沿 yaw 方向前进, 0 = 向北)。</summary>
        public float SunPitchDeg = float.NaN, SunYawDeg = float.NaN;
        /// <summary>太阳颜色; null = 沿用场景自带 (暖白)。</summary>
        public UnityEngine.Color? SunColor;

        // ---- 默认风 (切换时下发给 WindSettings.SetWeather) ----
        public float WindSpeedMps;
        public float WindFromDeg;
        public DrydenTurbulence.TurbulenceLevel Turbulence = DrydenTurbulence.TurbulenceLevel.Light;
        public bool EnableTurbulence = true;
    }

    /// <summary>已知场景表。海岛/雪谷在对应阶段做好前 Ready=false。</summary>
    public static class SceneRegistry
    {
        public static readonly SceneProfile Forest = new SceneProfile
        {
            Id = "forest",
            DisplayName = "森林",
            Ready = true,
            TerrainObjectName = "ForestTerrain",
            TerrainRaw = "Terrain/forest/forest.raw",
            TerrainMeta = "Terrain/forest/forest.meta.json",
            BoundaryRadius = 1800f,
            // 4 km 场地过渡撒布: 原 2 km 场地的 420/320/620 按面积比 (1800/1100)^2 ≈ 2.7 放大
            TreeCount = 1100, RockCount = 860, BushCount = 1660,
            Wildlife = true,
            BirdFlocks = 2,
            // 载入默认 0 级风况 (无风+紊流关, 2026-10-10 用户规则); 要风在 HUD 风况面板手动调
            EnableTurbulence = false,
        };

        public static readonly SceneProfile Island = new SceneProfile
        {
            Id = "island",
            DisplayName = "海岛",
            Ready = true,
            TerrainObjectName = "IslandTerrain",
            TerrainRaw = "Terrain/island/island.raw",
            TerrainMeta = "Terrain/island/island.meta.json",
            BoundaryRadius = 1800f,
            // 植被稀疏: 只有棕榈 (沙滩上缘) / 灌木 / 海岸岩, 不种阔叶树 (那是森林的特征)
            TreeCount = 0, RockCount = 160, BushCount = 320, PalmCount = 300,
            RunwayTreeCount = 0,
            ScatterAboveSeaOnly = true,
            Wildlife = false,
            BirdFlocks = 2,
            Props = "island",
            Sea = true,
            VolumeProfileResource = "Rendering/IslandVolumeProfile",
            // 海风: 来自东 (090°) = 对朝北跑道的侧风 (海在跑道东侧)
            WindSpeedMps = 6f, WindFromDeg = 90f,
            Turbulence = DrydenTurbulence.TurbulenceLevel.Light,
        };

        public static readonly SceneProfile Alpine = new SceneProfile
        {
            Id = "alpine",
            DisplayName = "雪谷",
            Ready = true,
            TerrainObjectName = "AlpineTerrain",
            TerrainRaw = "Terrain/alpine/alpine.raw",
            TerrainMeta = "Terrain/alpine/alpine.meta.json",
            BoundaryRadius = 1800f,
            PlatformMslM = 2000f,
            // 针叶林只在谷底和低坡 (树线以下); 谷壁上部只有岩石与雪。灌木/岩石沿用 (过渡), 岩石多些
            TreeCount = 0, RockCount = 220, BushCount = 0, ConiferCount = 1500,
            RunwayTreeCount = 0,
            Wildlife = false,
            BirdFlocks = 1,                                  // 少量猛禽
            VolumeProfileResource = "Rendering/AlpineVolumeProfile",
            // 太阳从南偏西、高度角 34°: 谷向南北, 光线沿谷前进, 两侧山体的影子斜向东西不会把整个谷底罩住
            SunPitchDeg = 34f, SunYawDeg = 18f,
            SunColor = new UnityEngine.Color(1.0f, 0.985f, 0.96f),   // 雪地上暖白会发黄奶油色, 用接近中性的白
            // 山谷风: 北风 8 m/s (对朝北的跑道是逆风; 想要"山风"紊流在 HUD 风况面板里加大风力等级)
            WindSpeedMps = 8f, WindFromDeg = 0f,
            Turbulence = DrydenTurbulence.TurbulenceLevel.Light,
        };

        public static readonly IReadOnlyList<SceneProfile> All = new[] { Forest, Island, Alpine };

        /// <summary>编辑期生成场景与首次启动用的默认场景。</summary>
        public static SceneProfile Default { get { return Forest; } }

        public static SceneProfile Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < All.Count; i++)
                if (All[i].Id == id) return All[i];
            return null;
        }
    }
}
