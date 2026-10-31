using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FlightSim.Tests
{
    /// <summary>
    /// U12 诊断: 工坊预览整机体黑 — 读 RT 像素验证 Unlit 预览确实输出内容。
    /// </summary>
    public class WorkshopPreviewRenderDiagPlayModeTests
    {
        [UnityTest]
        [Explicit("Requires an interactive HDRP rendering environment; batch mode may return black frames")]
        public IEnumerator Diag_PreviewRender_PixelsAndObjects()
        {
            var prev = new WorkshopPreview();
            try
            {
                prev.Create(320, 240);
                // 使用自包含的简单几何，避免依赖本机暂存网格或机型特定的背面/尺度差异。
                var triangles = new Vector3[]
                {
                    new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f),
                    new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(1f, 0f, 0f),
                    new Vector3(0f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
                    new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(0f, 1f, 0f),
                };
                string meshPath = WorkshopPlayModeTests.WriteTestStl(triangles, 4);
                var m = WorkshopStlLoader.LoadMesh(meshPath, Vector3.zero);
                prev.SetSlotMesh("fuselage", m, new Color32(0xC8, 0xCD, 0xD4, 255), "fuselage");
                Assert.IsNotNull(m, "应装载自包含的测试网格: " + meshPath);
                prev.SetCgMarker(new Vector3(0f, 0f, -0.65f), true);

                var root = GameObject.Find("WorkshopPreviewRoot");
                Assert.IsNotNull(root, "预览根物体应存在");
                foreach (Transform ch in root.transform)
                    Debug.Log($"[U12] {ch.name} pos={ch.position} scale={ch.localScale}");

                // 等 HDRP 完成网格和相机注册后再强制渲染并读回像素。
                yield return null;
                RenderTexture active = RenderTexture.active;
                RenderTexture rt = (RenderTexture)typeof(WorkshopPreview)
                    .GetProperty("Rt").GetValue(prev);
                for (int i = 0; i < 3; i++)
                {
                    prev.Render();
                    yield return null;
                }

                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = active;

                Color bg = new Color(0.043f, 0.067f, 0.106f, 1f);
                int bright = 0, total = 0;
                Color anyBright = Color.black;
                for (int y = rt.height / 4; y < rt.height * 3 / 4; y += 3)
                    for (int x = rt.width / 4; x < rt.width * 3 / 4; x += 3)
                    {
                        Color c = tex.GetPixel(x, y);
                        total++;
                        float lum = (c.r + c.g + c.b) / 3f;
                        if (lum > bg.r + bg.g + bg.b + 0.25f) { bright++; anyBright = c; }
                    }
                Debug.Log($"[U12] 亮色像素 {bright}/{total}, 采样亮色={anyBright}");
                Assert.Greater(bright, total / 100,
                    $"预览中亮色像素过少 ({bright}/{total}) — 飞机仍渲染为黑色剪影");
                Object.Destroy(tex);
            }
            finally { prev.Dispose(); }
        }
    }
}
