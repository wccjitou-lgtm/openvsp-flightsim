#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace FlightSim.EditorTools
{
    /// <summary>
    /// 雪谷 Volume Profile (C2): 复制机场 Profile 的全部组件, 再叠加雪谷专属项 ——
    /// 地面色调改雪白 (PBS 默认棕色 groundTint 会把低角度天光和云底染成褐色)、稀疏云、气溶胶减少 (高海拔空气更清澈)、
    /// 雾更近更蓝白、阴影距离加长 (800 m 高的谷壁)。放在 Resources 下, 运行时由 SceneSwitcher 按
    /// SceneProfile.VolumeProfileResource 加载。机场(森林) Profile 不改。幂等: 每次重建。
    ///   Unity.exe -batchmode -projectPath ... -executeMethod FlightSim.EditorTools.AlpineVolumeSetup.Create -quit
    /// </summary>
    public static class AlpineVolumeSetup
    {
        public const string AssetPath = "Assets/Resources/Rendering/AlpineVolumeProfile.asset";
        private const string AirportPath = "Assets/Rendering/AirportVolumeProfile.asset";

        public static void Create() { Ensure(); }

        public static VolumeProfile Ensure()
        {
            var airport = AssetDatabase.LoadAssetAtPath<VolumeProfile>(AirportPath);
            if (airport == null) { Debug.LogError("[AlpineVolume] 机场 Volume Profile 不存在: " + AirportPath); return null; }

            if (!AssetDatabase.IsValidFolder("Assets/Resources/Rendering"))
            {
                if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
                AssetDatabase.CreateFolder("Assets/Resources", "Rendering");
            }
            AssetDatabase.DeleteAsset(AssetPath);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "AlpineVolumeProfile";
            AssetDatabase.CreateAsset(profile, AssetPath);

            foreach (var c in airport.components)
            {
                if (c == null) continue;
                var clone = Object.Instantiate(c);
                clone.name = c.name;
                profile.components.Add(clone);
                AssetDatabase.AddObjectToAsset(clone, AssetPath);
            }

            PhysicallyBasedSky pbs;
            if (profile.TryGet(out pbs))
            {
                pbs.groundTint.overrideState = true;
                pbs.groundTint.value = new Color(0.70f, 0.74f, 0.80f);   // 雪地反照
                pbs.aerosolDensity.overrideState = true;
                pbs.aerosolDensity.value = pbs.aerosolDensity.value * 0.4f;
                pbs.horizonTint.overrideState = true;
                pbs.horizonTint.value = new Color(0.84f, 0.92f, 1f);
            }

            VolumetricClouds vc;
            if (profile.TryGet(out vc))
            {
                vc.cloudPreset = VolumetricClouds.CloudPresets.Sparse;
                var f = typeof(VolumetricClouds).GetField("m_CloudPreset",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var param = f != null ? f.GetValue(vc) : null;
                if (param != null) param.GetType().GetProperty("overrideState")?.SetValue(param, true, null);
            }

            Fog fog;
            if (profile.TryGet(out fog))
            {
                fog.meanFreePath.overrideState = true;
                fog.meanFreePath.value = 900f;
                fog.albedo.overrideState = true;
                fog.albedo.value = new Color(0.82f, 0.88f, 0.96f);
            }

            HDShadowSettings shadows;
            if (profile.TryGet(out shadows))
            {
                shadows.maxShadowDistance.overrideState = true;
                shadows.maxShadowDistance.value = 1200f;
            }

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            Debug.Log("[AlpineVolume] 已生成 " + AssetPath + " (" + profile.components.Count + " 个组件)");
            return profile;
        }
    }
}
#endif
