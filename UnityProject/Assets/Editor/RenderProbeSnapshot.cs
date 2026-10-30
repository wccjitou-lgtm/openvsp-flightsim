#if UNITY_EDITOR
using System.IO;
using FlightSim;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace FlightSim.EditorTools
{
    /// <summary>
    /// 导入机型视觉探针 (渲染升级 R1 验收用): 新建合成场景, 用机型工坊同款加载链路
    /// (RuntimeObjLoader.BuildChild → RenderMaterials 默认材质) 装载 VspImports 里的
    /// 真实机型 OBJ, 配 Directional Light (物理 lux) + 相机, 渲染 PNG 证据。
    /// 证明 "工坊换上机型后飞机可见且颜色正常 (非粉红)" —— 编辑模式可跑, 无需 GUI。
    /// Unity.exe -batchmode -force-d3d12 -projectPath ... \
    ///   -executeMethod FlightSim.EditorTools.RenderProbeSnapshot.RenderImportedModel -quit
    /// </summary>
    public static class RenderProbeSnapshot
    {
        // 模型相对工程根 (openvsp-flightsim) 的路径; CWD = UnityProject
        private static readonly string[] Models =
        {
            "../VspImports/TestUAVImport/model.obj",
        };

        public static void RenderImportedModel()
        {
            Directory.CreateDirectory("Logs/Snapshots");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 灯: 合成场景必须自带 (否则 Lit 材质全黑)。从相机侧上方打光 —— 无天空时
            // 环境光为零, 逆光位会让被摄面全黑 (第一版探针的教训)
            var sunGo = new GameObject("Directional Light");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(50f, 25f, 0f);
            var hd = sunGo.AddComponent<HDAdditionalLightData>();
            hd.EnableShadows(true);
            hd.SetIntensity(1.1e5f, LightUnit.Lux);

            // 相机: 斜上方看原点
            var camGo = new GameObject("Probe Camera");
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<HDAdditionalCameraData>();
            cam.transform.position = new Vector3(4f, 4f, -16f);
            cam.transform.LookAt(new Vector3(0f, 0f, 0.5f));

            // 地面参照物 (零高度参照, 顺带验证图元材质工厂)
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.localScale = new Vector3(30f, 1f, 30f);
            ground.GetComponent<Renderer>().sharedMaterial =
                RenderMaterials.Create(new Color(0.36f, 0.50f, 0.29f));

            float xOffset = 0f;
            foreach (var rel in Models)
            {
                string abs = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), rel));
                var root = new GameObject("Imported_" + Path.GetFileName(Path.GetDirectoryName(abs)));
                if (!File.Exists(abs))
                {
                    Debug.LogWarning("[RenderProbe] 模型不存在, 跳过: " + abs);
                    continue;
                }
                var child = RuntimeObjLoader.BuildChild(root.transform, abs);
                if (child == null)
                {
                    Debug.LogError("[RenderProbe] 加载失败: " + abs);
                    continue;
                }
                // R4: 应用与 AircraftVisual.ApplyLivery 同源的涂装 (公共静态, 单一规则)
                // —— 机身白金属漆 + 翼面/舵面军旗红, 截图里分区涂装直接可见
                var body = AircraftVisual.CreateBodyPaint();
                var accent = AircraftVisual.CreateAccentPaint();
                foreach (var r in child.GetComponentsInChildren<Renderer>())
                    r.sharedMaterial = AircraftVisual.IsAccentRenderer(r.name) ? accent : body;
                root.transform.position = new Vector3(xOffset, 2f, 0f);   // 悬空展示, 左右并排防互相穿插
                xOffset += 8f;
            }
            // 相机看向两机中点
            cam.transform.LookAt(new Vector3((xOffset - 8f) * 0.5f, 0.5f, 0f));

            const int W = 1280, H = 720;
            var rt = new RenderTexture(W, H, 24);
            cam.targetTexture = rt;
            cam.Render();   // 预热帧: 同帧新建的灯/材质在 HDRP LightLoop 里可能未同步
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            File.WriteAllBytes("Logs/Snapshots/imported_model_hdrp.png", tex.EncodeToPNG());
            cam.targetTexture = null;
            RenderTexture.active = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
            Debug.Log("[RenderProbe] 已渲染: Logs/Snapshots/imported_model_hdrp.png (合成场景不保存)");
        }
    }
}
#endif
