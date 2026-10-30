using UnityEngine;
using UnityEngine.Profiling;

namespace FlightSim
{
    /// <summary>
    /// 性能/抖动探针: 仅当命令行带 -fpsprobe 时激活, 正常运行完全不参与。
    ///   -probeseconds N  测量时长 (默认 60 s)
    ///   -keepvsync       保持游戏自身的 vsync/帧率设置 (默认临时关 vsync 测真实渲染能力)
    ///   -autofly         自动起飞→爬升→盘旋 (模拟"飞一会"的场景, 配合 -scene 使用)
    /// 每 5 s 窗口输出: fps、最差帧、>20ms/>33ms 帧数 (vsync 下 60↔30 fps 跳变的证据)、
    /// 相机加速度噪声 (m/s², 越大越抖)、相机相对飞机的位置抖动 (m)、航速/高度、内存。
    /// 打包版同样可用 (2026-10-11/12 海岛抖动诊断)。
    /// </summary>
    [DefaultExecutionOrder(32000)]
    public class FpsProbe : MonoBehaviour
    {
        private const float WindowSeconds = 5f;

        private float _totalSeconds = 60f;
        private bool _keepVsync, _autoFly, _seaStart, _cycle, _hover;
        private int _resW, _resH, _quality = -1; private bool _resFull, _resApplied; private float _lastCycle; private int _cycleIdx;
        private float _runStart, _windowStart;
        private int _windowFrames, _totalFrames, _slow20, _slow33, _totalSlow20, _totalSlow33;
        private float _windowWorstMs, _worstMs;
        private double _lastRt;
        private int _oldVsync, _oldTargetFps;

        // 相机运动抖动度
        private Vector3 _cam1, _cam2, _rel1, _rel2; private int _have;   // 前 1/2 帧的相机位置与相对位置
        private double _camWobSq, _relWobSq; private int _wobN;
        private double _speedSum; private int _speedN;
        // 机体自身 Transform 的抖动 (渲染插值是否健康) 与插值相位误差
        private Vector3 _ac1, _ac2; private int _acHave; private double _acWobSq; private int _acWobN;
        private double _phaseErrSum, _phaseErrMax; private int _phaseN;

