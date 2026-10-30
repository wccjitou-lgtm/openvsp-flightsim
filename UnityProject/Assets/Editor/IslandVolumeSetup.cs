#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace FlightSim.EditorTools
{
    /// <summary>
    /// 海岛 Volume Profile (B3): 复制机场 Profile 的全部组件 (后处理/阴影/AO 等保持一致),
    /// 再叠加海岛专属项 —— 开 HDRP 水渲染 (WaterRendering.enable 默认 false, 不开则水面不画)、
    /// 蓝绿色地面色调 (机场 Profile 用 PBS 默认棕色 groundTint, 地平线偏褐)、稀疏积云、更通透的雾。
    /// 放在 Resources 下, 运行时由 SceneSwitcher 按 SceneProfile.VolumeProfileResource 加载。
    /// 机场(森林) Profile 不改。幂等: 每次重建。
    ///   Unity.exe -batchmode -projectPath ... -executeMethod FlightSim.EditorTools.IslandVolumeSetup.Create -quit
    /// </summary>
    public static class IslandVolumeSetup
    {
        public const string AssetPath = "Assets/Resources/Rendering/IslandVolumeProfile.asset";
        private const string AirportPath = "Assets/Rendering/AirportVolumeProfile.asset";

        public static void Create() { Ensure(); }

        public static VolumeProfile Ensure()
        {
            var airport = AssetDatabase.LoadAssetAtPath<VolumeProfile>(AirportPath);
            if (airport == null) { Debug.LogError("[IslandVolume] 机场 Volume Profile 不存在: " + AirportPath); return null; }

            if (!AssetDatabase.IsValidFolder("Assets/Resources/Rendering"))
            {
                if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
                AssetDatabase.CreateFolder("Assets/Resources", "Rendering");
            }
            AssetDatabase.DeleteAsset(AssetPath);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "IslandVolumeProfile";
            AssetDatabase.CreateAsset(profile, AssetPath);

            foreach (var c in airport.components)
            {
                if (c == null) continue;
                var clone = Object.Instantiate(c);
                clone.name = c.name;
                profile.components.Add(clone);
                AssetDatabase.AddObjectToAsset(clone, AssetPath);
            }

            // 水渲染总开关
            var water = profile.Add<WaterRendering>(true);
            AssetDatabase.AddObjectToAsset(water, AssetPath);
            water.enable.overrideState = true;
            water.enable.value = true;
            // 默认 3 级 LOD / 最大网格 2.5 km 只铺到约 10 km, 地平线外露出 PBS 的行星地面 (一条灰带)
            water.numLevelOfDetails.overrideState = true;
            water.numLevelOfDetails.value = 4;
            water.maxGridSize.overrideState = true;
            water.maxGridSize.value = 6000f;
            water.elevationTransition.overrideState = true;
            water.elevationTransition.value = 1500f;

            PhysicallyBasedSky pbs;
            if (profile.TryGet(out pbs))
            {
                pbs.groundTint.overrideState = true;
                pbs.groundTint.value = new Color(0.10f, 0.22f, 0.32f);   // 近远海深色, 地平线以下不再是棕色
                pbs.aerosolDensity.overrideState = true;
                pbs.aerosolDensity.value = pbs.aerosolDensity.value * 0.5f;   // 通透海洋空气: 气溶胶减半
                pbs.horizonTint.overrideState = true;
                pbs.horizonTint.value = new Color(0.86f, 0.93f, 1f);
            }

            VolumetricClouds vc;
            if (profile.TryGet(out vc))
            {
                vc.cloudPreset = VolumetricClouds.CloudPresets.Sparse;   // 信风积云: 稀疏
                var f = typeof(VolumetricClouds).GetField("m_CloudPreset",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var param = f != null ? f.GetValue(vc) : null;
                if (param != null) param.GetType().GetProperty("overrideState")?.SetValue(param, true, null);
            }

            Fog fog;
            if (profile.TryGet(out fog))
            {
                fog.meanFreePath.overrideState = true;
                fog.meanFreePath.value = 1500f;                 // 海上能见度高于森林默认 400 m
                fog.albedo.overrideState = true;
                fog.albedo.value = new Color(0.80f, 0.88f, 0.96f);
            }

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            Debug.Log("[IslandVolume] 已生成 " + AssetPath + " (" + profile.components.Count + " 个组件)");
            return profile;
        }
    }
}
#endif
