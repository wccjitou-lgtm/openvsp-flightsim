using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 海岛附加物 (B1): 东北崖上的灯塔 + 跑道南侧沙滩伸向海里的码头。位置来自生成器输出的
    /// coast.json (features.lighthouse / features.pier), 高度贴 TerrainRegistry 的物理地形。
    /// 全部为占位图元 (无碰撞体、无外部资源); 美术替换排在后续阶段。
    /// 运行时在 Start 里等地形登记后建; 编辑模式 (快照) 手动调 <see cref="Build"/>。
    /// </summary>
    public class IslandProps : MonoBehaviour
    {
        [Tooltip("StreamingAssets 下的 coast.json 相对路径")]
        public string CoastFileName = "Terrain/island/island.coast.json";
        [Tooltip("提供海面高度的地形 (码头甲板按海面定高)")]
        public TerrainFromHeightmap Terrain;
        public bool BuildOnStart = true;

        public const float LighthouseHeightM = 20f;
        public const float DeckAboveSeaM = 1.4f;

        [System.Serializable] private class Pt { public float north; public float east; }
        [System.Serializable] private class PierPt { public float north; public float east; public float length_m; public float width_m; }
        [System.Serializable] private class Features { public Pt lighthouse; public PierPt pier; }
        [System.Serializable] private class CoastJson { public Features features; }

        public Vector3 LighthouseBase { get; private set; }
        public Vector3 PierStart { get; private set; }
        public float PierLengthM { get; private set; }

        private readonly List<Material> _mats = new List<Material>();

        private void Start()
        {
            if (BuildOnStart) StartCoroutine(BuildWhenTerrainReady());
        }

        private IEnumerator BuildWhenTerrainReady()
        {
            float t = 0f;
            while (!TerrainRegistry.HasTerrain && t < 60f) { yield return null; t += Time.unscaledDeltaTime; }
            if (!TerrainRegistry.HasTerrain) { Debug.LogWarning("[IslandProps] 60 s 内地形未就绪, 跳过"); yield break; }
            Build();
        }

        /// <summary>建灯塔与码头 (幂等: 先清空)。</summary>
        public void Build()
        {
            Clear();
            string path = Path.Combine(Application.streamingAssetsPath, CoastFileName);
            if (!File.Exists(path)) { Debug.LogError("[IslandProps] coast.json 不存在: " + path); return; }
            var cj = JsonUtility.FromJson<CoastJson>(File.ReadAllText(path));
            if (cj == null || cj.features == null) { Debug.LogError("[IslandProps] coast.json 无 features"); return; }

            if (cj.features.lighthouse != null) BuildLighthouse(cj.features.lighthouse);
            if (cj.features.pier != null) BuildPier(cj.features.pier);
        }

        public void Clear()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var c = transform.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(c); else DestroyImmediate(c);
            }
            DestroyMats();
        }

        private void OnDestroy() { DestroyMats(); }

        private void DestroyMats()
        {
            foreach (var m in _mats)
            {
                if (m == null) continue;
                if (Application.isPlaying) Destroy(m); else DestroyImmediate(m);
            }
            _mats.Clear();
        }

        private Material Paint(Color c, float smooth = 0.3f)
        {
            var m = RenderMaterials.Create(c, smooth);
            _mats.Add(m);
            return m;
        }

        private GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 localPos,
                                Vector3 localScale, Material m)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) { if (Application.isPlaying) Destroy(col); else DestroyImmediate(col); }
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            go.GetComponent<Renderer>().sharedMaterial = m;
            return go;
        }

        private static float GroundY(float east, float north)
        {
            return (float)TerrainRegistry.HeightAt(north, east);
        }

        // ---- 灯塔: 红白条纹塔身 (4 节) + 平台 + 灯室 (自发光) + 顶盖 ----
        private void BuildLighthouse(Pt p)
        {
            float y = GroundY(p.east, p.north);
            LighthouseBase = new Vector3(p.east, y, p.north);
            var root = new GameObject("Lighthouse");
            root.transform.SetParent(transform, false);
            root.transform.position = LighthouseBase;

            var white = Paint(new Color(0.92f, 0.92f, 0.90f), 0.35f);
            var red = Paint(new Color(0.75f, 0.12f, 0.10f), 0.35f);
            var dark = Paint(new Color(0.18f, 0.18f, 0.20f), 0.4f);
            var lamp = RenderMaterials.CreateEmissive(new Color(1f, 0.92f, 0.6f), 6000f);
            _mats.Add(lamp);

            float seg = LighthouseHeightM / 4f;
            for (int i = 0; i < 4; i++)
            {
                float r = 3.2f - 0.35f * i;   // 向上收细 (Cylinder 直径 = scale.x)
                Prim(PrimitiveType.Cylinder, "Tower" + i, root.transform,
                     new Vector3(0f, seg * (i + 0.5f) - 1f, 0f),
                     new Vector3(r * 2f, seg * 0.5f + 0.5f, r * 2f), i % 2 == 0 ? white : red);
            }
            float top = LighthouseHeightM - 1f;
            Prim(PrimitiveType.Cylinder, "Gallery", root.transform, new Vector3(0f, top + 0.15f, 0f),
                 new Vector3(5.6f, 0.15f, 5.6f), dark);
            Prim(PrimitiveType.Cylinder, "LampRoom", root.transform, new Vector3(0f, top + 1.5f, 0f),
                 new Vector3(2.6f, 1.2f, 2.6f), lamp);
            Prim(PrimitiveType.Sphere, "Cap", root.transform, new Vector3(0f, top + 2.9f, 0f),
                 new Vector3(3.2f, 1.6f, 3.2f), red);
        }

        // ---- 码头: 甲板 + 每 10 m 一对桩, 从岸线向东(海)伸出 ----
        private void BuildPier(PierPt p)
        {
            float seaY = Terrain != null && Terrain.HasSea ? Terrain.SeaLevelY : GroundY(p.east, p.north);
            float deckY = seaY + DeckAboveSeaM;
            PierStart = new Vector3(p.east - 6f, deckY, p.north);   // 起点略缩进沙滩
            PierLengthM = p.length_m + 6f;
            var root = new GameObject("Pier");
            root.transform.SetParent(transform, false);
            root.transform.position = PierStart;

            var wood = Paint(new Color(0.42f, 0.30f, 0.18f), 0.15f);
            var post = Paint(new Color(0.30f, 0.22f, 0.14f), 0.1f);
            float w = Mathf.Max(p.width_m, 2f);

            Prim(PrimitiveType.Cube, "Deck", root.transform, new Vector3(PierLengthM * 0.5f, 0f, 0f),
                 new Vector3(PierLengthM, 0.25f, w), wood);
            for (float x = 0f; x <= PierLengthM + 0.01f; x += 10f)
            {
                foreach (float side in new[] { -1f, 1f })
                {
                    float gy = GroundY(PierStart.x + x, PierStart.z + side * (w * 0.5f - 0.2f));
                    float bottom = Mathf.Min(gy - 0.3f, deckY - 0.6f);   // 插进地面/海底 0.3 m
                    float top = deckY - 0.1f;
                    float h = Mathf.Max(top - bottom, 0.5f);
                    // Cylinder 高 2 m (scale.y = 半高)
                    Prim(PrimitiveType.Cylinder, "Post", root.transform,
                         new Vector3(x, (bottom + top) * 0.5f - deckY, side * (w * 0.5f - 0.2f)),
                         new Vector3(0.35f, h * 0.5f, 0.35f), post);
                }
            }
        }
    }
}