        // 自动飞行
        private AircraftController _ac;
        private float _flyStart = -1f;
        private bool _loitering;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            var args = System.Environment.GetCommandLineArgs();
            bool on = false; float secs = 60f; bool keep = false, fly = false, sea = false, cyc = false, hov = false; int rw = 0, rh = 0; bool rf = false; int ql = -1;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-fpsprobe") on = true;
                else if (args[i] == "-keepvsync") keep = true;
                else if (args[i] == "-autofly") fly = true;
                else if (args[i] == "-seastart") sea = true;
                else if (args[i] == "-cycle") cyc = true;
                else if (args[i] == "-hover") hov = true;
                else if (args[i] == "-proberes" && i + 2 < args.Length) { int.TryParse(args[i + 1], out rw); int.TryParse(args[i + 2], out rh); }
                else if (args[i] == "-probefull") rf = true;
                else if (args[i] == "-probequality" && i + 1 < args.Length) int.TryParse(args[i + 1], out ql);
                else if (args[i] == "-probeseconds" && i + 1 < args.Length) float.TryParse(args[i + 1], out secs);
            }
            if (!on) return;
            var go = new GameObject("FpsProbe");
            var p = go.AddComponent<FpsProbe>();
            p._totalSeconds = secs; p._keepVsync = keep; p._autoFly = fly; p._seaStart = sea; p._cycle = cyc; p._hover = hov; p._resW = rw; p._resH = rh; p._resFull = rf; p._quality = ql;
        }

        private void Start()
        {
            _runStart = _windowStart = Time.unscaledTime;
            _lastRt = Time.realtimeSinceStartupAsDouble;
            _oldVsync = QualitySettings.vSyncCount;
            _oldTargetFps = Application.targetFrameRate;
            if (!_keepVsync)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 300;
            }
            Debug.Log("[FpsProbe] GPU: " + SystemInfo.graphicsDeviceName + " | API: " + SystemInfo.graphicsDeviceType + " | 显存 " + SystemInfo.graphicsMemorySize + "MB");
            Debug.Log(string.Format("[FpsProbe] 开始: {0}x{1} fs={2} vsync={3} targetFps={4} 画质={5} 时长={6}s autofly={7}",
                Screen.width, Screen.height, Screen.fullScreen, QualitySettings.vSyncCount,
                Application.targetFrameRate, QualitySettings.names[QualitySettings.GetQualityLevel()],
                _totalSeconds, _autoFly));
        }

        private void Update()
        {
            if (_quality >= 0 && Time.unscaledTime - _runStart > 3f)
            {
                QualitySettings.SetQualityLevel(_quality, true);
                _quality = -1;
                Debug.Log("[FpsProbe] 画质档 → " + QualitySettings.names[QualitySettings.GetQualityLevel()]);
            }
            if (_resW > 0 && !_resApplied && Time.unscaledTime - _runStart > 4f)
            {
                _resApplied = true;
                Screen.SetResolution(_resW, _resH, _resFull ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed);
            }
            if (_hover) DriveHover();
            else if (_autoFly) DriveFlight();
            if (_cycle) CycleScenes();
        }

        private void CycleScenes()
        {
            var sw = FindObjectOfType<SceneSwitcher>();
            if (sw == null || sw.IsLoading || Time.unscaledTime - _lastCycle < 15f) return;
            _lastCycle = Time.unscaledTime;
            string[] ids = { "island", "forest", "alpine" };
            _cycleIdx = (_cycleIdx + 1) % ids.Length;
            sw.SwitchTo(ids[_cycleIdx], true);
            Debug.Log("[FpsProbe] 循环切换场景 → " + ids[_cycleIdx]);
        }

        private void DriveHover()
        {
            if (_ac == null) _ac = FindObjectOfType<AircraftController>();
            if (_ac == null || _ac.State == null || _ac.Frozen) return;
            var sw = FindObjectOfType<SceneSwitcher>();
            if (sw != null && sw.IsLoading) return;
            if (_flyStart >= 0f) return;
            _flyStart = Time.unscaledTime;
            // 海面上方 ~6 m (海面比跑道平台低 9 m), 冻结物理 → 相机固定, 逐帧负载可比
            _ac.InitialPositionNed = new Vector3(400f, 900f, 3f);
            _ac.ResetToInitial();
            _ac.Frozen = true;
            Debug.Log("[FpsProbe] 悬停: 海面上方低空, 物理冻结");
        }

        private void DriveFlight()
        {
            if (_ac == null) _ac = FindObjectOfType<AircraftController>();
            if (_ac == null || _ac.State == null || _ac.Frozen) return;
            var sw = FindObjectOfType<SceneSwitcher>();
            if (sw != null && sw.IsLoading) return;
            if (_flyStart < 0f)
            {
                _flyStart = Time.unscaledTime;
                if (_seaStart)
                {
                    // 海岛: 跑道东侧 ~900 m 外是开阔海面 (NED: 北 400, 东 900, 上 60 m)
                    _ac.InitialPositionNed = new Vector3(400f, 900f, -60f);
                    _ac.ResetToInitial();
                    Debug.Log("[FpsProbe] 海面上空起飞点: N400 E900 高60m");
                }
                var ap = _ac.GetComponent<AutoPilotInput>();
                if (ap == null) ap = _ac.gameObject.AddComponent<AutoPilotInput>();
                _ac.OverrideInput = ap;
                Debug.Log("[FpsProbe] 自动飞行: 起飞爬升");
                return;
            }
            if (!_loitering && Time.unscaledTime - _flyStart > 30f)
            {
                var pilot = _ac.GetComponent<ModePilot>();
                if (pilot != null) { pilot.Engage(FlightMode.Loiter); _loitering = true; Debug.Log("[FpsProbe] 自动飞行: 切入盘旋"); }
            }
        }

        private void LateUpdate()
        {
            _windowFrames++; _totalFrames++;
            double rt = Time.realtimeSinceStartupAsDouble;
            double dt = rt - _lastRt; _lastRt = rt;
            float dtMs = (float)(dt * 1000.0);
            if (dtMs > _windowWorstMs) _windowWorstMs = dtMs;
            if (dtMs > _worstMs) _worstMs = dtMs;
            if (dtMs > 20f && dtMs < 1000f) { _slow20++; _totalSlow20++; }
            if (dtMs > 33f && dtMs < 1000f) { _slow33++; _totalSlow33++; }

            // 机体抖动度 + 插值相位误差
            if (_ac != null && dt > 0.0005 && dt < 0.2)
            {
                Vector3 ap = _ac.transform.position;
                if (_acHave >= 2) { _acWobSq += (ap - 2f * _ac1 + _ac2).sqrMagnitude; _acWobN++; }
                _ac2 = _ac1; _ac1 = ap; if (_acHave < 2) _acHave++;
                double fdt = Time.fixedDeltaTime;
                double aCode = (Time.timeAsDouble % fdt) / fdt;            // AircraftController 实际使用的插值系数
                double aTrue = (Time.timeAsDouble - Time.fixedTimeAsDouble) / fdt;   // 物理步实际走到的位置
                double d = aCode - aTrue; d -= System.Math.Round(d);       // 折叠到 [-0.5, 0.5]
                _phaseErrSum += d; _phaseN++;
                if (System.Math.Abs(d) > _phaseErrMax) _phaseErrMax = System.Math.Abs(d);
            }
            else _acHave = 0;

            // 相机抖动度: 位置二阶差分 (米), 不含时间除法, 不受 vsync 帧间隔抖动污染;
            // 平滑运动≈0, 逐帧来回抖 = 大。camWob=世界位置, relWob=相机相对飞机位置
            var cam = Camera.main;
            if (cam != null && dt > 0.0005 && dt < 0.2)
            {
                Vector3 p = cam.transform.position;
                Vector3 rel = _ac != null ? p - _ac.transform.position : Vector3.zero;
                if (_have >= 2)
                {
                    _camWobSq += (p - 2f * _cam1 + _cam2).sqrMagnitude;
                    _relWobSq += (rel - 2f * _rel1 + _rel2).sqrMagnitude;
                    _wobN++;
                }
                if (_have >= 1) { _speedSum += (p - _cam1).magnitude / dt; _speedN++; }
                _cam2 = _cam1; _cam1 = p; _rel2 = _rel1; _rel1 = rel;
                if (_have < 2) _have++;
            }
            else _have = 0;

            float t = Time.unscaledTime;
            if (t - _windowStart >= WindowSeconds)
            {
                double camWob = _wobN > 0 ? System.Math.Sqrt(_camWobSq / _wobN) * 1000.0 : 0;
                double relWob = _wobN > 0 ? System.Math.Sqrt(_relWobSq / _wobN) * 1000.0 : 0;
                double acWob = _acWobN > 0 ? System.Math.Sqrt(_acWobSq / _acWobN) * 1000.0 : 0;
                string ph = string.Format(" | 机体抖动={0:F1}mm 插值相位误差 均值={1:F3} 最大={2:F3}",
                    acWob, _phaseN > 0 ? _phaseErrSum / _phaseN : 0, _phaseErrMax);
                string fl = "";
                if (_ac != null && _ac.State != null)
                    fl = string.Format(" | V={0:F1}m/s alt={1:F0}m{2}", _ac.State.Airspeed(FlightDynamics.Math.Vec3.Zero), _ac.State.Altitude, _ac.IsCrashed ? " 坠毁" : "");
                Debug.Log(string.Format(
                    "[FpsProbe] {11}x{12} {0:F0}s fps={1:F1} 最差={2:F1}ms >20ms:{3} >33ms:{4} | 相机抖动={5:F1}mm 相对抖动={6:F1}mm 速度={7:F1}{8} | 托管={9:F0}MB Unity={10:F0}MB",
                    t - _runStart, _windowFrames / (t - _windowStart), _windowWorstMs, _slow20, _slow33,
                    camWob, relWob, _speedN > 0 ? _speedSum / _speedN : 0, ph + fl,
                    System.GC.GetTotalMemory(false) / 1048576.0, Profiler.GetTotalAllocatedMemoryLong() / 1048576.0, Screen.width, Screen.height));
                _windowFrames = 0; _windowWorstMs = 0f; _slow20 = 0; _slow33 = 0; _windowStart = t;
                _camWobSq = 0; _relWobSq = 0; _wobN = 0; _speedSum = 0; _speedN = 0; _acWobSq = 0; _acWobN = 0; _phaseErrSum = 0; _phaseErrMax = 0; _phaseN = 0;
            }

            if (t - _runStart >= _totalSeconds)
            {
                if (!_keepVsync) { QualitySettings.vSyncCount = _oldVsync; Application.targetFrameRate = _oldTargetFps; }
                Debug.Log(string.Format("[FpsProbe] 汇总: {0:F0}s / {1} 帧 = 平均 {2:F1} FPS, 最差帧 {3:F1} ms, >20ms {4} 帧, >33ms {5} 帧 @ {6}x{7}",
                    t - _runStart, _totalFrames, _totalFrames / (t - _runStart), _worstMs,
                    _totalSlow20, _totalSlow33, Screen.width, Screen.height));
#if UNITY_EDITOR
                UnityEditor.EditorApplication.Exit(0);
#else
                Application.Quit(0);
#endif
            }
        }
    }
}
