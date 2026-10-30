using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// Windows 11 Fluent 调色板 (全软件 IMGUI 统一主题, 2026-10-09)。
    /// 工坊 (WorkshopSkin) 用 Light; 飞行 HUD (FlightHUD/SceneSwitchPanel) 用 Dark。
    /// Fluent 特征: 平面填充无渐变 (贴图生成时渐变两色取同色)、小圆角、
    /// 1px 中性边框、蓝色强调、悬停/按下态只做明度微移。
    /// </summary>
    internal static class FluentTheme
    {
        // ---------- Light (工坊) ----------
        internal static readonly Color LightWindow = Hex(0xF3, 0xF3, 0xF3, 0xFF); // 窗口底
        internal static readonly Color LightCard = Hex(0xFA, 0xFA, 0xFA, 0xFF);   // 卡片/分组
        internal static readonly Color LightControl = Hex(0xFB, 0xFB, 0xFB, 0xFF);// 按钮/输入
        internal static readonly Color LightBorder = Hex(0xD1, 0xD1, 0xD1, 0xFF); // 1px 边
        internal static readonly Color LightText = Hex(0x1B, 0x1B, 0x1B, 0xFF);   // 主文字
        internal static readonly Color LightTextDim = Hex(0x5D, 0x5D, 0x5D, 0xFF);// 次级文字
        internal static readonly Color LightHover = Hex(0xF5, 0xF5, 0xF5, 0xFF);  // 悬停
        internal static readonly Color LightPressed = Hex(0xE5, 0xF1, 0xFB, 0xFF);// 按下/选中
        internal static readonly Color LightAccent = Hex(0x00, 0x78, 0xD4, 0xFF); // 强调蓝
        internal static readonly Color LightAccentHover = Hex(0x10, 0x6E, 0xBE, 0xFF);
        internal static readonly Color LightAccentSoft = Hex(0x00, 0x78, 0xD4, 0x30);

        // ---------- Dark (HUD, 场景上叠层需半透明) ----------
        internal static readonly Color DarkPanel = Hex(0x20, 0x20, 0x20, 0xD9);   // α≈0.85
        internal static readonly Color DarkCard = Hex(0x2B, 0x2B, 0x2B, 0xD9);
        internal static readonly Color DarkControl = Hex(0x33, 0x33, 0x33, 0xE0);
        internal static readonly Color DarkBorder = Hex(0x45, 0x45, 0x45, 0xFF);
        internal static readonly Color DarkText = Hex(0xF0, 0xF0, 0xF0, 0xFF);
        internal static readonly Color DarkTextDim = Hex(0xB8, 0xB8, 0xB8, 0xFF);
        internal static readonly Color DarkHover = Hex(0x3D, 0x3D, 0x3D, 0xE8);
        internal static readonly Color DarkAccent = Hex(0x60, 0xCD, 0xFF, 0xFF);  // 深色下浅蓝

        // ---------- 语义色 (两主题共用) ----------
        internal static readonly Color Warning = Hex(0xFC, 0xA1, 0x00, 0xFF);
        internal static readonly Color WarningOnLight = Hex(0xCA, 0x50, 0x10, 0xFF);
        internal static readonly Color Error = Hex(0xC4, 0x2B, 0x1C, 0xFF);

        private static Color Hex(int r, int g, int b, int a)
        {
            return new Color(r / 255f, g / 255f, b / 255f, a / 255f);
        }
    }
}
