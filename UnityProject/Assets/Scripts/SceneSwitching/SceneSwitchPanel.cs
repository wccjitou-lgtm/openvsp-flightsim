using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 场景切换按钮与载入遮罩 (IMGUI, 风格同 FlightHUD)。挂在场景级 "SceneSwitcher" 对象上
    /// (不能挂飞机: 工坊换机会销毁旧飞机连同其上组件)。
    /// 布局: 顶栏两个状态块右侧一排 [森林][海岛][雪谷], 当前场景琥珀高亮, 未就绪灰显;
    /// 飞机在空中时第一次点击变成"确认?", 下方提示 3 秒内再点才切换。
    /// 逻辑 (确认/忽略/切换) 全在 SceneSwitcher.Click, 本类只负责绘制与转发。
    /// 随 H 键隐藏 (跟随 FlightHUD 可见性), 载入遮罩例外 (载入时总要显示)。
    /// </summary>
    [RequireComponent(typeof(SceneSwitcher))]
    public class SceneSwitchPanel : MonoBehaviour
    {
        private SceneSwitcher _sw;
        private FlightHUD _hud;
        private Texture2D _btnTex, _btnActiveTex, _btnOffTex, _veil;
        private GUIStyle _idle, _active, _off, _armed, _hint, _loadTitle, _loadSub;
        private float _builtForK = -1f;
        private Font _font;

        private void Awake()
        {
            _sw = GetComponent<SceneSwitcher>();
            _font = FlightHUD.LoadOsFont("Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Arial");
            var border = FluentTheme.DarkBorder;
            // Fluent 深色 (2026-10-09 改版): 平面填充; 当前场景 = Fluent 蓝底
            _btnTex = FlightHUD.RoundedRect(48, 4, FluentTheme.DarkControl,
                FluentTheme.DarkControl, border, 1f);
            _btnActiveTex = FlightHUD.RoundedRect(48, 4, FluentTheme.DarkAccent,
                FluentTheme.DarkAccent, new Color(0.04f, 0.10f, 0.14f, 0.9f), 1f);
            _btnOffTex = FlightHUD.RoundedRect(48, 4, new Color(0.16f, 0.16f, 0.16f, 0.75f),
                new Color(0.16f, 0.16f, 0.16f, 0.78f), border, 1f);
            _veil = new Texture2D(1, 1);
            _veil.SetPixel(0, 0, new Color(0.02f, 0.03f, 0.04f, 0.82f));
            _veil.Apply();
        }

        private void OnDestroy()
        {
            Destroy(_btnTex); Destroy(_btnActiveTex); Destroy(_btnOffTex); Destroy(_veil);
        }

        private void BuildStyles(float k)
        {
            if (Mathf.Approximately(_builtForK, k)) return;
            _builtForK = k;
            int F(int px) => Mathf.Max(8, Mathf.RoundToInt(px * k));
            var accent = FluentTheme.DarkAccent;
            var amber = FluentTheme.Warning;

            GUIStyle Btn(Texture2D bg, Color text) => new GUIStyle
            {
                normal = { background = bg, textColor = text },
                hover = { background = bg, textColor = text },
                active = { background = bg, textColor = text },
                border = new RectOffset(12, 12, 12, 12),
                alignment = TextAnchor.MiddleCenter,
                font = _font, fontSize = F(15), fontStyle = FontStyle.Bold,
            };
            _idle = Btn(_btnTex, FluentTheme.DarkText);
            _active = Btn(_btnActiveTex, new Color(0.03f, 0.09f, 0.14f));
            _off = Btn(_btnOffTex, new Color(0.55f, 0.55f, 0.55f));
            _armed = Btn(_btnTex, amber);
            _hint = new GUIStyle { font = _font, fontSize = F(14), alignment = TextAnchor.UpperLeft,
                                   normal = { textColor = amber } };
            _loadTitle = new GUIStyle { font = _font, fontSize = F(34), fontStyle = FontStyle.Bold,
                                        alignment = TextAnchor.MiddleCenter, normal = { textColor = FluentTheme.DarkText } };
            _loadSub = new GUIStyle { font = _font, fontSize = F(16), alignment = TextAnchor.MiddleCenter,
                                      normal = { textColor = FluentTheme.DarkTextDim } };
        }

        private void OnGUI()
        {
            if (_sw == null) return;
            float k = HudLayout.Scale();
            BuildStyles(k);
            GUI.depth = -100;   // 比 HUD 更靠前 (遮罩要盖住 HUD)

            if (_hud == null) _hud = FindObjectOfType<FlightHUD>();
            if (_hud == null || _hud.IsShown) DrawButtons(k);
            if (_sw.IsLoading) DrawLoading(k);
        }

        // 顶栏最左: 3 个分段按钮 (宽 SceneBarW = 3×64 + 2×4), 与相机/键位 chip 同一行同高
        private void DrawButtons(float k)
        {
            var all = SceneRegistry.All;
            float gap = 4f * k;
            float w = (HudLayout.SceneBarW - gap / k * (all.Count - 1)) / all.Count * k;
            float h = HudLayout.TopRowH * k;
            float x0 = HudLayout.Margin * k, y = HudLayout.TopRowY * k;
            float now = Time.unscaledTime;

            bool enabledAll = !_sw.IsLoading;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                var r = new Rect(x0 + i * (w + gap), y, w, h);
                bool isCurrent = p == _sw.Current;
                bool pending = _sw.IsPending(p.Id, now);

                GUIStyle st = !p.Ready ? _off : isCurrent ? _active : pending ? _armed : _idle;
                string label = pending ? "确认?" : p.DisplayName;

                GUI.enabled = enabledAll && p.Ready;
                if (GUI.Button(r, label, st)) _sw.Click(p.Id, now);
                GUI.enabled = true;
            }

            if (!string.IsNullOrEmpty(_sw.PendingId) && _sw.IsPending(_sw.PendingId, now))
            {
                // 紧贴按钮下方一行 (HUD 的帮助面板从 y=80 起, 不会被它盖住)
                GUI.Label(new Rect(x0, y + h + 4f * k, HudLayout.SceneBarW * k, 22f * k),
                    "飞行中 · 再点一次确认", _hint);
            }
        }

        private void DrawLoading(float k)
        {
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _veil);
            string name = _sw.Target != null ? _sw.Target.DisplayName : "";
            float cy = Screen.height * 0.5f;
            GUI.Label(new Rect(0, cy - 40f * k, Screen.width, 56f * k), "正在载入 · " + name, _loadTitle);
            GUI.Label(new Rect(0, cy + 22f * k, Screen.width, 24f * k), "地形与植被生成中, 请稍候", _loadSub);
        }
    }
}
