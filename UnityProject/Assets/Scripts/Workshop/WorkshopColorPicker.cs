using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 机型工坊 U3: 部件外观颜色选择弹层 (预设网格 + HEX 输入)。
    /// 颜色权威 = WorkshopState.SlotInfo.color (HEX, 驱动预览与 sidecar);
    /// OpenVSP 材质 (set_color) 是独立的求解器侧通道, 不在本弹层混用 (A3 分治)。
    /// 纯外观: 应用即 SetSlotColor + dirty, 不触发几何链路 (不失效气动结果, A5)。
    /// </summary>
    public class WorkshopColorPicker
    {
        public string TargetSlot;             // null = 关闭
        public Rect Rect = new Rect(300, 200, 236, 240);
        public string HexInput = "";
        public string LastError = "";

        public const int WindowId = 9536;

        internal static readonly Color[] Presets =
        {
            new Color(0.92f, 0.92f, 0.94f), new Color(0.13f, 0.13f, 0.15f),
            new Color(0.55f, 0.57f, 0.60f), new Color(0.90f, 0.25f, 0.25f),
            new Color(0.95f, 0.55f, 0.15f), new Color(0.95f, 0.85f, 0.20f),
            new Color(0.35f, 0.75f, 0.40f), new Color(0.20f, 0.75f, 0.75f),
            new Color(0.29f, 0.52f, 0.95f), new Color(0.60f, 0.35f, 0.80f),
            new Color(0.95f, 0.60f, 0.75f), new Color(0.55f, 0.40f, 0.28f),
        };

        public void Open(string slot, Color current, Rect anchor)
        {
            TargetSlot = slot;
            HexInput = ColorUtility.ToHtmlStringRGB(current);
            LastError = "";
            Rect = new Rect(anchor.x, anchor.yMax + 4f, 236, 246);
            // 保持弹层在视口内
            Rect.x = Mathf.Clamp(Rect.x, 4f, Mathf.Max(4f, Screen.width - Rect.width - 4f));
            Rect.y = Mathf.Clamp(Rect.y, 4f, Mathf.Max(4f, Screen.height - Rect.height - 4f));
        }

        /// <summary>绘制弹层; 内部经 owner 应用颜色。返回 true = 弹层仍开启。</summary>
        public bool Draw(WorkshopUI owner, WorkshopState state)
        {
            if (string.IsNullOrEmpty(TargetSlot)) return false;
            string slot = TargetSlot;
            Rect = GUILayout.Window(WindowId, Rect, id =>
            {
                var s = state.Slot(slot);
                if (s == null) { TargetSlot = null; return; }
                Color cur = Parse(s.color);

                GUILayout.Label("外观颜色 — " + slot);
                // 预设网格 6×2
                for (int row = 0; row < 2; row++)
                {
                    GUILayout.BeginHorizontal();
                    for (int col = 0; col < 6; col++)
                    {
                        int i = row * 6 + col;
                        var c = Presets[i];
                        bool selected = ColorApprox(c, cur);
                        var prev = GUI.backgroundColor;
                        GUI.backgroundColor = c;
                        var style = new GUIStyle(GUI.skin.button);
                        if (selected) style.fontStyle = FontStyle.Bold;
                        if (GUILayout.Button(selected ? "●" : "", style,
                                             GUILayout.Width(30), GUILayout.Height(26)))
                        {
                            owner.ApplySlotColor(slot, ColorUtility.ToHtmlStringRGB(c));
                            HexInput = ColorUtility.ToHtmlStringRGB(c);
                            LastError = "";
                        }
                        GUI.backgroundColor = prev;
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(4);
                GUILayout.BeginHorizontal();
                GUILayout.Label("HEX #", GUILayout.Width(36));
                HexInput = GUILayout.TextField(HexInput, GUILayout.Width(90));
                if (GUILayout.Button("应用", GUILayout.Width(50)))
                {
                    string hx = HexInput.Trim().TrimStart('#');
                    if (ColorUtility.TryParseHtmlString("#" + hx, out _))
                    {
                        owner.ApplySlotColor(slot, hx.ToUpperInvariant());
                        LastError = "";
                    }
                    else
                    {
                        LastError = "无效 HEX (保留原色)";
                        HexInput = ColorUtility.ToHtmlStringRGB(Parse(s.color));
                    }
                }
                GUILayout.EndHorizontal();
                if (!string.IsNullOrEmpty(LastError))
                    GUILayout.Label(LastError, RedStyle());
                GUILayout.Label("预览与保存生效; 不改几何/质量/气动", WorkshopUiKit.Dim);
                WorkshopUiKit.Divider();
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("关闭", GUILayout.Width(64),
                                     GUILayout.Height(WorkshopUiKit.BtnH)))
                    TargetSlot = null;
                GUILayout.EndHorizontal();
                GUI.DragWindow(new Rect(0, 0, 10000, 20));
            }, "选择外观颜色");
            return true;
        }

        private static Color Parse(string hex)
        {
            return ColorUtility.TryParseHtmlString(hex != null && hex.StartsWith("#") ? hex : "#" + hex,
                out Color c) ? c : Color.gray;
        }

        private static bool ColorApprox(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.02f && Mathf.Abs(a.g - b.g) < 0.02f &&
                   Mathf.Abs(a.b - b.b) < 0.02f;
        }

        private static GUIStyle RedStyle()
        {
            var s = new GUIStyle(GUI.skin.label);
            s.normal.textColor = FluentTheme.Error;
            return s;
        }
    }
}
