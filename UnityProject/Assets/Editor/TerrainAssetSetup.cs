#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FlightSim.EditorTools
{
    /// <summary>
    /// 一次性生成地形贴层资产 (TerrainLayer 无法以内存实例持久化, 场景保存后
    /// 引用会丢失 → 地形渲染成洋红错误色)。生成到 Assets/Resources/Terrain/,
    /// TerrainFromHeightmap 运行时经 Resources.Load 引用。
    /// Unity.exe -batchmode -executeMethod FlightSim.EditorTools.TerrainAssetSetup.CreateLayerAssets -quit
    /// </summary>
    public static class TerrainAssetSetup
    {
        public static void CreateLayerAssets()
        {
            // dir 直接用类级同名字段, 不再局部重声明遮蔽
            Directory.CreateDirectory(dir);

            Create("Grass", new Color32(70, 95, 55, 255));
            Create("Rock", new Color32(110, 100, 88, 255));
            Create("Snow", new Color32(225, 228, 232, 255));

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[TerrainAssetSetup] 地形贴层资产已生成: " + dir);
        }

        /// <summary>
        /// 森林场景 5 层 (S2): 森林草地 / 泥土 / 岩石 / 河床卵石 / 沙泥岸。
        /// 每层 = 程序化着色噪声贴图 + 同源法线 (子资产内嵌, 与旧三层同样不落 PNG);
        /// 已存在则跳过 (幂等)。Unity.exe -batchmode -executeMethod
        /// FlightSim.EditorTools.TerrainAssetSetup.CreateForestLayerAssets -quit
        /// </summary>
        public static void CreateForestLayerAssets()
        {
            Directory.CreateDirectory(dir);
            //            名称           基色                       噪声频率 对比  种子 法线强度 平铺m 光滑度
            CreateTextured("ForestGrass", new Color32(64, 92, 48, 255),  6.0f, 0.16f, 311, 1.2f, 10f, 0.10f);
            CreateTextured("ForestDirt",  new Color32(104, 82, 58, 255), 8.0f, 0.12f, 312, 1.0f, 9f,  0.08f);
            CreateTextured("ForestRock",  new Color32(112, 106, 98, 255), 5.0f, 0.30f, 313, 2.2f, 8f,  0.18f);
            CreateTextured("RiverPebble", new Color32(128, 122, 112, 255), 14.0f, 0.28f, 314, 2.4f, 4f, 0.30f);
            CreateTextured("RiverSand",   new Color32(168, 150, 112, 255), 10.0f, 0.08f, 315, 0.6f, 6f, 0.12f);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[TerrainAssetSetup] 森林 5 层贴层资产已生成: " + dir);
        }

        /// <summary>
        /// 海岛 4 层 (B1): 沙 / 海岛草地 / 岩石 / 海蚀崖岩。同森林做法 (程序化贴图+法线+显式 mask), 幂等重建。
        /// </summary>
        public static void CreateIslandLayerAssets()
        {
            Directory.CreateDirectory(dir);
            //            名称           基色                          噪声频率 对比  种子 法线强度 平铺m 光滑度
            CreateTextured("IslandSand",  new Color32(214, 196, 150, 255), 12.0f, 0.10f, 321, 0.7f, 6f,  0.14f);
            CreateTextured("IslandGrass", new Color32(88, 116, 62, 255),    6.0f, 0.16f, 322, 1.2f, 10f, 0.10f);
            CreateTextured("IslandRock",  new Color32(120, 114, 104, 255),  5.0f, 0.30f, 323, 2.2f, 8f,  0.18f);
            CreateTextured("IslandCliff", new Color32(142, 128, 108, 255),  4.0f, 0.34f, 324, 2.6f, 12f, 0.16f);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[TerrainAssetSetup] 海岛 4 层贴层资产已生成: " + dir);
        }

        /// <summary>
        /// 雪谷 5 层 (C1/C2): 积雪 / 裸岩 / 碎石坡 / 枯草甸 / 冰碛土。同森林做法 (程序化贴图+法线+显式 mask), 幂等重建。
        /// 雪的噪声对比很低 (平整雪面), 光滑度 0.12 (HDRP TerrainLit 的光滑度来自 mask 的 A 通道)。
        /// </summary>
        public static void CreateAlpineLayerAssets()
        {
            Directory.CreateDirectory(dir);
            //            名称             基色                           噪声频率 对比  种子 法线强度 平铺m 光滑度
            CreateTextured("AlpineSnow",    new Color32(250, 252, 255, 255), 9.0f, 0.05f, 331, 0.5f, 8f,  0.12f);
            CreateTextured("AlpineRock",    new Color32(84, 86, 92, 255),    5.0f, 0.34f, 332, 2.6f, 10f, 0.16f);
            CreateTextured("AlpineScree",   new Color32(150, 152, 156, 255), 11.0f, 0.30f, 333, 2.4f, 6f, 0.14f);
            CreateTextured("AlpineMeadow",  new Color32(128, 112, 74, 255),  7.0f, 0.14f, 334, 1.0f, 9f,  0.08f);
            CreateTextured("AlpineMoraine", new Color32(112, 100, 88, 255),  8.0f, 0.20f, 335, 1.6f, 7f,  0.10f);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[TerrainAssetSetup] 雪谷 5 层贴层资产已生成: " + dir);
        }

        private static void CreateTextured(string name, Color32 tint, float freq, float contrast,
            int seed, float normalStrength, float tileM, float smoothness)
        {
            string path = dir + "/" + name + ".terrainlayer";
            // 森林层是纯生成物: 已存在则删除重建, 参数 (含 mask 图) 变更才会生效
            if (AssetDatabase.LoadAssetAtPath<TerrainLayer>(path) != null)
                AssetDatabase.DeleteAsset(path);
            const int size = 256;
            var gray = ProceduralTextures.NoiseGray(size, freq, contrast, seed);
            var normal = ProceduralTextures.NormalFromHeight(gray, normalStrength);
            normal.name = name + "Normal";

            // 灰度噪声均值 ~0.78: 除以 0.78 后乘基色, 使整体亮度≈基色
            var src = gray.GetPixels();
            var px = new Color[src.Length];
            Color t = tint;
            for (int i = 0; i < src.Length; i++)
            {
                float g = src[i].r / 0.78f;
                px[i] = new Color(Mathf.Clamp01(t.r * g), Mathf.Clamp01(t.g * g), Mathf.Clamp01(t.b * g), 1f);
            }
            var diffuse = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = name + "Tex" };
            diffuse.SetPixels(px);
            diffuse.Apply(true);
            Object.DestroyImmediate(gray);

            // HDRP TerrainLit 的光滑度来自 mask 图 (R 金属度 / G 遮蔽 / B 高度 / A 光滑度);
            // 不给 mask 时 layer.smoothness 滑条是否生效并不可靠 (截图里草地呈镜面水光),
            // 所以显式给 4x4 常量 mask (linear)。
            var mask = new Texture2D(4, 4, TextureFormat.RGBA32, false, true) { name = name + "Mask" };
            var mpx = new Color[16];
            for (int i = 0; i < 16; i++) mpx[i] = new Color(0f, 1f, 0.5f, smoothness);
            mask.SetPixels(mpx);
            mask.Apply(false);

            var layer = new TerrainLayer
            {
                name = name,
                diffuseTexture = diffuse,
                normalMapTexture = normal,
                maskMapTexture = mask,
                normalScale = 1f,
                tileSize = new Vector2(tileM, tileM),
                smoothness = smoothness,
            };
            AssetDatabase.CreateAsset(layer, path);
            AssetDatabase.AddObjectToAsset(diffuse, layer);
            AssetDatabase.AddObjectToAsset(normal, layer);
            AssetDatabase.AddObjectToAsset(mask, layer);
            AssetDatabase.SaveAssets();
            Debug.Log("[TerrainAssetSetup] 已生成 (含内嵌贴图+法线): " + path);
        }

        private static void Create(string name, Color32 color)
        {
            string path = dir + "/" + name + ".terrainlayer";
            if (AssetDatabase.LoadAssetAtPath<TerrainLayer>(path) != null)
            {
                Debug.Log("[TerrainAssetSetup] 已存在: " + name);
                return;
            }

            // 纹理直接作为 layer 资产的子对象嵌入 (不落 PNG, 免导入时序问题)
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = name + "Tex",
            };
            var cols = new Color32[4];
            for (int i = 0; i < 4; i++) cols[i] = color;
            tex.SetPixels32(cols);
            tex.Apply();

            var layer = new TerrainLayer
            {
                name = name,
                diffuseTexture = tex,
                tileSize = new Vector2(16f, 16f),
            };
            AssetDatabase.CreateAsset(layer, path);
            AssetDatabase.AddObjectToAsset(tex, layer);
            AssetDatabase.SaveAssets();
            Debug.Log("[TerrainAssetSetup] 已生成 (含内嵌纹理): " + path);
        }

        private const string dir = "Assets/Resources/Terrain";
    }
}
#endif
