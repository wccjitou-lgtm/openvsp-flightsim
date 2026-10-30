using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 海面 (海岛 B2): 一个覆盖全图并延伸到地平线的水面, 高度 = 地形 meta 的海面 Unity y。
    /// HDRP 下优先用 HDRP 水系统 (WaterSurface, 经 FlightSim.HdrpWater 程序集注册的工厂创建 ——
    /// 运行时程序集保持零管线依赖); 水系统不可用 (管线资产未开 supportWater / 非 HDRP) 时退回
    /// 一块大平面 + 高光滑度蓝色材质, <see cref="UsingHdrpWater"/> 如实反映用了哪种。
    /// 只管渲染; 物理地板与触水判定见 WaterFloorTerrain / TerrainRegistry.IsWater。
    /// </summary>
    public class SeaWater : MonoBehaviour
    {
        /// <summary>HDRP 水面工厂: 返回已配置好的水面 GameObject (由 FlightSim.HdrpWater 注册); 未注册 = null。</summary>
        public static System.Func<SeaWater, GameObject> HdrpFactory;
        /// <summary>当前管线资产是否开了水系统 (由 FlightSim.HdrpWater 注册)。</summary>
        public static System.Func<bool> HdrpWaterEnabled;

        [Tooltip("海面 Unity 世界 y (TerrainFromHeightmap.SeaLevelY)")]
        public float SeaLevelY = -9f;
        [Tooltip("提供海面高度的地形; 非空时 Start 自动取其 SeaLevelY 并等地形就绪后再建")]
        public TerrainFromHeightmap Terrain;
        public bool BuildOnStart = true;

        [Header("海况")]
        [Tooltip("海面上方风速 m/s (决定浪高/波纹强度)")]
        public float WindSpeedMps = 7f;
        [Tooltip("风吹来的方位 (度, 0=北 90=东); 浪向 = 风向")]
        public float WindFromDeg = 90f;

        [Header("外观")]
        public Color ShallowColor = new Color(0.12f, 0.66f, 0.70f);
        public Color DeepColor = new Color(0.02f, 0.30f, 0.42f);
        [Tooltip("退回平面方案时的边长 m")]
        public float FallbackExtentM = 40000f;
        public bool PreferHdrpWater = true;

        public bool UsingHdrpWater { get; private set; }
        public GameObject Surface { get; private set; }

        private Material _fallbackMat;

        private void Start()
        {
            if (BuildOnStart) StartCoroutine(BuildWhenTerrainReady());
        }

        private System.Collections.IEnumerator BuildWhenTerrainReady()
        {
            if (Terrain != null)
            {
                float t = 0f;
                while (Terrain != null && Terrain.PhysicsTerrain == null && t < 60f) { yield return null; t += Time.unscaledDeltaTime; }
                if (Terrain == null) yield break;
                if (!Terrain.HasSea) { Debug.LogWarning("[SeaWater] 地形无海, 不建水面"); yield break; }
                SeaLevelY = Terrain.SeaLevelY;
            }
            Build();
        }

        /// <summary>建水面 (幂等: 先清空)。</summary>
        public void Build()
        {
            Clear();
            transform.position = new Vector3(0f, SeaLevelY, 0f);

            if (PreferHdrpWater && HdrpFactory != null && HdrpWaterEnabled != null && HdrpWaterEnabled())
            {
                Surface = HdrpFactory(this);
                UsingHdrpWater = Surface != null;
            }
            if (Surface == null) Surface = BuildFallbackPlane();
            Debug.Log("[SeaWater] 海面已建: y=" + SeaLevelY.ToString("F1") + " m, " +
                      (UsingHdrpWater ? "HDRP 水系统" : "退回平面方案"));
        }

        public void Clear()
        {
            if (Surface != null)
            {
                if (Application.isPlaying) Destroy(Surface); else DestroyImmediate(Surface);
                Surface = null;
            }
            UsingHdrpWater = false;
            if (_fallbackMat != null)
            {
                if (Application.isPlaying) Destroy(_fallbackMat); else DestroyImmediate(_fallbackMat);
                _fallbackMat = null;
            }
        }

        private void OnDestroy() { Clear(); }

        private GameObject BuildFallbackPlane()
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Plane);
            g.name = "SeaPlane";
            var col = g.GetComponent<Collider>();
            if (col != null) DestroyImmediate(col);   // 刚建的对象, 立即移除 (Destroy 到帧末才生效)
            g.transform.SetParent(transform, false);
            g.transform.localPosition = Vector3.zero;
            g.transform.localScale = new Vector3(FallbackExtentM / 10f, 1f, FallbackExtentM / 10f);   // Plane 默认 10 m
            _fallbackMat = RenderMaterials.Create(Color.Lerp(ShallowColor, DeepColor, 0.55f), 0.92f);
            g.GetComponent<Renderer>().sharedMaterial = _fallbackMat;
            return g;
        }
    }
}
