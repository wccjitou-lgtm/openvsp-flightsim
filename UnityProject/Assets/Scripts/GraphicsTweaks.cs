using UnityEngine;
using UnityEngine.Rendering;

namespace FlightSim
{
    /// <summary>
    /// 全局画质微调: 场景加载前自动生效 (无需改场景文件)。
    /// Built-in 管线: MSAA 4x —— 3D 几何边缘 (树冠/岩石/地形轮廓) 与 IMGUI 四边形边缘
    /// (HUD 进度条/刻度线) 的抗锯齿; 字体本身由 FreeType 平滑渲染不受 MSAA 影响。
    /// 渲染升级 R5: HDRP 下 QualitySettings.antiAliasing 无效 (抗锯齿走 HDRP 相机的
    /// TAA, 见 FlightSimSceneSetup.SetupHdrpComponents), 仅 Built-in 分支设置 ——
    /// 保留 Built-in 回退能力。
    /// </summary>
    public static class GraphicsTweaks
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Apply()
        {
            if (GraphicsSettings.currentRenderPipeline != null) return;   // HDRP/URP: 相机级 AA
            QualitySettings.antiAliasing = 4;
        }
    }
}
