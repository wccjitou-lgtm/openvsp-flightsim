using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FlightSim.Tests
{
    /// <summary>
    /// U12 诊断: 工坊预览整机体黑 (用户截图) — 无头复现预览渲染, 读 RT 像素
    /// 判定是材质黑还是灯光/曝光问题; 枚举预览物体定位"机头前小实体"。
    /// </summary>
    public class WorkshopPreviewRenderDiagPlayModeTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void Diag_PreviewRender_PixelsAndObjects()
        {
            var prev = new WorkshopPreview();
            try
            {
                prev.Create(320, 240);
                // 使用随仓库提供的第一方 TestUAV，避免依赖开发者本机 work/ 暂存网格。
                string meshPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    Application.dataPath, "..", "..", "Data", "TestUAV.stl"));
                var m = WorkshopStlLoader.LoadMesh(meshPath, Vector3.zero);
                prev.SetSlotMesh("fuselage", m, new Color32(0xC8, 0xCD, 0xD4, 255), "fuselage");
                Assert.IsNotNull(m, "应装载随仓库提供的 TestUAV: " + meshPath);
                prev.SetCgMarker(new Vector3(0f, 0f, -0.65f), true);   // 示例机 CG 附近

                // 枚举物体 (定位机头前小实体是什么)
                var root = GameObject.Find("WorkshopPreviewRoot");
                Assert.IsNotNull(root, "预览根物体应存在");
                foreach (Transform ch in root.transform)
                    Debug.Log($"[U12] {ch.name} pos={ch.position} scale={ch.localScale}");

                // 渲两帧 (HDRP LightLoop 预热, R1 教训) 后读像素
                RenderTexture active = RenderTexture.active;
                RenderTexture rt = (RenderTexture)typeof(WorkshopPreview)
                    .GetProperty("Rt").GetValue(prev);
                for (int i = 0; i < 3; i++) prev.Render();

                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = active;

                // 中央区域像素分类: 背景 vs 飞机 (应出现槽位颜色的亮色像素)
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
                Debug.Log($"[U12] 亮色像素 {bright}/{total}, 采样亮色={anyBright} " +
                          "(Unlit 平涂后应为槽位颜色, 如机翼蓝 RGBA(0.29,0.64,1,1))");
                // U12 断言: 飞机区域必须有亮色像素 (Unlit 平涂; 飞机在画面中占比
                // ~2-5% (dist=3.2×半径), 修复前全黑剪影时≈0, 取 1% 分界)
                Assert.Greater(bright, total / 100,
                    $"预览中亮色像素过少 ({bright}/{total}) — 飞机仍渲染为黑色剪影");
                Object.Destroy(tex);
            }
            finally { prev.Dispose(); }
        }
    }
}
