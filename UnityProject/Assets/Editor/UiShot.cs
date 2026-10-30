#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FlightSim.EditorTools
{
    /// <summary>
    /// HUD 布局截图 (GUI 编辑器, 非 batchmode): 命令行 -uishot WxH 名字 [-uishot-scene 森林|海岛 id]。
    /// 把 Game 视图固定为 WxH → 打开机场场景并进入 Play → 约 12 s 后 ScreenCapture 存
    /// Logs/Snapshots/ui/名字_WxH.png (含 IMGUI) → 退出编辑器。每个分辨率一次进程。
    /// 状态机放 EditorApplication.update + SessionState, 经得起进 Play 的域重载。
    /// </summary>
    [InitializeOnLoad]
    public static class UiShot
    {
        private const string KeyPhase = "UiShot.Phase", KeyT0 = "UiShot.T0";
        private static string _w, _h, _name, _scene, _state;

        static UiShot()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 2; i++)
            {
                if (a[i] != "-uishot") continue;
                var wh = a[i + 1].Split('x');
                _w = wh[0]; _h = wh[1]; _name = a[i + 2];
                for (int j = 0; j < a.Length - 1; j++)
                {
                    if (a[j] == "-uishot-scene") _scene = a[j + 1];
                    if (a[j] == "-uishot-state") _state = a[j + 1];
                }
                EditorApplication.update -= Tick;
                EditorApplication.update += Tick;
                return;
            }
        }

        private static void Tick()
        {
            string phase = SessionState.GetString(KeyPhase, "");
            if (phase == "" && !EditorApplication.isPlaying && !EditorApplication.isCompiling && !EditorApplication.isUpdating)
            {
                SessionState.SetString(KeyPhase, "playing");
                SetGameViewSize(int.Parse(_w), int.Parse(_h));
                if (!string.IsNullOrEmpty(_scene)) PlayerPrefs.SetString(SceneSwitcher.PrefKey, _scene);
                else PlayerPrefs.DeleteKey(SceneSwitcher.PrefKey);
                EditorSceneManager.OpenScene("Assets/Scenes/Flight_Phase2.unity", OpenSceneMode.Single);
                EditorApplication.isPlaying = true;
                return;
            }
            if (phase == "playing" && EditorApplication.isPlaying)
            {
                if (SessionState.GetFloat(KeyT0, 0f) == 0f) SessionState.SetFloat(KeyT0, (float)EditorApplication.timeSinceStartup);
                double el = EditorApplication.timeSinceStartup - SessionState.GetFloat(KeyT0, 0f);
                if (el >= 8.0 && !SessionState.GetBool("UiShot.StateApplied", false))
                {
                    SessionState.SetBool("UiShot.StateApplied", true);
                    ApplyState();
                }
                if (el >= 12.5 && _state == "air" && !SessionState.GetBool("UiShot.Armed", false))
                {
                    SessionState.SetBool("UiShot.Armed", true);
                    var sw = UnityEngine.Object.FindObjectOfType<SceneSwitcher>();
                    if (sw != null) Debug.Log("[UiShot] 空中点击海岛 -> " + sw.Click("island", Time.unscaledTime));
                }
                if (el < 14.0) return;
                string dir = Path.GetFullPath("Logs/Snapshots/ui");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, _name + "_" + _w + "x" + _h + ".png");
                if (File.Exists(file)) File.Delete(file);
                ScreenCapture.CaptureScreenshot(file);
                SessionState.SetString(KeyPhase, "captured");
                return;
            }
            if (phase == "captured")
            {
                string file = Path.GetFullPath("Logs/Snapshots/ui/" + _name + "_" + _w + "x" + _h + ".png");
                if (!File.Exists(file)) return;
                PlayerPrefs.DeleteKey(SceneSwitcher.PrefKey);
                PlayerPrefs.Save();
                SessionState.EraseString(KeyPhase);
                SessionState.EraseBool("UiShot.StateApplied");
                SessionState.EraseBool("UiShot.Armed");
                SessionState.EraseFloat(KeyT0);
                EditorApplication.Exit(0);
            }
        }

        /// <summary>help = 展开键位说明; air = 飞机升空 + 自驾返航 (顶部模式提示) + 稍后空中点击海岛 (出现"确认");
        /// swapD = 换机到 VspImports/E2E_D (工坊飞翼) — 换机视觉回归用。</summary>
        private static void ApplyState()
        {
            if (_state == "swapD")
            {
                string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "VspImports", "E2E_D"));
                string cfg = Path.Combine(dir, "AircraftConfig.json");
                string obj = Path.Combine(dir, "model.obj");
                if (File.Exists(cfg))
                    AircraftSwapper.Swap(cfg, File.Exists(obj) ? obj : "");
                else
                    Debug.LogWarning("[UiShot] swapD: 缺 " + cfg);
                return;
            }
            if (_state == "help" || _state == "air") FlightHUD.HelpVisible = _state == "help";
            if (_state != "air") return;
            var ac = UnityEngine.Object.FindObjectOfType<AircraftController>();
            if (ac == null) return;
            ac.State.Position = new FlightDynamics.Math.Vec3(300, 40, -123);
            ac.State.VelocityBody = new FlightDynamics.Math.Vec3(28.4, 0, 0);
            ac.GetComponent<ModePilot>().Engage(FlightMode.Rtl);
            Debug.Log("[UiShot] 状态 air: 升空 + RTL");
        }

        internal static void SetGameViewSize(int w, int h)
        {
            var asm = typeof(Editor).Assembly;
            var sizesType = asm.GetType("UnityEditor.GameViewSizes");
            var single = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
            var instance = single.GetProperty("instance").GetValue(null, null);
            var group = sizesType.GetMethod("GetGroup").Invoke(instance, new object[] { 0 });   // Standalone
            var sizeType = asm.GetType("UnityEditor.GameViewSize");
            var enumType = asm.GetType("UnityEditor.GameViewSizeType");
            var ctor = sizeType.GetConstructor(new[] { enumType, typeof(int), typeof(int), typeof(string) });
            var size = ctor.Invoke(new object[] { Enum.ToObject(enumType, 1), w, h, "uishot" + w + "x" + h });
            group.GetType().GetMethod("AddCustomSize").Invoke(group, new[] { size });
            int idx = (int)group.GetType().GetMethod("GetTotalCount").Invoke(group, null) - 1;
            var gv = EditorWindow.GetWindow(asm.GetType("UnityEditor.GameView"));
            var cb = gv.GetType().GetMethod("SizeSelectionCallback",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            cb.Invoke(gv, new[] { (object)idx, size });
            Debug.Log("[UiShot] Game 视图 -> " + w + "x" + h);
        }
    }
}
#endif
