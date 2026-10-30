#if UNITY_EDITOR
using FlightDynamics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace FlightSim.EditorTools
{
    /// <summary>
    /// 机场场景一键搭建: 菜单 FlightSim → Create Airport Scene。
    /// 创建: 跑道环境 + 飞机 (模型+控制器+键盘输入) + 跟随相机 + 撒布/动物/飞鸟 + 灯光,
    /// 并保存到 Assets/Scenes/Flight_Phase2.unity (仅保留机场单场景)。
    /// </summary>
    public static class FlightSimSceneSetup
    {
        [MenuItem("FlightSim/Create Phase 2 Flight Scene")]
        public static void CreateScene() { CreateSceneInternal(useAutoPilot: false); }

        /// <summary>batchmode 自动化验证入口: 用 AutoPilotInput 替代键盘输入。</summary>
        public static void CreateSceneAutoPilot() { CreateSceneInternal(useAutoPilot: true); }

        [MenuItem("FlightSim/Create Airport Scene")]
        public static void CreateAirportScene()
        {
            CreateSceneInternal(false);   // 机场跑道 (覆盖 Flight_Phase2)
            // 加入 Build Settings (单场景, 运行时无切换)
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene("Assets/Scenes/Flight_Phase2.unity", true),
            };
            Debug.Log("机场场景已生成并加入 Build Settings (仅保留机场单场景)");
        }

        /// <summary>
        /// 天空/雾配置 (渲染升级 R2 起): 旧 Skybox/Procedural + RenderSettings 指数雾已删除
        /// (HDRP 不读它们) —— 天空/雾/环境光全部由全局 Volume 的
        /// PhysicallyBasedSky/VolumetricClouds/Fog 提供, 见 CreateGlobalVolume。
        /// </summary>

        /// <summary>
        /// 场景全局 Volume (渲染升级 R2): 引用 Assets/Rendering/AirportVolumeProfile.asset
        /// (物理天空 + 体积云 Cloudy 预设 + 体积雾)。曝光不在场景里, 由 GlobalSettings 的
        /// 默认 Volume Profile 提供 (Fixed EV, RenderPipelineSetup 管理)。
        /// </summary>
        private static void CreateGlobalVolume()
        {
            var volumeGo = new GameObject("GlobalVolume");
            var vol = volumeGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 0;
            vol.sharedProfile = EnsureAirportVolumeProfile();
            AlpineVolumeSetup.Ensure();
            IslandVolumeSetup.Ensure();   // 海岛 Profile 由机场 Profile 派生, 随场景重生成一起刷新 (Resources 下, 运行时按场景切换)
            Debug.Log("[SceneSetup] 全局 Volume 已创建 (物理天空/体积云/体积雾)");
        }

        private const string VolumeProfilePath = "Assets/Rendering/AirportVolumeProfile.asset";

        /// <summary>
        /// 机场 Volume Profile (幂等): VisualEnvironment(PBS+Dynamic 环境光) +
        /// PhysicallyBasedSky(默认参数) + VolumetricClouds(Cloudy 预设, 1.2~3.2km) +
        /// Fog(体积雾, 默认 400m 平均自由程)。VolumeComponent 必须逐个
        /// AddObjectToAsset 才持久化 (R1 踩坑); VisualEnvironment.fogType 是
        /// internal 字段, 经 SerializedObject 写。
        /// </summary>
        private static VolumeProfile EnsureAirportVolumeProfile()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Rendering"))
                AssetDatabase.CreateFolder("Assets", "Rendering");
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                profile.name = "AirportVolumeProfile";
                AssetDatabase.CreateAsset(profile, VolumeProfilePath);
            }

            VisualEnvironment ve;
            if (!profile.TryGet(out ve))
            {
                ve = profile.Add<VisualEnvironment>(true);
                AssetDatabase.AddObjectToAsset(ve, VolumeProfilePath);
            }
            ve.skyType.overrideState = true;
            ve.skyType.value = (int)SkyType.PhysicallyBased;   // 枚举 public: 4
            ve.skyAmbientMode.overrideState = true;
            ve.skyAmbientMode.value = SkyAmbientMode.Dynamic;  // 环境光来自天空 (替代旧 Skybox 环境光)

            PhysicallyBasedSky pbs;
            if (!profile.TryGet(out pbs))
            {
                pbs = profile.Add<PhysicallyBasedSky>(true);
                AssetDatabase.AddObjectToAsset(pbs, VolumeProfilePath);   // 默认大气参数即可
            }

            VolumetricClouds vc;
            if (!profile.TryGet(out vc))
            {
                vc = profile.Add<VolumetricClouds>(true);
                AssetDatabase.AddObjectToAsset(vc, VolumeProfilePath);
            }
            vc.enable.overrideState = true;
            vc.enable.value = true;
            vc.cloudPreset = VolumetricClouds.CloudPresets.Cloudy;   // 属性 setter 内部应用预设到各高级参数
            // m_CloudPreset 是私有字段 (SerializedObject 路径实测返回 null), 反射设 overrideState
            SetPrivateVolumeOverride(vc, "m_CloudPreset");
            // 底高 1.2km / 厚度 2km 是 HDRP 默认值, 不额外覆盖

            Fog fog;
            if (!profile.TryGet(out fog))
            {
                fog = profile.Add<Fog>(true);
                AssetDatabase.AddObjectToAsset(fog, VolumeProfilePath);
            }
            fog.enabled.overrideState = true;
            fog.enabled.value = true;
            fog.enableVolumetricFog.overrideState = true;
            fog.enableVolumetricFog.value = true;

            // 阴影 (渲染升级 R3): 阴影距离/级联是 Volume 设置 (HDShadowSettings),
            // 替代 Built-in 时代 QualitySettings.shadowDistance=150 的档位
            HDShadowSettings shadows;
            if (!profile.TryGet(out shadows))
            {
                shadows = profile.Add<HDShadowSettings>(true);
                AssetDatabase.AddObjectToAsset(shadows, VolumeProfilePath);
            }
            shadows.maxShadowDistance.overrideState = true;
            shadows.maxShadowDistance.value = 600f;
            shadows.cascadeShadowSplitCount.overrideState = true;
            shadows.cascadeShadowSplitCount.value = 4;

            // 后处理 (渲染升级 R5): 色调映射 + 弱 Bloom + 极弱 Vignette + SSAO。
            // 景深/运动模糊不加 (默认关, 按计划)。
            Tonemapping tm;
            if (!profile.TryGet(out tm))
            {
                tm = profile.Add<Tonemapping>(true);
                AssetDatabase.AddObjectToAsset(tm, VolumeProfilePath);
            }
            tm.mode.overrideState = true;
            // R5 定稿: Neutral (快照对比 — ACES 均值压暗 23%, 明亮训练场景取 Neutral;
            // ACES 版存档 Logs/Snapshots/airport_aces.png 供对比)
            tm.mode.value = TonemappingMode.Neutral;

            Bloom bloom;
            if (!profile.TryGet(out bloom))
            {
                bloom = profile.Add<Bloom>(true);
                AssetDatabase.AddObjectToAsset(bloom, VolumeProfilePath);
            }
            bloom.intensity.overrideState = true;
            bloom.intensity.value = 0.06f;          // 弱
            bloom.threshold.overrideState = true;
            bloom.threshold.value = 1.0f;           // 只对超白 (HDR) 发光

            Vignette vig;
            if (!profile.TryGet(out vig))
            {
                vig = profile.Add<Vignette>(true);
                AssetDatabase.AddObjectToAsset(vig, VolumeProfilePath);
            }
            vig.intensity.overrideState = true;
            vig.intensity.value = 0.12f;            // 极弱

            ScreenSpaceAmbientOcclusion ssao;
            if (!profile.TryGet(out ssao))
            {
                ssao = profile.Add<ScreenSpaceAmbientOcclusion>(true);
                AssetDatabase.AddObjectToAsset(ssao, VolumeProfilePath);
            }
            ssao.intensity.overrideState = true;
            ssao.intensity.value = 0.5f;
            // fogType 是 internal 字段 (FogType.Volumetric=3), 反射直写参数值
            var fogField = typeof(VisualEnvironment).GetField("fogType",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            object fogParam = fogField != null ? fogField.GetValue(ve) : null;
            if (fogParam != null)
            {
                var fogTypeEnum = fogParam.GetType().Assembly.GetType(
                    "UnityEngine.Rendering.HighDefinition.FogType");
                fogParam.GetType().GetProperty("overrideState")?.SetValue(fogParam, true, null);
                fogParam.GetType().GetProperty("value").SetValue(fogParam,
                    System.Enum.Parse(fogTypeEnum, "Volumetric"), null);
            }
            else
                Debug.LogWarning("[SceneSetup] 未能设置 VisualEnvironment.fogType (反射字段缺失)");

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            return profile;
        }

        /// <summary>把某私有 VolumeParameter 字段的 overrideState 置 true (值已由属性 setter 设过)。
        /// 注意 overrideState 在 core RP 14 是属性不是字段 (GetField 返回 null)。</summary>
        private static void SetPrivateVolumeOverride(object component, string fieldName)
        {
            var f = component.GetType().GetField(fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var param = f != null ? f.GetValue(component) : null;
            if (param != null)
                param.GetType().GetProperty("overrideState")?.SetValue(param, true, null);
            else
                Debug.LogWarning("[SceneSetup] 反射缺失: " + component.GetType().Name + "." + fieldName);
        }

        private static Light FindSun()
        {
            var go = GameObject.Find("Directional Light");
            return go != null ? go.GetComponent<Light>() : null;
        }

        /// <summary>
        /// HDRP 附加数据 (渲染升级 R1): 相机补 HDAdditionalCameraData, 太阳补
        /// HDAdditionalLightData 并用物理单位 (晴天 ~11 万 lux, 旧 intensity 1.1 是
        /// Built-in 无单位量纲, HDRP 下无效)。非 HDRP 管线下这些组件无渲染副作用,
        /// 保持场景生成代码单一路径。曝光由全局默认 Volume Profile (Fixed EV) 提供。
        /// null 入参安全 (探针/局部调用)。
        /// </summary>
        private static void SetupHdrpComponents(Camera cam, Light sun = null)
        {
            if (cam != null)
            {
                var hdcam = cam.GetComponent<HDAdditionalCameraData>();
                if (hdcam == null) hdcam = cam.gameObject.AddComponent<HDAdditionalCameraData>();
                // R5: TAA 抗锯齿 (几何边缘/移动物体; 景深与运动模糊按计划默认关)
                hdcam.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
                // R5: far 面从默认 1000 放宽到 4000 —— 与地面外圈/雾可见距离匹配,
                // 自由相机拉远时地面边缘不穿帮 (云在天空通道不受 far 裁剪)
                cam.farClipPlane = 4000f;
            }
            if (sun == null) return;
            sun.color = new Color(1.0f, 0.96f, 0.88f);   // 暖白 (原 SetupSkyAndFog 迁移)
            var hd = sun.GetComponent<HDAdditionalLightData>();
            if (hd == null) hd = sun.gameObject.AddComponent<HDAdditionalLightData>();
            hd.EnableShadows(true);
            hd.SetIntensity(1.1e5f, LightUnit.Lux);
        }

        /// <summary>创建飞机 (模型+控制器+输入+风设置), 返回根对象。</summary>
        private static GameObject CreateAircraft(bool useAutoPilot, Vector3 initialNed)
        {
            var aircraftGo = new GameObject("TestUAV");
            var controller = aircraftGo.AddComponent<AircraftController>();
            if (useAutoPilot) aircraftGo.AddComponent<AutoPilotInput>();
            else aircraftGo.AddComponent<KeyboardFlightInput>();
            aircraftGo.AddComponent<WindSettings>();
            aircraftGo.AddComponent<FlightHUD>();   // Phase 5 仪表叠层
            aircraftGo.AddComponent<AircraftVisual>();   // 视觉自愈 (模型/占位)
            // 默认 TestUAV 由 AircraftVisual 从 StreamingAssets 加载，避免写入开发机绝对路径。
            aircraftGo.GetComponent<AircraftVisual>().RuntimeObjPath = "";
            aircraftGo.AddComponent<DataLogger>();   // Phase 6 CSV 遥测记录
            aircraftGo.AddComponent<FlightGuard>();   // 场地边界自动返航
            aircraftGo.AddComponent<ModePilot>();      // 一键返航/降落/盘旋/自稳
            controller.InitialPositionNed = initialNed;

            GameObject modelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Models/TestUAV.obj");
            if (modelPrefab != null)
            {
                var model = (GameObject)PrefabUtility.InstantiatePrefab(modelPrefab);
                if (model == null) model = Object.Instantiate(modelPrefab);
                model.transform.SetParent(aircraftGo.transform, false);
                controller.AircraftMesh = model.GetComponentInChildren<Renderer>();
            }
            else
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.SetParent(aircraftGo.transform, false);
                cube.transform.localScale = new Vector3(4f, 0.35f, 2.8f);
                controller.AircraftMesh = cube.GetComponent<Renderer>();
            }

            aircraftGo.transform.position = new Vector3(initialNed.y, -initialNed.z, initialNed.x);
            return aircraftGo;
        }

        private static void CreateSceneInternal(bool useAutoPilot)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.DefaultGameObjects,
                UnityEditor.SceneManagement.NewSceneMode.Single);

            // ---- 灯光: 上午 10 点斜射 (长阴影); 物理单位/颜色在 SetupHdrpComponents ----
            var sun = GameObject.Find("Directional Light");
            if (sun != null)
                sun.transform.rotation = Quaternion.Euler(38f, -40f, 0f);
            SetupHdrpComponents(null, FindSun());   // HDRP: 太阳 lux + 暖色 (渲染升级 R1/R2)

            // ---- 全局 Volume: 物理天空 + 体积云 + 体积雾 (渲染升级 R2) ----
            CreateGlobalVolume();

            // ---- 环境 (跑道/地形/撒布/动物/飞鸟): 与运行时切换共用 EnvironmentBuilder, 挂在 EnvironmentRoot 下 ----
            var profile = SceneRegistry.Default;   // 森林; 4km 地形资产在 StreamingAssets/Terrain/forest/
            TerrainAssetSetup.CreateForestLayerAssets();   // 5 层贴层资产 (幂等)
            TerrainAssetSetup.CreateIslandLayerAssets();   // 海岛 4 层 (切换到海岛时运行时 Resources.Load)
            var env = EnvironmentBuilder.Build(profile);
            var runway = env.Runway;

            // ---- 飞机 (停在 800m 跑道入口内 12m 的中线上: NED (12,0,-0.327) → Unity (0, 0.327, 12)) ----
            // 统一走共享的 CreateAircraft 辅助方法,
            // 避免这里手写一份重复逻辑导致漏挂 FlightHUD/AircraftVisual。
            var aircraft = CreateAircraft(useAutoPilot,
                new Vector3(runway.RunwayStart.z + profile.StartAlongRunwayM, runway.RunwayStart.x,
                            -(runway.RunwayStart.y + 0.327f)));

            var guard = aircraft.GetComponent<FlightGuard>();
            if (guard != null) guard.BoundaryRadius = profile.BoundaryRadius;   // 4km 场地: 1800m (+200m 渐进带)

            // ---- 场景切换 (按钮 + 载入遮罩): 场景级对象, 不随飞机热替换销毁 ----
            var switcherGo = new GameObject("SceneSwitcher");
            switcherGo.AddComponent<SceneSwitcher>();
            switcherGo.AddComponent<SceneSwitchPanel>();

            // ---- 相机 ----
            var camGo = GameObject.Find("Main Camera");
            if (camGo == null) { camGo = new GameObject("Main Camera"); camGo.AddComponent<Camera>(); }
            camGo.transform.position = new Vector3(0f, 3f, -10f);
            var chase = camGo.AddComponent<ChaseCamera>();
            chase.Target = aircraft.transform;
            SetupHdrpComponents(camGo.GetComponent<Camera>());   // HDRP: 相机附加数据 (渲染升级 R1)

            // ---- 保存 ----
            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, "Assets/Scenes/Flight_Phase2.unity");
            Debug.Log("机场场景已创建并保存: Assets/Scenes/Flight_Phase2.unity (按 Play 试飞)");
        }
    }
}
#endif
