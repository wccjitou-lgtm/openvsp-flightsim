using System.Collections;
using FlightDynamics;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 运行时场景切换: 销毁旧 EnvironmentRoot → 按 SceneProfile 重建 → 等地形/跑道/撒布就绪 →
    /// 把飞机/边界/风复位到新场地。飞机、相机、HUD 不重建 (工坊导入的机型保留)。
    /// 切换期间飞机物理冻结 (AircraftController.Frozen), 避免无地形时下坠。
    /// 点击入口见 <see cref="Click"/>: 飞机在空中时需要二次确认, 避免误触把飞行打断。
    /// </summary>
    public class SceneSwitcher : MonoBehaviour
    {
        public const string PrefKey = "FlightSim.SceneId";
        public const float ConfirmWindowS = 3f;

        /// <summary>切换全局 Volume Profile: 参数 = Resources 路径, 空 = 恢复场景自带。由 FlightSim.HdrpWater 注册 (运行时程序集不引用管线); 未注册 = 不换天空。</summary>
        public static System.Func<string, bool> VolumeApplier;

        public enum ClickResult { Ignored, Armed, Switching }

        [Tooltip("编辑器内记住上次所选场景 (PlayerPrefs, 截图工具用); 打包版始终载入默认森林; 测试里关掉")]
        public bool PersistChoice = true;

        public SceneProfile Current { get; private set; }
        public SceneProfile Target { get; private set; }
        public bool IsLoading { get; private set; }
        public EnvironmentHandles Handles { get; private set; }

        /// <summary>等待二次确认的场景 id (空 = 无)。</summary>
        public string PendingId { get; private set; }
        private float _pendingUntil;

        /// <summary>最近一次载入耗时 (秒, 墙钟, 含帧间等待): 销毁 / 地形就绪 / 跑道就绪 / 撒布与动物就绪 / 总计。</summary>
        public float LastDestroyS, LastTerrainS, LastRunwayS, LastScatterS, LastTotalS;
        public string LastError { get; private set; }

        /// <summary>载入完成 (含失败) 时触发, 参数 = 新场景。</summary>
        public event System.Action<SceneProfile> Loaded;

        private const float TerrainTimeoutS = 60f, ContentTimeoutS = 12f;

        /// <summary>大气偏置是静态状态: 离开本组件 (退出 Play/测试销毁) 时必须复位, 否则泄漏到后续用例/会话。</summary>
        private void OnDestroy() { AtmosphereModel.AltitudeOffsetM = 0.0; }

        private void Start()
        {
            var existing = GameObject.Find(EnvironmentBuilder.RootName);
            if (existing != null && Handles == null) Handles = AdoptExisting(existing);
            if (Current == null) Current = SceneRegistry.Default;

            // 命令行 -scene <id>: 打包版诊断/截图用, 跳过面板点击直接载入指定场景
            // 命令行 -nowater: 诊断用, 配合 -scene 临时关掉海面 (隔离水系统开销)
            // 命令行 -weather: 诊断用, 配合 -scene 应用场景预设风/紊流 (等同面板点击切换)
            string cliScene = null;
            bool noWater = false, cliWeather = false;
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-scene") cliScene = args[i + 1];
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-nowater") noWater = true;
                else if (args[i] == "-weather") cliWeather = true;
            }
            if (!string.IsNullOrEmpty(cliScene))
            {
                var p = SceneRegistry.Get(cliScene);
                if (p != null && noWater) p.Sea = false;
                if (p != null && p.Ready && p != Current) { SwitchTo(p.Id, cliWeather); return; }
            }

            // 场景恢复只在编辑器生效 (SwapShot/UiShot/SceneOpener 截图工具靠 PrefKey 选场景);
            // 打包版始终载入默认场景 (森林) —— 2026-10-11 用户规则: 默认载入 = 森林。
            if (PersistChoice && Application.isEditor)
            {
                var saved = SceneRegistry.Get(PlayerPrefs.GetString(PrefKey, ""));
                // 启动恢复上次场景: 不带预设风 (用户规则 2026-10-10: 载入默认 0 级),
                // 预设风只在用户主动切换场景时下发
                if (saved != null && saved.Ready && saved != Current) SwitchTo(saved.Id, false);
                else CalmStartupWind();
            }
            else CalmStartupWind();
        }

        /// <summary>启动时把飞机风况置 0 级 (无风 + 紊流关), 覆盖场景文件里序列化的旧值。</summary>
        private static void CalmStartupWind()
        {
            var ac = FindObjectOfType<AircraftController>();
            var wind = ac != null ? ac.GetComponent<WindSettings>() : null;
            if (wind != null)
                wind.SetWeather(0f, 0f, false, DrydenTurbulence.TurbulenceLevel.Light, false);
        }

        /// <summary>编辑期生成的场景里已有 EnvironmentRoot: 按名字认领子对象。</summary>
        private static EnvironmentHandles AdoptExisting(GameObject root)
        {
            return new EnvironmentHandles
            {
                Root = root,
                Runway = root.GetComponentInChildren<RunwayEnvironment>(),
                Terrain = root.GetComponentInChildren<TerrainFromHeightmap>(),
                Scatter = root.GetComponentInChildren<ScatterEnvironment>(),
                Wildlife = root.GetComponentInChildren<WildlifeEnvironment>(),
                Birds = root.GetComponentInChildren<BirdFlock>(),
                Props = root.GetComponentInChildren<IslandProps>(),
                Sea = root.GetComponentInChildren<SeaWater>(),
            };
        }

        // ================= 点击入口 (面板调用) =================

        /// <summary>飞机在空中 (非坠毁、非接地) 时切换需要二次确认。</summary>
        public bool NeedsConfirm()
        {
            var ac = FindObjectOfType<AircraftController>();
            return ac != null && !ac.IsCrashed && !ac.OnGround;
        }

        public bool IsPending(string id, float now)
        {
            return PendingId == id && now <= _pendingUntil;
        }

        /// <summary>
        /// 按钮点击。地面/坠毁: 直接切换。空中: 第一次点击只"待确认"(3 s 内再点同一个才切换)。
        /// 载入中、场景未就绪、点击当前场景: 忽略。
        /// </summary>
        public ClickResult Click(string id, float now)
        {
            var p = SceneRegistry.Get(id);
            if (p == null || !p.Ready || IsLoading || p == Current) return ClickResult.Ignored;

            if (NeedsConfirm())
            {
                if (IsPending(id, now))
                {
                    PendingId = null;
                    return SwitchTo(id) ? ClickResult.Switching : ClickResult.Ignored;
                }
                PendingId = id;
                _pendingUntil = now + ConfirmWindowS;
                return ClickResult.Armed;
            }
            PendingId = null;
            return SwitchTo(id) ? ClickResult.Switching : ClickResult.Ignored;
        }

        /// <summary>无确认直接切换 (启动恢复、测试、脚本)。载入中或未就绪返回 false。
        /// applyWeather=false: 不应用场景预设风 (启动恢复用, 载入默认 0 级)。</summary>
        public bool SwitchTo(string id, bool applyWeather = true)
        {
            var p = SceneRegistry.Get(id);
            if (p == null || !p.Ready || IsLoading) return false;
            if (PersistChoice) PlayerPrefs.SetString(PrefKey, p.Id);
            StartCoroutine(SwitchRoutine(p, applyWeather));
            return true;
        }

        // ================= 切换流程 =================

        /// <summary>切换协程 (公开供 PlayMode 测试 yield 等待; 正常用 SwitchTo)。</summary>
        public IEnumerator SwitchRoutine(SceneProfile p, bool applyWeather = true)
        {
            IsLoading = true;
            Target = p;
            PendingId = null;
            LastError = null;
            float t0 = Time.realtimeSinceStartup;

            // 1) 冻结飞机 + 解除自驾; 让遮罩先画出来再做同步重活
            SetFrozen(true);
            foreach (var ac in FindObjectsOfType<AircraftController>())
            {
                var pilot = ac.GetComponent<ModePilot>();
                if (pilot != null) pilot.Disengage();
            }
            yield return null;
            yield return null;

            // 2) 销毁旧环境; 地形登记立即清掉, 避免新场景无地形时飞机继承旧地形
            float t1 = Time.realtimeSinceStartup;
            if (Handles != null && Handles.Root != null) Destroy(Handles.Root);
            Handles = null;
            TerrainRegistry.Clear();
            yield return null;   // Destroy 在帧末生效, 新对象必须晚一帧再建
            LastDestroyS = Time.realtimeSinceStartup - t1;

            // 3) 建新环境并等内容就绪 (内容在各组件 Start 里建)
            Handles = EnvironmentBuilder.Build(p);
            float t2 = Time.realtimeSinceStartup;
            var tf = Handles.Terrain;
            while (tf != null && tf.PhysicsTerrain == null && Time.realtimeSinceStartup - t2 < TerrainTimeoutS)
                yield return null;
            if (tf != null && tf.PhysicsTerrain == null) LastError = "地形载入超时: " + p.TerrainRaw;
            LastTerrainS = Time.realtimeSinceStartup - t2;

            if (Handles.Sea != null)
            {
                float ts = Time.realtimeSinceStartup;
                while (Handles.Sea.Surface == null && Time.realtimeSinceStartup - ts < ContentTimeoutS) yield return null;
            }

            float t3 = Time.realtimeSinceStartup;
            yield return WaitHasChildren(Handles.Runway != null ? Handles.Runway.transform : null);
            LastRunwayS = Time.realtimeSinceStartup - t3;

            float t4 = Time.realtimeSinceStartup;
            if (p.TreeCount + p.RockCount + p.BushCount > 0)
                yield return WaitHasChildren(Handles.Scatter != null ? Handles.Scatter.transform : null);
            if (Handles.Wildlife != null) yield return WaitHasChildren(Handles.Wildlife.transform);
            LastScatterS = Time.realtimeSinceStartup - t4;

            // 4) 飞机/边界/风复位到新场地
            Current = p;
            AtmosphereModel.AltitudeOffsetM = p.PlatformMslM;
            ApplySun(p);
            if (VolumeApplier != null) VolumeApplier(p.VolumeProfileResource);
            ApplyToAircraft(p, applyWeather);
            SetFrozen(false);

            LastTotalS = Time.realtimeSinceStartup - t0;
            IsLoading = false;
            Target = null;
            Debug.Log(string.Format(
                "[SceneSwitcher] 已载入 {0}: 总 {1:F2}s (销毁 {2:F2} 地形 {3:F2} 跑道 {4:F2} 撒布/动物 {5:F2}){6}",
                p.DisplayName, LastTotalS, LastDestroyS, LastTerrainS, LastRunwayS, LastScatterS,
                LastError != null ? " 错误: " + LastError : ""));
            if (Loaded != null) Loaded(p);
        }

        private Transform _sun;
        private Light _sunLight;
        private Quaternion _sceneSunRotation;
        private Color _sceneSunColor;
        private bool _sunCaptured;

        /// <summary>按 profile 转动场景里的太阳 (首次调用时记下场景自带朝向, profile 未指定时恢复它)。</summary>
        private void ApplySun(SceneProfile p)
        {
            if (_sun == null)
            {
                var go = GameObject.Find("Directional Light");
                if (go == null) return;
                _sun = go.transform;
                _sunLight = go.GetComponent<Light>();
                _sceneSunRotation = _sun.rotation;
                _sceneSunColor = _sunLight != null ? _sunLight.color : Color.white;
                _sunCaptured = true;
            }
            if (!_sunCaptured) return;
            _sun.rotation = float.IsNaN(p.SunPitchDeg) || float.IsNaN(p.SunYawDeg)
                ? _sceneSunRotation
                : Quaternion.Euler(p.SunPitchDeg, p.SunYawDeg, 0f);
            if (_sunLight != null) _sunLight.color = p.SunColor ?? _sceneSunColor;
        }

        private static IEnumerator WaitHasChildren(Transform t)
        {
            if (t == null) yield break;
            float start = Time.realtimeSinceStartup;
            while (t.childCount == 0 && Time.realtimeSinceStartup - start < ContentTimeoutS)
                yield return null;
        }

        private static void SetFrozen(bool frozen)
        {
            foreach (var ac in FindObjectsOfType<AircraftController>()) ac.Frozen = frozen;
        }

        /// <summary>起点/起落架地形/边界/风 → 新场景, 然后复位 (相机吸附、遥测换文件等由 ResetToInitial 负责)。</summary>
        private void ApplyToAircraft(SceneProfile p, bool applyWeather)
        {
            var runway = Handles != null ? Handles.Runway : null;
            foreach (var ac in FindObjectsOfType<AircraftController>())
            {
                if (runway != null)
                {
                    Vector3 s = runway.RunwayStart;   // Unity (x 东, y 上, z 北) → NED (北, 东, 下)
                    // 出生深度按当前机型的实际轮底深度 (原硬编码 0.327 = 默认机轮深,
                    // 换浅轮机后切场景会把轮子埋进跑道 0.2m+, 弹簧架弹起导致俯仰失控)
                    float wheelDepth = 0.327f;
                    if (ac.Plane != null && ac.Plane.Gear != null && ac.Plane.Gear.Wheels.Count > 0)
                    {
                        wheelDepth = 0f;
                        foreach (var w in ac.Plane.Gear.Wheels)
                            wheelDepth = Mathf.Max(wheelDepth, (float)w.Position.Z);
                    }
                    ac.InitialPositionNed = new Vector3(s.z + p.StartAlongRunwayM, s.x, -(s.y + wheelDepth));
                }
                if (ac.Plane != null && ac.Plane.Gear != null)
                    ac.Plane.Gear.Terrain = TerrainRegistry.Current;

                var guard = ac.GetComponent<FlightGuard>();
                if (guard != null) guard.BoundaryRadius = p.BoundaryRadius;

                var wind = ac.GetComponent<WindSettings>();
                if (wind != null)
                {
                    if (applyWeather)
                        wind.SetWeather(p.WindSpeedMps, p.WindFromDeg, false, p.Turbulence, p.EnableTurbulence);
                    else   // 启动恢复: 载入默认 0 级 (无风 + 紊流关)
                        wind.SetWeather(0f, 0f, false, DrydenTurbulence.TurbulenceLevel.Light, false);
                }

                ac.ResetToInitial();
            }
        }
    }
}
