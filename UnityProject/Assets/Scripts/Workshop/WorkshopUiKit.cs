using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 机型工坊 UI 重设计 (V 轮): 共享布局常量与通用视觉控件。
    /// 目标: 功能区排布整齐 (统一栅格)、简约干净 (统一行模板/分组标题条)、
    /// 视觉一致 (单一强调色 + 弱化次要信息)。只提供布局与样式, 不含业务逻辑。
    /// 所有样式惰性创建, 必须在 OnGUI 期间首次访问 (依赖 GUI.skin)。
    /// </summary>
    internal static class WorkshopUiKit
    {
        // ---------- 栅格常量 ----------
        public const float Pad = 10f;        // 面板内边距
        public const float Gap = 6f;         // 控件间距
        public const float RowH = 22f;       // 标准行高
        public const float LabelW = 92f;     // 参数行标签列宽
        public const float FieldW = 62f;     // 参数行数值输入框宽
        public const float UnitW = 26f;      // 参数行单位列宽
        public const float BtnH = 24f;       // 标准按钮高
        public const float SectionH = 20f;   // 分组标题条高

        // ---------- 颜色 (Fluent 浅色; Accent 即 WorkshopSkin.Green=LightAccent) ----------
        internal static readonly Color Accent = WorkshopSkin.Green;
        internal static readonly Color DimText = FluentTheme.LightTextDim;
        private static readonly Color BarBg = new Color(0.925f, 0.925f, 0.925f, 0.9f);
        // Chip 画在 3D 预览视口上 (深背景), 保持深色半透明, 不用浅色
        private static readonly Color ChipBg = new Color(0.06f, 0.06f, 0.06f, 0.72f);

        private static Texture2D _barTex, _chipTex, _lineTex;
        private static GUIStyle _section, _dim, _dimRight, _tableHead, _chip;

        /// <summary>分组标题条样式: 左侧 3px 强调色竖条 + 微亮底 + 13pt 粗体。</summary>
        internal static GUIStyle Section
        {
            get
            {
                if (_section == null)
                {
                    EnsureTextures();
                    _section = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = 13,
                        fontStyle = FontStyle.Bold,
                        padding = new RectOffset(10, 4, 3, 2),
                        fixedHeight = SectionH,
                        normal = { textColor = Accent, background = _barTex },
                        border = new RectOffset(4, 0, 0, 0),
                    };
                }
                return _section;
            }
        }

        /// <summary>弱化小字 (辅助说明/表头/提示)。</summary>
        internal static GUIStyle Dim
        {
            get
            {
                if (_dim == null)
                    _dim = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = DimText } };
                return _dim;
            }
        }

        internal static GUIStyle DimRight
        {
            get
            {
                if (_dimRight == null)
                    _dimRight = new GUIStyle(Dim) { alignment = TextAnchor.MiddleRight };
                return _dimRight;
            }
        }

        /// <summary>表格列表头 (小字弱化, 与数据行同列宽对齐)。</summary>
        internal static GUIStyle TableHead
        {
            get
            {
                if (_tableHead == null)
                    _tableHead = new GUIStyle(Dim) { fontStyle = FontStyle.Bold };
                return _tableHead;
            }
        }

        /// <summary>预览标注文字底色片样式 (CG/NP 标注, 网格上可读)。</summary>
        internal static GUIStyle Chip
        {
            get
            {
                if (_chip == null)
                {
                    EnsureTextures();
                    _chip = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = 12,
                        padding = new RectOffset(6, 6, 2, 2),
                        normal = { textColor = new Color(0.90f, 0.95f, 0.91f), background = _chipTex },
                        border = new RectOffset(4, 4, 4, 4),
                    };
                }
                return _chip;
            }
        }

        internal static Texture2D LineTex
        {
            get { EnsureTextures(); return _lineTex; }
        }

        private static void EnsureTextures()
        {
            if (_barTex == null)
            {
                // 32×20: 左 3px 强调条, 其余微亮底
                _barTex = new Texture2D(32, 20, TextureFormat.RGBA32, false)
                { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                var px = new Color[32 * 20];
                for (int y = 0; y < 20; y++)
                    for (int x = 0; x < 32; x++)
                        px[y * 32 + x] = x < 3
                            ? new Color(Accent.r, Accent.g, Accent.b, 0.85f) : BarBg;
                _barTex.SetPixels(px);
                _barTex.Apply();
            }
            if (_chipTex == null)
                _chipTex = FlightHUD.RoundedRect(24, 5, ChipBg, ChipBg,
                    new Color(1f, 1f, 1f, 0.08f), 1f);
            if (_lineTex == null)
            {
                _lineTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _lineTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.14f));   // 浅底上的分隔线
                _lineTex.Apply();
            }
        }

        // ---------- 通用控件 ----------

        /// <summary>分组标题条 (替代旧的 "▎标题" 纯文本)。</summary>
        internal static void SectionBar(string title)
        {
            GUILayout.Space(Gap);
            GUILayout.Label(title, Section, GUILayout.ExpandWidth(true),
                            GUILayout.Height(SectionH));
            GUILayout.Space(2f);
        }

        private static GUIStyle _winTitle;

        /// <summary>自绘窗口标题 (WindowFlat 皮肤的窗口第一行): 居中粗体 + 分隔线。
        /// 替代 IMGUI 标题栏 — 其与内容的间距由皮肤 padding 隐式决定, 实测不可控。</summary>
        internal static void WindowTitle(string title)
        {
            if (_winTitle == null)
                _winTitle = new GUIStyle(Section)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 14,
                    fontStyle = FontStyle.Bold,
                };
            GUILayout.Label(title, _winTitle, GUILayout.ExpandWidth(true),
                            GUILayout.Height(24));
            Divider();
            GUILayout.Space(2f);
        }

        /// <summary>1px 水平分隔线 (弹性宽)。</summary>
        internal static void Divider()
        {
            var r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.Height(5f));
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(r.x, r.y + 2f, r.width, 1f), LineTex);
        }

        /// <summary>标注底片文字 (CG/NP): 自动按文字长度给宽。</summary>
        internal static void ChipLabel(Rect r, string text)
        {
            GUI.Label(r, text, Chip);
        }

        /// <summary>均分宽度的按钮行 (按可用宽度平分, 间距 Gap)。返回被点按钮序号, 未点 -1。</summary>
        internal static int EvenButtonRow(params string[] labels)
        {
            var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.Height(BtnH));
            int hit = -1;
            float bw = (rect.width - Gap * (labels.Length - 1)) / labels.Length;
            for (int i = 0; i < labels.Length; i++)
            {
                var br = new Rect(rect.x + i * (bw + Gap), rect.y, bw, rect.height);
                if (GUI.Button(br, labels[i])) hit = i;
            }
            return hit;
        }

        /// <summary>GUI.enabled 作用域 (禁用一批控件)。</summary>
        internal static EnabledScope Enabled(bool enabled) { return new EnabledScope(enabled); }

        internal struct EnabledScope : System.IDisposable
        {
            private readonly bool _prev;
            public EnabledScope(bool enabled) { _prev = GUI.enabled; GUI.enabled = enabled; }
            public void Dispose() { GUI.enabled = _prev; }
        }
    }
}
