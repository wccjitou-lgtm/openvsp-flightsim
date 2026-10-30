using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace FlightSim
{
    /// <summary>
    /// 把 HDRP 水系统接到管线无关的 SeaWater 上 (运行时主程序集不引用 HDRP, 所以单独一个程序集)。
    /// 加载时注册工厂; SeaWater.Build 在水系统可用时调它, 否则退回平面方案。
    /// </summary>
    internal static class HdrpWaterAdapter
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            SeaWater.HdrpWaterEnabled = IsWaterEnabled;
            SeaWater.HdrpFactory = Create;
            SceneSwitcher.VolumeApplier = ApplyVolumeProfile;
        }

        private static Volume _globalVolume;
        private static VolumeProfile _sceneProfile;

        /// <summary>
        /// 切换全局 Volume 的 Profile: resourcePath 为 Resources 路径, 空 = 恢复场景自带的 Profile
        /// (首次调用时记下)。找不到全局 Volume 或资源时返回 false 且不改动。
        /// </summary>
        private static bool ApplyVolumeProfile(string resourcePath)
        {
            if (_globalVolume == null)
            {
                _sceneProfile = null;
                foreach (var v in Object.FindObjectsOfType<Volume>())
                    if (v.isGlobal && v.gameObject.name == "GlobalVolume") { _globalVolume = v; break; }
                if (_globalVolume == null) return false;
                _sceneProfile = _globalVolume.sharedProfile;
            }
            VolumeProfile target = _sceneProfile;
            if (!string.IsNullOrEmpty(resourcePath))
            {
                target = Resources.Load<VolumeProfile>(resourcePath);
                if (target == null) { Debug.LogWarning("[HdrpWater] Volume Profile 资源缺失: " + resourcePath); return false; }
            }
            ApplyDiagnosticFlags(target);
            _globalVolume.sharedProfile = target;
            return true;
        }

        // 打包版水面性能诊断: -wripples 0|1  -wband0 <倍率>  -wband1 <倍率>  -wtess <最大细分>  -wlod <层数>
        private static string Arg(string name)
        {
            var a = System.Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < a.Length; i++) if (a[i] == name) return a[i + 1];
            return null;
        }

        private static void ApplyDiagnosticFlags(WaterSurface w)
        {
            string v;
            if ((v = Arg("-wripples")) != null) w.ripples = v == "1";
            if ((v = Arg("-wband0")) != null) w.largeBand0Multiplier = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
            if ((v = Arg("-wband1")) != null) w.largeBand1Multiplier = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void ApplyDiagnosticFlags(VolumeProfile profile)
        {
            if (profile == null || !profile.TryGet(out WaterRendering wr)) return;
            string v;
            if ((v = Arg("-wtess")) != null) wr.maxTessellationFactor.Override(float.Parse(v, System.Globalization.CultureInfo.InvariantCulture));
            if ((v = Arg("-wlod")) != null) wr.numLevelOfDetails.Override(int.Parse(v));
            Debug.Log("[HdrpWater] 体积水渲染: tess=" + wr.maxTessellationFactor.value + " lod=" + wr.numLevelOfDetails.value +
                      " grid=" + wr.minGridSize.value + "~" + wr.maxGridSize.value);
        }

        private static bool IsWaterEnabled()
        {
            var asset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
            return asset != null && asset.currentPlatformRenderPipelineSettings.supportWater;
        }

        private static GameObject Create(SeaWater sea)
        {
            var go = new GameObject("HdrpSea");
            go.transform.SetParent(sea.transform, false);
            go.transform.localPosition = Vector3.zero;

            var w = go.AddComponent<WaterSurface>();
            // 必须 OceanSeaLake: Pool + Infinite 近处水面不渲染 (高空俯视透出沙底, 2026-10-11 实测)。
            // 性能开关在管线资产 waterSimulationResolution (128→64, 海岛帧率 43→136 fps);
            // caustics/foam 在这里关 (飞行视角不可见, 白吃帧时间)。
            w.surfaceType = WaterSurfaceType.OceanSeaLake;
            w.geometryType = WaterGeometryType.Infinite;

            // 风速 → 浪: 大尺度浪由 largeWindSpeed 驱动, 近处小波纹由 ripplesWindSpeed
            w.largeWindSpeed = Mathf.Clamp(sea.WindSpeedMps * 2.2f, 4f, 30f);
            w.largeWindOrientationValue = (sea.WindFromDeg + 180f) % 360f;
            w.largeChaos = 0.7f;
            w.ripplesWindSpeed = Mathf.Clamp(sea.WindSpeedMps, 2f, 20f);
            w.ripplesChaos = 0.8f;

            // 外观: 浅色热带海 (水下可见沙底), 远处偏深
            w.refractionColor = sea.ShallowColor;
            w.scatteringColor = sea.DeepColor;
            w.maxRefractionDistance = 6f;
            w.absorptionDistance = 25f;
            w.ambientScattering = 0.3f;
            w.heightScattering = 0.2f;
            w.displacementScattering = 0.4f;
            // 性能: caustics 焦散 (水下光斑) 与 foam 浪花模拟在飞行视角不可见, 关掉省帧时间;
            // 水模拟分辨率在管线资产 (64) —— 那才是大头, 别在这里找。
            w.caustics = false;
            w.foam = false;
            ApplyDiagnosticFlags(w);
            return go;
        }
    }
}
