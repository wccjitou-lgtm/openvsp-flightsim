using System;
using System.Globalization;
using System.IO;
using System.Text;
using FlightDynamics;
using FlightDynamics.Math;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FlightSim
{
    /// <summary>
    /// 飞行数据记录器 (Phase 6, 开发提示词 8 节): 把飞行状态时间序列写入 CSV,
    /// 用于试飞后与 OpenVSP/VSPAERO 预测数据对比校验 (Excel/Python 均可直接打开)。
    ///
    /// 列: t, 位置 NED, 姿态欧拉 (φθψ), 体轴速度, NED 速度, 空速, α/β, 高度,
    ///     垂直速度, 过载, 操纵面 (升降舵/副翼/方向舵偏角 + 油门), 地面状态。
    /// 行: 10 Hz (每 0.1 s 一行, 与物理 120 Hz 解耦, 曲线绘制足够)。
    ///
    /// 行为: Play 即自动开始记录; F9 停止/再开 (新文件); 停止 Play 或组件销毁时
    /// 自动 flush 关闭。文件落在本仓库 Logs/Telemetry/ 下, 文件名带时间戳。
    /// </summary>
    [RequireComponent(typeof(AircraftController))]
    public class DataLogger : MonoBehaviour
    {
        [Tooltip("采样间隔 s (默认 0.1 = 10 Hz)")]
        public float SampleInterval = 0.1f;

        public static readonly string[] Header =
        {
            "t_s", "pos_N_m", "pos_E_m", "pos_D_m",
            "roll_deg", "pitch_deg", "heading_deg",
            "u_mps", "v_mps", "w_mps", "vN_mps", "vE_mps", "vD_mps",
            "V_mps", "alpha_deg", "beta_deg", "alt_m", "vs_ms", "G",
            "elev_deg", "ail_deg", "rud_deg", "throttle", "on_ground",
            "dist_home_m", "crashed",
        };

        private AircraftController _c;
        private StreamWriter _writer;
        private string _currentFile;
        private double _tSinceSample;
        private bool _paused;
        private int _rows;

        public string CurrentFile { get { return _currentFile; } }
        public bool IsRecording { get { return _writer != null && !_paused; } }
        public int RowCount { get { return _rows; } }

        private void Awake()
        {
            _c = GetComponent<AircraftController>();
        }

        private void Start()
        {
            BeginFile();
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (WorkshopUI.IsOpen) return;   // R13 (F9 不切换记录)
            if (kb != null && kb.f9Key.wasPressedThisFrame)
            {
                if (IsRecording) Stop();
                else BeginFile();   // 再开 → 新文件
            }
        }

        private void FixedUpdate()
        {
            if (!IsRecording) return;
            _tSinceSample += Time.fixedDeltaTime;
            if (_tSinceSample < SampleInterval) return;
            _tSinceSample = 0;
            WriteRow();
        }

        /// <summary>开始新记录文件。</summary>
        public void BeginFile()
        {
            CloseFile();
            string dir = Path.Combine(Application.dataPath, "..", "Logs", "Telemetry");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
            string path = Path.Combine(dir, "flight_" + stamp + ".csv");
            int n = 1;
            while (File.Exists(path))   // 同毫秒碰撞兜底
                path = Path.Combine(dir, "flight_" + stamp + "_" + (n++) + ".csv");
            _currentFile = path;
            _writer = new StreamWriter(_currentFile, false, new UTF8Encoding(false));
            _writer.WriteLine(string.Join(",", Header));
            _rows = 0;
            _paused = false;
            _tSinceSample = SampleInterval;   // 首帧立即采样
            Debug.Log("[DataLogger] 开始记录: " + _currentFile);
        }

        /// <summary>停止记录并关闭文件。</summary>
        public void Stop()
        {
            _paused = true;
            CloseFile();
            Debug.Log("[DataLogger] 停止记录: " + _rows + " 行 → " + _currentFile);
        }

        private void WriteRow()
        {
            var s = _c.State;
            double t = Time.timeAsDouble;
            Vec3 wind = _c.LastWindNed;
            double[] e = s.Euler321();
            Vec3 vNed = s.VelocityNed;

            var f = new StringBuilder(256);
            Append(f, t);
            Append(f, s.Position.X); Append(f, s.Position.Y); Append(f, s.Position.Z);
            Append(f, e[0] * Mathd.Rad2Deg); Append(f, e[1] * Mathd.Rad2Deg);
            Append(f, e[2] * Mathd.Rad2Deg);
            Append(f, s.VelocityBody.X); Append(f, s.VelocityBody.Y); Append(f, s.VelocityBody.Z);
            Append(f, vNed.X); Append(f, vNed.Y); Append(f, vNed.Z);
            Append(f, s.Airspeed(wind));
            Append(f, s.Alpha(wind) * Mathd.Rad2Deg);
            Append(f, s.Beta(wind) * Mathd.Rad2Deg);
            Append(f, s.Altitude);
            Append(f, _c.VerticalSpeed);
            Append(f, _c.GLoad);
            Append(f, _c.ElevatorDeg); Append(f, _c.AileronDeg); Append(f, _c.RudderDeg);
            Append(f, _c.Throttle);
            f.Append(_c.OnGround ? "1" : "0").Append(',');
            Append(f, DistHome(s));
            f.Append(_c.IsCrashed ? "1" : "0").Append('\n');

            _writer.Write(f.ToString());
            _rows++;
        }

        /// <summary>距起飞点水平距离 (NED 水平分量)。</summary>
        private double DistHome(AircraftState s)
        {
            double dx = s.Position.X - _c.InitialPositionNed.x;
            double dy = s.Position.Y - _c.InitialPositionNed.y;
            return System.Math.Sqrt(dx * dx + dy * dy);
        }

        private static void Append(StringBuilder sb, double v)
        {
            sb.Append(v.ToString("G9", CultureInfo.InvariantCulture)).Append(',');
        }

        private void CloseFile()
        {
            if (_writer != null)
            {
                _writer.Flush();
                _writer.Dispose();
                _writer = null;
            }
        }

        private void OnDestroy()
        {
            CloseFile();   // 停止 Play 时确保落盘
        }
    }
}
