#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FlightSim.EditorTools
{
    /// <summary>
    /// 海岸视角快照 (batchmode, 与 SwapShot 同模式):
    ///   Unity.exe -batchmode -force-d3d12 -projectPath <proj> -quit
    ///     -coastshot <sceneId> <outName>
    /// 用途: 排查"飞近海岸看不到海" —— 把主相机架到海岸线西侧低空朝东看
    /// (沙滩带 + 海水交界面), 不走追尾相机。
    /// </summary>
    [InitializeOnLoad]
    public static class CoastShot
    {
        private const string KPhase = "CoastShot.Phase", KT0 = "CoastShot.T0";
        private static string _scene, _name;
        // 可选相机位姿: -coastshot <scene> <name> [x y z pitch yaw]
        private static Vector3 _pos = new Vector3(250f, 12f, 400f);
        private static float _pitch = 8f, _yaw = 90f;

        static CoastShot()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
            {
                if (a[i] != "-coastshot") continue;
                _scene = a[i + 1];
                _name = i + 2 < a.Length ? a[i + 2] : "coastshot";
                if (i + 7 < a.Length &&
                    float.TryParse(a[i + 3], out float px) && float.TryParse(a[i + 4], out float py) &&
                    float.TryParse(a[i + 5], out float pz) && float.TryParse(a[i + 6], out float pt) &&
                    float.TryParse(a[i + 7], out float yw))
                { _pos = new Vector3(px, py, pz); _pitch = pt; _yaw = yw; }
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
                PlayerPrefs.SetString(SceneSwitcher.PrefKey, _scene);
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
                if (el < 9.0) return;

                var camGo = GameObject.Find("Main Camera");
                var cam = camGo != null ? camGo.GetComponent<Camera>() : null;
                if (cam == null) { Debug.LogError("[CoastShot] 缺相机"); Exit(); return; }

                var chase = camGo.GetComponent<ChaseCamera>();
                if (chase != null) chase.enabled = false;   // 定住机位
                cam.enabled = false;

                // 海岸线观察位 (默认): 跑道平台东缘内侧 (x=东 250m, y=12m, z=北 400m),
                // 朝东略俯 —— 画面应包含沙滩带 (200~425m) 与海水 (425m 以外)
                cam.transform.position = _pos;
                cam.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);

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
                Debug.Log("[CoastShot] 已存 " + _name + ".png");
                Exit();
            }
        }

        private static void Exit()
        {
            SessionState.EraseString(KPhase);
            SessionState.EraseFloat(KT0);
            EditorApplication.Exit(0);
        }
    }
}
#endif
