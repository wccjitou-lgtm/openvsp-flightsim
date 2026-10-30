using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 机型工坊运行时 GUISkin: Windows 11 Fluent 浅色主题 (2026-10-09 改版,
    /// 原深绿航空主题全面替换)。调色板集中见 FluentTheme; 贴图复用
    /// FlightHUD.RoundedRect (Fluent 平面化: 渐变两色取同色 + 1px 中性边)。
    /// 只在机型工坊 (WorkshopUI/编辑窗) 画窗口期间通过 GUI.skin 临时套用, 不影响 HUD。
    /// 兼容说明: 保留 Green/Amber 字段名 (引用点多), 值改为 Fluent 强调蓝/警告色。
    /// </summary>
    internal static class WorkshopSkin
    {
        // 值即 FluentTheme.LightAccent / WarningOnLight; 保留旧名避免大面积改名
        internal static readonly Color Green = FluentTheme.LightAccent;
        internal static readonly Color Amber = FluentTheme.WarningOnLight;
        private static readonly Color TextCol = FluentTheme.LightText;
        private static readonly Color Border = FluentTheme.LightBorder;

        private static GUISkin _skin;
        private static Texture2D _btnTex;
        private static GUIStyle _section, _link;
        private static GUIStyle _winFlat;

        /// <summary>按钮底图 (入口按钮复用)。</summary>
        internal static Texture2D BtnTex { get { Get(); return _btnTex; } }
        internal static GUIStyle SectionStyle { get { Get(); return _section; } }
        internal static GUIStyle LinkStyle { get { Get(); return _link; } }
        /// <summary>自绘标题窗口 (编辑窗/操纵面窗): 无 IMGUI 标题栏, 内容顶到 10px。</summary>
        internal static GUIStyle WindowFlat { get { Get(); return _winFlat; } }

        /// <summary>必须在 OnGUI 内调用 (需要克隆当前默认皮肤)。</summary>
        internal static GUISkin Get()
        {
            if (_skin != null) return _skin;

            var font = FlightHUD.LoadOsFont("Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Arial");
            var s = Object.Instantiate(GUI.skin);
            s.hideFlags = HideFlags.HideAndDontSave;
            s.font = font;

            var T = FluentTheme.LightText;
            var accent = FluentTheme.LightAccent;
            // Fluent: 填充上下同色 (无渐变), 1px 中性边; 圆角 4-8px
            var panel = FlightHUD.RoundedRect(64, 8,
                FluentTheme.LightWindow, FluentTheme.LightWindow, Border, 1f);
            _btnTex = FlightHUD.RoundedRect(48, 4,
                FluentTheme.LightControl, FluentTheme.LightControl, Border, 1f);
            var btnOn = FlightHUD.RoundedRect(48, 4,
                FluentTheme.LightPressed, FluentTheme.LightPressed, accent, 1.2f);
            var field = FlightHUD.RoundedRect(32, 4,
                Color.white, Color.white, Border, 1f);
            var fieldFocus = FlightHUD.RoundedRect(32, 4,
                Color.white, Color.white, accent, 1.4f);
            var dropBox = FlightHUD.RoundedRect(48, 4,
                FluentTheme.LightCard, FluentTheme.LightCard, Border, 1.2f);
            var track = FlightHUD.RoundedRect(16, 4,
                FluentTheme.LightHover, FluentTheme.LightHover, Border, 0.8f);
            var thumb = FlightHUD.RoundedRect(16, 4,
                new Color(0.76f, 0.76f, 0.76f), new Color(0.70f, 0.70f, 0.70f),
                new Color(0.55f, 0.55f, 0.55f, 0.8f), 0.8f);
            var toggleOff = CheckBox(false);
            var toggleOn = CheckBox(true);

            var win = s.window;
            win.normal.background = win.onNormal.background = panel;
            win.border = new RectOffset(16, 16, 16, 16);
            win.padding = new RectOffset(14, 14, 34, 12);
            win.alignment = TextAnchor.UpperCenter;
            win.contentOffset = new Vector2(0f, 9f);
            win.fontSize = 14;
            win.fontStyle = FontStyle.Bold;
            win.normal.textColor = win.onNormal.textColor = T;

            // 自绘标题的编辑窗皮肤: IMGUI 标题栏高度/间距不可控 (实测 26 重叠、34 空带),
            // 编辑窗改用空标题 + 内容首行自绘标题 (WorkshopUiKit.WindowTitle)
            _winFlat = new GUIStyle(win)
            {
                padding = new RectOffset(14, 14, 10, 12),
                contentOffset = Vector2.zero,
            };

            s.label.fontSize = 14;
            s.label.normal.textColor = T;
            s.label.wordWrap = false;

            var btn = s.button;
            btn.normal.background = btn.hover.background = _btnTex;
            btn.active.background = btn.focused.background = btnOn;
            btn.onNormal.background = btn.onHover.background = btn.onActive.background =
                btn.onFocused.background = btnOn;
            btn.normal.textColor = btn.focused.textColor = T;
            btn.hover.textColor = T;
            btn.active.textColor = accent;
            btn.onNormal.textColor = btn.onHover.textColor = btn.onActive.textColor =
                btn.onFocused.textColor = accent;
            btn.border = new RectOffset(12, 12, 12, 12);
            btn.padding = new RectOffset(10, 10, 4, 4);
            btn.alignment = TextAnchor.MiddleCenter;
            btn.fontSize = 14;
            btn.fontStyle = FontStyle.Bold;

            var tf = s.textField;
            tf.normal.background = tf.hover.background = tf.active.background = field;
            tf.focused.background = fieldFocus;
            tf.normal.textColor = tf.hover.textColor = tf.active.textColor =
                tf.focused.textColor = T;
            tf.border = new RectOffset(8, 8, 8, 8);
            tf.padding = new RectOffset(7, 7, 4, 4);
            tf.fontSize = 14;
            tf.clipping = TextClipping.Clip;

            var tg = s.toggle;
            tg.normal.background = tg.hover.background = tg.active.background =
                tg.focused.background = toggleOff;
            tg.onNormal.background = tg.onHover.background = tg.onActive.background =
                tg.onFocused.background = toggleOn;
            tg.normal.textColor = tg.hover.textColor = tg.active.textColor =
                tg.onNormal.textColor = tg.onHover.textColor = tg.onActive.textColor =
                tg.focused.textColor = tg.onFocused.textColor = T;
            tg.border = new RectOffset(24, 0, 0, 0);
            tg.padding = new RectOffset(28, 0, 3, 3);
            tg.fontSize = 14;

            s.box.normal.background = dropBox;
            s.box.border = new RectOffset(12, 12, 12, 12);
            s.box.normal.textColor = T;

            s.verticalScrollbar.normal.background = track;
            s.verticalScrollbar.border = new RectOffset(5, 5, 5, 5);
            s.verticalScrollbar.fixedWidth = 10f;
            s.verticalScrollbarThumb.normal.background = thumb;
            s.verticalScrollbarThumb.border = new RectOffset(5, 5, 5, 5);
            s.verticalScrollbarThumb.fixedWidth = 10f;
            s.verticalScrollbarUpButton.fixedHeight = 0f;
            s.verticalScrollbarUpButton.fixedWidth = 0f;
            s.verticalScrollbarDownButton.fixedHeight = 0f;
            s.verticalScrollbarDownButton.fixedWidth = 0f;

            s.horizontalScrollbar.normal.background = track;
            s.horizontalScrollbar.border = new RectOffset(5, 5, 5, 5);
            s.horizontalScrollbar.fixedHeight = 10f;
            s.horizontalScrollbarThumb.normal.background = thumb;
            s.horizontalScrollbarThumb.border = new RectOffset(5, 5, 5, 5);
            s.horizontalScrollbarThumb.fixedHeight = 10f;
            s.horizontalScrollbarLeftButton.fixedWidth = 0f;
            s.horizontalScrollbarLeftButton.fixedHeight = 0f;
            s.horizontalScrollbarRightButton.fixedWidth = 0f;
            s.horizontalScrollbarRightButton.fixedHeight = 0f;

            _section = new GUIStyle(s.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 14,
                normal = { textColor = T },
            };
            _link = new GUIStyle(s.label)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = FluentTheme.LightTextDim },
                hover = { textColor = accent },
                active = { textColor = FluentTheme.LightAccentHover },
            };

            _skin = s;
            return s;
        }

        /// <summary>复选框贴图: 32x22, 左侧 16x16 圆角方框 (选中时 Fluent 蓝填充), 其余透明 (9-slice 左边框 24)。</summary>
        private static Texture2D CheckBox(bool on)
        {
            const int w = 32, h = 22, x0 = 4, y0 = 3, bs = 16;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int bx = x - x0, by = y - y0;
                    if (bx < 0 || by < 0 || bx >= bs || by >= bs) { px[y * w + x] = Color.clear; continue; }
                    bool edge = bx < 1 || by < 1 || bx >= bs - 1 || by >= bs - 1;
                    bool corner = (bx < 2 || bx >= bs - 2) && (by < 2 || by >= bs - 2);
                    if (corner) { px[y * w + x] = Color.clear; continue; }
                    if (edge) px[y * w + x] = on
                        ? FluentTheme.LightAccent
                        : new Color(0.55f, 0.55f, 0.55f, 0.9f);
                    else px[y * w + x] = on
                        ? FluentTheme.LightAccent
                        : new Color(1f, 1f, 1f, 0.95f);
                }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }
    }
}
