using System.Collections.Generic;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 飞机视觉装配 (Phase 5 修复"看不到飞机"):
    /// 场景中的模型子对象曾出现 mesh 引用断裂 (PrefabInstance GUID 丢失,
    /// Renderer 存在但 sharedMesh 为空 → 什么都渲染不出来)。
    /// 本组件在 Start 时检测: 收集所有 sharedMesh 为空的断裂子对象并删除;
    /// 若飞机没有任何有效 mesh, 从 Resources 加载 TestUAV 模型实例化;
    /// 模型也缺失时退回程序化拼装的小飞机。保证任何场景下飞机始终可见。
    /// 涂装配色见 <see cref="ApplyLivery"/>。
    /// </summary>
    [RequireComponent(typeof(AircraftController))]
    public class AircraftVisual : MonoBehaviour
    {
        [Header("导入机型 (Phase 7)")]
        [Tooltip("运行时 OBJ 绝对路径 (VspImports/<name>/model.obj); 非空优先于 Resources")]
        public string RuntimeObjPath = "";

        [Header("工坊涂装 (U10)")]
        [Tooltip("部件名 (OBJ 安全化后) → 颜色; 由换机链写入, 空则回落默认涂装")]
        public Dictionary<string, Color> PartColors = new Dictionary<string, Color>();

        /// <summary>是否走了占位/装配兜底路径 (影响 ApplyLivery 的配色选择)。</summary>
        private bool _usedFallbackVisual;

        private void Start()
        {
            // 清除断裂的视觉子对象 (Renderer 在但 mesh 引用丢失)
            var filters = GetComponentsInChildren<MeshFilter>(true);
            for (int i = filters.Length - 1; i >= 0; i--)
            {
                if (filters[i].sharedMesh == null && filters[i].transform != transform)
                {
                    Debug.Log("[AircraftVisual] 删除断裂视觉子对象: " + filters[i].name);
                    Destroy(filters[i].gameObject);
                }
            }

            // 导入机型的运行时 OBJ 优先 (Phase 7; R5 起默认机也走此链路取分组网格)
            string objPath = RuntimeObjPath;
            // 打包版: 场景里烘焙的是编辑器绝对路径 (Assets/Models/...), 游戏目录下不存在
            // → 兜底 StreamingAssets/Models/TestUAV.obj (构建时自动随包)
            if (string.IsNullOrEmpty(objPath) || !System.IO.File.Exists(objPath))
            {
                string sa = System.IO.Path.Combine(Application.streamingAssetsPath, "Models", "TestUAV.obj");
                if (System.IO.File.Exists(sa)) objPath = sa;
            }
            if (!string.IsNullOrEmpty(objPath))
            {
                // 清掉场景占位视觉 (prefab 单网格副本等) —— 与 AircraftSwapper 全新 GO
                // 的语义对齐, 防止分组模型与占位模型叠出双机身
                foreach (var mf in GetComponentsInChildren<MeshFilter>(true))
                    if (mf.transform != transform)
                        Destroy(mf.gameObject);

                var imported = RuntimeObjLoader.BuildChild(transform, objPath);
                if (imported != null)
                {
                    NormalizeAndReport();
                    return;
                }
                Debug.LogWarning("[AircraftVisual] 运行时 OBJ 加载失败, 回退 Resources: "
                                 + objPath);
            }

            // 无有效 mesh → 从 Resources 装配
            if (GetComponentInChildren<MeshFilter>() == null ||
                System.Array.TrueForAll(GetComponentsInChildren<MeshFilter>(), f => f.sharedMesh == null))
            {
                var model = Resources.Load<GameObject>("TestUAV");
                if (model != null)
                {
                    var go = Instantiate(model, transform, false);
                    go.transform.localPosition = Vector3.zero;
                    go.transform.localRotation = Quaternion.identity;
                    Debug.Log("[AircraftVisual] 已从 Resources 装配 TestUAV 模型");
                }
                else
                {
                    BuildPlaceholder();
                    Debug.LogWarning("[AircraftVisual] Resources/TestUAV 缺失, 使用程序化占位飞机");
                }
                _usedFallbackVisual = true;
            }

            NormalizeAndReport();
        }

        /// <summary>
        /// 诊断 + 可见性: 打印视觉包围盒; 调用 <see cref="ApplyLivery"/> 上色。
        /// U17: 删除"小模型 (<1.5m) 视觉放大到 ~4m"的旧 hack — 它让物理 (1:1 轮子
        /// 接地) 与视觉 (×3.33) 不一致: 小型飞翼停在跑道上时, 放大后的机翼向下
        /// 超出地面 ~0.4m, 看起来埋进地里, 爬升到该高度才"从地底下飞出"。
        /// 小机型的取景改由 ChaseCamera 按目标尺寸自适应偏移解决。
        /// </summary>
        private void NormalizeAndReport()
        {
            var rends = GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) { Debug.LogWarning("[AircraftVisual] 无任何 Renderer"); return; }

            Bounds b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);
            Debug.Log(string.Format(
                "[AircraftVisual] 视觉包围盒 (世界): {0} × {1} × {2} m, 子 Renderer {3} 个",
                b.size.x.ToString("F2"), b.size.y.ToString("F2"), b.size.z.ToString("F2"), rends.Length));

            ApplyLivery(rends);
        }

        /// <summary>翼面/舵面类部件判定 (涂装强调色); 供 ApplyLivery 与编辑器探针共用。</summary>
        public static bool IsAccentRenderer(string rendererName)
        {
            string[] keys = { "Wing", "Tail", "Elevator", "Rudder", "Aileron", "Fin" };
            foreach (var k in keys)
                if (rendererName.IndexOf(k, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        /// <summary>机身漆 (R4 金属漆: 低金属度+中高光滑度)。</summary>
        public static Material CreateBodyPaint()
        {
            var m = RenderMaterials.Create(new Color(0.92f, 0.94f, 0.96f), 0.78f, 0.18f);
            m.name = "LiveryBody";
            return m;
        }

        /// <summary>强调漆 (军旗红, 同为金属漆质感)。</summary>
        public static Material CreateAccentPaint()
        {
            var m = RenderMaterials.Create(new Color(0.82f, 0.18f, 0.16f), 0.70f, 0.25f);
            m.name = "LiveryAccent";
            return m;
        }

        /// <summary>
        /// 涂装配色。两种路径:
        ///   - 占位飞机 / 从 Resources 装配的兜底路径: 高可见度纯橙 (异常兜底, 用户
        ///     看到橙色就知道该去查模型加载)。
        ///   - 正常加载的真实模型: 金属漆机身 (低金属度+中高光滑度; HDRP 14 Lit 无
        ///     ClearCoat 材质类型 —— 那是 StackLit 的能力, 如实用 Standard 型近似)
        ///     + 按部件名上色: R4 起导入管线输出分部件 OBJ (组名=几何名), 翼面/
        ///     舵面类部件 (MainWing/HTail/Elevator/Rudder/...) 涂军旗红, 其余机身白。
        ///     老版单网格 OBJ 只有 1 个名为 ImportedModel 的 Renderer, 关键字不命中
        ///     → 纯白机身, 容错自然成立。
        /// </summary>
        private void ApplyLivery(Renderer[] rends)
        {
            if (_usedFallbackVisual)
            {
                var hiVis = RenderMaterials.Create(new Color(0.95f, 0.45f, 0.12f));
                foreach (var r in rends) r.material = hiVis;
                Debug.Log("[AircraftVisual] 占位/装配兜底模型: 高可见度橙色 (" + rends.Length + " 个 Renderer)");
                return;
            }

            // U10 工坊涂装: 换机链带来的每部件颜色优先 — 与工坊 3D 预览一致
            // (此前硬编码"白机身+红翼面", 用户在工坊设的颜色试飞时全部丢失)。
            // 匹配键 = OBJ 安全化组名 (stl_to_obj_unity.sanitize_group_name 同规则)。
            if (PartColors != null && PartColors.Count > 0)
            {
                var body = CreateBodyPaint();
                var matCache = new Dictionary<Color, Material>();
                int matched = 0;
                foreach (var r in rends)
                {
                    Material m;
                    if (PartColors.TryGetValue(SanitizeGroupName(r.name), out Color c))
                    {
                        if (!matCache.TryGetValue(c, out m))
                        {
                            m = RenderMaterials.Create(c, 0.70f, 0.22f);
                            m.name = "WorkshopPaint_" + r.name;
                            matCache[c] = m;
                        }
                        matched++;
                    }
                    else m = body;
                    r.material = m;
                }
                Debug.Log(string.Format(
                    "[AircraftVisual] 工坊涂装: {0}/{1} 个部件按工坊颜色 (未匹配用机身漆)",
                    matched, rends.Length));
                return;
            }

            var body2 = CreateBodyPaint();
            var accent = CreateAccentPaint();

            int accentCount = 0;
            foreach (var r in rends)
            {
                bool isAccent = IsAccentRenderer(r.name);
                r.material = isAccent ? accent : body2;
                if (isAccent) accentCount++;
            }
            Debug.Log(string.Format(
                "[AircraftVisual] 真实模型涂装: 金属漆机身 + {0}/{1} 个翼面/舵面部件红色",
                accentCount, rends.Length));
        }

        /// <summary>OBJ 组名安全化 (与 stl_to_obj_unity.sanitize_group_name 同规则:
        /// 非 [A-Za-z0-9_.-] 换 '_', 空名 → "Part")。工坊部件名→OBJ 组名的映射键。</summary>
        public static string SanitizeGroupName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Part";
            var sb = new System.Text.StringBuilder(name.Trim().Length);
            foreach (char ch in name.Trim())
                sb.Append((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
                          (ch >= '0' && ch <= '9') || ch == '_' || ch == '.' || ch == '-'
                    ? ch : '_');
            string s = sb.ToString();
            return s.Length == 0 ? "Part" : s;
        }

        /// <summary>"#RRGGBB"/"RRGGBB" → Color; 失败返回 false (U10)。</summary>
        public static bool TryParseHex(string hex, out Color c)
        {
            c = Color.white;
            if (string.IsNullOrEmpty(hex)) return false;
            string h = hex.TrimStart('#');
            if (h.Length != 6) return false;
            byte r, g, b;
            if (!byte.TryParse(h.Substring(0, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out r)) return false;
            if (!byte.TryParse(h.Substring(2, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out g)) return false;
            if (!byte.TryParse(h.Substring(4, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out b)) return false;
            c = new Color32(r, g, b, 255);
            return true;
        }

        /// <summary>程序化小飞机 (米级尺寸, 白色) —— 兜底视觉。</summary>
        private void BuildPlaceholder()
        {
            var root = new GameObject("PlaceholderAircraft");
            root.transform.SetParent(transform, false);

            Material mat = RenderMaterials.Create(new Color(0.92f, 0.94f, 0.96f));

            void Part(string name, Vector3 pos, Vector3 scale, PrimitiveType type, Quaternion rot)
            {
                var p = GameObject.CreatePrimitive(type);
                p.name = name;
                p.transform.SetParent(root.transform, false);
                p.transform.localPosition = pos;
                p.transform.localRotation = rot;
                p.transform.localScale = scale;
                p.GetComponent<Renderer>().sharedMaterial = mat;
            }

            Part("机身", new Vector3(0, 0, 0.2f), new Vector3(0.16f, 2.2f, 0.16f),
                PrimitiveType.Capsule, Quaternion.Euler(90f, 0f, 0f));
            Part("主翼", new Vector3(0, 0.05f, 0.25f), new Vector3(4.0f, 0.05f, 0.5f),
                PrimitiveType.Cube, Quaternion.identity);
            Part("平尾", new Vector3(0, 0.04f, -1.0f), new Vector3(1.3f, 0.04f, 0.3f),
                PrimitiveType.Cube, Quaternion.identity);
            Part("垂尾", new Vector3(0, 0.28f, -1.0f), new Vector3(0.04f, 0.45f, 0.35f),
                PrimitiveType.Cube, Quaternion.identity);
        }
    }
}
