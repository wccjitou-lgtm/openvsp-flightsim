#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FlightSim.EditorTools
{
    /// <summary>
    /// 换机视觉快照 (batchmode 可跑, 无需 GUI/焦点 — 与 UiShot 不同, 用 Camera.Render):
    ///   Unity.exe -batchmode -force-d3d12 -projectPath <proj> -quit
    ///     -swapshot <sceneId|-> <swapDirName|-> <outName>
    ///   例: -swapshot alpine E2E_D swapD_alpine   (雪谷场景换飞翼后的跑道视角 PNG)
    /// 流程: 打开机场场景 → 进 Play (SessionState 扛域重载) → 场景恢复/载入完成 →
    ///   可选换机 → 相机摆到飞机侧后上方 → RenderTexture 落盘 → 退出。
    /// </summary>
    [InitializeOnLoad]
    public static class SwapShot
    {
        private const string KPhase = "SwapShot.Phase", KT0 = "SwapShot.T0";
        private static string _scene, _swapDir, _name;

        static SwapShot()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
            {
                if (a[i] != "-swapshot") continue;
                _scene = i + 1 < a.Length ? a[i + 1] : "-";
                _swapDir = i + 2 < a.Length ? a[i + 2] : "-";
                _name = i + 3 < a.Length ? a[i + 3] : "swapshot";
                EditorApplication.update -= Tick;
                EditorApplication.update += Tick;
                return;
            }
        }

        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            string phase = SessionState.GetString(KPhase, "");

            if (phase == "" && !EditorApplication.isPlaying)
            {
                SessionState.SetString(KPhase, "playing");
                if (_scene != "-") PlayerPrefs.SetString(SceneSwitcher.PrefKey, _scene);
                else PlayerPrefs.DeleteKey(SceneSwitcher.PrefKey);
                EditorSceneManager.OpenScene("Assets/Scenes/Flight_Phase2.unity", OpenSceneMode.Single);
                EditorApplication.isPlaying = true;
                return;
            }
            if (phase == "playing" && EditorApplication.isPlaying)
            {
                if (SessionState.GetFloat(KT0, 0f) == 0f)
                    SessionState.SetFloat(KT0, (float)EditorApplication.timeSinceStartup);
                double el = EditorApplication.timeSinceStartup - SessionState.GetFloat(KT0, 0f);
                var sw = UnityEngine.Object.FindObjectOfType<SceneSwitcher>();
                if (sw != null && sw.IsLoading) return;   // 等场景载入完
                if (el < 8.0) return;

                if (_swapDir != "-" && !SessionState.GetBool("SwapShot.Swapped", false))
                {
                    SessionState.SetBool("SwapShot.Swapped", true);
                    string dir = Path.GetFullPath(Path.Combine(
                        Application.dataPath, "..", "..", "VspImports", _swapDir));
                    string cfg = Path.Combine(dir, "AircraftConfig.json");
                    string obj = Path.Combine(dir, "model.obj");
                    if (File.Exists(cfg))
                    {
                        AircraftSwapper.Swap(cfg, File.Exists(obj) ? obj : "");
                        Debug.Log("[SwapShot] 已换机: " + _swapDir);
                    }
                    else Debug.LogError("[SwapShot] 缺配置: " + cfg);
                    return;
                }
                if (el < 11.0) return;

                var ac = UnityEngine.Object.FindObjectOfType<AircraftController>();
                var camGo = GameObject.Find("Main Camera");
                var cam = camGo != null ? camGo.GetComponent<Camera>() : null;
                if (ac == null || cam == null) { Debug.LogError("[SwapShot] 缺飞机/相机"); Exit(); return; }

                // 飞机侧后上方观察位 (追尾相机的近似)
                Vector3 p = ac.transform.position;
                cam.transform.position = p + new Vector3(5f, 2.5f, -7f);
                cam.transform.LookAt(p + Vector3.up * 0.5f);
                var chase = camGo.GetComponent<ChaseCamera>();
                if (chase != null) chase.enabled = false;   // 定住机位
                cam.enabled = false;

                var rt = new RenderTexture(1280, 720, 24);
                cam.targetTexture = rt;
                cam.Render(); cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                cam.targetTexture = null;
                rt.Release();
                string outDir = Path.GetFullPath("Logs/Snapshots");
                Directory.CreateDirectory(outDir);
                File.WriteAllBytes(Path.Combine(outDir, _name + ".png"), tex.EncodeToPNG());
                Debug.Log("[SwapShot] 已存 " + _name + ".png  飞机位置=" + p +
                          " alt=" + ac.Altitude + " agl=" + ac.AltitudeAgl);
                Exit();
            }
        }

        private static void Exit()
        {
            SessionState.EraseString(KPhase);
            SessionState.EraseFloat(KT0);
            SessionState.EraseBool("SwapShot.Swapped");
            EditorApplication.Exit(0);
        }
    }
}
#endif
