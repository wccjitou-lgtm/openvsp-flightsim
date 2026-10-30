using UnityEngine;
using UnityEngine.Rendering;

namespace FlightSim
{
    /// <summary>
    /// 管线无关材质工厂 (渲染升级 R1)。全项目运行时代码里唯一允许 Shader.Find 的位置
    /// —— 其余代码一律经此创建材质, 保证 Built-in/HDRP/URP 三管线可切换。
    /// shader 选择按 GraphicsSettings.currentRenderPipeline 的实际类型名判断,
    /// 不引用 HDRP/URP 程序集 (运行时程序集保持零管线依赖)。
    /// 注意: Shader.Find 要求着色器已被工程引用; 当前仅编辑器内运行不受影响,
    /// 独立构建需另行保证变体打入 (见 渲染升级开发计划.md 风险 #7)。
    /// </summary>
    public static class RenderMaterials
    {
        /// <summary>当前生效管线的判别 (供测试与调用方分支用)。</summary>
        public enum Pipeline { BuiltIn, Hdrp, Urp }

        public static Pipeline CurrentPipeline()
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp == null) return Pipeline.BuiltIn;
            string n = rp.GetType().Name;
            if (n.Contains("HDRenderPipeline")) return Pipeline.Hdrp;
            return Pipeline.Urp;
        }

        /// <summary>
        /// 创建当前管线的 Lit 材质。color 走 [MainColor] (Standard=_Color,
        /// HDRP/URP Lit=_BaseColor), tex 走 [MainTexture] (_MainTex/_BaseColorMap),
        /// normalMap 写 _NormalMap (HDRP/URP) 或 _BumpMap (Standard),
        /// 光滑度按管线写 _Smoothness 或 _Glossiness。
        /// </summary>
        public static Material Create(Color color, float smoothness = 0.3f,
            float metallic = 0f, Texture tex = null, Texture normalMap = null)
        {
            var mat = new Material(FindLitShader());
            mat.color = color;
            if (tex != null) mat.mainTexture = tex;
            if (normalMap != null)
            {
                if (mat.HasProperty("_NormalMap")) mat.SetTexture("_NormalMap", normalMap);
                else if (mat.HasProperty("_BumpMap")) mat.SetTexture("_BumpMap", normalMap);
            }
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            else if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", smoothness);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
            return mat;
        }

        /// <summary>
        /// 无光照平涂材质 (U12): 工坊 3D 预览专用 — HDRP 下手动渲染的预览相机
        /// 拿不到场景 Volume 曝光 (预览在 y=-5000 隔离区, 场景体积可能有界),
        /// Lit 材质渲染成全黑剪影 (用户截图实测)。Unlit 平涂免疫灯光/曝光,
        /// 且设计预览本来就是平色语义。找不到 Unlit 着色器时回落 Lit。
        /// </summary>
        public static Material CreateUnlit(Color color)
        {
            Shader s;
            switch (CurrentPipeline())
            {
                case Pipeline.Hdrp: s = Shader.Find("HDRP/Unlit"); break;
                case Pipeline.Urp: s = Shader.Find("Universal Render Pipeline/Unlit"); break;
                default: s = Shader.Find("Unlit/Color"); break;
            }
            if (s == null)
                return Create(color);   // 兜底: Lit (接受可能偏暗)
            var mat = new Material(s);
            mat.color = color;          // [MainColor]: HDRP/URP Unlit=_BaseColor, 内置=_Color
            return mat;
        }

        /// <summary>
        /// 自发光材质 (跑道灯/PAPI 等, S1)。intensity 为 HDRP 的 nits 量级
        /// (白天固定曝光下需要上万才可见); Built-in 用 _EmissionColor + _EMISSION 关键字。
        /// 不依赖实时光源, 数十盏灯也只是几个共享材质。
        /// </summary>
        public static Material CreateEmissive(Color color, float intensity = 20000f)
        {
            var mat = Create(color * 0.25f, 0.6f);
            if (mat.HasProperty("_EmissiveColor"))
                mat.SetColor("_EmissiveColor", color * intensity);
            else if (mat.HasProperty("_EmissionColor"))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", color * Mathf.Max(1f, intensity / 5000f));
            }
            return mat;
        }

        /// <summary>
        /// 地形材质 (渲染升级 R3, 备用地形管线用): HDRP=HDRP/TerrainLit,
        /// URP=Universal Render Pipeline/Terrain/Lit, Built-in 返回 null (用引擎默认)。
        /// TerrainLit 的贴层仍来自 TerrainLayer 资产, 这里只换 shader 载体。
        /// </summary>
        public static Material CreateTerrainLit()
        {
            Shader s;
            switch (CurrentPipeline())
            {
                case Pipeline.Hdrp: s = Shader.Find("HDRP/TerrainLit"); break;
                case Pipeline.Urp: s = Shader.Find("Universal Render Pipeline/Terrain/Lit"); break;
                default: return null;   // Built-in: 引擎默认地形材质
            }
            return s != null ? new Material(s) : null;
        }

        private static Shader FindLitShader()
        {
            Shader s;
            switch (CurrentPipeline())
            {
                case Pipeline.Hdrp: s = Shader.Find("HDRP/Lit"); break;
                case Pipeline.Urp: s = Shader.Find("Universal Render Pipeline/Lit"); break;
                default: s = Shader.Find("Standard"); break;
            }
            if (s == null)
            {
                // 目标管线着色器缺失时逐级兜底, 避免把 null 交给调用方
                s = Shader.Find("Standard");
                if (s == null) s = Shader.Find("HDRP/Lit");
                if (s == null) s = Shader.Find("Universal Render Pipeline/Lit");
            }
            return s;
        }
    }
}
