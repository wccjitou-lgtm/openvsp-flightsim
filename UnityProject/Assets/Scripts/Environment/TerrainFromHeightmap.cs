using System.IO;
using FlightDynamics;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 高度图 → Unity Terrain 运行时生成 (Phase 4, 开发提示词 5.7 节; 森林场景 S2 扩展)。
    /// 读取 StreamingAssets 下的 16-bit RAW (与核心库/C# HeightmapTerrain 同格式,
    /// 由 Tools/terrain_tools.py 或 Tools/terrain_forest.py 生成), 构建 Terrain + TerrainCollider。
    ///
    /// Unity 轴对齐 (与 AircraftController 的 NED→Unity 映射一致):
    ///   Unity +z = 北 (行索引减小方向), +x = 东 (列索引增大方向),
    ///   地形中心位于 NED 原点 → Unity 世界原点 (0,0,0)。
    ///
    /// 森林场景 (MetaFileName 非空): 尺寸/高度比例/机场台地高度/河距图均从 meta.json 读取;
    /// 地形整体下移 AirportHeightM, 使机场台地面 = Unity y 0 (跑道面标高 0 的既有约定);
    /// 物理地形 (核心库 HeightmapTerrain + 同样的垂直偏移) 登记到 TerrainRegistry 并注入已有飞机。
    /// </summary>
    [RequireComponent(typeof(TerrainCollider))]
    public class TerrainFromHeightmap : MonoBehaviour
    {
        [Header("高度图 (StreamingAssets 下)")]
        public string RawFileName = "Terrain/mountain.raw";
        [Tooltip("非空: 从该 meta.json 读取 size/world/height_scale/airport_height/河距图 (森林场景)")]
        public string MetaFileName = "";
        public int SizePx = 513;
        public float WorldSizeM = 2000f;
        public float HeightScaleM = 536.9f;
        [Tooltip("机场台地高度 m: 地形整体下移该值, 台地面 = Unity y 0 (0 = 不偏移)")]
        public float AirportHeightM = 0f;

        [Tooltip("图边缘常数高度 m (meta.edge_height_m); >0 时在图外铺同高度远景地面, 避免看到世界边缘")]
        public float EdgeHeightM = 0f;

        [Header("渲染")]
        public bool GenerateSplatPrototype = true;
        [Tooltip("森林 5 层混合的 alphamap 分辨率 (2 的幂)")]
        public int ForestAlphamapRes = 1024;

        [System.Serializable]
        private class MetaJson
        {
            public int size_px;
            public float world_size_m;
            public float height_scale_m;
            public float airport_height_m;
            public float edge_height_m;
            public string riverdist_u8;
            public float riverdist_step_m;
            public string biome;              // "island" 等; 缺省 = 森林/旧行为
            public float sea_level_m;         // RAW 高度基准下的海面高度; 0 = 无海 (海面必为正)
            public float[] far_ground_rgb;    // 图外远景地面颜色; 缺省 = 森林草绿
            public float platform_msl_m;      // 机场台地真实海拔 m (高原场景); 0 = 海平面
        }

        /// <summary>机场台地真实海拔 m (meta.platform_msl_m; 雪谷 2000), 0 = 海平面。</summary>
        public float PlatformMslM { get; private set; }

        /// <summary>地形有海 (meta.sea_level_m &gt; 0)。</summary>
        public bool HasSea { get; private set; }
        /// <summary>海面的 Unity 世界 y (已扣除机场台地下移量); 仅 HasSea 时有意义。</summary>
        public float SeaLevelY { get; private set; }
        private string _biome = "";
        private Color _farGroundColor = new Color(0.25f, 0.36f, 0.19f);

        /// <summary>本组件构建出的核心库物理地形 (含偏移, 海床高度), 供测试比对。</summary>
        public ITerrainModel PhysicsTerrain { get; private set; }

        /// <summary>登记到 TerrainRegistry 的地形: 有海时是 PhysicsTerrain 外包"水面即地板", 否则就是 PhysicsTerrain。</summary>
        public ITerrainModel RegisteredTerrain { get; private set; }

        private byte[] _riverDist;      // uint8 河距图 (森林场景), 每级 RiverStepM
        private float _riverStepM = 2f;
        private float _seaLevelRaw;
        private readonly System.Collections.Generic.List<Material> _ownedMats = new System.Collections.Generic.List<Material>();

        private void Start()
        {
            LoadTerrain();
        }

        /// <summary>读取 RAW 并构建 TerrainData。可在编辑器/运行时调用。</summary>
        public TerrainData LoadTerrain()
        {
            if (!string.IsNullOrEmpty(MetaFileName) && !LoadMeta()) return null;

            string path = Path.Combine(Application.streamingAssetsPath, RawFileName);
            if (!File.Exists(path))
            {
                Debug.LogError("高度图不存在: " + path);
                return null;
            }

            byte[] raw = File.ReadAllBytes(path);
            var heights = new float[SizePx, SizePx];
            for (int r = 0; r < SizePx; r++)
                for (int c = 0; c < SizePx; c++)
                {
                    ushort v = (ushort)((raw[2 * (r * SizePx + c)] << 8) | raw[2 * (r * SizePx + c) + 1]);
                    // RAW 行序北->南; Unity heightmap 行 0 对应 local -z (南) -> 翻转
                    heights[SizePx - 1 - r, c] = v / 65535f;
                }

            var data = new TerrainData
            {
                heightmapResolution = SizePx,
                size = new Vector3(WorldSizeM, HeightScaleM, WorldSizeM),
            };
            data.SetHeights(0, 0, heights);

            if (GenerateSplatPrototype)
            {
                // 贴层必须用磁盘资产 (Resources/Terrain/*.terrainlayer) —— TerrainLayer
                // 无法以内存实例持久化, 场景保存后引用会丢失 → 地形渲染成洋红错误色。
                if (_biome == "island") BuildIslandSplat(data, heights);
                else if (_biome == "alpine") BuildAlpineSplat(data, heights);
                else if (_riverDist != null) BuildForestSplat(data, heights);
                else BuildAltitudeSplat(data, heights);
            }

            var terrain = GetComponent<Terrain>();
            var collider = GetComponent<TerrainCollider>();
            if (terrain == null) terrain = gameObject.AddComponent<Terrain>();
            if (collider == null) collider = gameObject.AddComponent<TerrainCollider>();

#if UNITY_EDITOR
            // TerrainData 必须持久化为磁盘资产 —— 场景内嵌序列化会丢失内部渲染
            // 状态 (高度/贴层 API 读着正常, 渲染却是洋红错误色)。运行时(Play)
            // 内存实例有效, 无需资产化。
            if (!Application.isPlaying)
            {
                string assetName = System.IO.Path.GetFileNameWithoutExtension(RawFileName);
                string assetPath = "Assets/Terrain/" + assetName + "_TerrainData.asset";
                if (UnityEditor.AssetDatabase.LoadAssetAtPath<TerrainData>(assetPath) != null)
                    UnityEditor.AssetDatabase.DeleteAsset(assetPath);   // 重建
                System.IO.Directory.CreateDirectory("Assets/Terrain");
                UnityEditor.AssetDatabase.CreateAsset(data, assetPath);
                UnityEditor.AssetDatabase.SaveAssets();
                Debug.Log("[TerrainFromHeightmap] TerrainData 已资产化: " + assetPath);
            }
#endif

            terrain.terrainData = data;
            collider.terrainData = data;

            // 渲染升级 R3: 管线相关地形材质 (HDRP=TerrainLit)。Built-in 下返回 null,
            // 用引擎默认地形材质, 保持备用管线在三种管线下都不粉红。
            var terrainMat = RenderMaterials.CreateTerrainLit();
            if (terrainMat != null) { terrain.materialTemplate = terrainMat; _ownedMats.Add(terrainMat); }
            ForceMatteTerrainSmoothness(terrain);

            // 中心对准世界原点 (Terrain 默认从自身位置展开 size); 垂直下移机场台地高度
            transform.position = new Vector3(-WorldSizeM / 2f, -AirportHeightM, -WorldSizeM / 2f);

            if (EdgeHeightM > 0f) BuildFarGround();
            InjectPhysicsTerrain(path);
            return data;
        }

        /// <summary>
        /// 把每贴层的光滑度相关 uniforms 显式写进地形 splat property block (2026-10-11 打包版镜面地面修复)。
        /// HDRP TerrainLit 的光滑度有两条采样路径: 有 mask 变体时 mask.A × remapScale.w + remapOffset.w;
        /// 变体被裁剪的打包版退回 漫反射alpha × _SmoothnessN。两条路径的值都从贴层资产显式写死,
        /// 不依赖引擎对 TerrainLayer 的隐式同步 —— 编辑器里正常不代表打包版变体/同步一致。
        /// </summary>
        private static void ForceMatteTerrainSmoothness(Terrain terrain)
        {
            var layers = terrain.terrainData != null ? terrain.terrainData.terrainLayers : null;
            if (layers == null || layers.Length == 0) return;
            var block = new MaterialPropertyBlock();
            terrain.GetSplatMaterialPropertyBlock(block);
            for (int i = 0; i < layers.Length; i++)
            {
                var l = layers[i];
                if (l == null) continue;
                block.SetFloat("_Smoothness" + i, l.smoothness);
                block.SetFloat("_Metallic" + i, l.metallic);
                block.SetFloat("_LayerHasMask" + i, l.maskMapTexture != null ? 1f : 0f);
                block.SetVector("_MaskMapRemapOffset" + i, l.maskMapRemapMin);
                block.SetVector("_MaskMapRemapScale" + i, l.maskMapRemapMax - l.maskMapRemapMin);
            }
            terrain.SetSplatMaterialPropertyBlock(block);
        }

        /// <summary>
        /// 远景地面: 图边缘已渐变为常数高度 EdgeHeightM, 图外铺四条同高度条带 (北/南/东/西各外延 6 km)
        /// 接住, 视线越过地形边缘时看到的是连续草地 + 雾, 不是"世界边缘悬崖"。
        /// 必须是图外的条带而不是整块平面: 整块平面会盖住图内所有低于边缘高度的河谷/河床。
        /// 条带向图内重叠 2 m 并低 2 cm (藏在边缘地表下, 免共面闪烁/缝隙); 无碰撞体 (飞行区在图内)。
        /// 已知局限: 河流在南北边缘穿出图时河床低于边缘高度, 条带处会有一道台阶 (S3 河流收口时处理)。
        /// </summary>
        private void BuildFarGround()
        {
            var old = transform.Find("FarGround");
            if (old != null) { if (Application.isPlaying) Destroy(old.gameObject); else DestroyImmediate(old.gameObject); }
            var root = new GameObject("FarGround");
            root.transform.SetParent(transform, false);

            float half = WorldSizeM / 2f, ext = 6000f, lap = 2f;
            float y = EdgeHeightM - AirportHeightM - 0.02f;
            var mat = RenderMaterials.Create(_farGroundColor, 0.1f);
            _ownedMats.Add(mat);
            // (中心 x, 中心 z, 尺寸 x, 尺寸 z) —— 世界坐标; 地形中心在世界原点
            var strips = new[]
            {
                new Vector4(0f,  half + ext / 2f - lap, WorldSizeM + 2f * ext, ext + lap),   // 北
                new Vector4(0f, -half - ext / 2f + lap, WorldSizeM + 2f * ext, ext + lap),   // 南
                new Vector4( half + ext / 2f - lap, 0f, ext + lap, WorldSizeM),              // 东
                new Vector4(-half - ext / 2f + lap, 0f, ext + lap, WorldSizeM),              // 西
            };
            string[] names = { "FarN", "FarS", "FarE", "FarW" };
            for (int i = 0; i < 4; i++)
            {
                var g = GameObject.CreatePrimitive(PrimitiveType.Plane);
                g.name = names[i];
                var col = g.GetComponent<Collider>();
                if (col != null) { if (Application.isPlaying) Destroy(col); else DestroyImmediate(col); }
                g.transform.SetParent(root.transform, false);
                g.transform.localScale = new Vector3(strips[i].z / 10f, 1f, strips[i].w / 10f);   // Plane 默认 10m
                g.transform.position = new Vector3(strips[i].x, y, strips[i].y);
                g.GetComponent<Renderer>().sharedMaterial = mat;
            }
        }

        private bool LoadMeta()
        {
            string mp = Path.Combine(Application.streamingAssetsPath, MetaFileName);
            if (!File.Exists(mp)) { Debug.LogError("地形 meta 不存在: " + mp); return false; }
            var m = JsonUtility.FromJson<MetaJson>(File.ReadAllText(mp));
            SizePx = m.size_px;
            WorldSizeM = m.world_size_m;
            HeightScaleM = m.height_scale_m;
            AirportHeightM = m.airport_height_m;
            EdgeHeightM = m.edge_height_m;
            _riverStepM = m.riverdist_step_m > 0f ? m.riverdist_step_m : 2f;
            _biome = m.biome ?? "";
            PlatformMslM = m.platform_msl_m;
            HasSea = m.sea_level_m > 0f;
            SeaLevelY = HasSea ? m.sea_level_m - AirportHeightM : 0f;
            _seaLevelRaw = m.sea_level_m;
            if (m.far_ground_rgb != null && m.far_ground_rgb.Length == 3)
                _farGroundColor = new Color(m.far_ground_rgb[0], m.far_ground_rgb[1], m.far_ground_rgb[2]);
            _riverDist = null;
            if (!string.IsNullOrEmpty(m.riverdist_u8))
            {
                string rp = Path.Combine(Path.GetDirectoryName(mp), m.riverdist_u8);
                if (File.Exists(rp))
                {
                    var b = File.ReadAllBytes(rp);
                    if (b.Length == SizePx * SizePx) _riverDist = b;
                    else Debug.LogWarning("[TerrainFromHeightmap] 河距图尺寸不符, 退回按海拔混合");
                }
            }
            return true;
        }

        /// <summary>旧版 3 层: 草地 → 岩石 → 雪线, 按归一化海拔 SmoothStep 混合 (备用山地)。</summary>
        private void BuildAltitudeSplat(TerrainData data, float[,] heights)
        {
            var grass = LoadLayer("Grass", new Color32(70, 95, 55, 255));
            var rock = LoadLayer("Rock", new Color32(110, 100, 88, 255));
            var snow = LoadLayer("Snow", new Color32(225, 228, 232, 255));
            data.terrainLayers = new[] { grass, rock, snow };

            data.alphamapResolution = SizePx;
            var map = new float[SizePx, SizePx, 3];
            for (int r = 0; r < SizePx; r++)
                for (int c = 0; c < SizePx; c++)
                {
                    float h = heights[r, c];
                    float rockW = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.35f, 0.55f, h));
                    float snowW = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.75f, 0.90f, h));
                    map[r, c, 0] = (1f - rockW) * (1f - snowW);  // 草地
                    map[r, c, 1] = rockW * (1f - snowW);         // 岩石
                    map[r, c, 2] = snowW;                        // 雪线
                }
            data.SetAlphamaps(0, 0, map);
        }

        /// <summary>
        /// 森林 5 层: 0 森林草地 1 泥土 2 岩石 3 河床卵石 4 沙/泥岸。权重来自坡度 (由高度图差分)、
        /// 到河中线距离 (河距图) 与低频斑块噪声; 逐点归一化。
        /// </summary>
        private void BuildForestSplat(TerrainData data, float[,] heights)
        {
            data.terrainLayers = new[]
            {
                LoadLayer("ForestGrass", new Color32(64, 92, 48, 255)),
                LoadLayer("ForestDirt", new Color32(104, 82, 58, 255)),
                LoadLayer("ForestRock", new Color32(112, 106, 98, 255)),
                LoadLayer("RiverPebble", new Color32(128, 122, 112, 255)),
                LoadLayer("RiverSand", new Color32(168, 150, 112, 255)),
            };

            int res = Mathf.Max(16, ForestAlphamapRes);
            data.alphamapResolution = res;
            var map = new float[res, res, 5];
            float cell = WorldSizeM / (SizePx - 1);
            float vScale = HeightScaleM;
            for (int a = 0; a < res; a++)
            {
                // alphamap 行 a 对应 Unity z (南→北, 与 heights 行同向)
                float fr = a * (SizePx - 1) / (float)(res - 1);
                int r = Mathf.Clamp(Mathf.RoundToInt(fr), 0, SizePx - 1);
                for (int b = 0; b < res; b++)
                {
                    int c = Mathf.Clamp(Mathf.RoundToInt(b * (SizePx - 1) / (float)(res - 1)), 0, SizePx - 1);
                    int r0 = Mathf.Max(r - 1, 0), r1 = Mathf.Min(r + 1, SizePx - 1);
                    int c0 = Mathf.Max(c - 1, 0), c1 = Mathf.Min(c + 1, SizePx - 1);
                    float dzdx = (heights[r, c1] - heights[r, c0]) * vScale / ((c1 - c0) * cell);
                    float dzdz = (heights[r1, c] - heights[r0, c]) * vScale / ((r1 - r0) * cell);
                    float slopeDeg = Mathf.Atan(Mathf.Sqrt(dzdx * dzdx + dzdz * dzdz)) * Mathf.Rad2Deg;

                    // RAW/河距图行序是北→南, heights 已翻转为南→北: 河距图索引用翻转后的行
                    float d = _riverDist[(SizePx - 1 - r) * SizePx + c] * _riverStepM;

                    float rock = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(26f, 40f, slopeDeg));
                    float pebble = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(8f, 15f, d));
                    float sand = (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(14f, 36f, d))) * (1f - pebble);
                    float patch = Mathf.PerlinNoise(r * 0.011f, c * 0.011f);
                    float dirt = 0.55f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 0.72f, patch));

                    float wPebble = pebble;
                    float wSand = sand * (1f - wPebble);
                    float wRock = rock * (1f - wPebble) * (1f - wSand);
                    float rest = Mathf.Max(0f, 1f - wPebble - wSand - wRock);
                    float wDirt = rest * dirt;
                    float wGrass = rest - wDirt;
                    float sum = wGrass + wDirt + wRock + wPebble + wSand;
                    if (sum < 1e-5f) { wGrass = 1f; sum = 1f; }
                    map[a, b, 0] = wGrass / sum;
                    map[a, b, 1] = wDirt / sum;
                    map[a, b, 2] = wRock / sum;
                    map[a, b, 3] = wPebble / sum;
                    map[a, b, 4] = wSand / sum;
                }
            }
            data.SetAlphamaps(0, 0, map);
        }

        /// <summary>
        /// 海岛 4 层: 0 沙 (海面以下与沙滩带) 1 海岛草地 2 岩石 (陡坡/高处) 3 海蚀崖岩 (近岸陡坡)。
        /// 权重来自相对海面高度与坡度 (高度图差分), 逐点归一化。
        /// </summary>
        private void BuildIslandSplat(TerrainData data, float[,] heights)
        {
            data.terrainLayers = new[]
            {
                LoadLayer("IslandSand", new Color32(214, 196, 150, 255)),
                LoadLayer("IslandGrass", new Color32(88, 116, 62, 255)),
                LoadLayer("IslandRock", new Color32(120, 114, 104, 255)),
                LoadLayer("IslandCliff", new Color32(142, 128, 108, 255)),
            };

            int res = Mathf.Max(16, ForestAlphamapRes);
            data.alphamapResolution = res;
            var map = new float[res, res, 4];
            float cell = WorldSizeM / (SizePx - 1);
            float vScale = HeightScaleM;
            for (int a = 0; a < res; a++)
            {
                int r = Mathf.Clamp(Mathf.RoundToInt(a * (SizePx - 1) / (float)(res - 1)), 0, SizePx - 1);
                for (int b = 0; b < res; b++)
                {
                    int c = Mathf.Clamp(Mathf.RoundToInt(b * (SizePx - 1) / (float)(res - 1)), 0, SizePx - 1);
                    int r0 = Mathf.Max(r - 1, 0), r1 = Mathf.Min(r + 1, SizePx - 1);
                    int c0 = Mathf.Max(c - 1, 0), c1 = Mathf.Min(c + 1, SizePx - 1);
                    float dzdx = (heights[r, c1] - heights[r, c0]) * vScale / ((c1 - c0) * cell);
                    float dzdz = (heights[r1, c] - heights[r0, c]) * vScale / ((r1 - r0) * cell);
                    float slopeDeg = Mathf.Atan(Mathf.Sqrt(dzdx * dzdx + dzdz * dzdz)) * Mathf.Rad2Deg;
                    float rel = heights[r, c] * vScale - _seaLevelRaw;   // 相对海面高度 m

                    float sand = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.8f, 4.5f, rel));
                    float cliff = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(30f, 46f, slopeDeg))
                                  * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(40f, 70f, rel)));
                    float rock = Mathf.Max(Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(24f, 38f, slopeDeg)),
                                           Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(70f, 105f, rel)));

                    float wSand = sand;
                    float wCliff = cliff * (1f - wSand);
                    float wRock = rock * (1f - wSand) * (1f - wCliff);
                    float wGrass = Mathf.Max(0f, 1f - wSand - wCliff - wRock);
                    float sum = wSand + wGrass + wRock + wCliff;
                    if (sum < 1e-5f) { wSand = 1f; sum = 1f; }
                    map[a, b, 0] = wSand / sum;
                    map[a, b, 1] = wGrass / sum;
                    map[a, b, 2] = wRock / sum;
                    map[a, b, 3] = wCliff / sum;
                }
            }
            data.SetAlphamaps(0, 0, map);
        }

        /// <summary>
        /// 雪谷 5 层: 0 积雪 1 裸岩 2 碎石坡 3 枯草甸 4 冰碛土。权重来自相对台地高度 (rel)、坡度与低频斑块噪声:
        /// 缓坡积雪 (越高越满, 谷底留有枯草/冰碛斑块), 陡坡 (>~35°) 挂不住雪露出裸岩, 中陡坡带碎石。逐点归一化。
        /// </summary>
        private void BuildAlpineSplat(TerrainData data, float[,] heights)
        {
            data.terrainLayers = new[]
            {
                LoadLayer("AlpineSnow", new Color32(250, 252, 255, 255)),
                LoadLayer("AlpineRock", new Color32(92, 90, 88, 255)),
                LoadLayer("AlpineScree", new Color32(150, 146, 140, 255)),
                LoadLayer("AlpineMeadow", new Color32(128, 112, 74, 255)),
                LoadLayer("AlpineMoraine", new Color32(112, 100, 88, 255)),
            };

            int res = Mathf.Max(16, ForestAlphamapRes);
            data.alphamapResolution = res;
            var map = new float[res, res, 5];
            float cell = WorldSizeM / (SizePx - 1);
            float vScale = HeightScaleM;
            for (int a = 0; a < res; a++)
            {
                int r = Mathf.Clamp(Mathf.RoundToInt(a * (SizePx - 1) / (float)(res - 1)), 0, SizePx - 1);
                for (int b = 0; b < res; b++)
                {
                    int c = Mathf.Clamp(Mathf.RoundToInt(b * (SizePx - 1) / (float)(res - 1)), 0, SizePx - 1);
                    int r0 = Mathf.Max(r - 1, 0), r1 = Mathf.Min(r + 1, SizePx - 1);
                    int c0 = Mathf.Max(c - 1, 0), c1 = Mathf.Min(c + 1, SizePx - 1);
                    float dzdx = (heights[r, c1] - heights[r, c0]) * vScale / ((c1 - c0) * cell);
                    float dzdz = (heights[r1, c] - heights[r0, c]) * vScale / ((r1 - r0) * cell);
                    float slopeDeg = Mathf.Atan(Mathf.Sqrt(dzdx * dzdx + dzdz * dzdz)) * Mathf.Rad2Deg;
                    float rel = heights[r, c] * vScale - AirportHeightM;   // 相对台地高度 m

                    // 陡坡挂不住雪露出岩石: 门槛随低频噪声 ±8° 起伏, 所以中陡坡 (30~50°) 上雪与岩成斑块而不是一刀切
                    float rockNoise = Mathf.PerlinNoise(r * 0.009f + 51.3f, c * 0.009f + 12.7f);
                    float effSlope = slopeDeg + (rockNoise - 0.5f) * 16f;
                    float steep = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(38f, 54f, effSlope));
                    float wRock = steep;
                    float scree = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(24f, 38f, effSlope))
                                  * (1f - steep) * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(40f, 220f, rel));
                    float wScree = scree * (1f - wRock);
                    float patch = Mathf.PerlinNoise(r * 0.013f + 17.3f, c * 0.013f + 4.1f);
                    float lowland = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(25f, 150f, rel));
                    float wMeadow = 0.7f * lowland * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.70f, 0.82f, patch))
                                    * (1f - wRock) * (1f - wScree);
                    float patch2 = Mathf.PerlinNoise(r * 0.031f + 91.7f, c * 0.031f + 33.9f);
                    float wMoraine = 0.6f * lowland * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.76f, 0.88f, patch2))
                                     * (1f - wRock) * (1f - wScree) * (1f - wMeadow);
                    float wSnow = Mathf.Max(0f, 1f - wRock - wScree - wMeadow - wMoraine);
                    float sum = wSnow + wRock + wScree + wMeadow + wMoraine;
                    if (sum < 1e-5f) { wSnow = 1f; sum = 1f; }
                    map[a, b, 0] = wSnow / sum;
                    map[a, b, 1] = wRock / sum;
                    map[a, b, 2] = wScree / sum;
                    map[a, b, 3] = wMeadow / sum;
                    map[a, b, 4] = wMoraine / sum;
                }
            }
            data.SetAlphamaps(0, 0, map);
        }

        /// <summary>
        /// 用同一份 RAW 构建核心库 HeightmapTerrain (物理碰撞高度源, 含与渲染一致的垂直偏移),
        /// 登记到 TerrainRegistry (后建的飞机、工坊换机自动继承) 并注入场景中已有飞机的起落架 ——
        /// 保证物理地形与视觉地形一致 (缺失时物理高度恒为 0, 飞机会无视视觉山体直接穿山)。
        /// </summary>
        private void InjectPhysicsTerrain(string rawPath)
        {
            var core = HeightmapTerrain.FromFile(rawPath, SizePx, WorldSizeM, HeightScaleM);
            PhysicsTerrain = AirportHeightM != 0f ? (ITerrainModel)new OffsetTerrain(core, AirportHeightM) : core;
            RegisteredTerrain = HasSea ? new WaterFloorTerrain(PhysicsTerrain, SeaLevelY) : PhysicsTerrain;
            TerrainRegistry.Set(RegisteredTerrain);

            int injected = 0;
            foreach (var ac in FindObjectsOfType<AircraftController>())
            {
                if (ac.Plane != null && ac.Plane.Gear != null)
                {
                    ac.Plane.Gear.Terrain = RegisteredTerrain;
                    injected++;
                }
            }
            Debug.Log(string.Format(
                "[TerrainFromHeightmap] 物理地形已注入 {0} 架飞机并登记 (核心库 HeightmapTerrain, {1}m×{1}m, 垂直偏移 {2}m)",
                injected, WorldSizeM, AirportHeightM));
        }

        private void OnDestroy()
        {
            if (RegisteredTerrain != null && ReferenceEquals(TerrainRegistry.Current, RegisteredTerrain))
                TerrainRegistry.Clear();

            // 运行时内存实例的 TerrainData (含 alphamap/高度图) 与本组件建的材质不随组件销毁,
            // 场景切换会逐次累积; 磁盘资产不碰。alphamap 贴图要显式销毁 (销毁 TerrainData 不会带走它们)
            foreach (var m in _ownedMats) if (m != null) Destroy(m);
            _ownedMats.Clear();
            var t = GetComponent<Terrain>();
            var data = t != null ? t.terrainData : null;
            if (data == null || !Application.isPlaying) return;
#if UNITY_EDITOR
            if (UnityEditor.AssetDatabase.Contains(data)) return;
#endif
            foreach (var tex in data.alphamapTextures) if (tex != null) Destroy(tex);
            Destroy(data);
        }

        /// <summary>加载磁盘贴层资产; 缺失时退回内存实例 (仅当次会话有效)。</summary>
        private static TerrainLayer LoadLayer(string name, Color32 fallbackColor)
        {
            var existing = Resources.Load<TerrainLayer>("Terrain/" + name);
            if (existing != null) return existing;
            Debug.LogWarning("[TerrainFromHeightmap] Resources/Terrain/" + name +
                             " 缺失, 使用内存贴层 (运行 TerrainAssetSetup.CreateLayerAssets 可固化)");
            return MakeFlatColorLayer(name, fallbackColor);
        }

        /// <summary>纯色 4x4 贴图 TerrainLayer (程序化, 不依赖外部美术资源)。</summary>
        private static TerrainLayer MakeFlatColorLayer(string name, Color32 color)
        {
            var tex = new Texture2D(4, 4);
            var cols = new Color32[16];
            for (int i = 0; i < 16; i++) cols[i] = color;
            tex.SetPixels32(cols); tex.Apply();
            return new TerrainLayer { name = name, diffuseTexture = tex, tileSize = new Vector2(16f, 16f) };
        }

        /// <summary>
        /// 对照验证: 用核心库 HeightmapTerrain 计算同一 NED 点的高度,
        /// 与 Unity Terrain 采样比对 (PlayMode 测试用)。
        /// </summary>
        public static double CoreHeightAt(string rawPath, int sizePx, double worldSize, double heightScale,
                                          double north, double east)
        {
            var t = HeightmapTerrain.FromFile(rawPath, sizePx, worldSize, heightScale);
            return t.HeightAt(north, east);
        }
    }
}
