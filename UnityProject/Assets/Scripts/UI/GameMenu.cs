using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 系统菜单 (打包版必需: 编辑器靠停止键, 独立程序需要退出通道):
    ///   Esc → 暂停并弹出 继续/设置/退出到桌面。
    ///   设置: 全屏开关 / 分辨率档位 / 画质档位, 即改即存 (PlayerPrefs, 下次启动生效保持)。
    /// 工坊打开时让位 (工坊用 F8 关闭; 输入隔离与之一致)。
    /// </summary>
    public class GameMenu : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoAttach()
        {
            if (FindObjectOfType<GameMenu>() == null)
                new GameObject("GameMenu").AddComponent<GameMenu>();
        }

        private bool _open, _settings;
        private float _savedTimeScale = 1f;
        private GUIStyle _panel, _title, _btn, _row, _dim;
        private Font _font;
        private Texture2D _panelTex, _btnTex, _dimTex;

        private void Start()
        {
            ApplySaved();   // 上次的显示/画质设置
        }

        private void Update()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null || !kb.escapeKey.wasPressedThisFrame) return;
            if (WorkshopUI.IsOpen) return;   // 工坊期间让位
            if (_open) Close(); else Open();
        }

        private void Open()
        {
            _open = true; _settings = false;
            _savedTimeScale = Time.timeScale;
            Time.timeScale = 0f;
        }

        private void Close()
        {
            _open = false;
            Time.timeScale = _savedTimeScale;
        }

        private void EnsureSkin()
        {
            if (_panel != null) return;
            _font = FlightHUD.LoadOsFont("Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Arial");
            _panelTex = FlightHUD.RoundedRect(64, 10, FluentTheme.DarkPanel, FluentTheme.DarkPanel,
                                              FluentTheme.DarkBorder, 1f);
            _btnTex = FlightHUD.RoundedRect(48, 5, FluentTheme.DarkControl, FluentTheme.DarkControl,
                                            FluentTheme.DarkBorder, 1f);
            _dimTex = Texture2D.whiteTexture;
            _panel = new GUIStyle
            {
                normal = { background = _panelTex },
                border = new RectOffset(12, 12, 12, 12),
                padding = new RectOffset(18, 18, 14, 14),
            };
            _title = new GUIStyle { font = _font, fontSize = 17, fontStyle = FontStyle.Bold,
                                    alignment = TextAnchor.MiddleCenter };
            _title.normal.textColor = FluentTheme.DarkText;
            _btn = new GUIStyle
            {
                font = _font, fontSize = 14, alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(10, 10, 10, 10),
                normal = { background = _btnTex, textColor = FluentTheme.DarkText },
                hover = { background = _btnTex, textColor = FluentTheme.DarkAccent },
                active = { background = _btnTex, textColor = FluentTheme.DarkAccent },
            };
            _row = new GUIStyle { font = _font, fontSize = 13, alignment = TextAnchor.MiddleLeft };
            _row.normal.textColor = FluentTheme.DarkText;
            _dim = new GUIStyle();
            _dim.normal.background = _dimTex;
        }

        private void OnGUI()
        {
            if (!_open) return;
            EnsureSkin();
            // 半透明压暗 (拦截下层点击)
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _dimTex);
            GUI.color = prev;

            float w = 300, h = _settings ? 268 : 210;
            var r = new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h);
            GUI.Box(r, GUIContent.none, _panel);
            GUILayout.BeginArea(new Rect(r.x + 18, r.y + 12, w - 36, h - 26));
            GUILayout.Label(_settings ? "设置" : "菜单", _title, GUILayout.Height(30));
            GUILayout.Space(6);
            if (!_settings)
            {
                if (GUILayout.Button("继续 (Esc)", _btn, GUILayout.Height(38))) Close();
                GUILayout.Space(8);
                if (GUILayout.Button("设置", _btn, GUILayout.Height(38))) _settings = true;
                GUILayout.Space(8);
                if (GUILayout.Button("退出到桌面", _btn, GUILayout.Height(38))) QuitGame();
            }
            else
            {
                DrawSettingRow("全屏", Screen.fullScreen ? "开" : "关", () =>
                {
                    Screen.fullScreen = !Screen.fullScreen;
                    PlayerPrefs.SetInt(PrefFullscreen, Screen.fullScreen ? 1 : 0);
                });
                DrawSettingRow("分辨率", ResolutionLabel(), CycleResolution);
                DrawSettingRow("画质", QualitySettings.names[QualitySettings.GetQualityLevel()],
                               CycleQuality);
                GUILayout.Space(8);
                if (GUILayout.Button("返回", _btn, GUILayout.Height(36))) _settings = false;
            }
            GUILayout.EndArea();
        }

        private void DrawSettingRow(string label, string value, System.Action onClick)
        {
            GUILayout.BeginHorizontal(GUILayout.Height(34));
            GUILayout.Label(label, _row, GUILayout.Width(70));
            if (GUILayout.Button(value + "  ▾", _btn, GUILayout.Height(30))) onClick();
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
        }

        // ================= 设置项 =================

        private const string PrefFullscreen = "GameMenu.Fullscreen";
        private const string PrefRes = "GameMenu.Resolution";
        private const string PrefQuality = "GameMenu.Quality";
        private static readonly int[,] Resolutions =
            { { 1280, 720 }, { 1920, 1080 }, { 2560, 1440 }, { 3840, 2160 } };

        private static string ResolutionLabel()
        {
            return Screen.width + "×" + Screen.height;
        }

        private void CycleResolution()
        {
            int cur = PlayerPrefs.GetInt(PrefRes, -1);
            int next = (cur + 1) % Resolutions.GetLength(0);
            PlayerPrefs.SetInt(PrefRes, next);
            ApplyResolution(next);
        }

        private void ApplyResolution(int idx)
        {
            if (idx < 0) return;
            Screen.SetResolution(Resolutions[idx, 0], Resolutions[idx, 1], Screen.fullScreen);
        }

        private void CycleQuality()
        {
            int next = (QualitySettings.GetQualityLevel() + 1) % QualitySettings.names.Length;
            QualitySettings.SetQualityLevel(next, true);
            PlayerPrefs.SetInt(PrefQuality, next);
        }

        private void ApplySaved()
        {
            if (PlayerPrefs.HasKey(PrefQuality))
            {
                int q = PlayerPrefs.GetInt(PrefQuality);
                if (q >= 0 && q < QualitySettings.names.Length)
                    QualitySettings.SetQualityLevel(q, true);
            }
            if (PlayerPrefs.HasKey(PrefFullscreen))
                Screen.fullScreen = PlayerPrefs.GetInt(PrefFullscreen) != 0;
            if (PlayerPrefs.HasKey(PrefRes))
                ApplyResolution(PlayerPrefs.GetInt(PrefRes, -1));
        }

        private static void QuitGame()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
