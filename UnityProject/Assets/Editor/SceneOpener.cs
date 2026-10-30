#if UNITY_EDITOR
using FlightDynamics;
using FlightSim.EditorTools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FlightSim.EditorTools
{
    /// <summary>
    /// GUI 模式自动体验入口:
    ///   Unity.exe -projectPath ... -executeMethod FlightSim.EditorTools.SceneOpener.OpenAirportAndPlay
    /// 打开机场场景 → 直接进入 Play (Game 视图自动前置)。
    /// </summary>
    public static class SceneOpener
    {
        /// <summary>
        /// 失焦白屏修复: 本机 (RTX A4000 + Todesk 虚拟显示器) 上编辑器失焦时视口表面被丢弃,
        /// Game 视图整块变白, 切回焦点又恢复 —— 表现为"闪屏"。
        /// InteractionMode=1 (No Throttling) 让编辑器失焦时仍持续渲染。
        /// </summary>
        public static void SetInteractionModeNoThrottle()
        {
            // 原生侧以 int 读写 (注册表 DWORD InteractionMode_h*), SetFloat 不会生效
            EditorPrefs.SetInt("InteractionMode", 1);
            Debug.Log($"[SceneOpener] InteractionMode -> No Throttling (当前读值: {EditorPrefs.GetInt("InteractionMode")})");
        }

        /// <summary>目检用: 记住"海岛"为上次所选场景后直接进入 Play (SceneSwitcher.Start 会恢复它)。</summary>
        public static void OpenIslandAndPlay()
        {
            PlayerPrefs.SetString(FlightSim.SceneSwitcher.PrefKey, FlightSim.SceneRegistry.Island.Id);
            OpenAirportAndPlay();
        }

        /// <summary>目检用: 记住"雪谷"为上次所选场景后直接进入 Play。</summary>
        public static void OpenAlpineAndPlay()
        {
            PlayerPrefs.SetString(FlightSim.SceneSwitcher.PrefKey, FlightSim.SceneRegistry.Alpine.Id);
            OpenAirportAndPlay();
        }

        public static void OpenAirportAndPlay()
        {
            SetInteractionModeNoThrottle();
            // 可选 -gameview WxH: 固定 Game 视图分辨率 (帧率对比要在同一分辨率下测)
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != "-gameview") continue;
                var wh = args[i + 1].Split('x');
                UiShot.SetGameViewSize(int.Parse(wh[0]), int.Parse(wh[1]));
            }
            EditorSceneManager.OpenScene("Assets/Scenes/Flight_Phase2.unity",
                OpenSceneMode.Single);
            // 聚焦飞机 (SceneView 跟随)
            var aircraft = GameObject.Find("TestUAV");
            if (aircraft != null)
            {
                Selection.activeGameObject = aircraft;
                SceneView.FrameLastActiveSceneView();
            }
            // 直接进入运行模式 (Game 视图自动获得焦点)
            EditorApplication.isPlaying = true;
            Debug.Log("[SceneOpener] 机场场景已打开并进入 Play —— ↑ 加油门起飞");
        }
    }
}
#endif
