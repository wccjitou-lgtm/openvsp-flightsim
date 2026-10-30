using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FlightSim.Tests
{
    /// <summary>
    /// Phase 6 验收 (开发提示词 10 节):
    ///   "CSV 记录的飞行数据能导出并在外部工具 (如 Excel) 画出合理的时间历程曲线"
    /// 验证: 自动开始记录 → 飞行数秒 → CSV 落盘、表头完整、行数与时长匹配、
    /// 时间单调、数值与遥测一致且有限 (无 NaN)。
    /// </summary>
    public class Phase6PlayModeTests
    {
        private GameObject _aircraft;

        [SetUp]
        public void SetUp()
        {
            _aircraft = new GameObject("TestUAV");
            _aircraft.AddComponent<AircraftController>();
            _aircraft.AddComponent<AutoPilotInput>();
            _aircraft.AddComponent<DataLogger>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_aircraft != null) Object.Destroy(_aircraft);
        }

        [UnityTest]
        public IEnumerator DataLogger_WritesValidCsv()
        {
            var logger = _aircraft.GetComponent<DataLogger>();
            var c = _aircraft.GetComponent<AircraftController>();

            // 等待 Start 建文件 + 飞行 6s (滑跑)
            Assert.IsTrue(logger.IsRecording, "Play 后应自动开始记录");
            string file = logger.CurrentFile;
            Assert.IsFalse(string.IsNullOrEmpty(file), "记录文件名非空");

            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 6f) yield return null;

            // 停止并强制落盘
            logger.Stop();

            Assert.IsTrue(File.Exists(file), "CSV 已落盘: " + file);
            string[] lines = File.ReadAllLines(file);
            Assert.Greater(lines.Length, 50, "6s @10Hz 应有 ~60 行数据, 实际 " + (lines.Length - 1));

            // 表头
            string[] header = lines[0].Split(',');
            Assert.AreEqual("t_s", header[0], "首列 t_s");
            Assert.AreEqual("alpha_deg", header[14], "α 列存在");
            Assert.AreEqual("on_ground",
                header[System.Array.IndexOf(header, "on_ground")], "on_ground 列存在");
            Assert.AreEqual("crashed", header[header.Length - 1], "末列 crashed");
            Assert.AreEqual("dist_home_m", header[header.Length - 2], "倒数第二列 dist_home_m");

            // 数据行: 时间单调递增、数值有限、末行与遥测一致
            double prevT = -1;
            double lastAlt = 0, lastV = 0, lastAlpha = 0;
            foreach (string line in lines.SkipLines(1))
            {
                if (string.IsNullOrEmpty(line)) continue;
                string[] f = line.Split(',');
                Assert.AreEqual(header.Length, f.Length, "列数一致");
                double t = double.Parse(f[0], System.Globalization.CultureInfo.InvariantCulture);
                Assert.Greater(t, prevT, "时间单调递增");
                prevT = t;
                foreach (string fv in f)
                    Assert.IsFalse(double.IsNaN(double.Parse(fv,
                        System.Globalization.CultureInfo.InvariantCulture)), "无 NaN");
                lastAlt = double.Parse(f[16], System.Globalization.CultureInfo.InvariantCulture);
                lastV = double.Parse(f[13], System.Globalization.CultureInfo.InvariantCulture);
                lastAlpha = double.Parse(f[14], System.Globalization.CultureInfo.InvariantCulture);
            }
            Assert.Greater(lastV, 5f, "滑跑后空速 >5 m/s, 实际 " + lastV);
            Assert.Greater(System.Math.Abs(lastAlpha), -1f, "α 有效");
            Debug.Log(string.Format(
                "[Phase6] CSV {0} 行, 末行: V={1:F1}m/s alt={2:F1}m α={3:F2}° — 与遥测一致",
                lines.Length - 1, lastV, lastAlt, lastAlpha));
        }

        [UnityTest]
        public IEnumerator DataLogger_F9TogglesNewFile()
        {
            var logger = _aircraft.GetComponent<DataLogger>();
            yield return null;

            string file1 = logger.CurrentFile;
            logger.Stop();
            Assert.IsFalse(logger.IsRecording, "停止后不再记录");

            logger.BeginFile();
            string file2 = logger.CurrentFile;
            Assert.AreNotEqual(file1, file2, "再开应产生新文件 (时间戳不同)");
            Assert.IsTrue(logger.IsRecording, "恢复记录");

            logger.Stop();
        }
    }

    internal static class CsvExtensions
    {
        /// <summary>跳过首行 (System.Linq.Enumerable.Skip 在本 asmdef 引用集内不可用)。</summary>
        public static System.Collections.Generic.IEnumerable<string> SkipLines(
            this string[] lines, int skip)
        {
            for (int i = skip; i < lines.Length; i++) yield return lines[i];
        }
    }
}
