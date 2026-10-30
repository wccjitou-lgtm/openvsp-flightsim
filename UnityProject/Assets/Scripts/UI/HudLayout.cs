using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 屏幕 UI (HUD / 场景切换 / 机型工坊入口) 共用的缩放与栅格常量。
    /// 所有布局按 "虚拟像素" (1080p 下 1 虚拟像素 = 1 屏幕像素) 设计, 乘 <see cref="Scale"/> 得屏幕像素。
    /// Scale 同时受高度与宽度约束, 窄窗口整体缩小而不是互相挤压; 下限 0.55 保证字号不低于 8 px。
    /// </summary>
    public static class HudLayout
    {
        public const float RefHeight = 1080f;
        /// <summary>虚拟宽度下限: 底部中央 (风况+姿态仪+模式键) 与左右侧面板并排所需的最小宽度。</summary>
        public const float MinVirtualWidth = 1220f;

        public const float Margin = 16f;
        public const float Gap = 12f;
        /// <summary>顶栏一行的高度与 y (各 chip / 场景切换同一行)。</summary>
        public const float TopRowY = 12f, TopRowH = 30f;

        /// <summary>右列 (发动机簇 / 性能参数) 宽度。</summary>
        public const float ColW = 250f;
        /// <summary>底部三组 (左速度 / 中央仪表 / 右高度) 共用高度, 底边统一在 Margin 处。</summary>
        public const float BottomH = 212f;
        public const float SidePanelW = 220f;

        /// <summary>场景切换分段按钮总宽 (3 × 64 + 2 × 4), 顶栏其余 chip 从其右侧排起。</summary>
        public const float SceneBarW = 200f;
        public const float TopChipGap = 8f;
        public const float CameraChipW = 190f, HelpChipW = 184f, WorkshopEntryW = 132f;

        public static Rect CameraChipRect(float scale)
        {
            return new Rect((Margin + SceneBarW + TopChipGap) * scale,
                TopRowY * scale, CameraChipW * scale, TopRowH * scale);
        }

        public static Rect HelpChipRect(float scale)
        {
            return new Rect(CameraChipRect(scale).xMax + TopChipGap * scale,
                TopRowY * scale, HelpChipW * scale, TopRowH * scale);
        }

        public static Rect WorkshopEntryRect(float scale)
        {
            return new Rect(HelpChipRect(scale).xMax + TopChipGap * scale,
                TopRowY * scale, WorkshopEntryW * scale, TopRowH * scale);
        }

        public static Rect HeadingValueRect(float screenWidth, float scale)
        {
            var chip = new Rect(screenWidth * 0.5f - 30f * scale, 19f * scale,
                60f * scale, 22f * scale);
            if (chip.Overlaps(WorkshopEntryRect(scale)) || chip.Overlaps(HelpChipRect(scale)) ||
                chip.Overlaps(CameraChipRect(scale)))
                chip.y = 82f * scale;
            return chip;
        }

        public static float Scale() { return Scale(Screen.width, Screen.height); }

        public static float Scale(float screenW, float screenH)
        {
            float byH = screenH / RefHeight;
            float byW = screenW / MinVirtualWidth;
            return Mathf.Clamp(Mathf.Min(byH, byW), 0.55f, 1.6f);
        }

        /// <summary>风况面板宽 / 模式键宽 (虚拟像素): 底部中央簇 = 风况 + Gap + 姿态仪 (BottomH) + Gap + 模式键。</summary>
        public const float WindPanelW = 240f, ModeButtonW = 130f;

        /// <summary>
        /// 底部中央簇相对屏幕中线的左右半径 (虚拟像素), 与 FlightHUD 的摆放公式一致;
        /// 供布局守卫测试核对它与左右侧面板之间留有空隙。
        /// </summary>
        public static void BottomClusterExtent(out float left, out float right)
        {
            float half = BottomH * 0.5f;
            left = half + Gap + WindPanelW;
            right = half + Gap + ModeButtonW;
        }
    }
}
