using System.Collections.Generic;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 机型工坊 W2: 机翼编辑窗 (机翼1/机翼2/平尾/垂尾共用, 提示词 §5.5/§5.6)。
    /// 垂尾差异: 默认带方向舵 + 「左右对称」勾选与「左右间距」(双垂尾)。
    /// 多段机翼 (XSec>2): 梯形参数置灰 + 「转为单段梯形」(确认后不可撤销)。
    /// </summary>
    public class WingEditorWindow : WorkshopEditorWindow
    {
        public bool isVtail;
        public float spanFull = 2f;             // UI 显示全展 (parm=半展, W0 结论)
        public string spanText = "2";
        public float rootChord = 0.25f, tipChord = 0.15f;
        public string rootText = "0.25", tipText = "0.15";
        public float sweep, twist, dihedral, incidence;
        public string sweepText = "0", twistText = "0", dihedralText = "0", incText = "0";
        public float px, py, pz;
        public string tx = "0", ty = "0", tz = "0";
        public float mass = 0.6f;
        public string massText = "0.6";
        public string material = "White";
        public bool multiSegment;
        public bool symmetric = true;
        // 双垂尾 (§5.6)
        public bool twin;
        public float spacing;                    // 左右间距 (m)
        public string spacingText = "0";
        // 翼型
        public List<string> airfoilNames = new List<string>();
        public string airfoilJson = "[]";        // list_airfoils 原始 items
        public int airfoilSel;
        // 操纵面
        public List<ControlInfo> controls = new List<ControlInfo>();
        public int selectedControl = -1;

        private Vector2 scroll;

        public class ControlInfo
        {
            public string id, name;
            public float etaStart, etaEnd, lenC;
            public string role = "aileron", userName = "";
        }

        public WingEditorWindow(WorkshopUI owner, string geomId, string title, bool vtail,
                                string slotKey = "")
            : base(owner, geomId, title) { isVtail = vtail; _slotKey = slotKey; }

        private readonly string _slotKey;

        public void LoadFromSummary(string json)
        {
            SummaryJson = json;
            multiSegment = WorkshopJson.Bool(json, "multi_segment");
            spanFull = WorkshopJson.Num(json, "span_half", 1f) * 2f;
            spanText = spanFull.ToString("0.###");
            rootChord = WorkshopJson.Num(json, "root_chord", 0.25f); rootText = rootChord.ToString("0.###");
            tipChord = WorkshopJson.Num(json, "tip_chord", 0.15f); tipText = tipChord.ToString("0.###");
            sweep = WorkshopJson.Num(json, "sweep"); sweepText = sweep.ToString("0.##");
            twist = WorkshopJson.Num(json, "twist"); twistText = twist.ToString("0.##");
            dihedral = WorkshopJson.Num(json, "dihedral"); dihedralText = dihedral.ToString("0.##");
            incidence = WorkshopJson.Num(json, "rot_y"); incText = incidence.ToString("0.##");
            px = WorkshopJson.Num(json, "x"); py = WorkshopJson.Num(json, "y"); pz = WorkshopJson.Num(json, "z");
            tx = px.ToString("0.###"); ty = py.ToString("0.###"); tz = pz.ToString("0.###");
            mass = WorkshopJson.Num(json, "mass_kg", WorkshopJson.Num(json, "point_mass", 0.5f)); massText = mass.ToString("0.###");
            material = WorkshopJson.Str(json, "material") ?? "White";
            symmetric = WorkshopJson.Bool(json, "symmetric", true);
            twin = isVtail && symmetric && Mathf.Abs(py) > 1e-4f;   // 双垂尾=对称+Y 偏移
            spacing = twin ? Mathf.Abs(py) * 2f : 0f;
            spacingText = spacing.ToString("0.###");
            controls.Clear();
            string arr = WorkshopJson.Raw(json, "controls");
            if (arr != null)
                foreach (string obj in WorkshopWidgets.SplitArray(arr))
                    controls.Add(new ControlInfo {
                        id = WorkshopJson.Str(obj, "id"),
                        name = WorkshopJson.Str(obj, "name"),
                        etaStart = WorkshopJson.Num(obj, "eta_start"),
                        etaEnd = WorkshopJson.Num(obj, "eta_end"),
                        lenC = WorkshopJson.Num(obj, "len_c"),
                        role = RoleOf(WorkshopJson.Str(obj, "name")),
                    });
        }

        private static string RoleOf(string ctlName)
        {
            switch (ctlName)
            {
                case "Aileron": return "aileron";
                case "Elevator": return "elevator";
                case "Rudder": return "rudder";
                default: return "flap";
            }
        }

        protected override void DrawBody(int id)
        {
            if (multiSegment)
            {
                WorkshopWidgets.GroupLabel("几何");
                WorkshopWidgets.Hint("多段机翼: 梯形参数不可编辑 (可改颜色/质量/位置)");
                if (GUILayout.Button("转为单段梯形 (不可撤销)",
                                     GUILayout.Height(WorkshopUiKit.BtnH)))
                    Owner.SendWingToSingleTaper(GeomId);
                DrawOuterSection();
                return;
            }

            WorkshopWidgets.GroupLabel("几何");
            if (WorkshopWidgets.FloatRow("全展长", "m", ref spanFull, ref spanText, 0.1f, 20f, 0.05f))
                Pend("Span", "XSec_1", spanFull / 2f);
            if (WorkshopWidgets.FloatRow("根部弦长", "m", ref rootChord, ref rootText, 0.02f, 3f, 0.005f))
                Pend("Root_Chord", "XSec_1", rootChord);
            if (WorkshopWidgets.FloatRow("翼尖弦长", "m", ref tipChord, ref tipText, 0.01f, 3f, 0.005f))
                Pend("Tip_Chord", "XSec_1", tipChord);
            if (WorkshopWidgets.FloatRow("后掠角", "°", ref sweep, ref sweepText, -60f, 75f, 0.5f))
                Pend("Sweep", "XSec_1", sweep);
            if (WorkshopWidgets.FloatRow("翼尖扭转", "°", ref twist, ref twistText, -15f, 15f, 0.25f))
                Pend("Twist", "XSec_1", twist);
            if (WorkshopWidgets.FloatRow("上反角", "°", ref dihedral, ref dihedralText, -20f, 20f, 0.25f))
                Pend("Dihedral", "XSec_1", dihedral);
            if (WorkshopWidgets.FloatRow("安装角", "°", ref incidence, ref incText, -10f, 10f, 0.25f))
                Pend("Y_Rotation", "XForm", incidence);   // R05: 绝对旋转
            DrawOuterSection();
        }

        private void DrawOuterSection()
        {
            WorkshopWidgets.GroupLabel("装配位置 (机体系)");
            if (WorkshopWidgets.FloatRow("X (机尾+)", "m", ref px, ref tx, -5f, 8f, 0.01f))
                Pend("X_Location", "XForm", px);
            if (WorkshopWidgets.FloatRow("Y (右+)", "m", ref py, ref ty, -5f, 5f, 0.01f))
                Pend("Y_Location", "XForm", py);
            if (WorkshopWidgets.FloatRow("Z (上+)", "m", ref pz, ref tz, -2f, 2f, 0.01f))
                Pend("Z_Location", "XForm", pz);

            WorkshopWidgets.GroupLabel("质量与外观");
            if (WorkshopWidgets.FloatRow("质量", "kg", ref mass, ref massText, 0.01f, 100f, 0.05f))
                Owner.SendSetMass(GeomId, mass);
            Owner.DrawMaterialSelector(ref material, GeomId,
                SlotKeyOfGeom(GeomId));

            if (!isVtail)
            {
                if (WorkshopWidgets.ToggleRow("左右对称 (镜像)", ref symmetric))
                    Owner.SendSymmetric(GeomId, symmetric);
            }
            else
            {
                WorkshopWidgets.GroupLabel("垂尾对称 (双垂尾 = 勾选 + 间距)");
                if (WorkshopWidgets.ToggleRow("左右对称", ref twin))
                    Owner.SendVtailSymmetry(GeomId, twin, spacing);
                using (var s = new GUITempDisable(!twin))
                {
                    if (WorkshopWidgets.FloatRow("左右间距", "m", ref spacing, ref spacingText, 0f, 3f, 0.01f))
                        Owner.SendVtailSymmetry(GeomId, true, spacing);
                }
            }

            WorkshopWidgets.GroupLabel("翼型");
            DrawAirfoilRow();
            WorkshopWidgets.GroupLabel("操纵面");
            WorkshopWidgets.Hint("名称 = 功能类型, 决定模拟器三轴控制");
            DrawControlsSection();
        }

        private void DrawAirfoilRow()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("根/尖共用", WorkshopUiKit.Dim, GUILayout.Width(WorkshopUiKit.LabelW));
            airfoilSel = Mathf.Clamp(airfoilSel, 0, Mathf.Max(0, airfoilNames.Count - 1));
            if (GUILayout.Button((airfoilNames.Count > 0 ? airfoilNames[airfoilSel] : "加载中…") + " ▾",
                                 GUILayout.ExpandWidth(true), GUILayout.Height(WorkshopUiKit.RowH)))
                Owner.OpenAirfoilPicker(this);
            if (GUILayout.Button("上传 .dat", GUILayout.Width(80),
                                 GUILayout.Height(WorkshopUiKit.RowH)))
                Owner.PickAndUploadAirfoil(this);
            GUILayout.EndHorizontal();
        }

        private void DrawControlsSection()
        {
            scroll = GUILayout.BeginScrollView(scroll, false, true, GUILayout.Height(96));
            for (int i = 0; i < controls.Count; i++)
            {
                var c = controls[i];
                GUILayout.BeginHorizontal(GUILayout.Height(24));
                GUILayout.Label(string.Format("{0}  {1:F0}%-{2:F0}%  弦{3:F0}%",
                    c.name, c.etaStart * 100f, c.etaEnd * 100f, c.lenC * 100f),
                    GUILayout.ExpandWidth(true));
                if (GUILayout.Button("编辑", GUILayout.Width(46), GUILayout.Height(22)))
                { selectedControl = i; Owner.OpenControlEditor(this, c); }
                if (GUILayout.Button("删除", GUILayout.Width(46), GUILayout.Height(22)))
                { Owner.SendDelControl(GeomId, c.id); selectedControl = -1; }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("新建操纵面", GUILayout.Width(120),
                                 GUILayout.Height(WorkshopUiKit.BtnH)))
            {
                // 默认功能类型按槽位: 平尾=升降舵, 垂尾=方向舵, 其余=副翼
                // (此前一律默认副翼 — 在平尾上新建会被命名为 Aileron, 换机校验报"无升降舵")
                bool tailDefault = isVtail || _slotKey == "htail";
                var c = new ControlInfo
                {
                    role = isVtail ? "rudder" : (_slotKey == "htail" ? "elevator" : "aileron"),
                    etaStart = tailDefault ? 0.10f : 0.55f,
                    etaEnd = 0.95f,
                    lenC = tailDefault ? 0.35f : 0.25f,
                };
                Owner.SendAddControl(GeomId, c);
            }
        }

        protected override void OnResetDefault()
        {
            spanFull = isVtail ? 0.3f : 2f;
            Pend("Span", "XSec_1", spanFull / 2f);
            Pend("Root_Chord", "XSec_1", isVtail ? 0.12f : 0.25f);
            Pend("Tip_Chord", "XSec_1", isVtail ? 0.08f : 0.15f);
            Pend("Sweep", "XSec_1", 0f);
            Pend("Twist", "XSec_1", 0f);
            Pend("Dihedral", "XSec_1", 0f);
        }

        private struct GUITempDisable : System.IDisposable
        {
            private readonly bool _prev;
            public GUITempDisable(bool disabled) { _prev = GUI.enabled; if (disabled) GUI.enabled = false; }
            public void Dispose() { GUI.enabled = _prev; }
        }
    }

    /// <summary>操纵面编辑窗 (提示词 §5.5 操纵面编辑页): 三参数联动钳制 + 功能类型。</summary>
    public class ControlSurfaceEditorWindow
    {
        public WingEditorWindow Wing;
        public WingEditorWindow.ControlInfo Ctl;
        public Rect Rect = new Rect(200, 160, 400, 200);   // 高度按内容 (提示+3行+类型+按钮) 收紧, 不再留大段空白
        public bool CloseRequested;
        public float posPct, spanPct, chordPct;
        public string posText, spanText, chordText;

        private static readonly string[] RoleZh = { "副翼", "升降舵", "方向舵", "襟翼(不参与三轴)" };
        private static readonly string[] RoleKeys = { "aileron", "elevator", "rudder", "flap" };
        private int _roleIdx;

        public void Open(WingEditorWindow wing, WingEditorWindow.ControlInfo c)
        {
            Wing = wing;
            Ctl = c;
            posPct = c.etaStart * 100f;
            spanPct = (c.etaEnd - c.etaStart) * 100f;
            chordPct = c.lenC * 100f;
            posText = posPct.ToString("0.#");
            spanText = spanPct.ToString("0.#");
            chordText = chordPct.ToString("0.#");
            _roleIdx = System.Array.IndexOf(RoleKeys, c.role);
            if (_roleIdx < 0) _roleIdx = 0;
            CloseRequested = false;
        }

        public void Draw(int id)
        {
            // 位置% + 展长% ≤ 100 联动钳制 (提示词 §5.5)
            WorkshopWidgets.Hint("位置/展长以机翼单边展长为 100%, 弦长以当地弦长为 100%");
            if (WorkshopWidgets.FloatRow("位置", "%", ref posPct, ref posText, 0f, 100f, 1f))
            { posPct = Mathf.Min(posPct, 100f - spanPct); posText = posPct.ToString("0.#"); }
            if (WorkshopWidgets.FloatRow("展长", "%", ref spanPct, ref spanText, 1f, 100f, 1f))
            { spanPct = Mathf.Min(spanPct, 100f - posPct); spanText = spanPct.ToString("0.#"); }
            WorkshopWidgets.FloatRow("弦长", "%", ref chordPct, ref chordText, 2f, 95f, 1f);
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.Label("功能类型", GUILayout.Width(WorkshopUiKit.LabelW));
            if (GUILayout.Button(RoleZh[_roleIdx] + " ▾", GUILayout.Width(170),
                                 GUILayout.Height(WorkshopUiKit.RowH)))
                _roleIdx = (_roleIdx + 1) % RoleZh.Length;   // 循环切换 (简化下拉)
            GUILayout.EndHorizontal();
            WorkshopUiKit.Divider();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("取消", GUILayout.Width(80), GUILayout.Height(WorkshopUiKit.BtnH)))
                CloseRequested = true;
            if (GUILayout.Button("应用", GUILayout.Width(80), GUILayout.Height(WorkshopUiKit.BtnH)))
            {
                Ctl.etaStart = posPct / 100f;
                Ctl.etaEnd = Mathf.Clamp((posPct + spanPct) / 100f, Ctl.etaStart + 1e-3f, 1f);
                Ctl.lenC = chordPct / 100f;
                Ctl.role = RoleKeys[_roleIdx];
                Ctl.name = RoleKeys[_roleIdx] == "flap" ? (Ctl.userName.Length > 0 ? Ctl.userName : "Flap")
                     : char.ToUpper(RoleKeys[_roleIdx][0]) + RoleKeys[_roleIdx].Substring(1);
                Wing.Owner.SendEditControl(Wing.GeomId, Ctl);
                CloseRequested = true;
                GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0, 0, 10000, 24));
        }
    }
}
