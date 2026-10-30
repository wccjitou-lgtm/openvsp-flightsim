using FlightDynamics;
using FlightDynamics.Math;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace FlightSim
{
    /// <summary>
    /// 飞行 HUD (开发提示词 7 节, Phase 5): OnGUI 叠层仪表, 航空绿主题深度精修版。
    /// 数据全部取自 AircraftController 遥测 (与物理状态同帧一致)。
    ///   空速表 / 高度表 / 垂直速度 / 姿态仪(天地球面填充+滚转刻度弧) /
    ///   航向纸带罗盘(顶部) / 过载 G / 风向罗盘 / 油门 / 操纵面偏转 / 性能参数
    /// 视觉: 圆角渐变面板 (运行时生成 9-slice 贴图) + Consolas 数字 / 雅黑中文动态字体 +
    ///   标题 accent 细线 + 统一右列宽度。技术栈保持 OnGUI (团队决定不迁移 UGUI)。
    /// H 键隐藏, K 键位说明。
    /// </summary>
    [RequireComponent(typeof(AircraftController))]
    public class FlightHUD : MonoBehaviour
    {
        [Header("视觉提示阈值 (纯视觉提示, 不接管任何飞控/告警逻辑)")]
        [Tooltip("空中空速低于此值时空速数字变琥珀色提示")]
        public float LowSpeedWarningSpeed = 12f;

        private AircraftController _c;
        private Vector2 _smWindVec;   // 风显示平滑 (矢量域, 避免角度回绕问题)
        private bool _show = true;

        /// <summary>HUD 当前是否显示 (H 键切换); 场景切换面板跟随。</summary>
        public bool IsShown { get { return _show; } }

        // ---- 运行时生成的 UI 贴图 ----
        private Texture2D _solid;      // 纯色填充 (进度条, 保持硬边)
        private Texture2D _aaStroke;   // 抗锯齿描边 (旋转直线/圆/箭头/滑条轨道细线)
        private Texture2D _panelTex;   // 圆角渐变面板 (9-slice)
        private Texture2D _chipTex;    // 小圆角片 (顶栏/航向数字/横幅)
        private Texture2D _thumbTex;   // 滑条圆形拇指
        private Texture2D _toggleOnTex, _toggleOffTex;
        private Texture2D _skyTex, _groundTex;   // 姿态仪天/地渐变
        private Texture2D _btnTex, _btnActiveTex, _alertTex;

        // ---- 动态字体 (数字等宽 Consolas, 中文雅黑; 取不到回退默认) ----
        private Font _numFont, _labelFont;

        private GUIStyle _big, _bigWarn, _mid, _midNum, _small, _label, _key;
        private GUIStyle _labelR, _smallR, _unit, _midR, _chipText, _specValue, _specLive;

        /// <summary>右上发动机/操纵面簇高度 (虚拟像素); 性能参数面板排在其下方。</summary>
        private const float EngineClusterH = 224f;
        private GUIStyle _panelStyle, _chipStyle, _alertStyle;
        private GUIStyle _sliderThumbStyle;
        private GUIStyle _btnIdleStyle, _btnActiveStyle;
        private float _stylesBuiltForK = -1f;   // 上次建样式时的 k, 变化 (换分辨率/窗口大小) 时重建

        private void Awake()
        {
            _c = GetComponent<AircraftController>();
            BuildTextures();
            _numFont = LoadOsFont("Consolas", "Courier New");
            _labelFont = LoadOsFont("Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Arial");
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (WorkshopUI.IsOpen) return;   // R13: 工坊打字不切 HUD/自驾模式
            if (kb.hKey.wasPressedThisFrame) _show = !_show;
            if (kb.kKey.wasPressedThisFrame) HelpVisible = !HelpVisible;
            var pilot = GetComponent<ModePilot>();
            if (pilot != null)
            {
                if (kb.digit1Key.wasPressedThisFrame) pilot.Engage(FlightMode.Rtl);
                else if (kb.digit2Key.wasPressedThisFrame) pilot.Engage(FlightMode.Land);
                else if (kb.digit3Key.wasPressedThisFrame) pilot.Engage(FlightMode.Loiter);
                else if (kb.digit4Key.wasPressedThisFrame) pilot.Engage(FlightMode.Stab);
                else if (kb.digit0Key.wasPressedThisFrame) pilot.Disengage();
            }
        }

        private void OnGUI()
        {
            if (WorkshopUI.IsOpen || !_show || _c == null || _c.Plane == null || _c.State == null)
                return;
            float k = HudLayout.Scale();
            BuildStyles(k);

            DrawTopBar(k);
            DrawHeadingRibbon(k);
            DrawSpeedAltitudeCluster(k);
            DrawAttitudeIndicator(k);
            DrawEngineSurfacesCluster(k);
            DrawSpecsPanel(k);
            DrawHelpPanel(k);
            DrawStatusBanners(k);
            DrawModeButtons(k);
            DrawWindPanel(k);
        }

        // ================= UI 贴图/字体生成 =================

        private void BuildTextures()
        {
            _solid = new Texture2D(1, 1);
            _solid.SetPixel(0, 0, Color.white);
            _solid.Apply();

            // 抗锯齿描边: 1 列 × 32 行, alpha 在"宽度"方向(纹理 V 轴)中心=1、两端平滑降到 0
            const int aaH = 32;
            var cols = new Color[aaH];
            for (int i = 0; i < aaH; i++)
            {
                float v = (i + 0.5f) / aaH;
                float t = Mathf.Abs(2f * v - 1f);
                float a = Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(0.6f, 1f, t));
                cols[i] = new Color(1f, 1f, 1f, a);
            }
            _aaStroke = new Texture2D(1, aaH);
            _aaStroke.SetPixels(cols);
            _aaStroke.wrapMode = TextureWrapMode.Clamp;
            _aaStroke.Apply();

            var panelBorder = FluentTheme.DarkBorder;
            // Fluent 深色 (2026-10-09 改版, 原深绿航空主题): 平面填充 + 1px 中性边
            _panelTex = RoundedRect(64, 8,
                FluentTheme.DarkPanel, FluentTheme.DarkPanel, panelBorder, 1f);
            _chipTex = RoundedRect(48, 8,
                FluentTheme.DarkCard, FluentTheme.DarkCard, panelBorder, 1f);
            _thumbTex = Circle(24, FluentTheme.DarkAccent,
                new Color(0.04f, 0.10f, 0.14f, 1f));
            _toggleOnTex = RoundedRect(24, 4,
                FluentTheme.DarkAccent, FluentTheme.DarkAccent,
                new Color(0.04f, 0.10f, 0.14f, 0.9f), 1f);
            _toggleOffTex = RoundedRect(24, 4,
                FluentTheme.DarkControl, FluentTheme.DarkControl, panelBorder, 1f);
            _btnTex = RoundedRect(48, 4,
                FluentTheme.DarkControl, FluentTheme.DarkControl, panelBorder, 1f);
            _btnActiveTex = RoundedRect(48, 4,
                FluentTheme.DarkAccent, FluentTheme.DarkAccent,
                new Color(0.04f, 0.10f, 0.14f, 0.9f), 1f);
            _alertTex = RoundedRect(48, 8,
                new Color(0.48f, 0.07f, 0.07f, 0.88f),
                new Color(0.48f, 0.07f, 0.07f, 0.92f),
                new Color(1f, 0.4f, 0.35f, 0.55f), 1.2f);
            // 姿态仪天/地: 上深下浅的蓝天 / 上浅下深的棕地
            _skyTex = VerticalGradient(64, new Color(0.16f, 0.36f, 0.58f, 1f),
                new Color(0.42f, 0.62f, 0.82f, 1f));
            _groundTex = VerticalGradient(64, new Color(0.55f, 0.42f, 0.27f, 1f),
                new Color(0.30f, 0.22f, 0.14f, 1f));
        }

        /// <summary>圆角矩形 9-slice 贴图: 纵向渐变填充 + 边缘描边带 + 1px 抗锯齿。</summary>
        internal static Texture2D RoundedRect(int size, int radiusPx,
            Color fillTop, Color fillBottom, Color border, float borderPx)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            var px = new Color[size * size];
            float half = size * 0.5f;
            float inner = half - radiusPx;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // 圆角矩形 SDF (负=内部)
                    float qx = Mathf.Abs((x + 0.5f) - half) - inner;
                    float qy = Mathf.Abs((y + 0.5f) - half) - inner;
                    float ax = Mathf.Max(qx, 0f), ay = Mathf.Max(qy, 0f);
                    float sd = Mathf.Sqrt(ax * ax + ay * ay)
                        + Mathf.Min(Mathf.Max(qx, qy), 0f) - radiusPx;

                    if (sd >= 0.6f) { px[y * size + x] = Color.clear; continue; }
                    Color c = Color.Lerp(fillTop, fillBottom, (y + 0.5f) / size);
                    float bw = Mathf.InverseLerp(-borderPx - 0.75f, -0.75f, sd);   // 0 深内 → 1 边缘
                    c = Color.Lerp(c, new Color(border.r, border.g, border.b,
                        Mathf.Max(c.a, border.a)), bw);
                    c.a *= Mathf.Clamp01(0.6f - sd);
                    px[y * size + x] = c;
                }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        /// <summary>圆形贴图 (滑条拇指): 中心填充 + 外圈环。</summary>
        private static Texture2D Circle(int size, Color fill, Color rim)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            var px = new Color[size * size];
            float c0 = size * 0.5f, r = size * 0.5f - 1f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - c0) * (x + 0.5f - c0)
                        + (y + 0.5f - c0) * (y + 0.5f - c0)) / r;
                    if (d >= 1.05f) { px[y * size + x] = Color.clear; continue; }
                    Color c = Color.Lerp(fill, rim, Mathf.SmoothStep(0f, 1f,
                        Mathf.InverseLerp(0.55f, 0.95f, d)));
                    c.a *= Mathf.Clamp01((1.05f - d) * 6f);
                    px[y * size + x] = c;
                }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        private static Texture2D VerticalGradient(int h, Color top, Color bottom)
        {
            var tex = new Texture2D(2, h, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            var px = new Color[2 * h];
            for (int y = 0; y < h; y++)
            {
                Color c = Color.Lerp(top, bottom, (y + 0.5f) / h);
                px[y * 2] = c; px[y * 2 + 1] = c;
            }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        internal static Font LoadOsFont(params string[] names)
        {
            foreach (var n in names)
            {
                try
                {
                    var f = Font.CreateDynamicFontFromOSFont(n, 16);
                    if (f != null) return f;
                }
                catch { }
            }
            return null;   // 回退: GUIStyle.font = null 用 Unity 内置默认字体
        }

        /// <summary>
        /// 按当前缩放因子 k 建 GUIStyle (fontSize 随 k 缩放; 贴图边框固定像素)。
        /// k 变化 (窗口缩放/切换分辨率) 时重建。
        /// </summary>
        private void BuildStyles(float k)
        {
            if (Mathf.Approximately(_stylesBuiltForK, k)) return;
            _stylesBuiltForK = k;

            int F(int basePx) => Mathf.Max(8, Mathf.RoundToInt(basePx * k));

            _big = new GUIStyle { font = _numFont, fontSize = F(42), fontStyle = FontStyle.Bold, normal = { textColor = HudGreen } };
            _bigWarn = new GUIStyle { font = _numFont, fontSize = F(42), fontStyle = FontStyle.Bold, normal = { textColor = HudAmber } };
            _mid = new GUIStyle { font = _labelFont, fontSize = F(26), fontStyle = FontStyle.Bold, normal = { textColor = HudGreen } };
            _midNum = new GUIStyle { font = _numFont, fontSize = F(26), fontStyle = FontStyle.Bold, normal = { textColor = HudGreen } };
            _small = new GUIStyle { font = _labelFont, fontSize = F(15), normal = { textColor = FluentTheme.DarkText } };
            _label = new GUIStyle { font = _labelFont, fontSize = F(15), normal = { textColor = FluentTheme.DarkTextDim } };
            _key = new GUIStyle { font = _numFont, fontSize = F(15), fontStyle = FontStyle.Bold, normal = { textColor = HudGreen } };

            // 右对齐 / 居中裁剪变体: 单位、数值、chip 文字不会写出各自的矩形
            _labelR = new GUIStyle(_label) { alignment = TextAnchor.MiddleRight, clipping = TextClipping.Clip };
            _smallR = new GUIStyle(_small) { alignment = TextAnchor.MiddleRight, clipping = TextClipping.Clip };
            _unit = new GUIStyle(_label) { alignment = TextAnchor.LowerRight };
            // 地面/空中状态: 比数值小一号、上对齐, 与同行的 G 数值基线接近, 不会掉到 G 条上
            _midR = new GUIStyle(_mid) { alignment = TextAnchor.UpperRight, fontSize = F(20), clipping = TextClipping.Clip };
            _chipText = new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip };
            _specValue = new GUIStyle(_key) { alignment = TextAnchor.MiddleRight, fontSize = _small.fontSize,
                fontStyle = FontStyle.Normal, clipping = TextClipping.Clip, normal = { textColor = HudGreen } };
            _specLive = new GUIStyle(_specValue) { normal = { textColor = HudAmber } };

            _panelStyle = new GUIStyle { normal = { background = _panelTex }, border = new RectOffset(16, 16, 16, 16) };
            _chipStyle = new GUIStyle { normal = { background = _chipTex }, border = new RectOffset(14, 14, 14, 14) };
            _alertStyle = new GUIStyle { normal = { background = _alertTex }, border = new RectOffset(14, 14, 14, 14) };

            _sliderThumbStyle = new GUIStyle
            {
                normal = { background = _thumbTex },
                fixedWidth = Mathf.Max(10f, 13f * k),
                fixedHeight = Mathf.Max(10f, 13f * k),
            };

            _btnIdleStyle = new GUIStyle
            {
                normal = { background = _btnTex, textColor = FluentTheme.DarkText },
                hover = { background = _btnTex, textColor = FluentTheme.DarkAccent },
                active = { background = _btnTex, textColor = FluentTheme.DarkText },
                border = new RectOffset(12, 12, 12, 12),
                alignment = TextAnchor.MiddleCenter,
                font = _labelFont, fontSize = F(15), fontStyle = FontStyle.Bold,
            };
            _btnActiveStyle = new GUIStyle
            {
                // Fluent 蓝底上用近黑文字 (白字在 #60CDFF 上对比度不足)
                normal = { background = _btnActiveTex, textColor = new Color(0.03f, 0.09f, 0.14f) },
                hover = { background = _btnActiveTex, textColor = Color.black },
                active = { background = _btnActiveTex, textColor = Color.black },
                border = new RectOffset(12, 12, 12, 12),
                alignment = TextAnchor.MiddleCenter,
                font = _labelFont, fontSize = F(15), fontStyle = FontStyle.Bold,
            };
        }

        // ================= 布局 =================

        private void DrawTopBar(float k)
        {
            // 顶栏一行: [场景切换 (SceneSwitchPanel 画)] [相机 chip] [键位提示 chip], 同一 y / 同一高度。
            // 原先的 Unity 场景名 (Flight_Phase2) 对用户没有信息量, 已去掉, 当前场景由切换按钮高亮表示。
            var cam = Camera.main != null ? Camera.main.GetComponent<ChaseCamera>() : null;
            string camMode = cam != null ? CamModeName(cam.Mode) : "";

            var r = HudLayout.CameraChipRect(k);
            GUI.Label(r, GUIContent.none, _chipStyle);
            GUI.Label(r, "相机: " + camMode + " (V 切换)", _chipText);

            var r2 = HudLayout.HelpChipRect(k);
            GUI.Label(r2, GUIContent.none, _chipStyle);
            GUI.Label(r2, "K 键位说明 · H 隐藏", _chipText);
        }

        // ================= 航向纸带罗盘 (顶部中央) =================

        private void DrawHeadingRibbon(float k)
        {
            float w = 300 * k, h = 34 * k;
            float cx = Screen.width * 0.5f;
            Rect r = new Rect(cx - w * 0.5f, 44 * k, w, h);
            GUI.Label(r, GUIContent.none, _chipStyle);

            float hdg = (_c.HeadingDeg + 360f) % 360f;
            float pxPerDeg = w / 64f;   // ±32° 窗口

            GUI.BeginGroup(r);
            int start = Mathf.CeilToInt((hdg - 36f) / 5f) * 5;
            var tickColor = new Color(0.85f, 0.92f, 0.87f, 0.85f);
            for (int d = start; d <= (int)(hdg + 36f); d += 5)
            {
                float x = w * 0.5f + (d - hdg) * pxPerDeg;
                if (x < 2f || x > w - 2f) continue;
                int dd = ((d % 360) + 360) % 360;
                bool major = dd % 30 == 0;
                float tickLen = major ? 10f * k : (dd % 10 == 0 ? 7f * k : 4.5f * k);
                DrawLine(new Vector2(x, h - 3 * k), new Vector2(x, h - 3 * k - tickLen),
                    tickColor, major ? 1.8f : 1.1f);
                if (major)
                {
                    var st = new GUIStyle(_key) { alignment = TextAnchor.MiddleCenter };
                    GUI.Label(new Rect(x - 22 * k, 2 * k, 44 * k, 18 * k), RibbonLabel(dd), st);
                }
            }
            GUI.EndGroup();

            // 中央 caret (底部上指) + 航向数字 chip (骑在纸带上缘)
            DrawArrowHead(new Vector2(cx, r.yMax - 2f * k), Vector2.down, 5f * k, Color.yellow);
            var chip = HudLayout.HeadingValueRect(Screen.width, k);
            GUI.Label(chip, GUIContent.none, _chipStyle);
            var num = new GUIStyle(_key) { alignment = TextAnchor.MiddleCenter, fontSize = _key.fontSize + 1 };
            GUI.Label(chip, string.Format("{0:000}", Mathf.RoundToInt(hdg) % 360), num);
        }

        private static string RibbonLabel(int deg)
        {
            switch (deg)
            {
                case 0: return "N";
                case 45: return "NE";
                case 90: return "E";
                case 135: return "SE";
                case 180: return "S";
                case 225: return "SW";
                case 270: return "W";
                case 315: return "NW";
                default: return (deg / 10).ToString();
            }
        }

        // ================= 性能参数面板 (右侧, 发动机簇下方, 右列同宽对齐) =================

        /// <summary>性能参数行 (名称, 数值文本, 数值浮点 — 供面板与测试共用)。</summary>
        public struct SpecRow
        {
            public string Name, Text;
            public double Value;
            public bool IsLive;   // 实时量 (绿), 其余为设计常量 (灰绿)
        }

        /// <summary>计算全部性能参数行 (公开供 PlayMode 测试断言)。</summary>
        public static System.Collections.Generic.List<SpecRow> ComputeSpecs(
            Aircraft plane, AircraftConfigLoader.DesignData design)
        {
            var a = plane.Aero.Data;
            double b = a.Bref, S = a.Sref, c = a.Cref;
            double ar = b * b / S;
            double sT = design.HtailHalfSpanM * design.HtailRootChordM * (1 + design.HtailTaper);
            double vH = sT * design.HtailArmM / (S * c);
            double sV = design.VtailHeightM * design.VtailRootChordM * (1 + design.VtailTaper) / 2;
            double vV = sV * design.HtailArmM / (S * b);
            double sm = a.CLalpha > 1e-9 ? -a.Cmalpha / a.CLalpha * 100 : 0;   // % MAC
            double maxLD = a.KInduced > 1e-12 && a.CD0 > 1e-12
                ? 1 / (2 * System.Math.Sqrt(a.KInduced * a.CD0)) : 0;
            double wingLoad = plane.Mass.MassKg * 9.81 / S;   // N/m²
            double vs = System.Math.Sqrt(2 * plane.Mass.MassKg * 9.81 /
                (AtmosphereModel.Density(0) * S * 1.5));       // CLmax=1.5 估计, 密度取跑道面 (高原场景随海拔)

            return new System.Collections.Generic.List<SpecRow>
            {
                new SpecRow { Name = "翼展", Text = b.ToString("F2") + " m", Value = b },
                new SpecRow { Name = "机翼面积", Text = S.ToString("F2") + " m²", Value = S },
                new SpecRow { Name = "展弦比", Text = ar.ToString("F2"), Value = ar },
                new SpecRow { Name = "平均弦长", Text = c.ToString("F3") + " m", Value = c },
                new SpecRow { Name = "平尾容量", Text = vH.ToString("F3"), Value = vH },
                new SpecRow { Name = "垂尾容量", Text = vV.ToString("F4"), Value = vV },
                new SpecRow { Name = "起飞质量", Text = plane.Mass.MassKg.ToString("F1") + " kg", Value = plane.Mass.MassKg },
                new SpecRow { Name = "翼载荷", Text = wingLoad.ToString("F1") + " N/m²", Value = wingLoad },
                new SpecRow { Name = "升力线斜率", Text = a.CLalpha.ToString("F2") + " /rad", Value = a.CLalpha },
                new SpecRow { Name = "静稳定裕度", Text = sm.ToString("F1") + " %MAC", Value = sm },
                new SpecRow { Name = "最大升阻比", Text = maxLD.ToString("F1"), Value = maxLD },
                new SpecRow { Name = "失速速度(估)", Text = vs.ToString("F1") + " m/s", Value = vs },
                new SpecRow { Name = "最大推力", Text = plane.Engine.MaxThrustN.ToString("F0") + " N", Value = plane.Engine.MaxThrustN },
            };
        }

        private void DrawSpecsPanel(float k)
        {
            var design = AircraftConfigLoader.LastDesign ?? new AircraftConfigLoader.DesignData();
            var rows = ComputeSpecs(_c.Plane, design);

            float w = HudLayout.ColW * k;   // 右列统一宽度 (发动机簇与性能参数同宽)
            float pad = 14 * k, lineH = 23 * k;
            float titleBlock = 38 * k;      // 标题 + accent 线 + 与首行的留白
            float h = pad * 2 + titleBlock + (rows.Count + 1) * lineH;   // +1 = 实时 L/D 行

            // 右列: 发动机簇 (16..240) 下方 12; 底部不得压到右下高度簇 (顶 = H - 16 - 212) 再留 12
            float top = (HudLayout.Margin + EngineClusterH + HudLayout.Gap) * k;
            float limit = Screen.height - (HudLayout.Margin + HudLayout.BottomH + HudLayout.Gap) * k;
            if (top + h > limit)   // 矮视口: 压缩行距兜底
            {
                float s = Mathf.Max(0.6f, (limit - top - pad * 2 - titleBlock) / ((rows.Count + 1) * lineH));
                lineH = Mathf.Max(17f, lineH * s);
                h = Mathf.Min(pad * 2 + titleBlock + (rows.Count + 1) * lineH, limit - top);
            }

            Rect r = new Rect(Screen.width - w - HudLayout.Margin * k, top, w, h);
            Panel(r);

            float y = r.y + pad;
            GUI.Label(new Rect(r.x + pad, y, w - pad * 2, 24 * k), "性能参数", _mid);
            AccentLine(r.x + pad, y + 28 * k, 60 * k);
            y += titleBlock - 4 * k;
            foreach (var row in rows)
            {
                GUI.Label(new Rect(r.x + pad, y, w * 0.5f, lineH), row.Name, _label);
                GUI.Label(new Rect(r.x + w * 0.5f, y, w * 0.5f - pad, lineH), row.Text, _specValue);
                y += lineH;
            }

            // 实时升阻比 (当前 CL/CD)
            double cd = _c.LastAero.CD, cl = _c.LastAero.CL;
            string ldTxt = cd > 1e-6 ? (cl / cd).ToString("F1") : "—";
            GUI.Label(new Rect(r.x + pad, y, w * 0.5f, lineH), "实时升阻比", _label);
            GUI.Label(new Rect(r.x + w * 0.5f, y, w * 0.5f - pad, lineH), ldTxt, _specLive);
        }

        // ================= 风况调节面板 (姿态仪左侧, 与模式按钮对称) =================

        private float _windLevel = -1;   // -1 = 尚未从 WindSettings 同步
        private float _windDir;
        private bool _windVeer;
        private float _syncedSpd, _syncedDir;   // 滑条最近一次与 WindSettings 对齐时的风速/风向
        private bool _syncedVeer;

        /// <summary>风力等级 → (稳定风速 m/s, 紊流等级, 是否含紊流)。</summary>
        private static (float spd, DrydenTurbulence.TurbulenceLevel lv, bool turb) LevelToWind(int level) =>
            level switch
            {
                0 => (0f, DrydenTurbulence.TurbulenceLevel.Light, false),
                1 => (2f, DrydenTurbulence.TurbulenceLevel.Light, true),
                2 => (4f, DrydenTurbulence.TurbulenceLevel.Light, true),
                3 => (6f, DrydenTurbulence.TurbulenceLevel.Moderate, true),
                4 => (8f, DrydenTurbulence.TurbulenceLevel.Moderate, true),
                5 => (10f, DrydenTurbulence.TurbulenceLevel.Severe, true),
                6 => (12f, DrydenTurbulence.TurbulenceLevel.Severe, true),
                _ => (14f, DrydenTurbulence.TurbulenceLevel.Severe, true),
            };

        private void DrawWindPanel(float k)
        {
            var wind = GetComponent<WindSettings>();
            if (wind == null) return;

            // 首帧、以及风被外部改动 (场景切换下发默认风) 时, 把 WindSettings 的当前值同步到滑条
            if (_windLevel < 0 || Mathf.Abs(wind.WindSpeed - _syncedSpd) > 0.01f
                || Mathf.Abs(wind.WindDirectionDeg - _syncedDir) > 0.01f || wind.DirectionVeer != _syncedVeer)
            {
                // 反解等级: 取最接近的风速档
                _windLevel = Mathf.Clamp(Mathf.Round(wind.WindSpeed / 2f), 0f, 7f);
                _windDir = wind.WindDirectionDeg;
                _windVeer = wind.DirectionVeer;
                _syncedSpd = wind.WindSpeed; _syncedDir = wind.WindDirectionDeg; _syncedVeer = wind.DirectionVeer;
            }

            // 底部中央簇的左块: 姿态仪 (宽 BottomH) 左侧, 与右侧模式键对称; 底边与其他底部块统一
            float w = HudLayout.WindPanelW * k, h = HudLayout.BottomH * k;
            float adiHalf = HudLayout.BottomH * k * 0.5f;
            float x = Screen.width * 0.5f - adiHalf - HudLayout.Gap * k - w;
            float y = Screen.height - h - HudLayout.Margin * k;
            Rect r = new Rect(x, y, w, h);
            Panel(r);

            float pad = 14 * k, rowH = 22 * k;
            float inner = w - pad * 2;
            var trackColor = new Color(0.376f, 0.804f, 1f, 0.55f);
            GUI.Label(new Rect(r.x + pad, r.y + 10 * k, inner, 28 * k), "风况", _mid);
            AccentLine(r.x + pad, r.y + 40 * k, 40 * k);

            // 行 1: 风速描述 (左) + 等级 (右); 下方整宽滑条 (轨道 = 一条细直线, 拇指小圆点)
            float lvRow = r.y + 54 * k;
            float lvY = r.y + 90 * k;
            DrawLine(new Vector2(r.x + pad, lvY), new Vector2(r.xMax - pad, lvY), trackColor, 2f * k);
            float lv = Mathf.Round(GUI.HorizontalSlider(
                new Rect(r.x + pad, lvY - 8 * k, inner, 16 * k),
                _windLevel, 0f, 7f, GUIStyle.none, _sliderThumbStyle));
            var (spd, lvTurb, lvOn) = LevelToWind((int)lv);
            GUI.Label(new Rect(r.x + pad, lvRow, inner - 44 * k, rowH),
                spd < 0.1f ? "无风" : string.Format("{0:F0} m/s · 紊流{1}", spd,
                    !lvOn ? "关" : lvTurb == DrydenTurbulence.TurbulenceLevel.Light ? "轻"
                    : lvTurb == DrydenTurbulence.TurbulenceLevel.Moderate ? "中" : "强"), _label);
            GUI.Label(new Rect(r.xMax - pad - 44 * k, lvRow, 44 * k, rowH), ((int)lv) + " 级", _smallR);

            // 行 2: "来风方向" (左) + 角度与方位 (右); 下方整宽滑条
            float dirRow = r.y + 112 * k;
            float dirY = r.y + 148 * k;
            DrawLine(new Vector2(r.x + pad, dirY), new Vector2(r.xMax - pad, dirY), trackColor, 2f * k);
            float dir = GUI.HorizontalSlider(
                new Rect(r.x + pad, dirY - 8 * k, inner, 16 * k),
                _windDir, 0f, 360f, GUIStyle.none, _sliderThumbStyle);
            GUI.Label(new Rect(r.x + pad, dirRow, inner * 0.5f, rowH), "来风方向", _label);
            GUI.Label(new Rect(r.x + pad + inner * 0.5f, dirRow, inner * 0.5f, rowH),
                string.Format("{0:F0}° {1}", dir, Cardinal(dir)), _smallR);

            // 行 3: 方向缓变勾选 (自绘开关盒 + 短文字; 说明放在文字里会撑出面板)
            float togY = r.y + 172 * k;
            var tog = new Rect(r.x + pad, togY + 3 * k, 16 * k, 16 * k);
            bool veer = GUI.Toggle(tog, _windVeer, GUIContent.none, GUIStyle.none);
            GUI.DrawTexture(tog, veer ? _toggleOnTex : _toggleOffTex);
            GUI.Label(new Rect(tog.xMax + 8 * k, togY, inner - 24 * k, rowH), "风向缓变", _small);

            // 有变化 → 实时应用 (保持紊流状态连续)
            if (Mathf.Abs(lv - _windLevel) > 0.01f || Mathf.Abs(dir - _windDir) > 0.5f || veer != _windVeer)
            {
                _windLevel = lv; _windDir = dir; _windVeer = veer;
                wind.SetWeather(spd, dir, veer, lvTurb, lvOn);
                _syncedSpd = spd; _syncedDir = dir; _syncedVeer = veer;
            }
        }

        // ================= 飞行模式按钮 (姿态仪右侧, 点击或数字键 1-4) =================

        private static readonly (FlightMode mode, string label, string key)[] ModeButtons =
        {
            (FlightMode.Rtl, "一键返航", "1"),
            (FlightMode.Land, "一键降落", "2"),
            (FlightMode.Loiter, "盘旋", "3"),
            (FlightMode.Stab, "自稳模式", "4"),
        };

        private void DrawModeButtons(float k)
        {
            var pilot = GetComponent<ModePilot>();
            if (pilot == null) return;

            // 姿态仪右侧竖排四键: 总高 = BottomH, 与风况面板/姿态仪同顶同底
            float total = HudLayout.BottomH * k, gap = HudLayout.Gap * k;
            float bh = (total - gap * (ModeButtons.Length - 1)) / ModeButtons.Length;
            float w = HudLayout.ModeButtonW * k;
            float x = Screen.width * 0.5f + HudLayout.BottomH * k * 0.5f + HudLayout.Gap * k;
            float y = Screen.height - total - HudLayout.Margin * k;

            var keyHint = new GUIStyle(_key)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = Mathf.Max(8, Mathf.RoundToInt(13 * k)),
                normal = { textColor = new Color(0.72f, 0.72f, 0.72f, 0.9f) },
            };
            var keyHintActive = new GUIStyle(keyHint) { normal = { textColor = new Color(0.25f, 0.15f, 0.02f) } };

            for (int i = 0; i < ModeButtons.Length; i++)
            {
                var (mode, label, key) = ModeButtons[i];
                var rect = new Rect(x, y + i * (bh + gap), w, bh);
                bool active = pilot.Mode == mode;
                if (GUI.Button(rect, label, active ? _btnActiveStyle : _btnIdleStyle))
                    pilot.Engage(mode);
                GUI.Label(new Rect(rect.x + 10 * k, rect.y, 18 * k, rect.height), key, active ? keyHintActive : keyHint);
            }

            // 当前模式 + 阶段 (按钮组上方居中小字, 与按钮同宽, 超长裁剪)
            if (pilot.Mode != FlightMode.Manual)
            {
                var st = new GUIStyle(_chipText);
                st.normal.textColor = HudAmber;
                GUI.Label(new Rect(x, y - 26 * k, w, 22 * k),
                    ModePilot.ModeName(pilot.Mode) + " · " + pilot.Phase, st);
            }
        }

        // ================= 键位说明面板 (K 开关, 贴左缘, 不挡中央飞行区) =================

        /// <summary>帮助面板可见性 (static: 会话内跨场景保持用户选择)。
        /// 默认关闭 —— 默认开着一整块键位说明面板会让首次进入的画面显得很杂,
        /// 键位提示已经在顶栏"K 键位说明"里给了入口, 需要时按 K 再展开。</summary>
        public static bool HelpVisible = false;

        private static readonly (string group, (string key, string desc)[])[] HelpContent =
        {
            ("飞行操纵", new[]
            {
                ("W / S", "俯仰 (S 拉杆抬头 · 遥控器习惯)"), ("A / D", "滚转 (D 右压杆)"),
                ("Q / E", "方向舵 (Q 左舵 / E 右舵)"),
            }),
            ("油门与地面", new[]
            {
                ("Shift / Ctrl", "油门增减"), ("B", "刹车 (按住)"),
            }),
            ("配平", new[]
            {
                ("R / F", "俯仰配平 (抬头/低头)"),
            }),
            ("视角 (V 切换)", new[]
            {
                ("追尾", "第三人称跟随"), ("座舱", "第一人称随机身"),
                ("自由观察", "RMB 拖动旋转 · 滚轮缩放"),
            }),
            ("功能", new[]
            {
                ("Backspace", "复位到跑道重新起飞"),
                ("F9", "遥测记录 开/停"), ("H", "隐藏 HUD"), ("K", "本面板"),
                ("Esc", "菜单 (继续/设置/退出)"),
                ("靠近边界", "自动转向起飞点"),
                ("1/2/3/4", "返航 / 降落 / 盘旋 / 自稳"),
            }),
        };

        /// <summary>坠毁横幅 (红) 与边界返航提示 (琥珀)。</summary>
        private void DrawStatusBanners(float k)
        {
            // 边界返航提示 (航向纸带下方, 琥珀色)
            var guard = GetComponent<FlightGuard>();
            if (guard != null && guard.IsTurningBack)
            {
                float bannerY = Mathf.Max(84f * k,
                    HudLayout.HeadingValueRect(Screen.width, k).yMax + 6f * k);
                var br = new Rect(Screen.width * 0.5f - 280 * k, bannerY, 560 * k, 30 * k);
                GUI.Label(br, GUIContent.none, _chipStyle);
                var style = new GUIStyle(_small)
                {
                    normal = { textColor = HudAmber },
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = Mathf.Max(8, Mathf.RoundToInt(17 * k)),
                };
                GUI.Label(br, string.Format("⚠ 接近场地边界  距起飞点 {0:F0} m — 自动返航中{1}",
                                              guard.DistanceToHome, guard.IsClimbingToClear ? " · 爬升避地形" : ""), style);
            }

            // 坠毁横幅 (居中, 圆角红底)
            if (_c.IsCrashed)
            {
                float w = 620 * k, h = 110 * k;
                Rect r = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.32f, w, h);
                GUI.Label(r, GUIContent.none, _alertStyle);

                var title = new GUIStyle(_mid) { alignment = TextAnchor.MiddleCenter };
                title.normal.textColor = new Color(1f, 0.35f, 0.3f);
                GUI.Label(new Rect(r.x, r.y + 12 * k, r.width, 52 * k), "已 坠 毁", title);
                var sub = new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter };
                sub.normal.textColor = new Color(1f, 0.75f, 0.7f);
                string reason = string.IsNullOrEmpty(_c.CrashReason) ? "" : _c.CrashReason + " · ";
                GUI.Label(new Rect(r.x, r.y + 64 * k, r.width, 34 * k),
                    reason + "按 Backspace 复位到跑道重新起飞", sub);
            }
        }

        private void DrawHelpPanel(float k)
        {
            if (!HelpVisible) return;

            float w = 340 * k;   // 最长一条 "RMB 拖动旋转 · 滚轮缩放" 约 210 px + 键列 112 px
            float lineH = 24 * k, groupGap = 8 * k, groupH = 22 * k, pad = 14 * k;

            float Measure() => pad * 2 + 30 * k
                + TotalEntries() * lineH + HelpContent.Length * (groupGap + groupH);

            // 可用竖直空间: 顶栏 (含场景切换确认提示, 到 y≈70) 下方 → 左下空速簇上方留一个 Gap
            float top = 80 * k;
            float limit = Screen.height - (HudLayout.Margin + HudLayout.BottomH + HudLayout.Gap) * k;
            float h = Measure();

            if (top + h > limit)   // 视口过矮: 压缩行距/组距兜底 (字体不变)
            {
                float inner = h - pad * 2;
                float s = Mathf.Max(0.6f, (limit - top - pad * 2) / inner);
                lineH = Mathf.Max(18f, lineH * s);
                groupGap *= s;
                groupH = Mathf.Max(18f, groupH * s);
                h = Mathf.Min(Measure(), limit - top);
            }

            Rect r = new Rect(HudLayout.Margin * k, top, w, h);
            Panel(r);

            float y = r.y + pad;
            GUI.Label(new Rect(r.x + pad, y, w - pad * 2, 26 * k), "键位说明 (K 收起)", _mid);
            AccentLine(r.x + pad, y + 27 * k, 70 * k);
            y += 32 * k;
            foreach (var (group, entries) in HelpContent)
            {
                GUI.Label(new Rect(r.x + pad, y, w - pad * 2, 20 * k), group, _label);
                y += groupH;
                foreach (var (key, desc) in entries)
                {
                    GUI.Label(new Rect(r.x + pad, y, 112 * k, lineH), key, _key);
                    GUI.Label(new Rect(r.x + pad + 118 * k, y, w - pad * 2 - 118 * k, lineH), desc, _small);
                    y += lineH;
                }
                y += groupGap;
            }
        }

        private static int TotalEntries()
        {
            int n = 0;
            foreach (var (_, entries) in HelpContent) n += entries.Length;
            return n;
        }

        private void DrawSpeedAltitudeCluster(float k)
        {
            // 左右两块: 同宽 SidePanelW、同高 BottomH、底边在 Margin 处; 数值左对齐, 单位右对齐
            float w = HudLayout.SidePanelW * k, h = HudLayout.BottomH * k;
            float pad = 16 * k;
            float top = Screen.height - h - HudLayout.Margin * k;
            float inner = w - pad * 2;

            // 左: 空速 + 垂直速度
            Rect left = new Rect(HudLayout.Margin * k, top, w, h);
            Panel(left);
            float lx = left.x + pad;
            GUI.Label(new Rect(lx, left.y + 12 * k, inner, 24 * k), "空速 IAS", _label);
            AccentLine(lx, left.y + 36 * k, 46 * k);
            bool lowSpeed = !_c.OnGround && _c.Airspeed < LowSpeedWarningSpeed;
            var spdRow = new Rect(lx, left.y + 42 * k, inner, 56 * k);
            GUI.Label(spdRow, _c.Airspeed.ToString("F1"), lowSpeed ? _bigWarn : _big);
            GUI.Label(new Rect(spdRow.x, spdRow.y - 14 * k, spdRow.width, spdRow.height), "m/s", _unit);

            GUI.Label(new Rect(lx, left.y + 112 * k, inner, 24 * k), "垂直速度", _label);
            AccentLine(lx, left.y + 136 * k, 46 * k);
            string vsArrow = _c.VerticalSpeed > 0.2f ? "▲ " : (_c.VerticalSpeed < -0.2f ? "▼ " : "· ");
            var vsRow = new Rect(lx, left.y + 142 * k, inner, 44 * k);
            GUI.Label(vsRow, vsArrow + Mathf.Abs(_c.VerticalSpeed).ToString("F1"), _midNum);
            GUI.Label(new Rect(vsRow.x, vsRow.y - 10 * k, vsRow.width, vsRow.height), "m/s", _unit);

            // 右: 高度 (含离地) + 过载 + 地面/空中状态
            Rect right = new Rect(Screen.width - w - HudLayout.Margin * k, top, w, h);
            Panel(right);
            float rx = right.x + pad;
            GUI.Label(new Rect(rx, right.y + 12 * k, inner, 24 * k), "高度 ALT", _label);
            AccentLine(rx, right.y + 36 * k, 46 * k);
            var altRow = new Rect(rx, right.y + 42 * k, inner, 56 * k);
            GUI.Label(altRow, _c.Altitude.ToString("F0"), _big);
            GUI.Label(new Rect(altRow.x, altRow.y - 14 * k, altRow.width, altRow.height), "m", _unit);
            // 有地形时追加离地高度 AGL (海拔 = 相对机场台地面; AGL = 相对脚下地形)
            GUI.Label(new Rect(rx, right.y + 96 * k, inner, 20 * k),
                TerrainRegistry.HasTerrain ? "离地 AGL  " + _c.AltitudeAgl.ToString("F0") + " m" : "相对机场面", _label);

            GUI.Label(new Rect(rx, right.y + 124 * k, inner, 24 * k), "过载 G", _label);
            AccentLine(rx, right.y + 148 * k, 46 * k);
            var gRow = new Rect(rx, right.y + 154 * k, inner, 34 * k);
            GUI.Label(new Rect(gRow.x, gRow.y, inner * 0.4f, gRow.height), _c.GLoad.ToString("F2"), _midNum);
            GUI.Label(new Rect(gRow.x + inner * 0.4f, gRow.y + 5 * k, inner * 0.6f, gRow.height - 5 * k),
                _c.OnGround ? "● 地面" : "○ 空中", _midR);
            float gNorm = Mathf.Clamp(_c.GLoad, -2f, 5f);
            Rect gBar = new Rect(rx, right.y + 194 * k, inner, 8 * k);
            GUI.Label(gBar, GUIContent.none, _chipStyle);
            float gMid = gBar.x + gBar.width * (2f / 7f);   // 0..5 映射, 1G 在 2/7 处
            float gX = gBar.x + gBar.width * Mathf.Clamp((gNorm + 2f) / 7f, 0f, 1f);
            DrawRect(new Rect(Mathf.Min(gMid, gX), gBar.y + 2 * k, Mathf.Abs(gX - gMid) + 1, gBar.height - 4 * k),
                     FluentTheme.DarkAccent);
        }

        private void DrawAttitudeIndicator(float k)
        {
            // 面板边长 = BottomH, 与风况面板/模式键同高; 仪表盘上移, 底部留一行放俯仰/滚转数字 (不再压在仪表上)
            float s = HudLayout.BottomH * k;
            Rect r = new Rect((Screen.width - s) * 0.5f, Screen.height - s - HudLayout.Margin * k, s, s);
            Panel(r);

            float pitch = _c.PitchDeg;
            float roll = _c.BankDeg;
            float winR = s * 0.375f;
            float u = winR / 0.44f;   // 仪表内部比例基准 (沿用原 190 面板下的比例关系)
            float pxPerDeg = u / 55f;
            Vector2 c = new Vector2(r.center.x, r.y + s * 0.465f);

            // ---- 天/地球面填充 (内接方形裁剪区, 随姿态旋转/平移) ----
            float half = winR * 0.82f;   // 稍小于内接正方形, 四角留 bezel
            Rect inner = new Rect(c.x - half, c.y - half, half * 2f, half * 2f);

            GUI.BeginGroup(inner);
            Vector2 ic = new Vector2(inner.width * 0.5f, inner.height * 0.5f);
            var oldM = GUI.matrix;
            GUIUtility.RotateAroundPivot(-roll, ic);   // 右压坡度时地平线右侧上抬

            float hy = ic.y + pitch * pxPerDeg;        // 抬头 → 地平线下移
            float big = u * 2f;
            GUI.DrawTexture(new Rect(ic.x - big, hy - big, big * 2f, big), _skyTex);
            GUI.DrawTexture(new Rect(ic.x - big, hy, big * 2f, big), _groundTex);
            DrawLine(new Vector2(ic.x - winR, hy), new Vector2(ic.x + winR, hy),
                Color.white, 2f * k);

            // 俯仰刻度梯 (±10/±20°, 随地平线运动)
            for (int pdg = -20; pdg <= 20; pdg += 10)
            {
                if (pdg == 0) continue;
                float ly = hy - pdg * pxPerDeg;
                float len = (pdg % 20 == 0 ? 0.13f : 0.08f) * u;
                DrawLine(new Vector2(ic.x - len, ly), new Vector2(ic.x + len, ly),
                    new Color(1f, 1f, 1f, 0.75f), 1f * k);
                if (pdg % 20 == 0)
                {
                    var lb = new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter };
                    string txt = (pdg > 0 ? "+" : "") + pdg;
                    GUI.Label(new Rect(ic.x + len + 3 * k, ly - 8 * k, 28 * k, 16 * k), txt, lb);
                    GUI.Label(new Rect(ic.x - len - 31 * k, ly - 8 * k, 28 * k, 16 * k), txt, lb);
                }
            }
            GUI.matrix = oldM;
            GUI.EndGroup();

            // 窗口圆环 (球面 bezel)
            DrawCircle(c, winR, new Color(1f, 1f, 1f, 0.35f));
            DrawCircle(c, winR - 2.5f * k, new Color(0.376f, 0.804f, 1f, 0.20f));

            // ---- 固定机体符号 (黄色, 不随姿态) ----
            DrawLine(c + new Vector2(-30 * k, 0), c + new Vector2(-10 * k, 0), Color.yellow, 2.5f * k);
            DrawLine(c + new Vector2(10 * k, 0), c + new Vector2(30 * k, 0), Color.yellow, 2.5f * k);
            DrawLine(c + new Vector2(-10 * k, 0), c + new Vector2(0, 8 * k), Color.yellow, 2.5f * k);
            DrawLine(c + new Vector2(10 * k, 0), c + new Vector2(0, 8 * k), Color.yellow, 2.5f * k);
            DrawRect(new Rect(c.x - 1.5f * k, c.y - 1.5f * k, 3 * k, 3 * k), Color.yellow);

            // ---- 滚转角刻度弧 (固定于姿态仪顶部; 三角指针随实际坡度转动) ----
            float bankRadius = winR + 8f * k;
            var bankColor = new Color(0.85f, 0.9f, 0.85f, 0.8f);
            float[] bankTicks = { 0f, 10f, 20f, 30f, 45f, 60f };
            foreach (float bt in bankTicks)
            {
                float tickLen = (bt == 0f || bt == 30f || bt == 60f) ? 9f * k : 6f * k;
                DrawLine(BankArcPos(c, bankRadius - tickLen, bt), BankArcPos(c, bankRadius, bt), bankColor, 1.5f * k);
                if (bt != 0f)
                    DrawLine(BankArcPos(c, bankRadius - tickLen, -bt), BankArcPos(c, bankRadius, -bt), bankColor, 1.5f * k);
            }
            float rollPtr = Mathf.Clamp(roll, -65f, 65f);
            Vector2 ptrTip = BankArcPos(c, bankRadius - 3f * k, rollPtr);
            Vector2 ptrOut = (ptrTip - c).normalized;
            DrawArrowHead(ptrTip, -ptrOut, 6f * k, Color.yellow);

            // 俯仰/滚转数字
            GUI.Label(new Rect(r.x + 8 * k, r.yMax - 28 * k, r.width - 16 * k, 22 * k),
                string.Format("俯仰 {0:F1}°   滚转 {1:F1}°", pitch, roll), _chipText);
        }

        private void DrawEngineSurfacesCluster(float k)
        {
            float w = HudLayout.ColW * k, h = EngineClusterH * k;
            Rect r = new Rect(Screen.width - w - HudLayout.Margin * k, HudLayout.Margin * k, w, h);
            Panel(r);
            float pad = 14 * k;
            float x = r.x + pad, y = r.y + 10 * k;

            // 油门
            GUI.Label(new Rect(x, y, w, 22 * k), "油门 THR", _label);
            AccentLine(x, y + 22 * k, 40 * k);
            y += 30 * k;
            Rect tBar = new Rect(x, y, w - pad * 2 - 48 * k, 12 * k);
            GUI.Label(tBar, GUIContent.none, _chipStyle);
            DrawRect(new Rect(tBar.x + 2 * k, tBar.y + 2 * k,
                (tBar.width - 4 * k) * Mathf.Clamp01(_c.Throttle), tBar.height - 4 * k),
                     new Color(1f, 0.72f, 0.2f));
            GUI.Label(new Rect(tBar.xMax + 6 * k, y - 3 * k, 48 * k, 20 * k),
                (_c.Throttle * 100f).ToString("F0") + "%", _key);
            y += 28 * k;

            // 操纵面偏转指示
            GUI.Label(new Rect(x, y, w, 22 * k), "操纵面", _label);
            AccentLine(x, y + 22 * k, 40 * k);
            y += 28 * k;
            float barH = 92 * k;
            float maxE = 25f, maxA = 20f, maxR = 25f;
            var surf = _c.Surfaces;
            if (surf != null)
            {
                maxE = (float)(surf.MaxElevatorRad * Mathd.Rad2Deg);
                maxA = (float)(surf.MaxAileronRad * Mathd.Rad2Deg);
                maxR = (float)(surf.MaxRudderRad * Mathd.Rad2Deg);
            }
            SurfBar(x, y, barH, 20 * k, _c.ElevatorDeg, maxE, "升降");
            SurfBar(x + 52 * k, y, barH, 20 * k, _c.AileronDeg, maxA, "副翼");
            SurfBar(x + 104 * k, y, barH, 20 * k, _c.RudderDeg, maxR, "方向");

            // 风向指示 (右下角小罗盘)
            Rect wind = new Rect(x + 140 * k, y - 4 * k, 88 * k, 100 * k);
            Vector2 wc = new Vector2(wind.x + wind.width * 0.5f, wind.y + 40 * k);
            float rad = 32 * k;
            DrawCircle(wc, rad, new Color(1f, 1f, 1f, 0.5f));
            // 显示平滑 (τ≈1.5s): 紊流导致的矢量抖动滤掉, 读数连续稳定
            Vector2 wTarget = new Vector2(
                -Mathf.Cos(_c.WindFromDeg * Mathf.Deg2Rad),
                -Mathf.Sin(_c.WindFromDeg * Mathf.Deg2Rad)) * _c.WindSpeed;
            float wAlpha = 1f - Mathf.Exp(-Time.deltaTime / 1.5f);
            _smWindVec = Vector2.Lerp(_smWindVec, wTarget, wAlpha);
            float smSpeed = _smWindVec.magnitude;
            float smFrom = smSpeed > 0.05f
                ? Mathf.Atan2(-_smWindVec.y, -_smWindVec.x) * Mathf.Rad2Deg : _c.WindFromDeg;

            float rel = (smFrom - _c.HeadingDeg) * Mathf.Deg2Rad;
            Vector2 from = wc + new Vector2(Mathf.Sin(rel), -Mathf.Cos(rel)) * rad;  // 风来的方向
            Vector2 tip = Vector2.Lerp(from, wc, 0.75f);
            DrawLine(from, tip, Color.cyan, 2f * k);
            DrawArrowHead(tip, (wc - from).normalized, 6 * k, Color.cyan);
            var windTxt = new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(wind.x, wind.y + 76 * k, wind.width, 18 * k),
                smSpeed < 0.05f ? "无风" : string.Format("{0:F1} m/s", smSpeed), windTxt);
            if (smSpeed >= 0.05f)
                GUI.Label(new Rect(wind.x, wind.y + 92 * k, wind.width, 16 * k),
                    string.Format("来风 {0:F0}°", ((smFrom % 360f) + 360f) % 360f), windTxt);
        }

        /// <summary>垂直操纵面指示条 (上=正偏转, 中线=0)。</summary>
        private void SurfBar(float x, float y, float h, float w, float valueDeg, float maxDeg, string name)
        {
            Rect bar = new Rect(x, y, w, h);
            GUI.Label(bar, GUIContent.none, _chipStyle);
            float mid = y + h * 0.5f;
            DrawLine(new Vector2(x + 2, mid), new Vector2(x + w - 2, mid), new Color(1f, 1f, 1f, 0.4f), 1f);
            float frac = Mathf.Clamp(valueDeg / maxDeg, -1f, 1f);
            float my = mid - frac * h * 0.5f;
            DrawRect(new Rect(x + 3, Mathf.Min(mid, my), w - 6, Mathf.Abs(my - mid) + 1f),
                     new Color(0.55f, 0.9f, 1f));
            GUI.Label(new Rect(x - 8, y + h + 2, w + 16, 18), name, _small);
        }

        // ================= 绘制原语 =================

        /// <summary>圆角渐变面板背板 (9-slice), 替代硬边 GUI.Box + L 角括号。</summary>
        private void Panel(Rect r)
        {
            GUI.Label(r, GUIContent.none, _panelStyle);
        }

        /// <summary>面板标题下的 accent 细线 (Fluent 蓝低透明, 视觉分区)。</summary>
        private void AccentLine(float x, float y, float w)
        {
            DrawRect(new Rect(x, y, w, 2f), new Color(0.376f, 0.804f, 1f, 0.45f));
        }

        /// <summary>硬边纯色填充 (进度条等需要清晰边界的场合)。</summary>
        private void DrawRect(Rect r, Color c)
        {
            Color old = GUI.color; GUI.color = c;
            GUI.DrawTexture(r, _solid);
            GUI.color = old;
        }

        /// <summary>抗锯齿描边: 用于所有旋转直线/圆/箭头, 见 <see cref="_aaStroke"/> 说明。</summary>
        private void DrawLine(Vector2 a, Vector2 b, Color c, float w)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.5f) return;
            var old = GUI.matrix;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
            Color oldColor = GUI.color; GUI.color = c;
            GUI.DrawTexture(new Rect(a.x, a.y - w * 0.5f, len, w), _aaStroke);
            GUI.color = oldColor;
            GUI.matrix = old;
        }

        private void DrawArrowHead(Vector2 tip, Vector2 dirTo, float size, Color c)
        {
            Vector2 left = tip - dirTo * size + new Vector2(-dirTo.y, dirTo.x) * size * 0.55f;
            Vector2 right = tip - dirTo * size + new Vector2(dirTo.y, -dirTo.x) * size * 0.55f;
            DrawLine(tip, left, c, 2f);
            DrawLine(tip, right, c, 2f);
        }

        private void DrawCircle(Vector2 c, float r, Color col)
        {
            const int N = 36;
            Vector2 prev = c + new Vector2(r, 0);
            for (int i = 1; i <= N; i++)
            {
                float ang = i * Mathf.PI * 2f / N;
                Vector2 p = c + new Vector2(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r);
                DrawLine(prev, p, col, 1.5f);
                prev = p;
            }
        }

        private static Vector2 Dir(float degScreenAngle)
        {
            float a = degScreenAngle * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        }

        /// <summary>姿态仪滚转刻度弧上的点: bankDeg=0 在正上方, 正值顺时针 (右坡度)。</summary>
        private static Vector2 BankArcPos(Vector2 center, float radius, float bankDeg)
        {
            float a = bankDeg * Mathf.Deg2Rad;
            return center + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * radius;
        }

        private static string Cardinal(float headingDeg)
        {
            string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            int i = Mathf.RoundToInt(((headingDeg % 360f) + 360f) % 360f / 45f) % 8;
            return names[i];
        }

        private static string CamModeName(ChaseCamera.CameraMode m)
        {
            switch (m)
            {
                case ChaseCamera.CameraMode.Cockpit: return "座舱";
                case ChaseCamera.CameraMode.Free: return "自由观察 (RMB 拖动/滚轮)";
                default: return "追尾";
            }
        }

        private static readonly Color HudGreen = FluentTheme.DarkAccent;   // 保留旧名: 主强调色
        private static readonly Color HudAmber = FluentTheme.Warning;      // 保留旧名: 警告语义
    }
}
