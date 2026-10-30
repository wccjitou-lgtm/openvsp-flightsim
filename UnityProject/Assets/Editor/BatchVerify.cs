#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FlightSim.EditorTools
{
    /// <summary>
    /// batchmode 自动化验证 (无人工操作):
    ///   Unity.exe -batchmode -projectPath ... -executeMethod FlightSim.EditorTools.BatchVerify.Run
    ///
    /// 流程: 建场景(自动驾驶输入替代键盘) → 进 PlayMode → 实时飞行 25s →
    /// 采集遥测 → 与核心库基准对比 → 结果写 Logs/batch_verify.json → 退出。
    /// 退出码: 0 全部通过, 1 有失败项。
    /// </summary>
    public static class BatchVerify
    {
        private static double _playStart = -1;
        private static string _resultPath;

        // 核心库 headless 基准 (ConsoleFlightSim --headless 25: 高度 28.7m/空速 17.4)
        // 这里放宽为一致性区间: Unity FixedUpdate 定步长实现应给出同量级结果。
        private const float MinAltitude = 15f;      // 25s 至少爬到此高度
        private const float MaxBank = 8f;           // 增稳下滚转峰
        private const float MaxSpeed = 35f;

        public static void Run()
        {
            _resultPath = Path.Combine(Application.dataPath, "..", "Logs", "batch_verify.json");
            Directory.CreateDirectory(Path.GetDirectoryName(_resultPath));

            // 1. 建场景 (自动驾驶版)
            FlightSimSceneSetup.CreateSceneAutoPilot();
            var scene = EditorSceneManagerEx.OpenFlightScene();

            // 2. 进 PlayMode
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.isPlaying = true;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                _playStart = EditorApplication.timeSinceStartup;
                EditorApplication.update += WatchFlight;
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                EditorApplication.update -= WatchFlight;
                EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            }
        }

        private static void WatchFlight()
        {
            if (!EditorApplication.isPlaying) return;
            double elapsed = EditorApplication.timeSinceStartup - _playStart;
            if (elapsed < 25.0) return;

            var controller = Object.FindObjectOfType<AircraftController>();
            var autopilot = Object.FindObjectOfType<AutoPilotInput>();
            bool pass = true;
            var lines = new System.Text.StringBuilder();

            float alt = controller ? controller.Altitude : -1f;
            float spd = controller ? controller.Airspeed : -1f;
            float bank = controller ? controller.BankDeg : 999f;
            float pitch = controller ? controller.PitchDeg : 999f;

            lines.AppendLine("{");
            lines.AppendLine(string.Format("  \"elapsed_s\": {0:F1},", elapsed));
            lines.AppendLine(string.Format("  \"altitude_m\": {0:F1},", alt));
            lines.AppendLine(string.Format("  \"airspeed_ms\": {0:F1},", spd));
            lines.AppendLine(string.Format("  \"pitch_deg\": {0:F1},", pitch));
            lines.AppendLine(string.Format("  \"bank_deg\": {0:F2},", bank));
            lines.AppendLine(string.Format("  \"on_ground\": {0},", controller && controller.OnGround));
            lines.AppendLine(string.Format("  \"has_autopilot\": {0},", autopilot != null));
            lines.AppendLine("  \"checks\": {");

            void Check(string name, bool ok)
            {
                lines.AppendLine(string.Format("    \"{0}\": {1},", name, ok ? "true" : "false"));
                if (!ok) pass = false;
            }

            Check("airborne_15m", alt >= MinAltitude);
            Check("bank_bounded", Mathf.Abs(bank) <= MaxBank);
            Check("speed_sane", spd <= MaxSpeed && spd > 5f);
            Check("no_nan", !float.IsNaN(alt) && !float.IsNaN(bank) && !float.IsNaN(pitch));
            Check("autopilot_active", autopilot != null);
            lines.AppendLine("  }");
            lines.AppendLine(string.Format("  \"pass\": {0}", pass ? "true" : "false"));
            lines.AppendLine("}");

            File.WriteAllText(_resultPath, lines.ToString());
            Debug.Log("[BatchVerify] 结果已写入 " + _resultPath + (pass ? " — 全部通过" : " — 存在失败项"));

            EditorApplication.isPlaying = false;
            EditorApplication.delayCall += () => EditorApplication.Exit(pass ? 0 : 1);
        }
    }

    /// <summary>EditorSceneManager 的小封装 (避免版本 using 歧义)。</summary>
    internal static class EditorSceneManagerEx
    {
        public static UnityEngine.SceneManagement.Scene OpenFlightScene()
        {
            return UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                "Assets/Scenes/Flight_Phase2.unity",
                UnityEditor.SceneManagement.OpenSceneMode.Single);
        }
    }
}
#endif
