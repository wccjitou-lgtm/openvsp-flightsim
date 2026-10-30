using System.Collections.Generic;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 机型工坊 W2: 编辑窗通用控件与基类。
    /// 提示词 §5.3: 每个数值参数同时有滑块+数值输入框 (双向同步), 越界钳制,
    /// 非法输入不崩溃; 浮动非模态窗 (GUI.Window), 可拖动, 3D 实时可见。
    /// </summary>
    public class WorkshopWidgets
    {
        /// <summary>滑块 + 输入框双向同步。返回 true 表示值被用户改了 (已钳制)。
        /// V 轮: 行模板统一 (标签 LabelW / 滑块弹性 / 输入 FieldW / 单位 UnitW)。</summary>
        public static bool FloatRow(string label, string unit, ref float value,
                                    ref string text, float min, float max, float step)
        {
            bool changed = false;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(WorkshopUiKit.LabelW));
            float sliderVal = Mathf.Clamp(value, min, max);
            float nv = GUILayout.HorizontalSlider(sliderVal, min, max,
                GUILayout.MinWidth(80), GUILayout.MaxWidth(260), GUILayout.ExpandWidth(true));
            if (!Mathf.Approximately(nv, sliderVal))
            {
                if (step > 0f) nv = Mathf.Round(nv / step) * step;
                value = Mathf.Clamp(nv, min, max);
                text = value.ToString("0.###");
                changed = true;
            }
            string nt = GUILayout.TextField(text ?? "", GUILayout.Width(WorkshopUiKit.FieldW));
            if (nt != (text ?? ""))
            {
                text = nt;
                if (float.TryParse(nt, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float pv))
                {
                    float clamped = Mathf.Clamp(pv, min, max);
                    if (!Mathf.Approximately(clamped, value))
                    {
                        value = clamped;
                        changed = true;
                    }
                }
                // 非法输入: 保留文本但不动 value (不崩溃, 下次滑块会重写)
            }
            GUILayout.Label(unit ?? "", WorkshopUiKit.Dim, GUILayout.Width(WorkshopUiKit.UnitW));
            GUILayout.EndHorizontal();
            return changed;
        }

        public static bool IntRow(string label, string unit, ref int value, ref string text,
                                  int min, int max)
        {
            float f = value;
            bool c = FloatRow(label, unit, ref f, ref text, min, max, 1f);
            if (c) value = Mathf.Clamp(Mathf.RoundToInt(f), min, max);
            return c;
        }

        public static bool ToggleRow(string label, ref bool value)
        {
            bool nv = GUILayout.Toggle(value, "  " + label);
            if (nv != value) { value = nv; return true; }
            return false;
        }

        /// <summary>编辑窗内分组小标题 (弱化粗体, 与气动面板 SectionBar 层级区分)。</summary>
        public static void GroupLabel(string title)
        {
            GUILayout.Space(4);
            GUILayout.Label(title, WorkshopUiKit.TableHead);
            GUILayout.Space(2);   // 标题与参数行之间留缝 (原紧贴)
        }

        /// <summary>弱化说明小字 (参数辅助说明)。</summary>
        public static void Hint(string text)
        {
            GUILayout.Label(text, WorkshopUiKit.Dim);
        }

        private static GUIStyle _warnStyle;

        /// <summary>警示小字 (琥珀色, 需要用户注意的设计风险)。</summary>
        public static void HintWarn(string text)
        {
            if (_warnStyle == null)
                _warnStyle = new GUIStyle(WorkshopUiKit.Dim)
                { normal = { textColor = WorkshopSkin.Amber } };
            GUILayout.Label(text, _warnStyle);
        }

        /// <summary>从 worker dataJson (list_airfoils/list_materials 的 items 数组) 取名列表。</summary>
        public static List<string> ItemNames(string itemsJson)
        {
            var names = new List<string>();
            string arr = WorkshopJson.Raw(itemsJson, "items");
            if (arr == null) return names;
            foreach (string obj in SplitArray(arr))
                names.Add(WorkshopJson.Str(obj, "name") ?? "");
            return names;
        }

        /// <summary>JSON 对象数组的原文拆分 (顶层, 括号配平)。</summary>
        public static List<string> SplitArray(string jsonArray)
        {
            var outList = new List<string>();
            if (string.IsNullOrEmpty(jsonArray) || jsonArray[0] != '[') return outList;
            int depth = 0, instr = 0, start = -1;
            for (int i = 0; i < jsonArray.Length; i++)
            {
                char c = jsonArray[i];
                if (instr > 0)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') instr = 0;
                    continue;
                }
                if (c == '"') instr = 1;
                else if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0) { outList.Add(jsonArray.Substring(start, i - start + 1)); start = -1; }
                }
            }
            return outList;
        }
    }

    /// <summary>
    /// 编辑窗基类: 持 summary 缓存 + 待发参数 (节流合并), 由 WorkshopUI 统一绘制/发送。
    /// 子类在窗口体里改值后调用 Pend(); 结构性操作 (半径列表/操纵面/翼型) 直接
    /// 调 owner.SendWorkerCommand 即时发送。
    /// </summary>
    public abstract class WorkshopEditorWindow
    {
        public string GeomId;
        public string Title;
        public Rect Rect = new Rect(120, 90, 430, 520);
        public bool CloseRequested;
        public string SummaryJson;      // 最近一次 get_geom_summary 响应 (子类缓存解析值)

        public readonly WorkshopUI Owner;   // public: 操纵面编辑窗等伴随窗需要访问
        protected readonly List<WorkshopJsonParm> Pending = new List<WorkshopJsonParm>();

        /// <summary>按几何 id 反查槽位 key (颜色入口用; 找不到返回 wing1 兜底)。</summary>
        protected string SlotKeyOfGeom(string gid)
        {
            var g = Owner != null ? Owner.SlotGeomOf(gid) : null;
            return g != null ? g : "wing1";
        }

        protected WorkshopEditorWindow(WorkshopUI owner, string geomId, string title)
        {
            Owner = owner;
            GeomId = geomId;
            Title = title;
        }

        public struct WorkshopJsonParm
        {
            public string Name, Group;
            public float Value;
        }

        protected void Pend(string name, string group, float value)
        {
            Pending.RemoveAll(p => p.Name == name && p.Group == group);
            Pending.Add(new WorkshopJsonParm { Name = name, Group = group, Value = value });
        }

        /// <summary>取走待发参数 (WorkshopUI 节流 flush 时调用)。</summary>
        public List<WorkshopJsonParm> TakePending()
        {
            var p = new List<WorkshopJsonParm>(Pending);
            Pending.Clear();
            return p;
        }

        public bool HasPending { get { return Pending.Count > 0; } }

        public void Draw(int windowId)
        {
            DrawBody(windowId);
            WorkshopUiKit.Divider();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("恢复默认", GUILayout.Width(80),
                                 GUILayout.Height(WorkshopUiKit.BtnH)))
                OnResetDefault();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("关闭", GUILayout.Width(64),
                                 GUILayout.Height(WorkshopUiKit.BtnH)))
                CloseRequested = true;
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0, 0, 10000, 24));
        }

        protected abstract void DrawBody(int windowId);
        protected abstract void OnResetDefault();
    }

    // ==================== 机身编辑窗 (提示词 §5.4) ====================

    public class FuselageEditorWindow : WorkshopEditorWindow
    {
        public float length = 1.6f;
        public string lengthText = "1.6";
        public int xsecCount = 6;
        public string xsecText = "6";
        public float[] radii = new float[0];       // 直径缓存 (summary diameters/2)
        public string[] radiiTexts;
        public float massKg = 1f;
        public string massText = "1";
        public string material = "White";
        public bool importedPreserve;              // 导入机身未转圆 (shapes 含非圆/椭)
        public bool roundNoticeShown;              // 一次性转圆提示已弹

        private Vector2 scroll;

        public FuselageEditorWindow(WorkshopUI owner, string geomId, string title)
            : base(owner, geomId, title) { }

        public void LoadFromSummary(string json)
        {
            SummaryJson = json;
            length = WorkshopJson.Num(json, "length", 1.6f);
            lengthText = length.ToString("0.###");
            xsecCount = WorkshopJson.Int(json, "num_xsecs", 6);
            xsecText = xsecCount.ToString();
            var diams = ParseFloatArray(WorkshopJson.Raw(json, "diameters"));
            radii = new float[diams.Count];
            radiiTexts = new string[diams.Count];
            for (int i = 0; i < diams.Count; i++)
            {
                radii[i] = diams[i] / 2f;
                radiiTexts[i] = radii[i].ToString("0.####");
            }
            importedPreserve = !WorkshopJson.Bool(json, "all_round_or_ellipse", true);
            material = WorkshopJson.Str(json, "material") ?? "White";
        }

        public static List<float> ParseFloatArray(string jsonArray)
        {
            var res = new List<float>();
            if (string.IsNullOrEmpty(jsonArray) || jsonArray[0] != '[') return res;
            foreach (string tok in jsonArray.Trim('[', ']').Split(','))
            {
                if (float.TryParse(tok.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float v))
                    res.Add(v);
                else
                    res.Add(0f);   // null 项 (非圆截面无直径)
            }
            return res;
        }

        protected override void DrawBody(int id)
        {
            WorkshopWidgets.GroupLabel("几何");
            if (WorkshopWidgets.FloatRow("机身长度", "m", ref length, ref lengthText, 0.2f, 30f, 0.05f))
                Pend("Length", "Design", length);
            if (WorkshopWidgets.IntRow("截面数", "", ref xsecCount, ref xsecText, 2, 30))
                Owner.SendFuselageRadii(GeomId, radii, xsecCount);
            WorkshopWidgets.GroupLabel("截面半径 (m)");
            WorkshopWidgets.Hint("截面沿机身长度等分, 之间以光滑连续曲面连接");
            scroll = GUILayout.BeginScrollView(scroll, false, true,
                GUILayout.Height(Mathf.Min(230, 28 * radii.Length + 8)));
            bool anyChanged = false;
            for (int i = 0; i < radii.Length; i++)
            {
                if (i == 0)
                {
                    WorkshopWidgets.Hint("截面 0 为机头闭合点, 不可修改");
                    continue;
                }
                float r = radii[i];
                string t = radiiTexts[i];
                if (WorkshopWidgets.FloatRow("截面 " + i, "m", ref r, ref t, 0f, 3f, 0.005f))
                {
                    radii[i] = r;
                    radiiTexts[i] = t;
                    anyChanged = true;
                }
                else radiiTexts[i] = t;
            }
            GUILayout.EndScrollView();
            if (anyChanged)
                Owner.SendFuselageRadii(GeomId, radii, radii.Length);
            WorkshopWidgets.GroupLabel("质量与外观");
            if (WorkshopWidgets.FloatRow("质量 (均布)", "kg", ref massKg, ref massText, 0.01f, 500f, 0.1f))
                Owner.SendSetMass(GeomId, massKg);
            Owner.DrawMaterialSelector(ref material, GeomId,
                SlotKeyOfGeom(GeomId));
            if (importedPreserve)
                WorkshopWidgets.Hint("导入机身: 未编辑截面前保持原截面形状");
            // F2: 粗机身 (直径/机长 > 0.1) 作薄面进 VSPAERO 阻力解会失真 (e2e 实测)
            float maxDiam = 0f;
            for (int i = 0; i < radii.Length; i++) maxDiam = Mathf.Max(maxDiam, radii[i] * 2f);
            if (length > 1e-3f && maxDiam / length > 0.1f)
                WorkshopWidgets.HintWarn(
                    "⚠ 机身较粗 (直径/机长 > 0.1): 薄面气动计算的阻力可能失真甚至为负,"
                    + " 建议减小直径, 或在 OpenVSP 中将机身设为厚体集合");
        }

        protected override void OnResetDefault()
        {
            length = 1.6f; lengthText = "1.6";
            xsecCount = 6; xsecText = "6";
            Owner.SendFuselageRadii(GeomId,
                new[] { 0f, 0.15f, 0.19f, 0.19f, 0.15f, 0.08f }, 6);
            Pend("Length", "Design", 1.6f);
        }
    }

    // ==================== 配重编辑窗 (提示词 §5.7) ====================

    public class BallastEditorWindow : WorkshopEditorWindow
    {
        public float mass = 0.2f;
        public string massText = "0.2";
        public float px, py, pz;
        public string tx = "0", ty = "0", tz = "0";
        public string material = "Red Default";

        public BallastEditorWindow(WorkshopUI owner, string geomId, string title)
            : base(owner, geomId, title) { }

        public void LoadFromSummary(string json)
        {
            SummaryJson = json;
            mass = WorkshopJson.Num(json, "point_mass", 0.2f);
            massText = mass.ToString("0.###");
            px = WorkshopJson.Num(json, "x"); py = WorkshopJson.Num(json, "y"); pz = WorkshopJson.Num(json, "z");
            tx = px.ToString("0.###"); ty = py.ToString("0.###"); tz = pz.ToString("0.###");
            material = WorkshopJson.Str(json, "material") ?? "Red Default";
        }

        protected override void DrawBody(int id)
        {
            WorkshopWidgets.GroupLabel("质量");
            if (WorkshopWidgets.FloatRow("质量", "kg", ref mass, ref massText, 0.001f, 50f, 0.01f))
                Owner.SendSetMass(GeomId, mass);
            WorkshopWidgets.GroupLabel("位置 (机体系)");
            if (WorkshopWidgets.FloatRow("X (机尾+)", "m", ref px, ref tx, -5f, 5f, 0.01f))
                Pend("X_Location", "XForm", px);
            if (WorkshopWidgets.FloatRow("Y (右+)", "m", ref py, ref ty, -5f, 5f, 0.01f))
                Pend("Y_Location", "XForm", py);
            if (WorkshopWidgets.FloatRow("Z (上+)", "m", ref pz, ref tz, -5f, 5f, 0.01f))
                Pend("Z_Location", "XForm", pz);
            WorkshopWidgets.GroupLabel("外观");
            Owner.DrawMaterialSelector(ref material, GeomId,
                SlotKeyOfGeom(GeomId));
            WorkshopWidgets.Hint("小球参与质量/重心与惯量, 不进入气动面");
        }

        protected override void OnResetDefault()
        {
            mass = 0.2f; massText = "0.2";
            Owner.SendSetMass(GeomId, 0.2f);
            Pend("X_Location", "XForm", 0.10f);
            Pend("Y_Location", "XForm", 0f);
            Pend("Z_Location", "XForm", 0f);
        }
    }
}
