#if UNITY_EDITOR
using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace FlightSim.EditorTools
{
    /// <summary>
    /// 渲染管线配置入口 (渲染升级 R0 探针版 → R1 扩展为正式版)。
    /// R0 范围: 最小 HDRP 配置 —— HDRP Asset + HDRP Global Settings + 默认 Volume Profile
    /// (含固定曝光), 指派 GraphicsSettings.renderPipelineAsset, 色彩空间切 Linear。
    /// 不改任何业务代码、不生成/修改场景。
    ///
    /// 幂等: 资产已存在则只做指派检查, 可重复执行。
    /// 批处理调用:
    ///   Unity.exe -batchmode -projectPath ... \
    ///     -executeMethod FlightSim.EditorTools.RenderPipelineSetup.ApplyProbe -quit
    ///
    /// 实现说明: HDRP 14.0.12 的 HDRenderPipelineGlobalSettings 是 internal 类,
    /// 其官方工厂方法 Create/UpdateGraphicsSettings 完成了 Runtime/Editor/RayTracing
    /// 资源填充与默认 Volume Profile 创建 (源码 Runtime/RenderPipeline/
    /// HDRenderPipelineGlobalSettings.cs:130) —— 手工拼这些引用极易漏, 因此全部经
    /// 反射调用官方路径; 反射拿到的实例以 UnityEngine.Object/SerializedObject 操作。
    /// HDRP Asset 本体是 public 类, 按官方菜单逻辑直接 CreateInstance。
    /// </summary>
    public static class RenderPipelineSetup
    {
        private const string Folder = "Assets/Rendering";
        private const string AssetPath = Folder + "/HDRenderPipelineAsset.asset";
        private const string GlobalSettingsPath = Folder + "/HDRenderPipelineGlobalSettings.asset";
        private const string GsTypeName = "UnityEngine.Rendering.HighDefinition.HDRenderPipelineGlobalSettings";

        /// <summary>R0 探针入口 (R1 已扩展为正式版 Apply, 此名保留兼容既有命令)。</summary>
        public static void ApplyProbe() { Apply(); }

        /// <summary>R1 正式入口: 最小可用 HDRP 配置 (幂等)。</summary>
        public static void Apply()
        {
            try
            {
                EnsureFolder();

                // ---- 1. HDRP Global Settings (官方工厂, 含资源填充 + 默认 Volume Profile) ----
                var gsType = FindType(GsTypeName);
                if (gsType == null)
                    throw new InvalidOperationException("未找到类型 " + GsTypeName + " (HDRP 未编译?)");

                var gsObj = AssetDatabase.LoadAssetAtPath(GlobalSettingsPath, gsType);
                if (gsObj == null)
                {
                    var create = gsType.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic, null,
                        new[] { typeof(string), gsType }, null);
                    if (create == null)
                        throw new InvalidOperationException("未找到 HDRenderPipelineGlobalSettings.Create (API 变更?)");
                    gsObj = (UnityEngine.Object)create.Invoke(null, new object[] { GlobalSettingsPath, null });
                    Debug.Log("[RenderPipelineSetup] 已创建 GlobalSettings: " + GlobalSettingsPath);
                }

                var update = gsType.GetMethod("UpdateGraphicsSettings",
                    BindingFlags.Static | BindingFlags.NonPublic, null, new[] { gsType }, null);
                if (update == null)
                    throw new InvalidOperationException("未找到 HDRenderPipelineGlobalSettings.UpdateGraphicsSettings");
                update.Invoke(null, new[] { gsObj });
                Debug.Log("[RenderPipelineSetup] GlobalSettings 已注册到 GraphicsSettings");

                // ---- 2. 默认 Volume Profile: 持久化子资产 + 固定曝光 ----
                // (HDRP 官方 GetOrCreateDefaultVolumeProfile 只做内存赋值不落盘,
                //  m_DefaultVolumeProfile 会保持 fileID:0 —— 自建子资产并显式指派)
                var so = new SerializedObject(gsObj);
                var profile = so.FindProperty("m_DefaultVolumeProfile").objectReferenceValue as VolumeProfile;
                if (profile == null)
                {
                    profile = ScriptableObject.CreateInstance<VolumeProfile>();
                    profile.name = "DefaultVolumeProfile";
                    AssetDatabase.AddObjectToAsset(profile, GlobalSettingsPath);
                    so.FindProperty("m_DefaultVolumeProfile").objectReferenceValue = profile;
                    so.ApplyModifiedProperties();
                    Debug.Log("[RenderPipelineSetup] 已创建并持久化 DefaultVolumeProfile (子资产)");
                }
                Exposure exp;
                if (!profile.TryGet(out exp))
                {
                    exp = profile.Add<Exposure>(true);
                    // VolumeComponent 也是 ScriptableObject, 必须作为子资产写入才会持久化
                    // (profile.Add 只改内存 components 列表)
                    AssetDatabase.AddObjectToAsset(exp, GlobalSettingsPath);
                }
                exp.mode.overrideState = true;
                exp.mode.value = ExposureMode.Fixed;
                exp.fixedExposure.overrideState = true;
                exp.fixedExposure.value = 14f;   // 晴天户外 EV100 ~14 (R2 换物理天空时再精调)
                EditorUtility.SetDirty(profile);
                EditorUtility.SetDirty(gsObj);
                Debug.Log("[RenderPipelineSetup] 默认 Volume Profile: Fixed EV100=14");

                // ---- 3. HDRP Asset (三档质量, R6) + 指派 ----
                // High=现状(云+SSAO+2048 阴影); Medium=1024 阴影; Low=云/SSAO 关+512 阴影。
                // 云/AO 在资产支持位上关 (Volume 覆盖仍在但不渲), 阴影走方向阴影图分辨率。
                var highAsset = EnsureQualityAsset("HDRenderPipelineAsset", true, true, 2048);
                EnsureQualityAsset("HDRenderPipelineAsset_Medium", true, true, 1024);
                EnsureQualityAsset("HDRenderPipelineAsset_Low", false, false, 512);

                if (GraphicsSettings.renderPipelineAsset != highAsset)
                {
                    GraphicsSettings.renderPipelineAsset = highAsset;
                    Debug.Log("[RenderPipelineSetup] 已指派 GraphicsSettings.renderPipelineAsset (High)");
                }
                AssignAllQualityLevels();

                // ---- 4. 色彩空间: HDRP 强制要求 Linear ----
                if (PlayerSettings.colorSpace != ColorSpace.Linear)
                {
                    PlayerSettings.colorSpace = ColorSpace.Linear;
                    Debug.Log("[RenderPipelineSetup] 色彩空间已切换为 Linear (触发纹理重导入属预期)");
                }

                // ---- 5. 关闭异步着色器编译 ----
                // HDRP 下批处理单帧 Camera.Render (SceneSnapshot/RenderProbe) 会因异步编译
                // 未完成而整帧跳过物体, 只剩背景色 —— 同步编译保证快照工具可用。
                if (UnityEditor.EditorSettings.asyncShaderCompilation)
                {
                    UnityEditor.EditorSettings.asyncShaderCompilation = false;
                    Debug.Log("[RenderPipelineSetup] 已关闭异步着色器编译 (批处理快照必需)");
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log("[RenderPipelineSetup] ApplyProbe 完成: pipeline=" +
                          (GraphicsSettings.currentRenderPipeline != null
                              ? GraphicsSettings.currentRenderPipeline.name : "null"));
            }
            catch (Exception ex)
            {
                Debug.LogError("[RenderPipelineSetup] ApplyProbe 失败: " + ex);
                EditorApplication.Exit(1);   // 批处理下非零退出, 避免伪装成功
            }
        }

        /// <summary>
        /// 创建/更新一档 HDRP 质量资产 (幂等): 体积云支持 / SSAO 支持 / 方向阴影图分辨率。
        /// 这些是资产级支持位与预算; 场景级观感 (云量/雾/阴影距离) 由 Volume Profile 决定,
        /// 三档共享同一 Profile —— Low 档云/AO 不渲即是降档效果。
        /// </summary>
        private static HDRenderPipelineAsset EnsureQualityAsset(string assetName,
            bool clouds, bool ssao, int shadowRes)
        {
            string path = Folder + "/" + assetName + ".asset";
            var asset = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                asset.name = assetName;
                AssetDatabase.CreateAsset(asset, path);
                Debug.Log("[RenderPipelineSetup] 已创建 " + path);
            }
            var so = new SerializedObject(asset);
            so.FindProperty("m_RenderPipelineSettings.supportVolumetricClouds").boolValue = clouds;
            so.FindProperty("m_RenderPipelineSettings.supportSSAO").boolValue = ssao;
            so.FindProperty("m_RenderPipelineSettings.hdShadowInitParams.maxDirectionalShadowMapResolution").intValue = shadowRes;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
            return asset;
        }

        /// <summary>质量档名 → 资产 (0,1=Low / 2,3=Medium / 4,5=High, 对应 Unity 6 档)。</summary>
        private static HDRenderPipelineAsset TierAssetForLevel(int level)
        {
            string name = level <= 1 ? "HDRenderPipelineAsset_Low"
                         : level <= 3 ? "HDRenderPipelineAsset_Medium"
                         : "HDRenderPipelineAsset";
            return AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(Folder + "/" + name + ".asset");
        }

        /// <summary>R6 画质切换入口 (编辑器菜单 / executeMethod / 运行时脚本均可调)。
        /// 切 QualitySettings 档位 → per-level SRP 资产随之切换。运行时切换 HDRP 资产
        /// 会触发一次管线重建 (短暂卡顿), 未能验证的部分见 render_spike.md。</summary>
        [MenuItem("FlightSim/Render Quality/High (clouds+SSAO+2048)")]
        public static void SetQualityHigh() { SetQualityLevel(4); }

        [MenuItem("FlightSim/Render Quality/Medium (clouds+SSAO+1024)")]
        public static void SetQualityMedium() { SetQualityLevel(2); }

        [MenuItem("FlightSim/Render Quality/Low (no clouds/SSAO, 512)")]
        public static void SetQualityLow() { SetQualityLevel(0); }

        private static void SetQualityLevel(int level)
        {
            QualitySettings.SetQualityLevel(level, false);
            var rp = GraphicsSettings.currentRenderPipeline;   // QualitySettings 无公开 per-level 资产 API (R1 踩过)
            Debug.Log("[RenderPipelineSetup] 画质 → " + QualitySettings.names[level] +
                      " (level " + level + ", pipeline=" + (rp != null ? rp.name : "null") + ")");
        }

        /// <summary>
        /// 6 个 Unity 质量档映射到三档 HDRP 资产 (0,1=Low / 2,3=Medium / 4,5=High)。
        /// per-level 的 SRP 资产没有公开编辑器 API, 直接经 SerializedObject 写
        /// QualitySettings.asset 的 m_QualitySettings[].customRenderPipeline。
        /// </summary>
        private static void AssignAllQualityLevels()
        {
            var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset");
            if (settings == null || settings.Length == 0)
            {
                Debug.LogWarning("[RenderPipelineSetup] 未找到 QualitySettings.asset, 跳过质量档指派");
                return;
            }
            var so = new SerializedObject(settings[0]);
            var levels = so.FindProperty("m_QualitySettings");
            for (int i = 0; i < levels.arraySize; i++)
            {
                var rp = levels.GetArrayElementAtIndex(i).FindPropertyRelative("customRenderPipeline");
                rp.objectReferenceValue = TierAssetForLevel(i);
            }
            so.ApplyModifiedProperties();
            Debug.Log("[RenderPipelineSetup] " + levels.arraySize +
                      " 个质量档已映射: 0-1=Low, 2-3=Medium, 4-5=High");
        }

        private static void EnsureFolder()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", "Rendering");
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }
    }
}
#endif
