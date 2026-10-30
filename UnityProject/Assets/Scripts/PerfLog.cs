using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 常驻轻量帧率日志: 每 10 s 往 Player.log 写一行 (平均帧率 / 最差帧 / >20ms 与 >33ms 帧数 / 场景 / 分辨率),
    /// 用于现场排查"飞一会后画面抽动"这类随时间出现的问题 (配合 Tools/监控GPU.bat 的温度/频率日志按时间对照)。
    /// 开销可忽略 (每帧两次加法); -fpsprobe 激活时让位给 FpsProbe, 不重复输出。
    /// </summary>
    public class PerfLog : MonoBehaviour
    {
        private const float WindowSeconds = 10f;
        private float _windowStart;
        private double _lastRt;
        private int _frames, _slow20, _slow33;
        private float _worstMs;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            foreach (var a in System.Environment.GetCommandLineArgs())
                if (a == "-fpsprobe") return;
            var go = new GameObject("PerfLog");
            DontDestroyOnLoad(go);
            go.AddComponent<PerfLog>();
        }

        private void Start()
        {
            _windowStart = Time.unscaledTime;
            _lastRt = Time.realtimeSinceStartupAsDouble;
        }

        private void Update()
        {
            double rt = Time.realtimeSinceStartupAsDouble;
            float ms = (float)((rt - _lastRt) * 1000.0);
            _lastRt = rt;
            _frames++;
            if (ms < 1000f)   // 排除场景载入的长帧
            {
                if (ms > _worstMs) _worstMs = ms;
                if (ms > 20f) _slow20++;
                if (ms > 33f) _slow33++;
            }

            float t = Time.unscaledTime;
            if (t - _windowStart < WindowSeconds) return;
            var sw = FindObjectOfType<SceneSwitcher>();
            Debug.Log(string.Format("[PerfLog] {0:HH:mm:ss} {1} {2}x{3} fps={4:F1} 最差={5:F1}ms >20ms:{6} >33ms:{7} vsync={8}",
                System.DateTime.Now, sw != null && sw.Current != null ? sw.Current.Id : "?",
                Screen.width, Screen.height, _frames / (t - _windowStart), _worstMs, _slow20, _slow33,
                QualitySettings.vSyncCount));
            _windowStart = t; _frames = 0; _slow20 = 0; _slow33 = 0; _worstMs = 0f;
        }
    }
}
