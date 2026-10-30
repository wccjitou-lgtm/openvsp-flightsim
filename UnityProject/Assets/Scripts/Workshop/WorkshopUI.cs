using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 机型工坊 W1 (骨架与预览): F8 全屏开关; 三栏布局 (飞行器设计 / 气动计算 /
    /// 实时外形展示) + 底部常驻条 (提示词 §4)。
    /// W1 范围: worker 联通、新建/导入/保存/另存为、6 槽位表格 + 部件库、
    /// 3D 只读预览 (分槽位着色 + 自动旋转/拖动/缩放) + CG 标注。
    /// 编辑窗 (W2)、气动面板 (W3)、换机试飞 (W4) 以占位呈现。
    /// 工坊打开时模拟暂停 (timeScale=0), 关闭恢复。
    /// </summary>
    public class WorkshopUI : MonoBehaviour
    {
        private WorkshopWorkerClient _worker = new WorkshopWorkerClient();
        private readonly WorkshopState _state = new WorkshopState();
        private WorkshopPreview _preview;

        private bool _open;
        private bool _toggleRequested;
        private float _savedTimeScale = 1f;
        private float _col1Frac = 0.28f, _col2Frac = 0.32f;   // 栏③吃余量
        private Vector2 _logScroll;
        private readonly List<string> _log = new List<string>();
        private float _meshRefreshAt = -1f;
        private bool _needMeshRefresh;
        private string _projectRoot;
        private bool _workerReady;

        // 弹窗 (确认/命名/槽位下拉)
        private string _confirmText, _confirmYes;
        private System.Action _confirmAction;
        private string _namingSlot, _namingText = "";
        private Rect _popupRect = new Rect(40, 60, 1080, 640);

        // 槽位下拉展开态 (运行时 IMGUI 无 GUILayout.Popup, 自绘弹层, VspImportUI 同思路)
        private string _openSlot;
        private Vector2 _openSlotScreenPos;   // 捕获时的屏幕坐标 (穿透嵌套, U17v2)
        private float _openSlotBtnH;
        private List<string> _openLabels = new List<string>();
        private List<string> _openActions = new List<string>();
        private Vector2 _openSlotScroll;   // U14 下拉超高滚动

        // W2 编辑窗
        private readonly List<WorkshopEditorWindow> _editors = new List<WorkshopEditorWindow>();
        private readonly ControlSurfaceEditorWindow _ctlEditor = new ControlSurfaceEditorWindow();
        private float _parmFlushAt = -1f;
        private string _airfoilJson = "{\"items\":[]}";       // list_airfoils 缓存
        private WingEditorWindow _airfoilPickerFor;
        private Rect _airfoilPickerRect;
        private Vector2 _airfoilScroll;
        private string _materialsJson = "{\"items\":[]}";     // list_materials 缓存
        private string _colorTarget;
        private Rect _colorPickerRect;
        private Vector2 _colorScroll;

        // W3 气动 + R10 模型版本 (气动结果绑定; 模型变化即失效)
        public int ModelVersion { get; private set; }
        private readonly WorkshopAeroPanel _aero;
        /// <summary>按几何 id 反查槽位 key (编辑窗颜色入口); 非槽位几何返回 null。</summary>
        public string SlotGeomOf(string geomId)
        {
            foreach (var s in _state.slots)
                if (s.geomId == geomId) return s.key;
            var g = _state.GeomById(geomId);
            return g != null && !string.IsNullOrEmpty(g.slot) ? g.slot : null;
        }

        public string Wing1GeomId { get { return _state.Slot("wing1") != null ? _state.Slot("wing1").geomId : null; } }

        public WorkshopUI()
        {
            _aero = new WorkshopAeroPanel(this);
        }

        /// <summary>R13: 工坊打开标志 — 飞行快捷键/输入采集在此期间隔离。</summary>
        public static bool IsOpen { get; private set; }

        /// <summary>U7: 换机占用 (准备副本或管线运行中) — 算例启动/库换机的互斥依据。</summary>
        public bool SwapBusy { get { return _swapRunner.IsRunning || _swapPreparing; } }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoAttach()
        {
            if (GameObject.Find("WorkshopUI") == null)
                new GameObject("WorkshopUI").AddComponent<WorkshopUI>();
        }

        private void Update()
        {
            if (_toggleRequested || Input.GetKeyDown(KeyCode.F8))
            {
                _toggleRequested = false;
                ToggleOpen();
            }
            // 换机链路 (保存副本 → 管线 → 热换机) 不依赖工坊窗口开合: 点击后关窗回到飞行也要完成换机
            if (!_open && _swapPreparing) _worker?.Poll(AppendLog, OnWorkerTimeout);
            PumpSwap();
            if (!_open) return;
            _worker?.Poll(AppendLog, OnWorkerTimeout);
            if (_needMeshRefresh && Time.realtimeSinceStartup >= _meshRefreshAt)
            {
                _needMeshRefresh = false;
                RefreshMeshesAndMass();
            }
            // 编辑窗待发参数节流合并 (拖动流畅; worker 往返 ~11ms)
            bool anyPending = false;
            foreach (var e in _editors) anyPending |= e.HasPending;
            if (anyPending && _parmFlushAt < 0f)
                _parmFlushAt = Time.realtimeSinceStartup + 0.08f;
            if (_parmFlushAt > 0f && Time.realtimeSinceStartup >= _parmFlushAt)
            {
                _parmFlushAt = -1f;
                FlushEditorParms();
            }
            _aero.Poll(_state);
            _preview?.Render();
        }

        private void PumpSwap()
        {
            // 进程退出后队列里可能还留着 [done]/[ui] 结束 (OutDir 只在 Drain 时才会被设置),
            // 所以必须以"队列非空"为继续条件, 不能看 OutDir/LastError。
            if (!(_swapRunner.IsRunning || _swapRunner.HasPending)) return;
            // R07: 不依赖同帧 IsRunning 跃迁 (进程可能在两帧间结束), 用结束行事件
            bool finished = false;
            _swapRunner.Drain(line =>
            {
                AppendLog("[换机] " + line);
                if (line.StartsWith("[stage]"))
                {
                    int sp = line.IndexOf(' ');
                    if (sp > 0) _swapStage = line.Substring(sp).Trim();
                }
                if (line == "[ui] 结束")
                    finished = true;   // 子进程 [done] 行已先行入队, OutDir 已解析
            });
            if (finished)
            {
                if (_swapRunner.OutDir != "") FinishSwap();
                else if (_swapRunner.LastError != "")
                    AppendLog("[换机] ✗ " + _swapRunner.LastError + " (工坊状态已保留)");
            }
        }

        private void FlushEditorParms()
        {
            // 合并全部窗口的待发参数为一次 set_parms (同参数后者覆盖)
            var merged = new Dictionary<string, WorkshopEditorWindow.WorkshopJsonParm>();
            string gid = null;
            foreach (var e in _editors)
                foreach (var p in e.TakePending())
                {
                    gid = e.GeomId;
                    merged[p.Group + "/" + p.Name] = p;
                }
            if (gid == null || merged.Count == 0) return;
            var sb = new System.Text.StringBuilder("[");
            bool first = true;
            foreach (var p in merged.Values)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":").Append(WorkshopJson.Escape(p.Name))
                  .Append(",\"group\":").Append(WorkshopJson.Escape(p.Group))
                  .Append(",\"value\":").Append(p.Value.ToString("0.######",
                      System.Globalization.CultureInfo.InvariantCulture)).Append('}');
            }
            sb.Append("]");
            _worker.Call("set_parms",
                WorkshopReq.Obj().S("geom_id", gid).Raw("parms", sb.ToString()).Str(), r =>
                {
                    if (!r.ok) AppendLog("[参数] " + r.error);
                    else OnModelChanged(r.dataJson);
                });
        }

        private void ToggleOpen()
        {
            _open = !_open;
            IsOpen = _open;
            if (_open)
            {
                _savedTimeScale = Time.timeScale;
                Time.timeScale = 0f;                       // 模拟暂停 (提示词 §4)
                _projectRoot = ProjectRoot();
                if (_preview == null) _preview = new WorkshopPreview();
                _preview.Create(Mathf.RoundToInt(Screen.width * 0.40f),
                                Mathf.RoundToInt(Screen.height * 0.8f));
                // U11: 重开工坊时预览是空的 (Create 重建) — 模型非空则自动装载
                // 网格, 否则选色/看模型都要等下一次几何变更才有内容
                if (_state.geoms.Count > 0) ScheduleMeshRefresh();
                if (!_worker.Start(_projectRoot))
                    AppendLog("[worker] " + _worker.LastError);
                else if (!_workerReady)
                    _worker.Call("ping", "{}", r =>
                    {
                        _workerReady = r.ok;
                        if (r.ok)
                        {
                            AppendLog("[worker] 就绪 python=" +
                                      WorkshopJson.Str(r.dataJson, "python"));
                            _worker.Call("list_library", "{}", rr =>
                            {
                                if (rr.ok) _state.LoadLibrary(rr.dataJson);
                            });
                            RefreshAirfoils();
                            _worker.Call("list_materials", "{}", rr => { if (rr.ok) _materialsJson = rr.dataJson; });
                        }
                        else AppendLog("[worker] ping 失败: " + r.error);
                    });
            }
            else
            {
                Time.timeScale = _savedTimeScale;
                _preview?.Dispose();
                CloseTransientWindows();   // 审查: 关闭编辑窗/选择器 (几何 id 会随换机失效)
            }
        }

        /// <summary>关闭编辑窗与各类弹层 (工坊关闭/换机后旧引用失效)。</summary>
        private void CloseTransientWindows()
        {
            foreach (var e in _editors) e.CloseRequested = true;
            _ctlEditor.Wing = null;
            _airfoilPickerFor = null;
            _colorTarget = null;
            _colorPicker.TargetSlot = null;
            _openSlot = null;
            _confirmText = null;
            _confirmAction = null;
            _namingSlot = null;
        }

        private static string ProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        }

        private void OnDestroy()
        {
            IsOpen = false;
            Time.timeScale = 1f;
            _swapRunner.Cancel();          // 审查: 防孤儿管线进程
            _aero.Cancel();                // 审查: 防孤儿 vspaero 进程
            _preview?.Dispose();
            _preview = null;
            _worker?.Dispose();
        }

        private void AppendLog(string s)
        {
            _log.Add(s);
            if (_log.Count > 200) _log.RemoveAt(0);
            _logScroll.y = float.MaxValue;
        }

        // ================= 布局 =================
        private void OnGUI()
        {
            var prevSkin = GUI.skin;
            int prevDepth = GUI.depth;
            Color prevColor = GUI.color, prevBackground = GUI.backgroundColor;
            Color prevContent = GUI.contentColor;
            Matrix4x4 prevMatrix = GUI.matrix;
            bool prevEnabled = GUI.enabled;
            try
            {
                GUI.depth = -200;
                GUI.color = GUI.backgroundColor = GUI.contentColor = Color.white;
                GUI.matrix = Matrix4x4.identity;
                GUI.enabled = true;
                if (!_open) { DrawEntryButton(); return; }
                GUI.skin = WorkshopSkin.Get();

                DrawFullscreen();

                // 弹层 (最上层): 编辑窗 / 槽位下拉 / 确认 / 命名 / 翼型·材质选择器
                for (int i = 0; i < _editors.Count; i++)
                {
                    var e = _editors[i];
                    // 自绘标题窗口: IMGUI 标题栏间距不可控 (实测 26 重叠/34 空带),
                    // WindowFlat 皮肤无标题栏, 标题由内容首行 WindowTitle 绘制
                    e.Rect = GUILayout.Window(9540 + i, e.Rect,
                        id => TitledBody(e.Title, e.Draw, id), "", WorkshopSkin.WindowFlat);
                }
                _editors.RemoveAll(e => e.CloseRequested);
                if (_ctlEditor.Wing != null && !_ctlEditor.CloseRequested)
                    _ctlEditor.Rect = GUILayout.Window(9539, _ctlEditor.Rect,
                        id => TitledBody("操纵面 — " + (_ctlEditor.Ctl != null ? _ctlEditor.Ctl.name : ""),
                                         _ctlEditor.Draw, id),
                        "", WorkshopSkin.WindowFlat);
                DrawOpenSlotDropdown();
                if (_colorPicker.Draw(this, _state)) { /* 选色弹层开启中 */ }
                if (_airfoilPickerFor != null) DrawAirfoilPicker();
                if (_colorTarget != null) DrawColorPicker();
                if (_confirmText != null) DrawConfirm();
                if (_namingSlot != null) DrawNaming();
            }
            finally
            {
                GUI.skin = prevSkin;
                GUI.depth = prevDepth;
                GUI.color = prevColor;
                GUI.backgroundColor = prevBackground;
                GUI.contentColor = prevContent;
                GUI.matrix = prevMatrix;
                GUI.enabled = prevEnabled;
            }
        }

        /// <summary>WindowFlat 窗口的内容包装: 首行自绘标题, 其余为原窗口体。</summary>
        private static void TitledBody(string title, GUI.WindowFunction body, int id)
        {
            WorkshopUiKit.WindowTitle(title);
            body(id);
        }

        private GUIStyle _entryStyle;
        private Texture2D _entryTex;

        /// <summary>屏幕入口按钮 (左上角 K 键位提示右侧; F8 等效)。
        /// Fluent 深色半透明 (贴在 3D 场景顶栏, 与 HUD chip 同族; 2026-10-09 改版)。</summary>
        private void DrawEntryButton()
        {
            float k = HudLayout.Scale();
            if (_entryTex == null)
                _entryTex = FlightHUD.RoundedRect(48, 4, FluentTheme.DarkControl,
                    FluentTheme.DarkControl, FluentTheme.DarkBorder, 1f);
            if (_entryStyle == null)
            {
                _entryStyle = new GUIStyle
                {
                    normal = { background = _entryTex, textColor = FluentTheme.DarkAccent },
                    hover = { background = _entryTex, textColor = FluentTheme.DarkText },
                    active = { background = _entryTex, textColor = FluentTheme.DarkAccent },
                    border = new RectOffset(12, 12, 12, 12),
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = Mathf.RoundToInt(13f * k),
                    fontStyle = FontStyle.Bold,
                };
            }
            _entryStyle.fontSize = Mathf.Max(8, Mathf.RoundToInt(13f * k));
            var r = HudLayout.WorkshopEntryRect(k);
            if (GUI.Button(r, "机型工坊 (F8)", _entryStyle))
                _toggleRequested = true;
        }

        // ================= 主窗口 (UI 优化 U1: 可拖动浮动窗) =================
        private Rect _winRect = new Rect(120, 80, 1080, 660);
        private bool _winRectInit;
        private GUIStyle _titleStyle;

        private enum Col1Tab { File, Compute, Preview }
        private Col1Tab _col1Tab = Col1Tab.File;
        private Vector2 _fileScroll;
        private Vector2 _aeroLogScroll;   // 算力页运行日志滚动 (原栏②, 迁入页签)
        private readonly WorkshopColorPicker _colorPicker = new WorkshopColorPicker();

        /// <summary>紧凑模式: 小视口 (<1180×680) 时区一增加「预览」页, 预览内嵌。</summary>
        private bool Compact
        {
            get { return Screen.width < 1180f || Screen.height < 680f; }
        }

        private void DrawFullscreen()
        {
            if (!_winRectInit) { InitWinRect(); _winRectInit = true; }
            ClampWindowIntoView();
            // The root uses absolute areas, so keep its size independent of GUILayout.
            _winRect = GUI.Window(9530, _winRect, DrawWindowBody, "");
        }

        private void InitWinRect()
        {
            // 首开默认: 视口 84% 居中, 上限 1200×760, 四周 ≥24px 场景可见
            float w = Mathf.Min(Screen.width * 0.84f, 1200f);
            float h = Mathf.Min(Screen.height * 0.84f, 760f);
            _winRect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
        }

        /// <summary>窗口钳制 (UW03): 尺寸异常恢复默认; 位置至少保证标题栏+关闭钮可见。</summary>
        private void ClampWindowIntoView()
        {
            if (float.IsNaN(_winRect.x) || float.IsNaN(_winRect.width) ||
                _winRect.width < 720f || _winRect.height < 420f)
                InitWinRect();
            _winRect.width = Mathf.Clamp(_winRect.width, 720f, Mathf.Max(720f, Screen.width - 48f));
            _winRect.height = Mathf.Clamp(_winRect.height, 420f, Mathf.Max(420f, Screen.height - 48f));
            _winRect.x = Mathf.Clamp(_winRect.x, 120f - _winRect.width, Screen.width - 120f);
            _winRect.y = Mathf.Clamp(_winRect.y, 0f, Mathf.Max(0f, Screen.height - 40f));
        }

        private void DrawWindowBody(int id)
        {
            float w = _winRect.width, h = _winRect.height;

            // ---- 标题栏 (V 轮: 标题加粗单行 + 状态弱化右对齐; 仅空白区响应拖动) ----
            float rightBtns = (!_worker.IsAlive ? 104f : 0f) + 94f;
            if (_titleStyle == null)
                _titleStyle = new GUIStyle(GUI.skin.label)
                { fontStyle = FontStyle.Bold, fontSize = 15, normal = { textColor = WorkshopSkin.Green } };
            GUI.Label(new Rect(14, 7, Mathf.Max(80f, w - rightBtns - 16f), 22),
                string.Format("机型工坊 — {0}{1}", _state.projectName, _state.dirty ? " *" : ""),
                _titleStyle);
            string status = string.Format("质量 {0:F2} kg · worker {1:F0} ms · {2}",
                _state.mass.massKg,
                _worker.LastRoundtripMs < 0 ? 0 : _worker.LastRoundtripMs,
                _swapRunner.IsRunning ? "换机导入中" : _aero.Running ? "气动计算中" : "就绪");
            GUI.Label(new Rect(14, 28, Mathf.Max(80f, w - rightBtns - 16f), 16), status,
                      WorkshopUiKit.DimRight);
            if (!_worker.IsAlive && GUI.Button(new Rect(w - 208, 7, 100, 24), "重启服务"))
            {
                _workerReady = false;
                if (_worker.Restart(_projectRoot)) AppendLog("[worker] 已重启");
                else AppendLog("[worker] 重启失败: " + _worker.LastError);
            }
            if (GUI.Button(new Rect(w - 102, 7, 90, 24), new GUIContent("✕ 关闭", "关闭工坊 (F8)")))
                _toggleRequested = true;
            GUI.DragWindow(new Rect(0, 0, w, 30));   // 只标题栏空白区

            const float titleH = 50f, botH = 96f;   // 增高: 日志可见行更多
            float bodyY = titleH, bodyH = h - titleH - botH;

            if (Compact)
            {
                // 紧凑: [区一页签(文件/算力/预览) | 区二建模]; 预览页时区一占满
                bool fullPreview = _col1Tab == Col1Tab.Preview;
                float x1 = fullPreview ? w : Mathf.Round(w * 0.55f);
                GUILayout.BeginArea(new Rect(0, bodyY, x1 - 3, bodyH), GUI.skin.box);
                DrawColumn1();
                GUILayout.EndArea();
                if (!fullPreview)
                {
                    GUILayout.BeginArea(new Rect(x1 + 3, bodyY, w - x1 - 3, bodyH), GUI.skin.box);
                    DrawModelingArea();
                    GUILayout.EndArea();
                }
            }
            else
            {
                // 常规: 区一(页签: 文件/算力) | 区二建模 | 区三预览
                float x1 = Mathf.Round(w * _col1Frac);
                float x2 = Mathf.Round(w * (_col1Frac + _col2Frac));
                GUILayout.BeginArea(new Rect(0, bodyY, x1 - 3, bodyH), GUI.skin.box);
                DrawColumn1();
                GUILayout.EndArea();
                GUILayout.BeginArea(new Rect(x1 + 4, bodyY, x2 - x1 - 8, bodyH), GUI.skin.box);
                DrawModelingArea();
                GUILayout.EndArea();
                GUILayout.BeginArea(new Rect(x2 + 4, bodyY, w - x2 - 6, bodyH), GUI.skin.box);
                DrawColumn3();
                GUILayout.EndArea();

                // 分隔条 (UI06 修复: 各自唯一 controlID)
                int divA = GUIUtility.GetControlID("wsDivA".GetHashCode(), FocusType.Passive);
                int divB = GUIUtility.GetControlID("wsDivB".GetHashCode(), FocusType.Passive);
                DragDivider(new Rect(x1 - 4, bodyY, 9, bodyH), divA, d =>
                {
                    _col1Frac = Mathf.Clamp(_col1Frac + d / w, 250f / w, 0.45f);
                });
                DragDivider(new Rect(x2 - 4, bodyY, 9, bodyH), divB, d =>
                {
                    _col2Frac = Mathf.Clamp(_col2Frac + d / w, 300f / w, 0.95f - _col1Frac);
                });
            }

            DrawBottomBar(new Rect(4, h - botH, w - 8, botH));
            DrawResizeHandle(w, h);
        }

        private void DrawResizeHandle(float w, float h)
        {
            var r = new Rect(w - 20, h - 20, 17, 17);
            int cid = GUIUtility.GetControlID("wsResize".GetHashCode(), FocusType.Passive);
            GUI.Box(r, "◢");
            var e = Event.current;
            if (e.type == EventType.MouseDown && r.Contains(e.mousePosition))
            {
                GUIUtility.hotControl = cid;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && GUIUtility.hotControl == cid)
            {
                _winRect.width += e.delta.x;
                _winRect.height += e.delta.y;   // ClampWindowIntoView 每帧钳制
                e.Use();
            }
            else if (e.type == EventType.MouseUp && GUIUtility.hotControl == cid)
            {
                GUIUtility.hotControl = 0;
                e.Use();
            }
        }

        /// <summary>槽位外观颜色色块 (18×18, 点击开选色弹层; 未分配禁用)。</summary>
        private void DrawSlotColorSwatch(WorkshopState.SlotInfo s)
        {
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = ParseColor(s.color);
            UnityEngine.GUI.enabled = s.geomId != null;
            if (GUILayout.Button("", GUILayout.Width(20), GUILayout.Height(20)))
            {
                var r = GUILayoutUtility.GetLastRect();
                r.position = GUIUtility.GUIToScreenPoint(r.position);   // U14c
                _colorPicker.Open(s.key, ParseColor(s.color), r);
            }
            UnityEngine.GUI.enabled = true;
            GUI.backgroundColor = prev;
        }

        /// <summary>应用槽位外观颜色 (A4/A5: 只改该槽材质实例 + dirty; 不走几何链路,
        /// 不失效气动结果; sidecar 已存 SlotInfo.color)。</summary>
        public void ApplySlotColor(string slot, string hexNoHash)
        {
            var s = _state.Slot(slot);
            if (s == null) return;
            s.color = "#" + (hexNoHash ?? "").TrimStart('#');
            _state.dirty = true;
            _preview?.SetSlotColor(slot, ParseColor(s.color));
            // U11: 预览还没装载该槽网格时 (刚重开工坊/换机后), 选色是静默空操作 —
            // 触发一次网格刷新, 让颜色立即以新值呈现
            if (_preview != null && !_preview.HasSlot(slot))
                ScheduleMeshRefresh();
        }

        private void TabButton(string label, Col1Tab tab)
        {
            bool cur = _col1Tab == tab;
            string text = (cur ? "▶ " : "  ") + label;
            var prev = GUI.backgroundColor;
            if (cur) GUI.backgroundColor = FluentTheme.LightPressed;
            // V 轮: 页签等宽平分栏宽
            if (GUILayout.Button(text, GUILayout.Height(26), GUILayout.ExpandWidth(true)))
                _col1Tab = tab;
            GUI.backgroundColor = prev;
        }

        private static void DragDivider(Rect r, int ctrlId, System.Action<float> onDelta)
        {
            var e = Event.current;
            // 可视中线 (V 轮: 让用户看出此处可拖), 命中区仍为整条 9px
            bool hot = GUIUtility.hotControl == ctrlId || r.Contains(e.mousePosition);
            var acc = FluentTheme.LightAccent;
            GUI.color = hot ? new Color(acc.r, acc.g, acc.b, 0.55f)
                            : new Color(acc.r, acc.g, acc.b, 0.20f);
            GUI.DrawTexture(new Rect(r.x + r.width * 0.5f - 1f, r.y + 6f, 2f, r.height - 12f),
                            WorkshopUiKit.LineTex);
            GUI.color = Color.white;
            if (e.type == EventType.MouseDown && r.Contains(e.mousePosition))
            {
                GUIUtility.hotControl = ctrlId;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && GUIUtility.hotControl == ctrlId)
            {
                onDelta(e.delta.x);
                e.Use();
            }
            else if (e.type == EventType.MouseUp && GUIUtility.hotControl == ctrlId)
            {
                GUIUtility.hotControl = 0;
                e.Use();
            }
        }

        // ================= 栏① =================
        private static readonly string[] SlotZh = { "机身", "机翼1", "机翼2", "平尾", "垂尾", "配重" };

        private void DrawColumn1()
        {
            // ---- 页眉 (UI02 修复: 文件/算力设置互斥页签, 紧凑模式加预览页) ----
            GUILayout.BeginHorizontal();
            TabButton("文件", Col1Tab.File);
            TabButton("算力设置", Col1Tab.Compute);
            if (Compact) TabButton("预览", Col1Tab.Preview);
            GUILayout.EndHorizontal();
            GUILayout.Space(2);

            if (_col1Tab == Col1Tab.Preview)
            {
                DrawColumn3();   // 紧凑模式: 内嵌 3D 预览 (共享同一 Preview 实例)
                return;
            }
            if (_col1Tab == Col1Tab.Compute)
            {
                _aero.Draw(_state);
                WorkshopUiKit.SectionBar("运行日志");
                _aeroLogScroll = GUILayout.BeginScrollView(_aeroLogScroll, false, true,
                    GUILayout.Height(110));
                int firstL = Mathf.Max(0, _aero.Log.Count - 12);
                for (int i = firstL; i < _aero.Log.Count; i++)
                    GUILayout.Label(_aero.Log[i], WorkshopUiKit.Dim);
                GUILayout.EndScrollView();
                return;
            }

            // ---- 文件页 (独立滚动, 切页保留; V 轮: 2×2 等宽按钮网格, 不随栏宽换行) ----
            _fileScroll = GUILayout.BeginScrollView(_fileScroll, false, true);
            WorkshopUiKit.SectionBar("工程文件");
            switch (WorkshopUiKit.EvenButtonRow("新建", "导入 .vsp3"))
            {
                case 0: DoNew(); break;
                case 1: DoOpen(); break;
            }
            GUILayout.Space(WorkshopUiKit.Gap);
            switch (WorkshopUiKit.EvenButtonRow("保存", "另存为"))
            {
                case 0: DoSave(false); break;
                case 1: DoSave(true); break;
            }
            GUILayout.Space(WorkshopUiKit.Gap);
            if (GUILayout.Button("载入示例 (小型固定翼)", GUILayout.Height(WorkshopUiKit.BtnH)))
                ConfirmIfDirty("载入示例 (R02: 与新建/导入同款数据保护)", () => DoLoadExample());
            GUILayout.Label("当前工程: " + _state.projectName +
                            (_state.dirty ? " (未保存)" : ""), WorkshopUiKit.Dim);

            DrawFileLibrary();
            GUILayout.Space(2);
            GUILayout.EndScrollView();   // 文件页滚动收尾 (切页保留位置)
        }

        /// <summary>区二 · 模型建模区 (独立区域, 切页不清空)。
        /// V 轮: 列宽自适应 (部件选择列吃余量), 表头弱化+分隔线, 行高统一。</summary>
        private void DrawModelingArea()
        {
            WorkshopUiKit.SectionBar("模型建模区");
            const float nameW = 42f, swatchW = 24f, editW = 48f, saveW = 66f;
            GUILayout.BeginHorizontal();
            GUILayout.Label("部件", WorkshopUiKit.TableHead, GUILayout.Width(nameW));
            GUILayout.Label("部件选择", WorkshopUiKit.TableHead, GUILayout.Width(SlotDropdownWidth));
            GUILayout.Label("色", WorkshopUiKit.TableHead, GUILayout.Width(swatchW));
            GUILayout.Label("编辑", WorkshopUiKit.TableHead, GUILayout.Width(editW));
            GUILayout.Label("存部件", WorkshopUiKit.TableHead, GUILayout.Width(saveW));
            GUILayout.EndHorizontal();
            WorkshopUiKit.Divider();

            for (int i = 0; i < _state.slots.Count; i++)
            {
                var s = _state.slots[i];
                GUILayout.BeginHorizontal(GUILayout.Height(26));
                GUILayout.Label(SlotZh[i], GUILayout.Width(nameW), GUILayout.Height(24));
                DrawSlotPopup(s);
                DrawSlotColorSwatch(s);                       // U3: 外观颜色入口
                using (new WorkshopUiKit.EnabledScope(s.geomId != null))
                {
                    if (GUILayout.Button("编辑", GUILayout.Width(editW), GUILayout.Height(24)))
                        OpenEditor(s);
                    if (GUILayout.Button("保存部件", GUILayout.Width(saveW), GUILayout.Height(24)))
                    {
                        _namingSlot = s.key;
                        _namingText = s.geom ?? "";
                    }
                }
                GUILayout.EndHorizontal();
            }
        }

        /// <summary>机型库 (文件页内容)。V 轮: 名称吃余量, 信息弱化, 操作右对齐。</summary>
        private void DrawFileLibrary()
        {
            WorkshopUiKit.SectionBar("机型库 (已算好的机型, 免重算换机)");
            GUILayout.BeginHorizontal();
            GUILayout.Label(string.Format("共 {0} 个", _libEntries.Count), WorkshopUiKit.Dim);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("刷新", GUILayout.Width(56), GUILayout.Height(22))) RefreshLibrary();
            GUILayout.EndHorizontal();
            _libScroll = GUILayout.BeginScrollView(_libScroll, false, true,
                GUILayout.Height(110));
            string swapDir = null;
            foreach (var e in _libEntries)
            {
                GUILayout.BeginHorizontal(GUILayout.Height(24));
                string warn = e.WarningCount > 0 ? " ⚠" + e.WarningCount : "";
                GUILayout.Label(e.DisplayName + warn, GUILayout.MinWidth(60),
                                GUILayout.ExpandWidth(true));
                GUILayout.Label(e.HasReport
                    ? string.Format("{0:F1}kg  b={1:F1}m  S={2:F1}m²", e.MassKg, e.BrefM, e.SrefM2)
                    : "(无 report)", WorkshopUiKit.DimRight, GUILayout.Width(150));
                if (GUILayout.Button("换上", GUILayout.Width(46), GUILayout.Height(22)))
                    swapDir = e.Dir;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (swapDir != null) SwapFromLibrary(swapDir);
        }

        private List<WorkshopLibrary.LibraryEntry> _libEntries =
            new List<WorkshopLibrary.LibraryEntry>();
        private Vector2 _libScroll;

        private void RefreshLibrary()
        {
            _libEntries = WorkshopLibrary.ScanLibrary(Path.Combine(_projectRoot, "VspImports"));
        }

        private void SwapFromLibrary(string dir)
        {
            // U7 互斥: 管线换机进行中直接换库机型 → FinishSwap 稍后再次换机,
            // 飞行中飞机被静默重置; 算例运行中换机 → 双进程 CPU 争用
            if (_swapRunner.IsRunning || _swapPreparing)
            {
                AppendLog("[机型库] 换机管线进行中, 请等完成后再从库换机");
                return;
            }
            if (_aero.Running)
            {
                AppendLog("[机型库] 气动计算进行中, 请先停止再换机");
                return;
            }
            string cfg = Path.Combine(dir, "AircraftConfig.json");
            string obj = Path.Combine(dir, "model.obj");
            if (!File.Exists(cfg)) { AppendLog("[机型库] 找不到 " + cfg); return; }
            // U7: 库目录可能被手改/残留坏 config — 换机前先验解析 (同 FinishSwap)
            try { AircraftConfigLoader.Parse(File.ReadAllText(cfg)); }
            catch (System.Exception ex)
            {
                AppendLog("[机型库] 配置校验失败, 不换机: " + ex.Message);
                return;
            }
            AircraftSwapper.Swap(cfg, File.Exists(obj) ? obj : "", LoadColorMap(dir));
            AppendLog("[机型库] 已换机 ← " + Path.GetFileName(dir));
            if (_open) ToggleOpen();
        }

        /// <summary>打开 (或聚焦) 槽位几何的编辑窗, 并拉取 summary。W2。</summary>
        private void OpenEditor(WorkshopState.SlotInfo s)
        {
            var exist = _editors.Find(e => e.GeomId == s.geomId);
            if (exist != null) { exist.Rect = new Rect(120 + 30 * _editors.Count, 90, exist.Rect.width, exist.Rect.height); return; }
            WorkshopEditorWindow win;
            bool isWing = s.key == "wing1" || s.key == "wing2" || s.key == "htail" || s.key == "vtail";
            if (isWing)
                win = new WingEditorWindow(this, s.geomId, SlotZh[_state.slots.IndexOf(s)] + " · " + s.geom,
                                           s.key == "vtail", s.key);
            else if (s.key == "fuselage")
                win = new FuselageEditorWindow(this, s.geomId, "机身 · " + s.geom);
            else
                win = new BallastEditorWindow(this, s.geomId, "配重 · " + s.geom);
            _editors.Add(win);
            RefreshEditorSummary(win);
        }

        private void RefreshEditorSummary(WorkshopEditorWindow win)
        {
            _worker.Call("get_geom_summary",
                WorkshopReq.Obj().S("geom_id", win.GeomId).Str(), r =>
            {
                if (!r.ok) { AppendLog("[编辑] " + r.error); return; }
                var f = win as FuselageEditorWindow;
                if (f != null) { f.LoadFromSummary(r.dataJson); return; }
                var b = win as BallastEditorWindow;
                if (b != null) { b.LoadFromSummary(r.dataJson); return; }
                var wg = win as WingEditorWindow;
                if (wg != null)
                {
                    wg.LoadFromSummary(r.dataJson);
                    wg.airfoilNames = WorkshopWidgets.ItemNames(_airfoilJson);
                }
            });
        }

        /// <summary>GUI.enabled 的 using 作用域 (Unity 无内置版本)。</summary>
        private struct GUIEnabledScope : System.IDisposable
        {
            private readonly bool _prev;
            public GUIEnabledScope(bool enabled) { _prev = GUI.enabled; GUI.enabled = enabled; }
            public void Dispose() { GUI.enabled = _prev; }
        }

        /// <summary>槽位下拉固定宽度 (U14: 用户要求下拉框宽度固定; U15: 180px)。</summary>
        private const float SlotDropdownWidth = 180f;

        /// <summary>槽位下拉: 三类分组 — 「无」/ 现有部件 / 新建 (槽位默认参数) (U14; 自绘弹层)。
        /// U16 (用户反馈): 当前部件置顶标 ✓; 被替代/停用的部件标「已停用」保留在列表中,
        /// 选中即重新启用 (assign_slot 恢复显示与集合)。</summary>
        private void DrawSlotPopup(WorkshopState.SlotInfo s)
        {
            string curLabel = s.geomId != null
                ? (s.geom + " (" + s.typeName + ")")
                : "无";
            // GetRect+GUI.Button: 矩形在布局/重绘/点击事件中一致
            // (GetLastRect 在部分事件返回 (0,0,0,0), 弹层会跑到左上角)
            // 按钮宽度固定 (U14: 下拉框宽度固定, 不随栏宽伸缩)
            var btnRect = GUILayoutUtility.GetRect(new GUIContent(curLabel + " ▾"),
                GUI.skin.button, GUILayout.Width(SlotDropdownWidth), GUILayout.Height(24));
            if (GUI.Button(btnRect, curLabel + " ▾"))
            {
                if (_openSlot == s.key) { _openSlot = null; return; }
                _openSlot = s.key;
                // U17 修复 v2: 捕获时 GUIToScreenPoint (自动穿透 窗口+BeginArea 嵌套
                // 与编辑器 Game 视图缩放/偏移), 绘制时 ScreenToGUIPoint 换回 GUI 空间。
                // 之前手工加 _winRect 偏移漏了 BeginArea(建模区) 一层 — 弹层错位。
                _openSlotScreenPos = GUIUtility.GUIToScreenPoint(btnRect.position);
                _openSlotBtnH = btnRect.height;
                BuildSlotDropdownEntries(s, _openLabels, _openActions);
            }
        }

        /// <summary>构建槽位下拉条目 (labels/actions 平行数组; 抽出供 PlayMode 测试)。</summary>
        private void BuildSlotDropdownEntries(WorkshopState.SlotInfo s,
            List<string> labels, List<string> actions)
        {
            labels.Clear();
            actions.Clear();

            // 第一类: 无 (不激活该部件; 直接一项, 不加表头)
            labels.Add("无"); actions.Add("");

            // 第二类: 现有部件 — 当前(置顶 ✓) → 未分配可见 → 已停用(被替代) → 部件库
            labels.Add("现有部件"); actions.Add("hdr:");
            int existing = 0;
            foreach (var g in _state.geoms)
            {
                if (g.slot != s.key) continue;   // 当前部件 (本槽位激活中)
                labels.Add("✓ " + g.name + " (" + g.type + ") — 当前");
                actions.Add("cur:");
                existing++;
            }
            foreach (var g in _state.geoms)
            {
                if (g.slot != null || g.hidden ||
                    !WorkshopState.TypeFitsSlot(s.key, g.type)) continue;
                labels.Add(g.name + " (" + g.type + ")");
                actions.Add("geom:" + g.id);
                existing++;
            }
            foreach (var g in _state.geoms)
            {
                if (g.slot != null || !g.hidden ||
                    !WorkshopState.TypeFitsSlot(s.key, g.type)) continue;
                // 已停用 = 被替代/选"无"后隐藏; 选中即重新启用
                labels.Add(g.name + " (" + g.type + ") — 已停用");
                actions.Add("geom:" + g.id);
                existing++;
            }
            string libType = s.key == "fuselage" ? "fuselage"
                           : s.key == "ballast" ? "ballast" : "wing";
            foreach (var li in _state.library)
            {
                if (li.type != libType) continue;
                labels.Add("[库] " + li.name);
                actions.Add("lib:" + li.path);
                existing++;
            }
            if (existing == 0)
            {
                labels.Add("(暂无可用部件)");
                actions.Add("dis:");
            }

            // 第三类: 新建 (槽位默认参数)
            labels.Add("新建"); actions.Add("hdr:");
            labels.Add("新建" + SlotZh[_state.slots.IndexOf(s)] + " (默认参数)");
            actions.Add("add:");
        }

        /// <summary>槽位下拉弹层 (固定宽度; 三类分组表头; 点选执行, 点外部关闭)。
        /// U15 修复: 必须用 GUI.Window 绘制 — 普通 GUI.Box/Button 会渲染在主窗口
        /// 下面 (弹层被主窗遮住, 表现为"点击没有反应"); 与工坊其它弹层一致。</summary>
        private void DrawOpenSlotDropdown()
        {
            if (_openSlot == null) return;
            const float lh = 24f, headerH = 20f;
            // U17v2: 屏幕坐标 → 顶层 GUI 坐标 (ScreenToGUIPoint 处理 Game 视图
            // 偏移与缩放); 钳制在 GUI 空间做 (Screen.width 即 GUI 宽)
            Vector2 guiPos = GUIUtility.ScreenToGUIPoint(_openSlotScreenPos);
            float x = Mathf.Clamp(guiPos.x, 4f,
                Mathf.Max(4f, Screen.width - SlotDropdownWidth - 6f));
            float listH = 0f;
            for (int i = 0; i < _openLabels.Count; i++)
                listH += _openActions[i] == "hdr:" ? headerH : lh;
            const float maxH = 330f;
            var screenRect = new Rect(x, guiPos.y + _openSlotBtnH + 2f,
                                      SlotDropdownWidth, Mathf.Min(listH + 8f, maxH));
            var e = Event.current;
            if (e.type == EventType.MouseDown && !screenRect.Contains(e.mousePosition))
            {
                _openSlot = null;
                return;
            }
            // box 样式窗口: 无标题栏; 窗内为局部坐标
            GUI.Window(9527, screenRect, DrawSlotDropdownBody, GUIContent.none, GUI.skin.box);
        }

        /// <summary>下拉弹层内容 (窗内局部坐标; 分组表头/禁用项/悬停高亮/点击执行)。</summary>
        private void DrawSlotDropdownBody(int winId)
        {
            const float lh = 24f, headerH = 20f;
            float listH = 0f;
            for (int i = 0; i < _openLabels.Count; i++)
                listH += _openActions[i] == "hdr:" ? headerH : lh;
            float w = SlotDropdownWidth - 4f;
            bool scroll = listH > 322f;
            var e = Event.current;
            var headerStyle = WorkshopUiKit.Dim ?? GUI.skin.label;
            Rect viewRect = new Rect(2f, 2f, w, 322f);
            Rect contentRect = new Rect(0, 0, w - (scroll ? 14f : 0f), listH);
            if (scroll)
                _openSlotScroll = GUI.BeginScrollView(viewRect, _openSlotScroll, contentRect);
            float y = 2f;
            float rowW = scroll ? contentRect.width : w;
            for (int i = 0; i < _openLabels.Count; i++)
            {
                bool isHeader = _openActions[i] == "hdr:";
                float h = isHeader ? headerH : lh;
                var rowRect = new Rect(scroll ? 0f : 2f, y, rowW, h);
                y += h;
                if (isHeader)
                {
                    GUI.Label(rowRect, "— " + _openLabels[i] + " —", headerStyle);
                    continue;
                }
                if (_openActions[i] == "dis:")
                {
                    var prevD = GUI.color;
                    GUI.color *= new Color(1f, 1f, 1f, 0.45f);
                    GUI.Label(rowRect, _openLabels[i]);
                    GUI.color = prevD;
                    continue;
                }
                // 悬停行高亮 (V 轮: 按钮皮肤 hover 只改字色, 列表项需要底色反馈)
                if (rowRect.Contains(e.mousePosition))
                {
                    var acc = FluentTheme.LightAccent;
                    GUI.color = new Color(acc.r, acc.g, acc.b, 0.14f);
                    GUI.DrawTexture(rowRect, WorkshopUiKit.LineTex);
                    GUI.color = Color.white;
                }
                if (GUI.Button(rowRect, _openLabels[i]))
                {
                    string act = _openActions[i];
                    string slot = _openSlot;
                    _openSlot = null;
                    if (act == "") DoClearSlot(slot);
                    else if (act == "cur:") { /* 当前部件: 仅关闭弹层, 不变更 */ }
                    else if (act.StartsWith("geom:")) DoAssignSlot(slot, act.Substring(5));
                    else if (act.StartsWith("lib:")) DoInsertPart(slot, act.Substring(4));
                    else if (act.StartsWith("add:")) DoAddPart(slot);
                }
            }
            if (scroll) GUI.EndScrollView();
        }

        /// <summary>新建部件入槽 (U14: 下拉第三类; worker add_part 用槽位默认参数)。</summary>
        private void DoAddPart(string slot)
        {
            _worker.Call("add_part", WorkshopReq.Obj().S("slot", slot).Str(), r =>
            {
                if (!r.ok) { AppendLog("[新建] " + r.error); return; }
                OnModelChanged(r.dataJson);
                AppendLog("[新建] 已创建 " + slot + " 默认部件");
            });
        }

        // ================= 栏③ =================
        private void DrawColumn3()
        {
            WorkshopUiKit.SectionBar("实时外形展示");
            Rect rt = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                if (rt.width > 16 && rt.height > 16 && _preview != null)
                {
                    _preview.RebuildRt(Mathf.RoundToInt(rt.width), Mathf.RoundToInt(rt.height));
                    if (_preview.Rt != null) GUI.DrawTexture(rt, _preview.Rt, ScaleMode.StretchToFill);
                    HandlePreviewInput(rt);
                    DrawCgNpAnnotation(rt);
                }
            else GUILayout.Label("预览待 worker 联通后可用", WorkshopUiKit.Dim);
            // 操作提示放在预览底部 (画在深色预览纹理上, 用深色主题的弱化色)
            var hint = new Rect(rt.x, rt.yMax - 18f, rt.width, 16f);
            if (_previewHintStyle == null)
                _previewHintStyle = new GUIStyle(GUI.skin.label)
                { fontSize = 12, alignment = TextAnchor.MiddleRight,
                  normal = { textColor = FluentTheme.DarkTextDim } };
            GUI.Label(hint, "拖动旋转 · 滚轮缩放 · 静止 3s 后自动旋转", _previewHintStyle);
        }

        private GUIStyle _previewHintStyle;

        private void HandlePreviewInput(Rect rt)
        {
            var e = Event.current;
            if (e.type == EventType.MouseDrag && rt.Contains(e.mousePosition) && _preview != null)
            {
                _preview.Orbit(e.delta.x * 0.4f, -e.delta.y * 0.4f);
                e.Use();
            }
            else if (e.type == EventType.ScrollWheel && rt.Contains(e.mousePosition) && _preview != null)
            {
                _preview.Zoom(e.delta.y > 0 ? 1.12f : 0.89f);
                e.Use();
            }
        }

        /// <summary>CG/NP 标注 (提示词 §7): CG 红球+文字; NP 蓝菱形+CG-NP 连线+SM 警告。</summary>
        private void DrawCgNpAnnotation(Rect rt)
        {
            if (_preview == null) return;
            Vector2 cgPx = Vector2.zero;
            bool hasCg = false;
            if (_state.mass.massKg > 0f)
            {
                Vector3 unityCg = WorkshopStlLoader.ToUnity(_state.mass.cgOvsp, Vector3.zero);
                Vector3 world = new Vector3(unityCg.x, -5000f + unityCg.y, unityCg.z);
                Vector3 p = _preview.WorldToRtPixels(world);
                if (p.z > 0f)
                {
                    cgPx = new Vector2(rt.x + p.x, rt.yMax - p.y);
                    hasCg = true;
                    string cgText = string.Format("CG ({0:F2}, {1:F2}, {2:F2}) m",
                        _state.mass.cgOvsp.x, _state.mass.cgOvsp.y, _state.mass.cgOvsp.z);
                    // V 轮: 标注文字带半透明底片, 网格背景上也可读
                    var cgSize = WorkshopUiKit.Chip.CalcSize(new GUIContent(cgText));
                    WorkshopUiKit.ChipLabel(
                        new Rect(cgPx.x + 8, cgPx.y - 10, cgSize.x + 4, cgSize.y + 2), cgText);
                    GUI.Box(new Rect(cgPx.x - 3, cgPx.y - 3, 6, 6), "");
                }
            }

            // NP: VSPAERO 结果优先, 否则升力线估算 (提示词 §7 标注来源)
            float xnp = float.NaN;
            bool fromVspaero = false;
            if (_aero.ResultJson.Length > 0)
            {
                float v = WorkshopJson.Num(_aero.ResultJson, "Xnp_m", float.NaN);
                if (!float.IsNaN(v)) { xnp = v; fromVspaero = true; }
            }
            if (float.IsNaN(xnp) && !float.IsNaN(_npEstimateM)) xnp = _npEstimateM;
            if (!float.IsNaN(xnp) && _state.mass.massKg > 0f)
            {
                var ovsp = new Vector3(xnp, _state.mass.cgOvsp.y, _state.mass.cgOvsp.z);
                Vector3 u = WorkshopStlLoader.ToUnity(ovsp, Vector3.zero);
                Vector3 world = new Vector3(u.x, -5000f + u.y, u.z);
                Vector3 p = _preview.WorldToRtPixels(world);
                if (p.z > 0f)
                {
                    var npPx = new Vector2(rt.x + p.x, rt.yMax - p.y);
                    var box = new Rect(npPx.x - 5, npPx.y - 5, 10, 10);
                    GUI.color = new Color(0.35f, 0.75f, 1f);
                    GUI.Box(box, "");
                    GUI.color = Color.white;
                    float sm = (xnp - _state.mass.cgOvsp.x) /
                               Mathf.Max(1e-3f, _aero.cref) * 100f;
                    // F3: 估算系统性偏保守 (实测比 VSPAERO 靠前可达 ~1 MAC), 标注出处与倾向
                    string smTag = sm < 0f
                        ? (fromVspaero ? "⚠ 静不稳定" : "估算为负·偏保守 (以气动计算为准)")
                        : sm > 40f ? "⚠ 过稳定" : "";
                    string npText = string.Format("NP x={0:F3} ({1})  SM={2:+0.0;-0.0}% {3}",
                        xnp, fromVspaero ? "VSPAERO" : "估算·偏保守", sm, smTag);
                    var npSize = WorkshopUiKit.Chip.CalcSize(new GUIContent(npText));
                    WorkshopUiKit.ChipLabel(
                        new Rect(npPx.x + 8, npPx.y - 10, npSize.x + 4, npSize.y + 2), npText);
                    if (hasCg) GUILine(cgPx, npPx, new Color(1f, 0.6f, 0.2f, 0.9f));
                }
            }
        }

        private static Texture2D _lineTex;

        /// <summary>IMGUI 线段 (RotateAroundPivot + 1×1 纹理, HUD 同思路)。</summary>
        private static void GUILine(Vector2 a, Vector2 b, Color col)
        {
            if (_lineTex == null)
            {
                _lineTex = new Texture2D(1, 1);
                _lineTex.SetPixel(0, 0, Color.white);
                _lineTex.Apply();
            }
            var seg = b - a;
            float len = seg.magnitude;
            if (len < 1f) return;
            float angle = Mathf.Atan2(seg.y, seg.x) * Mathf.Rad2Deg;
            var prev = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, a);
            GUI.color = col;
            GUI.DrawTexture(new Rect(a.x, a.y - 0.75f, len, 1.5f), _lineTex);
            GUI.color = Color.white;
            GUI.matrix = prev;
        }

        // ================= 底部条 =================
        private void DrawBottomBar(Rect r)
        {
            // V 轮: 两行化 — 日志整宽在上, 状态+操作按钮在下右对齐; 窄窗不再互相挤压
            GUILayout.BeginArea(r, GUI.skin.box);
            _logScroll = GUILayout.BeginScrollView(_logScroll, false, true,
                GUILayout.ExpandWidth(true), GUILayout.Height(40));
            int first = Mathf.Max(0, _log.Count - 8);
            for (int i = first; i < _log.Count; i++)
                GUILayout.Label(_log[i], WorkshopUiKit.Dim);
            GUILayout.EndScrollView();
            WorkshopUiKit.Divider();

            string reason = SwapBlockReason();
            GUILayout.BeginHorizontal(GUILayout.Height(30));
            if (!string.IsNullOrEmpty(reason))
                GUILayout.Label("换机不可用: " + reason, WorkshopUiKit.Dim,
                                GUILayout.ExpandWidth(true));
            else
            {
                // 非阻塞警告 (缺槽位/缺轴) 也亮出来, 确认框同款明细
                var warns = SwapWarnings();
                if (warns.Count > 0)
                {
                    if (_swapWarnStyle == null)
                        _swapWarnStyle = new GUIStyle(WorkshopUiKit.Dim)
                        { normal = { textColor = FluentTheme.WarningOnLight } };
                    GUILayout.Label("可换机 · 注意: " + string.Join("; ", warns),
                                    _swapWarnStyle, GUILayout.ExpandWidth(true));
                }
                else
                    GUILayout.Label(_swapRunner.IsRunning ? "" : "模型与结果就绪后可换机试飞",
                                    WorkshopUiKit.Dim, GUILayout.ExpandWidth(true));
            }
            if (_swapRunner.IsRunning && GUILayout.Button("停止", GUILayout.Width(56),
                    GUILayout.Height(26)))
            {
                _swapRunner.Cancel();
                AppendLog("[换机] 已请求取消 (taskkill 进程树)");
            }
            using (var scope = new GUIEnabledScope(
                string.IsNullOrEmpty(reason) && !_swapRunner.IsRunning && !_swapPreparing))
            {
                if (GUILayout.Button(
                        _swapRunner.IsRunning ? "导入中… (" + _swapStage + ")"
                        : _swapPreparing ? "准备副本…" : "生成模拟机型并换机试飞",
                        GUILayout.Width(200), GUILayout.Height(26)))
                    ValidateAndSwap();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private readonly PipelineRunner _swapRunner = new PipelineRunner();
        private string _swapStage = "";
        private string _swapVspPath;
        private bool _swapPreparing;   // 保存副本/清理排除件阶段 (管线尚未启动)

        /// <summary>换机前置校验 (硬性, 空串=可换机)。
        /// 用户规则 (2026-10-09): 飞翼等布局没有平尾/垂尾, 混控布局不要求三轴舵面齐全 —
        /// 硬性条件只保留主机翼/互斥/worker/质量; 缺槽位与缺轴降级为 SwapWarnings 确认。</summary>
        private string SwapBlockReason()
        {
            if (_swapRunner.IsRunning) return "";
            if (_aero.Running) return "气动计算进行中 (先停止再换机, CPU 争用)";   // U7 互斥
            if (_state.Slot("wing1") == null || _state.Slot("wing1").geomId == null)
                return "缺少 机翼1 (至少需要一个主升力面)";
            if (!_workerReady) return "worker 未就绪";
            if (_state.mass.massKg > 0f && _state.mass.massKg < 0.8f)
                return string.Format("总质量 {0:F2} kg 过轻 (数值稳定风险, 请为翼面设置质量)",
                    _state.mass.massKg);
            return "";
        }

        /// <summary>换机非阻塞警告 (用户规则: 缺机身/平尾/垂尾与缺操纵轴不拦截,
        /// 确认后放行 — 混控布局如升降舵/副翼混控允许缺轴)。</summary>
        private List<string> SwapWarnings()
        {
            var w = new List<string>();
            if (_state.Slot("fuselage") == null || _state.Slot("fuselage").geomId == null)
                w.Add("无机身 (飞翼布局)");
            if (_state.Slot("htail") == null || _state.Slot("htail").geomId == null)
                w.Add("无平尾");
            if (_state.Slot("vtail") == null || _state.Slot("vtail").geomId == null)
                w.Add("无垂尾");
            var axes = new HashSet<string>();
            foreach (var g in _state.geoms)
                if (g.subsurfs != null)
                    foreach (var sub in g.subsurfs)
                        if (sub.type == 3)
                        {
                            if (sub.name.EndsWith("Elevator")) axes.Add("pitch");
                            if (sub.name.EndsWith("Aileron")) axes.Add("roll");
                            if (sub.name.EndsWith("Rudder")) axes.Add("yaw");
                        }
            if (!axes.Contains("pitch")) w.Add("无升降舵 (Elevator) → 俯仰通道无气动权限");
            if (!axes.Contains("roll")) w.Add("无副翼 (Aileron) → 滚转通道无气动权限");
            if (!axes.Contains("yaw")) w.Add("无方向舵 (Rudder) → 偏航通道无气动权限");
            return w;
        }

        private void ValidateAndSwap()
        {
            // SM<0 静不稳定 或 存在缺槽位/缺轴警告: 列明细, 明确确认后放行 (提示词 §8.1)
            float smPct = CurrentSmPercent();
            var warns = SwapWarnings();
            // SM 拦截只信实测 (VSPAERO): 估算器系统性偏前 (本项目布局偏前可达 ~1 MAC,
            // 示例机实测 SM=+13% 而估算显示 -114%), 估算负值只会误报不会漏报,
            // 不足以拦换机; 显示层已标"估算·偏保守"
            bool unstable = smPct < 0f && SmMeasured();
            if (unstable || warns.Count > 0)
            {
                var sb = new System.Text.StringBuilder("当前设计:\n");
                if (unstable)
                    sb.Append(string.Format("· 静不稳定 (SM = {0:0.0}% < 0)\n", smPct));
                foreach (var wn in warns) sb.Append("· ").Append(wn).Append('\n');
                sb.Append("确定要继续换机吗?");
                _confirmText = sb.ToString();
                _confirmYes = "仍要换机";
                _confirmAction = DoSwap;
                return;
            }
            DoSwap();
        }

        private float CurrentSmPercent()
        {
            float xnp = float.NaN, cref = _aero.cref, xcg = _state.mass.cgOvsp.x;
            if (_aero.ResultJson.Length > 0)
                xnp = WorkshopJson.Num(_aero.ResultJson, "Xnp_m", float.NaN);
            if (float.IsNaN(xnp)) xnp = _npEstimateM;
            if (float.IsNaN(xnp) || _state.mass.massKg <= 0f) return float.NaN;
            return (xnp - xcg) / Mathf.Max(1e-3f, cref) * 100f;
        }

        /// <summary>当前 SM 是否来自 VSPAERO 实测 (而非估算)。</summary>
        private bool SmMeasured()
        {
            return _aero.ResultJson.Length > 0 &&
                   !float.IsNaN(WorkshopJson.Num(_aero.ResultJson, "Xnp_m", float.NaN));
        }

        /// <summary>保存工作模型 → import_vsp.py 全管线 (prep→3 工况→gen) → Swap (提示词 §8.2/8.3)。
        /// U7: 链上 worker 请求用 180s 超时 (复杂模型 MassProp 可超 30s) 且登记
        /// 请求 id — OnWorkerTimeout 只对链上请求解除准备态, 无关请求超时不再误伤。</summary>
        private readonly HashSet<int> _swapChainIds = new HashSet<int>();

        private void DoSwap()
        {
            string root = _projectRoot;
            string work = Path.Combine(root, "work", "workshop_swap");
            Directory.CreateDirectory(work);
            _swapVspPath = Path.Combine(work, "swap.vsp3").Replace('\\', '/');
            _swapPreparing = true;
            int sid = _worker.Call("save",
                WorkshopReq.Obj().S("path", _swapVspPath).Str(), r =>
            {
                _swapChainIds.Remove(r.id);
                if (!r.ok)
                {
                    _swapPreparing = false;
                    AppendLog("[换机] 保存临时模型失败: " + r.error);
                    return;
                }
                // R06: 副本上物理删除排除件 — 试飞输入只含活动部件 (工坊本体不动)。
                // 必须等它改写完 swap.vsp3 再启动管线, 否则管线会读到未清理/写到一半的副本。
                // U06 兜底: 打开工程后直接换机时 perGeom 还没算过 (LoadMass 只在
                // mass_props 回调里填), 空 mass_map 会丢 Shell 质量 → Ixx≈0 → 地面 NaN。
                if (_state.mass.perGeom.Length == 0)
                {
                    int mid = _worker.Call("mass_props", "{}", mr =>
                    {
                        _swapChainIds.Remove(mr.id);
                        if (mr.ok)
                        {
                            _state.LoadMass(mr.dataJson);
                            AppendLog("[换机] 补算部件质量: " + _state.mass.perGeom.Length + " 件");
                        }
                        else AppendLog("[换机] 质量补算失败 (按原始质量继续): " + mr.error);
                        BakeSwapCopyAndRun(root);
                    }, 180f);
                    if (mid > 0) _swapChainIds.Add(mid);
                }
                else BakeSwapCopyAndRun(root);
            }, 180f);
            if (sid > 0) _swapChainIds.Add(sid);
        }

        /// <summary>NaN 修复: 副本删排除件 + 部件质量烘纯 PointMass (Shell/Density
        /// 口径管线不认且 tess 后丢失), 用工坊 mass_props 的部件质量。</summary>
        private void BakeSwapCopyAndRun(string root)
        {
            var mm = new System.Text.StringBuilder("{");
            for (int i = 0; i < _state.mass.perGeom.Length; i++)
            {
                if (i > 0) mm.Append(',');
                mm.Append(WorkshopJson.Escape(_state.mass.perGeom[i].name)).Append(':')
                  .Append(_state.mass.perGeom[i].massKg.ToString("0.###",
                      System.Globalization.CultureInfo.InvariantCulture));
            }
            mm.Append('}');
            int did = _worker.Call("delete_hidden",
                WorkshopReq.Obj().S("path", _swapVspPath).Raw("mass_map", mm.ToString()).Str(), rh =>
                {
                    _swapChainIds.Remove(rh.id);
                    if (!rh.ok) AppendLog("[换机] 副本清理警告: " + rh.error);
                    else AppendLog(string.Format(
                        "[换机] 副本就绪: 移除排除 {0} 件, 质量规范化 {1} 件 (纯 PointMass)",
                        WorkshopJson.Int(rh.dataJson, "removed", 0),
                        WorkshopJson.Int(rh.dataJson, "mass_normalized", 0)));
                    StartSwapPipeline(root);
                }, 180f);
            if (did > 0) _swapChainIds.Add(did);
        }

        private void StartSwapPipeline(string root)
        {
            _swapPreparing = false;
            // 面板参数 → import_vsp 参数: 配平迎角/马赫/尾迹; 扫描范围用面板值 (全 0/单点时回落管线默认)
            float alpha = _aero.alphaRange.x;
            float mach = _aero.machRange.x;
            if (mach < 1e-4f) mach = 0.06f;   // 不可压默认
            var extra = new System.Text.StringBuilder();
            extra.Append("--wake ").Append(_aero.wakeIter);
            // "--opt=值" 写法: 起点为负 (如 -4,10,7) 时 argparse 会把 "-4,10,7" 当成选项而报错
            if (_aero.alphaRange.z >= 2f)
                extra.Append(" --alpha-range=").Append(_aero.alphaRange.x.ToString("0.##",
                    System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                      .Append(_aero.alphaRange.y.ToString("0.##",
                    System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                      .Append(Mathf.RoundToInt(_aero.alphaRange.z));
            // U7: 接入起落架自动生成 (apply_overrides.auto_gear) — 弹簧/阻尼按
            // 质量缩放, 轮位取整机包围盒 (默认 GEAR_DEFAULT 是 14kg TestUAV 的
            // 固定值, 小飞机轮子会悬在 CG 下 0.35m "高跷" 上)
            string ovPath = Path.Combine(root, "work", "workshop_swap", "overrides.json")
                .Replace('\\', '/');
            try { File.WriteAllText(ovPath, "{\"gear\":{\"auto\":true}}"); }
            catch (System.Exception ex) { AppendLog("[换机] overrides 写入失败: " + ex.Message); }
            string name = wc_AsciiName(_state.projectName);
            _swapRunner.Start(_worker.PythonExe, root, _swapVspPath, name,
                alpha, mach, 0f, false, extra.ToString() + " --overrides \"" + ovPath + "\"");
            AppendLog("[换机] 管线启动: " + name + " (约 1 分钟, 可再点停止)");
        }

        private void OnWorkerTimeout(WorkshopWorkerClient.JsonReply r)
        {
            AppendLog("[超时] " + r.error);
            // U7: 只有换机链上的请求超时才解除准备态 — 之前任意请求超时 (如编辑窗
            // summary 排队超 30s) 都会解锁按钮, 而换机链迟到回复仍会推进, 产生双链
            if (_swapChainIds.Remove(r.id))
            {
                _swapPreparing = false;
                AppendLog("[换机] 准备阶段请求超时, 已取消本轮换机 (可重试)");
            }
            // U8 自愈: worker 进程活着但 >90s 无任何成功应答 = 挂死 (用户实测:
            // 改几次外形参数后所有请求超时, worker 零 CPU 空转 — 任何 C 层阻塞
            // 都会这样)。自动重启恢复服务; 模型回到上次保存, 未保存编辑需重做。
            if (_worker.IsAlive &&
                Time.realtimeSinceStartup - _worker.LastReplyAt > 90f &&
                !_swapRunner.IsRunning && !_swapPreparing &&
                Time.realtimeSinceStartup - _lastWorkerAutoRestart > 120f)
            {
                _lastWorkerAutoRestart = Time.realtimeSinceStartup;
                AppendLog("[worker] 存活但 90s 无应答, 自动重启 (模型回到上次保存, " +
                          "未保存的编辑请重做; 若频繁出现请反馈操作步骤)");
                if (_worker.Restart(_projectRoot))
                {
                    _workerReady = false;
                    _worker.Call("ping", "{}", pr =>
                    {
                        _workerReady = pr.ok;
                        AppendLog(pr.ok ? "[worker] 已重启就绪" : "[worker] 重启后 ping 失败: " + pr.error);
                    });
                }
                else AppendLog("[worker] 自动重启失败: " + _worker.LastError);
            }
        }
        private float _lastWorkerAutoRestart = -9999f;

        /// <summary>工程名 ASCII 化 (与 import_vsp.py sanitize_internal 一致, W4 统一)。</summary>
        private static string wc_AsciiName(string s)
        {
            string r = WorkshopLibrary.SanitizeInternal(s ?? "");
            return string.IsNullOrEmpty(r) || r == "Unnamed" ? "WorkshopAircraft" : "W_" + r;
        }

        /// <summary>[done] 后: 质量一致性校验 (±1%, 提示词 §8.4) → 热换机 → 关闭工坊。</summary>
        private void FinishSwap()
        {
            string dir = _swapRunner.OutDir;
            string cfg = Path.Combine(dir, "AircraftConfig.json");
            string obj = Path.Combine(dir, "model.obj");
            if (!File.Exists(cfg))
            {
                AppendLog("[换机] 未找到 AircraftConfig.json: " + cfg);
                return;
            }
            // 质量比对: 工坊 mass_props vs 管线 MassProp (R12: 质量在嵌套 mass 节)
            string cfgText;
            try { cfgText = File.ReadAllText(cfg); }
            catch (System.IO.IOException ex)
            {
                AppendLog("[换机] 配置读取失败: " + ex.Message);
                return;
            }
            // U7 防御: config 解析失败的新机会让 AircraftController.Awake 抛异常,
            // FixedUpdate 每步 NRE 刷屏 (8.7GB 日志事故模式) — 换机前先验一遍
            try { AircraftConfigLoader.Parse(cfgText); }
            catch (System.Exception ex)
            {
                AppendLog("[换机] 配置校验失败, 不换机: " + ex.Message);
                return;
            }
            string massObj = WorkshopJson.Raw(cfgText, "mass");
            float mCfg = WorkshopJson.Num(massObj, "mass_kg", float.NaN);
            if (!float.IsNaN(mCfg) && _state.mass.massKg > 1e-6f)
            {
                float diff = Mathf.Abs(mCfg - _state.mass.massKg) / _state.mass.massKg * 100f;
                AppendLog(string.Format("[换机] 质量比对: 工坊 {0:F3} kg vs 管线 {1:F3} kg (差 {2:F2}%)",
                    _state.mass.massKg, mCfg, diff));
                if (diff > 1f)
                    AppendLog("[换机] ⚠ 质量差异超过 1% (提示词 §8.4 视为缺陷, 请反馈)");
            }
            // U10: 槽位颜色随行 — 部件名(几何名)→#RRGGBB 传给试飞涂装 (与 3D 预览
            // 一致), 并写 colors.json 到产物目录 (机型库"换上"时复用)
            var partColors = BuildSlotColorMap();
            try
            {
                var sbc = new System.Text.StringBuilder("{");
                bool first = true;
                foreach (var kv in partColors)
                {
                    if (!first) sbc.Append(',');
                    first = false;
                    sbc.Append(WorkshopJson.Escape(kv.Key)).Append(':')
                        .Append(WorkshopJson.Escape(kv.Value));
                }
                sbc.Append('}');
                File.WriteAllText(Path.Combine(dir, "colors.json"), sbc.ToString());
            }
            catch (System.Exception ex) { AppendLog("[换机] colors.json 写入失败: " + ex.Message); }
            var go = AircraftSwapper.Swap(cfg, File.Exists(obj) ? obj : "", partColors);
            AppendLog("[换机] 已换机: " + go.name + " ← " + dir +
                      string.Format(" (工坊涂装 {0} 部件)", partColors.Count));
            if (_open) ToggleOpen();   // 关闭工坊进入飞行
        }

        /// <summary>U10: 槽位 → (几何名, #RRGGBB) 颜色表 (试飞涂装用)。</summary>
        private Dictionary<string, string> BuildSlotColorMap()
        {
            var map = new Dictionary<string, string>();
            foreach (var s in _state.slots)
                if (!string.IsNullOrEmpty(s.geom) && !string.IsNullOrEmpty(s.color) &&
                    !map.ContainsKey(s.geom))
                    map[s.geom] = s.color;
            return map;
        }

        /// <summary>U10: 机型库目录的 colors.json → 颜色表 (无文件返回空表=默认涂装)。</summary>
        private static Dictionary<string, string> LoadColorMap(string dir)
        {
            var map = new Dictionary<string, string>();
            string cf = Path.Combine(dir, "colors.json");
            if (!File.Exists(cf)) return map;
            try
            {
                foreach (var kv in WorkshopJson.SplitObject(File.ReadAllText(cf)))
                {
                    string hex = kv.Value.Trim('"', ' ');
                    if (hex.StartsWith("#")) map[kv.Key] = hex;
                }
            }
            catch { }
            return map;
        }

        // ================= 文件操作 =================
        private void ConfirmIfDirty(string what, System.Action go)
        {
            if (!_state.dirty) { go(); return; }
            _confirmText = "当前工程有未保存修改,确定要" + what + "吗?";
            _confirmYes = "确定";
            _confirmAction = go;
        }

        private GUIStyle _swapWarnStyle;

        private void DrawConfirm()
        {
            // 高度按内容行数自适应 (缺轴/缺槽位警告可能多行)
            int lines = 1;
            if (_confirmText != null)
                foreach (char ch in _confirmText) if (ch == '\n') lines++;
            float ph = Mathf.Clamp(70f + lines * 20f, 110f, 400f);
            var r = new Rect(Screen.width / 2 - 210, Screen.height / 2 - ph / 2, 420, ph);
            GUI.Box(new Rect(0, 0, Screen.width, Screen.height), "");   // 遮罩
            _popupRect = GUILayout.Window(9528, r, id =>
            {
                GUILayout.Label(_confirmText);
                WorkshopUiKit.Divider();
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("取消", GUILayout.Width(90), GUILayout.Height(WorkshopUiKit.BtnH)))
                {
                    _confirmText = null; _confirmAction = null;
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button(_confirmYes, GUILayout.Width(110),
                                     GUILayout.Height(WorkshopUiKit.BtnH)))
                {
                    var act = _confirmAction;
                    _confirmText = null; _confirmAction = null;
                    act?.Invoke();
                    GUIUtility.ExitGUI();
                }
                GUILayout.EndHorizontal();
                GUI.DragWindow(new Rect(0, 0, 10000, 24));
            }, "机型工坊");
        }

        private void DrawNaming()
        {
            var r = new Rect(Screen.width / 2 - 180, Screen.height / 2 - 70, 360, 122);
            GUI.Box(new Rect(0, 0, Screen.width, Screen.height), "");
            _popupRect = GUILayout.Window(9529, r, id =>
            {
                GUILayout.Label("保存部件到部件库 (名称可用中文):");
                _namingText = GUILayout.TextField(_namingText, GUILayout.Height(WorkshopUiKit.RowH));
                WorkshopUiKit.Divider();
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("取消", GUILayout.Width(80), GUILayout.Height(WorkshopUiKit.BtnH)))
                {
                    _namingSlot = null;
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button("保存", GUILayout.Width(80), GUILayout.Height(WorkshopUiKit.BtnH)))
                {
                    string name = _namingText.Trim();
                    string slot = _namingSlot;
                    _namingSlot = null;
                    if (name.Length > 0)
                    {
                        var s = _state.Slot(slot);
                        string libType = slot == "fuselage" ? "fuselage"
                                       : slot == "ballast" ? "ballast" : "wing";
                        _worker.Call("save_part",
                            WorkshopReq.Obj().S("geom_id", s.geomId).S("name", name)
                                           .S("type", libType).Str(), rep =>
                            {
                                if (rep.ok)
                                {
                                    AppendLog("[部件库] 已保存 " + name);
                                    _worker.Call("list_library", "{}", rr =>
                                    { if (rr.ok) _state.LoadLibrary(rr.dataJson); });
                                }
                                else AppendLog("[部件库] 保存失败: " + rep.error);
                            });
                    }
                    GUIUtility.ExitGUI();
                }
                GUILayout.EndHorizontal();
                GUI.DragWindow(new Rect(0, 0, 10000, 24));
            }, "保存部件");
        }

        private void DoNew()
        {
            ConfirmIfDirty("新建", () =>
            {
                _worker.Call("new", "{}", r =>
                {
                    if (!r.ok) { AppendLog("[新建] " + r.error); return; }
                    ModelVersion++;                  // R10
                    _aero.ResultJson = "";
                    _state.LoadGeoms(r.dataJson);
                    _state.projectName = "Unnamed";
                    _state.projectPath = "";
                    _state.dirty = false;
                    ClearPreview();
                    AppendLog("[新建] 空工程 (默认示例机 W2 提供「载入示例」)");
                });
            });
        }

        /// <summary>载入示例 (R02): 走未保存确认, 成功后视为新工程 (清路径, 首存进另存为)。</summary>
        private void DoLoadExample()
        {
            _worker.Call("load_example", "{}", r =>
            {
                if (!r.ok) { AppendLog("[示例] " + r.error); return; }
                OnModelChanged(r.dataJson);
                _state.projectName = "示例机";
                _state.projectPath = "";   // R02: 不再沿用原工程路径 (防覆盖 A.vsp3)
                _state.dirty = true;
                AppendLog("[示例] 已载入示例机 (机身+主翼+平尾+垂尾+配重)");
            });
        }

        private void DoOpen()
        {
            ConfirmIfDirty("导入新文件", () =>
            {
                string picked = WorkshopFileDialog.PickOpenVsp3();
                if (picked == null) return;
                // R06: 先读 sidecar (槽位+排除名单), open 时带回 worker 应用
                _state.LoadSidecar(picked, out _, out string[] excludedNames);
                _worker.Call("open",
                    WorkshopReq.Obj().S("path", picked.Replace('\\', '/'))
                                   .Raw("excluded_names", ExcludedJson(excludedNames)).Str(), r =>
                {
                    if (!r.ok)
                    {
                        AppendLog("[导入] " + r.error);
                        // R01: worker 已按快照恢复原模型 (几何 id 已变), 刷新本地状态对齐
                        _worker.Call("list_geoms", "{}", rr =>
                        {
                            if (rr.ok) _state.LoadGeoms(rr.dataJson);
                        });
                        return;
                    }
                    _state.LoadGeoms(r.dataJson);
                    ModelVersion++;                  // R10
                    _aero.ResultJson = "";
                    _state.projectName = Path.GetFileNameWithoutExtension(picked);
                    _state.projectPath = picked;
                    _state.dirty = false;
                    bool restored = _state.LoadSidecar(picked, out string aeroJson, out _);
                    AppendLog("[导入] " + _state.projectName + " 几何 " + _state.geoms.Count +
                              (restored ? " (已恢复槽位分配)" : " (无 sidecar, 尝试启发式预分配)"));
                    if (!string.IsNullOrEmpty(aeroJson)) _aero.ApplyJson(aeroJson);
                    _aero.autoRefApplied = restored && !string.IsNullOrEmpty(aeroJson);
                    RestoreSlotsFromSidecar();
                    if (!restored)
                    {
                        // F4: 无 sidecar — worker 启发式预分配 (机身/机翼1/平尾/垂尾/机翼2)
                        _worker.Call("auto_assign", "{}", ar =>
                        {
                            if (!ar.ok) return;
                            int n = WorkshopJson.SplitObject(
                                WorkshopJson.Raw(ar.dataJson, "assigned") ?? "").Count;
                            if (n > 0)
                            {
                                OnModelChanged(ar.dataJson);
                                AppendLog("[导入] 启发式预分配 " + n +
                                          " 个槽位 (可在建模区表格中改)");
                            }
                        });
                    }
                    SyncAeroControls();
                    MaybeAutoRefFromModel();
                });
            });
        }

        /// <summary>workshop.json 槽位恢复: 按几何名逐槽 assign (vsp3 重开后 id 已变)。</summary>
        private void RestoreSlotsFromSidecar()
        {
            foreach (var s in _state.slots)
            {
                if (string.IsNullOrEmpty(s.geom)) continue;
                var g = _state.GeomByName(s.geom);
                if (g != null && WorkshopState.TypeFitsSlot(s.key, g.type))
                    _worker.Call("assign_slot",
                        WorkshopReq.Obj().S("slot", s.key).S("geom_id", g.id).Str(),
                        r => { if (r.ok) OnModelChanged(r.dataJson); });
                else
                    s.geom = null;   // 文件里几何对不上, 置无
            }
            ScheduleMeshRefresh();
        }

        private void DoSave(bool saveAs)
        {
            string target = _state.projectPath;
            if (saveAs || string.IsNullOrEmpty(target))
            {
                target = WorkshopFileDialog.PickSaveVsp3(_state.projectName + ".vsp3");
                if (target == null) return;
            }
            _worker.Call("save", WorkshopReq.Obj().S("path", target.Replace('\\', '/')).Str(), r =>
            {
                if (!r.ok) { AppendLog("[保存] " + r.error); return; }
                _state.projectPath = target;
                _state.projectName = Path.GetFileNameWithoutExtension(target);
                _state.dirty = false;
                _state.SaveSidecar(target, _aero.ToJson());
                AppendLog("[保存] " + target + " (+ .workshop.json)");
            });
        }

        // ================= 槽位操作 =================
        private void DoAssignSlot(string slot, string geomId)
        {
            _worker.Call("assign_slot",
                WorkshopReq.Obj().S("slot", slot).S("geom_id", geomId).Str(),
                r =>
                {
                    if (!r.ok) { AppendLog("[槽位] " + r.error); return; }
                    OnModelChanged(r.dataJson);
                });
        }

        private void DoClearSlot(string slot)
        {
            _worker.Call("clear_slot", WorkshopReq.Obj().S("slot", slot).Str(), r =>
            {
                if (!r.ok) { AppendLog("[槽位] " + r.error); return; }
                OnModelChanged(r.dataJson);
            });
        }

        private void DoInsertPart(string slot, string path)
        {
            _worker.Call("insert_part",
                WorkshopReq.Obj().S("slot", slot).S("path", path.Replace('\\', '/')).Str(), r =>
                {
                    if (!r.ok) { AppendLog("[部件库] " + r.error); return; }
                    OnModelChanged(r.dataJson);
                });
        }

        private void OnModelChanged(string geomsJson)
        {
            _state.LoadGeoms(geomsJson);
            _state.dirty = true;
            ModelVersion++;                  // R10: 旧气动结果立即失效
            _aero.ResultJson = "";
            SyncAeroControls();
            MaybeAutoRefFromModel();
            RefreshNpEstimate();   // 中性点估算随几何实时更新 (设计参考)
            ScheduleMeshRefresh();
        }

        /// <summary>栏②操纵面组按当前模型 SS_Control 子面重建 (提示词 §6 动态生成)。</summary>
        private void SyncAeroControls()
        {
            _aero.Controls.Clear();
            foreach (var g in _state.geoms)
            {
                if (g.subsurfs == null) continue;
                foreach (var sub in g.subsurfs)
                    if (sub.type == 3)
                        _aero.Controls.Add(new WorkshopAeroPanel.CtlDeflection
                        {
                            name = sub.name,
                            deg = 0f,
                            mirrored = sub.name == "Elevator",   // 升降舵对称/副翼反对称 (铁律3)
                        });
            }
        }

        /// <summary>机翼1 激活后首次自动切「取自模型」(对参考图的唯一偏离, 提示词 §6)。</summary>
        private void MaybeAutoRefFromModel()
        {
            if (_aero.autoRefApplied || string.IsNullOrEmpty(Wing1GeomId)) return;
            _aero.autoRefApplied = true;
            _worker.Call("estimate_np", "{}", r =>
            {
                if (!r.ok) return;
                _aero.sref = WorkshopJson.Num(r.dataJson, "sref_m2", _aero.sref);
                _aero.bref = WorkshopJson.Num(r.dataJson, "bref_m", _aero.bref);
                _aero.cref = WorkshopJson.Num(r.dataJson, "mac_m", _aero.cref);
                _npEstimateM = WorkshopJson.Num(r.dataJson, "xnp_m", float.NaN);
                _aero.refFromModel = true;
                if (_state.mass.massKg > 0f)   // Xref 默认取重心
                {
                    _aero.xref = _state.mass.cgOvsp.x;
                    _aero.yref = _state.mass.cgOvsp.y;
                    _aero.zref = _state.mass.cgOvsp.z;
                }
                AppendLog("[气动] 已按机翼1切换「取自模型」: Sref=" + _aero.sref.ToString("0.###") +
                          " bref=" + _aero.bref.ToString("0.###") + " cref=" + _aero.cref.ToString("0.###"));
            });
        }

        private float _npEstimateM = float.NaN;

        /// <summary>中性点实时估算 (设计参考, 不参与飞控): 每次几何变化重估。
        /// worker 从当前 parm 直接算 (升力线+机身经验公式), ~1ms 级。</summary>
        private void RefreshNpEstimate()
        {
            _worker.Call("estimate_np", "{}", r =>
            {
                if (!r.ok) { _npEstimateM = float.NaN; return; }
                _npEstimateM = WorkshopJson.Num(r.dataJson, "xnp_m", float.NaN);
            });
        }

        /// <summary>「计算重心」: mass_props → 写回 Xref/Yref/Zref 并同步栏③ (提示词 §6)。</summary>
        public void RequestComputeCg(WorkshopAeroPanel panel)
        {
            _worker.Call("mass_props", "{}", r =>
            {
                if (!r.ok) { AppendLog("[重心] " + r.error); return; }
                _state.LoadMass(r.dataJson);
                panel.xref = _state.mass.cgOvsp.x;
                panel.yref = _state.mass.cgOvsp.y;
                panel.zref = _state.mass.cgOvsp.z;
                if (_preview != null)
                {
                    Vector3 u = WorkshopStlLoader.ToUnity(_state.mass.cgOvsp, Vector3.zero);
                    _preview.SetCgMarker(new Vector3(u.x, u.y, u.z), true);
                }
                AppendLog(string.Format("[重心] {0:F3} kg @ ({1:F3}, {2:F3}, {3:F3})",
                    _state.mass.massKg, panel.xref, panel.yref, panel.zref));
            });
        }

        /// <summary>力矩参考位置默认跟随重心: 非手动模式下把 Xref/Yref/Zref 同步为当前重心。</summary>
        private void SyncRefToCg(WorkshopAeroPanel panel)
        {
            if (panel == null || panel.refManual || _state.mass.massKg <= 0f) return;
            panel.xref = _state.mass.cgOvsp.x;
            panel.yref = _state.mass.cgOvsp.y;
            panel.zref = _state.mass.cgOvsp.z;
        }

        /// <summary>「运行计算」: 跟随重心时先刷新质量, 再保存当前模型到临时 vsp3 → 独立 vspscript 进程 (铁律 1/2)。</summary>
        public void StartAeroRun(WorkshopAeroPanel panel)
        {
            if (panel.refManual) { StartAeroRunNow(panel); return; }
            _worker.Call("mass_props", "{}", mr =>
            {
                if (mr.ok) { _state.LoadMass(mr.dataJson); SyncRefToCg(panel); }
                else AppendLog("[重心] " + mr.error);
                StartAeroRunNow(panel);
            });
        }

        private void StartAeroRunNow(WorkshopAeroPanel panel)
        {
            string root = _projectRoot;
            string work = Path.Combine(root, "work", "workshop_aero_ui");
            Directory.CreateDirectory(work);
            string tmp = Path.Combine(work, "current.vsp3").Replace('\\', '/');
            _worker.Call("save", WorkshopReq.Obj().S("path", tmp).Str(), r =>
            {
                if (!r.ok)
                {
                    AppendLog("[气动] 保存临时模型失败: " + r.error);
                    AppendLog("[气动] 请确认工坊内有几何 (载入示例后再试)");
                    return;
                }
                string py = _worker.PythonExe;
                if (!panel.Start(py, root, tmp))
                    AppendLog("[气动] 启动失败 (python=" + py + ")");
                else
                    AppendLog("[气动] 运行计算… (面板参数已导出)");
            });
        }

        private void ScheduleMeshRefresh()
        {
            _needMeshRefresh = true;
            _meshRefreshAt = Time.realtimeSinceStartup + 0.25f;
        }

        /// <summary>export_meshes → 分槽位网格 (+操纵面高亮) + mass_props → CG 标注 (§5.3 链路)。</summary>
        private void RefreshMeshesAndMass()
        {
            _worker.Call("export_meshes", "{}", r =>
            {
                if (!r.ok) { AppendLog("[预览] " + r.error); return; }
                foreach (var s in _state.slots)
                {
                    string meshPath = WorkshopState.MeshPathFor(r.dataJson, s.key);
                    Mesh m = null;
                    if (meshPath != null) m = WorkshopStlLoader.LoadMesh(meshPath, Vector3.zero);
                    if (_preview != null)
                    {
                        _preview.SetSlotMesh(s.key, m, ParseColor(s.color), s.geom ?? s.key);
                        // 操纵面高亮: 槽位几何的 SS_Control 子面 (水平翼/垂尾条带法)
                        var geoms = _state.GeomById(s.geomId);
                        if (geoms != null && geoms.subsurfs != null && geoms.subsurfs.Length > 0)
                        {
                            var pars = new List<Vector3>();
                            foreach (var sub in geoms.subsurfs)
                                if (sub.type == 3 && sub.eta_end > sub.eta_start)
                                    pars.Add(new Vector3(sub.eta_start, sub.eta_end,
                                                         Mathf.Max(0.05f, sub.len_c)));
                            _preview.SetControlOverlays(s.key,
                                meshPath != null && pars.Count > 0
                                    ? WorkshopStlLoader.BuildControlOverlays(meshPath, Vector3.zero, pars)
                                    : null);
                        }
                        else _preview.SetControlOverlays(s.key, null);
                    }
                    s.meshTris = m != null ? m.triangles.Length / 3 : 0;
                }
                _worker.Call("mass_props", "{}", mr =>
                {
                    if (!mr.ok) { AppendLog("[质量] " + mr.error); return; }
                    _state.LoadMass(mr.dataJson);
                    SyncRefToCg(_aero);
                    if (_preview != null)
                    {
                        Vector3 u = WorkshopStlLoader.ToUnity(_state.mass.cgOvsp, Vector3.zero);
                        _preview.SetCgMarker(new Vector3(u.x, u.y, u.z), _state.mass.massKg > 0f);
                    }
                });
            });
        }

        private static Color ParseColor(string hex)
        {
            if (!string.IsNullOrEmpty(hex) && hex.StartsWith("#") &&
                ColorUtility.TryParseHtmlString(hex, out Color c)) return c;
            return Color.gray;
        }

        /// <summary>string[] → JSON 字符串数组 (null/空 → [])。</summary>
        private static string ExcludedJson(string[] names)
        {
            if (names == null || names.Length == 0) return "[]";
            var sb = new System.Text.StringBuilder("[");
            for (int i = 0; i < names.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(WorkshopJson.Escape(names[i]));
            }
            return sb.Append(']').ToString();
        }

        private void ClearPreview()
        {
            if (_preview == null) return;
            foreach (var s in _state.slots)
                _preview.SetSlotMesh(s.key, null, Color.gray, s.key);
            _preview.SetCgMarker(Vector3.zero, false);
            _state.mass = new WorkshopState.MassResult();
        }

        // ================= W2 编辑窗支持方法 =================

        private void RefreshWingEditors()
        {
            foreach (var e in _editors)
                if (e is WingEditorWindow) RefreshEditorSummary(e);
        }

        /// <summary>机身半径/截面数 (§5.4: 导入机身首次编辑触发转圆一次性提示)。</summary>
        public void SendFuselageRadii(string geomId, float[] radii, int count)
        {
            var win = _editors.Find(e => e.GeomId == geomId) as FuselageEditorWindow;
            if (win != null && win.importedPreserve && !win.roundNoticeShown)
            {
                win.roundNoticeShown = true;
                _confirmText = "该机身为导入的保形截面。\n编辑截面数或半径将把全部截面转为圆形, 且不可撤销。确定继续吗?";
                _confirmYes = "转为圆形";
                _confirmAction = () =>
                {
                    win.importedPreserve = false;
                    DoSendFuselageRadii(geomId, radii, count);
                };
                return;
            }
            DoSendFuselageRadii(geomId, radii, count);
        }

        private void DoSendFuselageRadii(string geomId, float[] radii, int count)
        {
            var sb = new System.Text.StringBuilder("[");
            for (int i = 0; i < radii.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append((radii[i] * 2f).ToString("0.######",
                    System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append(']');
            _worker.Call("fuselage_set_radii",
                WorkshopReq.Obj().S("geom_id", geomId).Raw("diameters", sb.ToString())
                               .I("resample_to", count).Str(), r =>
                {
                    if (!r.ok) { AppendLog("[机身] " + r.error); return; }
                    var win = _editors.Find(x => x.GeomId == geomId) as FuselageEditorWindow;
                    if (win != null) win.LoadFromSummary(r.dataJson);
                    _state.dirty = true;
                    ScheduleMeshRefresh();
                });
        }

        public void SendSetMass(string geomId, float massKg)
        {
            _worker.Call("set_mass",
                WorkshopReq.Obj().S("geom_id", geomId).F("mass_kg", massKg).Str(), r =>
                {
                    if (!r.ok) { AppendLog("[质量] " + r.error); return; }
                    _state.dirty = true;
                    ScheduleMeshRefresh();   // 质量/重心刷新
                });
        }

        public void SendSymmetric(string geomId, bool on)
        {
            _worker.Call("set_symmetric",
                WorkshopReq.Obj().S("geom_id", geomId).B("on", on).Str(), r =>
                { if (r.ok) { OnModelChanged(r.dataJson); ScheduleMeshRefresh(); } });
        }

        /// <summary>双垂尾 (§5.6): 开 = 对称 + Y=±间距/2; 关 = 单垂尾 (对称关, Y=0)。</summary>
        public void SendVtailSymmetry(string geomId, bool twin, float spacing)
        {
            _worker.Call("set_symmetric",
                WorkshopReq.Obj().S("geom_id", geomId).B("on", twin).Str(), r0 =>
            {
                if (!r0.ok) { AppendLog("[垂尾] " + r0.error); return; }
                float y = twin ? spacing / 2f : 0f;
                string parmsJson = "[{\"name\":\"Y_Location\",\"group\":\"XForm\",\"value\":" +
                    y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "}]";
                _worker.Call("set_parms",
                    WorkshopReq.Obj().S("geom_id", geomId).Raw("parms", parmsJson).Str(),
                    r1 =>
                    {
                        if (!r1.ok) { AppendLog("[垂尾] " + r1.error); return; }
                        OnModelChanged(r1.dataJson);
                        RefreshWingEditors();
                        ScheduleMeshRefresh();
                    });
            });
        }

        public void SendWingToSingleTaper(string geomId)
        {
            _worker.Call("wing_to_single_taper",
                WorkshopReq.Obj().S("geom_id", geomId).Str(), r =>
                {
                    if (!r.ok) { AppendLog("[机翼] " + r.error); return; }
                    AppendLog("[机翼] 已转为单段梯形");
                    OnModelChanged(WorkshopJson.Raw(r.dataJson, "geoms") ?? r.dataJson);
                    foreach (var e in _editors.ToArray())
                        if (e.GeomId == geomId) e.CloseRequested = true;   // 旧几何已删
                    ScheduleMeshRefresh();
                });
        }

        public void SendAddControl(string geomId, WingEditorWindow.ControlInfo c)
        {
            _worker.Call("add_control",
                WorkshopReq.Obj().S("geom_id", geomId).S("role", c.role)
                               .F("eta_start", c.etaStart).F("eta_end", c.etaEnd)
                               .F("len_c", c.lenC).Str(), r =>
                {
                    if (!r.ok) { AppendLog("[操纵面] " + r.error); return; }
                    RefreshModelAndView();
                });
        }

        /// <summary>list_geoms → 状态 + 刷新机翼编辑窗 (操纵面变化后)。</summary>
        private void RefreshModelAndView()
        {
            _worker.Call("list_geoms", "{}", r =>
            {
                if (!r.ok) return;
                _state.LoadGeoms(r.dataJson);
                _state.dirty = true;
                RefreshWingEditors();
                ScheduleMeshRefresh();
            });
        }

        public void SendEditControl(string geomId, WingEditorWindow.ControlInfo c)
        {
            _worker.Call("edit_control",
                WorkshopReq.Obj().S("geom_id", geomId).S("subsurf_id", c.id)
                               .S("role", c.role).F("eta_start", c.etaStart)
                               .F("eta_end", c.etaEnd).F("len_c", c.lenC)
                               .S("user_name", c.userName).Str(), r =>
                {
                    if (!r.ok) { AppendLog("[操纵面] " + r.error); return; }
                    // 改功能类型会重命名子面 — 必须 list_geoms 重载 _state,
                    // 否则换机校验 (SwapWarnings) 仍按旧名判缺轴
                    RefreshModelAndView();
                });
        }

        public void SendDelControl(string geomId, string subsurfId)
        {
            _worker.Call("del_control",
                WorkshopReq.Obj().S("geom_id", geomId).S("subsurf_id", subsurfId).Str(), r =>
                {
                    if (!r.ok) { AppendLog("[操纵面] " + r.error); return; }
                    RefreshModelAndView();
                });
        }

        public void OpenControlEditor(WingEditorWindow wing, WingEditorWindow.ControlInfo c)
        {
            _ctlEditor.Open(wing, c);
        }

        public void OpenAirfoilPicker(WingEditorWindow wing)
        {
            _airfoilPickerFor = wing;
            _airfoilPickerRect = new Rect(300, 150, 260, 320);
            _airfoilScroll = Vector2.zero;
        }

        public void PickAndUploadAirfoil(WingEditorWindow wing)
        {
            string picked = WorkshopFileDialog.PickOpenDat();
            if (picked == null) return;
            _worker.Call("upload_airfoil",
                WorkshopReq.Obj().S("path", picked.Replace('\\', '/')).Str(), r =>
                {
                    string ok = WorkshopJson.Str(r.dataJson, "ok");
                    if (ok == "True" || ok == "true")
                    {
                        AppendLog("[翼型] 上传成功 " + WorkshopJson.Str(r.dataJson, "detail"));
                        RefreshAirfoils();
                    }
                    else AppendLog("[翼型] 上传失败: " + (WorkshopJson.Str(r.dataJson, "error") ?? r.error));
                });
        }

        private void RefreshAirfoils()
        {
            _worker.Call("list_airfoils", "{}", r =>
            {
                if (!r.ok) return;
                _airfoilJson = r.dataJson;
                foreach (var e in _editors)
                {
                    var w = e as WingEditorWindow;
                    if (w != null) w.airfoilNames = WorkshopWidgets.ItemNames(_airfoilJson);
                }
            });
        }

        private void DrawAirfoilPicker()
        {
            var e = Event.current;
            if (e.type == EventType.MouseDown && !_airfoilPickerRect.Contains(e.mousePosition))
            { _airfoilPickerFor = null; return; }
            _airfoilPickerRect = GUILayout.Window(9538, _airfoilPickerRect, id =>
            {
                _airfoilScroll = GUILayout.BeginScrollView(_airfoilScroll, false, true);
                foreach (string obj in WorkshopWidgets.SplitArray(WorkshopJson.Raw(_airfoilJson, "items")))
                {
                    string nm = WorkshopJson.Str(obj, "name");
                    string kind = WorkshopJson.Str(obj, "kind");
                    string src = WorkshopJson.Str(obj, "source");
                    if (GUILayout.Button(nm + "  [" + (kind == "naca4" ? "NACA" : src) + "]",
                                         GUILayout.Height(WorkshopUiKit.RowH)))
                    {
                        var wing = _airfoilPickerFor;
                        _airfoilPickerFor = null;
                        string path = WorkshopJson.Str(obj, "path") ?? "";
                        var ob = WorkshopReq.Obj().S("geom_id", wing.GeomId).S("kind", kind);
                        if (kind == "naca4")
                            ob = ob.F("thick", WorkshopJson.Num(obj, "thick", 0.12f))
                                   .F("camber", WorkshopJson.Num(obj, "camber", 0f))
                                   .F("camber_loc", WorkshopJson.Num(obj, "camber_loc", 0.2f));
                        else
                            ob = ob.S("file", path);
                        _worker.Call("set_airfoil", ob.Str(), rr =>
                        {
                            if (!rr.ok) { AppendLog("[翼型] " + rr.error); return; }
                            AppendLog("[翼型] 已应用 " + nm);
                            ScheduleMeshRefresh();
                        });
                        GUIUtility.ExitGUI();
                    }
                }
                GUILayout.EndScrollView();
                GUI.DragWindow(new Rect(0, 0, 10000, 24));
            }, "选择翼型");
        }

        /// <summary>编辑窗外观入口 (U3 补强): 颜色色块 (主, 与主行同源) + OpenVSP 材质 (次级)。</summary>
        public void DrawMaterialSelector(ref string current, string geomId, string slotKey)
        {
            var slot = _state.Slot(slotKey);
            GUILayout.BeginHorizontal();
            if (slot != null)
            {
                var prev = GUI.backgroundColor;
                GUI.backgroundColor = ParseColor(slot.color);
                if (GUILayout.Button("", GUILayout.Width(22), GUILayout.Height(22)))
                {
                    var r = GUILayoutUtility.GetLastRect();
                    r.position = GUIUtility.GUIToScreenPoint(r.position);   // U14c
                    _colorPicker.Open(slotKey, ParseColor(slot.color), r);
                }
                GUI.backgroundColor = prev;
            }
            GUILayout.Label("OpenVSP 材质", GUILayout.Width(WorkshopUiKit.LabelW));
            if (GUILayout.Button((string.IsNullOrEmpty(current) ? "默认" : current) + " ▾",
                                 GUILayout.ExpandWidth(true), GUILayout.Height(WorkshopUiKit.RowH)))
            {
                _colorTarget = geomId;
                _colorPickerRect = new Rect(340, 190, 220, 300);
                _colorScroll = Vector2.zero;
            }
            GUILayout.EndHorizontal();
        }

        private void DrawColorPicker()
        {
            var e = Event.current;
            if (e.type == EventType.MouseDown && !_colorPickerRect.Contains(e.mousePosition))
            { _colorTarget = null; return; }
            string target = _colorTarget;
            _colorPickerRect = GUILayout.Window(9537, _colorPickerRect, id =>
            {
                _colorScroll = GUILayout.BeginScrollView(_colorScroll, false, true);
                foreach (string nm in WorkshopWidgets.ItemNames(_materialsJson))
                    if (GUILayout.Button(nm))
                    {
                        string picked = nm;
                        _colorTarget = null;
                        _worker.Call("set_color",
                            WorkshopReq.Obj().S("geom_id", target).S("material", picked).Str(), r =>
                            {
                                if (!r.ok) { AppendLog("[材质] " + r.error); return; }
                                // UI05: 材质变化也是工程修改; UI10: 窗已关时 Find 为 null 须防护
                                _state.dirty = true;
                                var win = _editors.Find(x => x != null && x.GeomId == target);
                                if (win != null) RefreshEditorSummary(win);
                            });
                        GUIUtility.ExitGUI();
                    }
                GUILayout.EndScrollView();
                GUI.DragWindow(new Rect(0, 0, 10000, 24));
            }, "选择材质");
        }
    }
}
