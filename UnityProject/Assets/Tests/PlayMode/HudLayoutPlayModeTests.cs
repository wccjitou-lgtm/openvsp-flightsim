using NUnit.Framework;
using UnityEngine;

namespace FlightSim.Tests
{
    /// <summary>
    /// HUD 布局守卫: 常见分辨率下底部三组 (左速度 / 中央仪表 / 右高度) 不互相重叠、不超出屏幕,
    /// 顶栏左块不碰航向罗盘数字块与机型工坊按钮。只验证栅格常数与缩放规则, 画面观感靠截图目检。
    /// </summary>
    public class HudLayoutPlayModeTests
    {
        private static readonly int[][] Resolutions =
        {
            new[] { 3840, 2160 }, new[] { 2560, 1440 }, new[] { 1920, 1080 }, new[] { 1600, 900 },
            new[] { 1280, 720 }, new[] { 986, 555 }, new[] { 1024, 768 }, new[] { 1440, 900 },
            new[] { 1280, 1024 }, new[] { 800, 600 },
        };

        [Test]
        public void Scale_IsClampedAndMonotonicInSize()
        {
            foreach (var r in Resolutions)
            {
                float k = HudLayout.Scale(r[0], r[1]);
                Assert.That(k, Is.InRange(0.55f, 1.6f), r[0] + "x" + r[1]);
            }
            Assert.AreEqual(1f, HudLayout.Scale(1920, 1080), 1e-4f, "1080p 下 1 虚拟像素 = 1 屏幕像素");
            Assert.Greater(HudLayout.Scale(1920, 1080), HudLayout.Scale(1280, 720));
            Assert.Less(HudLayout.Scale(1280, 1024), HudLayout.Scale(1920, 1080), "窄屏被宽度约束, 不能按高度放大");
        }

        [Test]
        public void BottomRow_NeverOverlapsAndStaysOnScreen_AtCommonResolutions()
        {
            HudLayout.BottomClusterExtent(out float cl, out float cr);
            foreach (var r in Resolutions)
            {
                float k = HudLayout.Scale(r[0], r[1]);
                if (k <= 0.55f + 1e-4f && r[0] / HudLayout.MinVirtualWidth < 0.55f) continue;   // 低于支持下限 (宽 < 671 px)
                float vw = r[0] / k;
                float mid = vw * 0.5f;
                float leftPanelRight = HudLayout.Margin + HudLayout.SidePanelW;
                float rightPanelLeft = vw - HudLayout.Margin - HudLayout.SidePanelW;
                string tag = r[0] + "x" + r[1] + " (虚拟宽 " + vw.ToString("F0") + ")";
                Assert.GreaterOrEqual(mid - cl, leftPanelRight + HudLayout.Gap * 0.5f, "中央簇左缘压到速度面板 " + tag);
                Assert.LessOrEqual(mid + cr, rightPanelLeft - HudLayout.Gap * 0.5f, "中央簇右缘压到高度面板 " + tag);
                Assert.Greater(mid - cl, 0f, tag);
            }
        }

        [Test]
        public void TopRow_LeftBlockClearsCompassChipAndWorkshopButton()
        {
            // 顶栏左块 = 场景切换 + 相机 chip(190) + 键位 chip(184) + 间隙; 航向数字块在屏幕中线 ±30, 工坊按钮从中线 +190 起
            float leftBlockRight = HudLayout.Margin + HudLayout.SceneBarW + 8f + 190f + 8f + 184f;
            foreach (var r in Resolutions)
            {
                float k = HudLayout.Scale(r[0], r[1]);
                if (r[0] / HudLayout.MinVirtualWidth < 0.55f) continue;
                float mid = r[0] / k * 0.5f;
                Assert.Less(leftBlockRight, mid - 30f, "顶栏左块碰到航向数字块 " + r[0] + "x" + r[1]);
            }
        }

        [Test]
        public void RightColumn_StacksWithoutOverlap_AtDesignHeight()
        {
            // 虚拟高度 = 屏幕高 / k; 发动机簇 (224) + Gap + 性能参数 (最小可压缩 ≥ 标题+若干行) 必须在右下高度面板之上
            foreach (var r in Resolutions)
            {
                float k = HudLayout.Scale(r[0], r[1]);
                if (r[0] / HudLayout.MinVirtualWidth < 0.55f) continue;
                float vh = r[1] / k;
                float engineBottom = HudLayout.Margin + 224f;
                float altitudeTop = vh - HudLayout.Margin - HudLayout.BottomH;
                Assert.Greater(altitudeTop - engineBottom, 120f,
                    "右列留给性能参数的高度过小 " + r[0] + "x" + r[1] + ": " + (altitudeTop - engineBottom).ToString("F0"));
            }
        }
    }
}
