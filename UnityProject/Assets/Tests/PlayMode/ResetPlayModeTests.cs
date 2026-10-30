using System.Collections;
using FlightDynamics.Math;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FlightSim.Tests
{
    /// <summary>
    /// "Backspace 复位到跑道重新起飞" 功能验收:
    ///   飞行爬升离开跑道 → 复位 → 状态回到跑道起点 (位置/速度/累积输入全清)
    ///   → 自动驾驶重新经历滑跑+抬轮爬升 (不是直接巡航) → 遥测开新 CSV。
    /// </summary>
    public class ResetPlayModeTests
    {
        private GameObject _aircraft;

        [SetUp]
        public void SetUp()
        {
            _aircraft = new GameObject("TestUAV");
            _aircraft.AddComponent<AircraftController>();
            _aircraft.AddComponent<AutoPilotInput>();
            _aircraft.AddComponent<DataLogger>();
            _aircraft.AddComponent<ModePilot>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_aircraft != null) Object.Destroy(_aircraft);
        }

        private AircraftController Controller
        { get { return _aircraft != null ? _aircraft.GetComponent<AircraftController>() : null; } }

        [UnityTest]
        public IEnumerator ResetToInitial_ReturnsToRunwayAndTakesOffAgain()
        {
            var c = Controller;
            var logger = _aircraft.GetComponent<DataLogger>();
            string file1 = logger.CurrentFile;

            // ---- 第一段: 滑跑+爬升到明显离开跑道 ----
            float maxAlt1 = 0;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 16f)
            {
                yield return null;
                maxAlt1 = Mathf.Max(maxAlt1, c.Altitude);
            }
            Assert.Greater(c.Altitude, 3f, "16s 后应已爬升离地 (基准 ~15m+), 实际 " + c.Altitude);
            Assert.Greater(c.State.Position.X, 30f, "应已向北滑跑/飞行一段");
            float altBeforeReset = c.Altitude;
            float throttleBeforeReset = c.Throttle;

            // ---- 复位 ----
            c.ResetToInitial();

            // State 立即生效 (遥测字段要等下一个 FixedUpdate 刷新, 先查 State)
            Vec3 p = c.State.Position;
            Assert.Less(Mathf.Abs((float)p.X), 0.05f, "北向归零");
            Assert.Less(Mathf.Abs((float)p.Y), 0.05f, "东向归零");
            Assert.Less(Mathf.Abs((float)(p.Z + 0.327)), 0.05f, "垂向回跑道起点");
            Assert.Less((float)c.State.VelocityBody.X, 0.5f, "速度回出生值 (0)");
            Assert.AreNotEqual(file1, logger.CurrentFile, "复位后遥测开新 CSV 文件");

            // 轮询等待遥测刷新 (高速帧率下某帧可能不含物理步, 单帧 yield 不保证)
            double waited = 0;
            float tPoll = Time.realtimeSinceStartup;
            while (Mathf.Abs(c.Altitude - 0.327f) > 0.05f && waited < 1.0)
            {
                yield return null;
                waited += Time.realtimeSinceStartup - tPoll;
                tPoll = Time.realtimeSinceStartup;
            }
            Assert.Less(Mathf.Abs(c.Altitude - 0.327f), 0.05f, "高度回跑道起点静平衡");

            // ---- 第二段: 应重新经历滑跑 → 抬轮 → 爬升 ----
            float minAltEarly = float.MaxValue;
            t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 14f)
            {
                yield return null;
                if (Time.realtimeSinceStartup - t0 < 6f)
                    minAltEarly = Mathf.Min(minAltEarly, c.Altitude);
            }
            // 前 6s 应贴地滑跑 (自动驾驶 _climbed 已清, 不会直接巡航)
            Assert.Less(minAltEarly, 1.5f, "复位后前段应重新滑跑贴地, 实际最低 " + minAltEarly);
            Assert.Greater(c.Altitude, 3f, "14s 后再次爬升离地, 实际 " + c.Altitude);
            Assert.IsFalse(float.IsNaN(c.Altitude) || float.IsNaN(c.BankDeg), "复位后遥测无 NaN");
            Debug.Log(string.Format(
                "[Reset] 第一段峰高 {0:F1}m (复位时 {1:F1}m) → 复位 → 第二段重新滑跑后 {2:F1}m ✓",
                maxAlt1, altBeforeReset, c.Altitude));
        }

        [UnityTest]
        public IEnumerator Reset_DisengagesModePilot()
        {
            // 复位联动验收: 自驾模式接管中触发复位 → ModePilot 解除、
            // 交还手动输入, 之后自动驾驶重新滑跑起飞 (不是自驾残留开车)。
            var c = Controller;
            var pilot = _aircraft.GetComponent<ModePilot>();

            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 12f) yield return null;
            Assert.Greater(c.Altitude, 3f, "12s 应已爬升离地, 实际 " + c.Altitude);

            pilot.Engage(FlightMode.Loiter);
            Assert.AreEqual(FlightMode.Loiter, pilot.Mode, "模式接管");
            Assert.IsNotNull(c.OverrideInput, "接管指针生效");

            c.ResetToInitial();
            Assert.AreEqual(FlightMode.Manual, pilot.Mode, "复位应解除自动驾驶模式");
            Assert.IsNull(c.OverrideInput, "复位应交还手动输入");

            // 解除后自动驾驶恢复开车: 复位回起点重新滑跑 → 再次爬升
            float minAltEarly = float.MaxValue;
            t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 14f)
            {
                yield return null;
                if (Time.realtimeSinceStartup - t0 < 6f)
                    minAltEarly = Mathf.Min(minAltEarly, c.Altitude);
            }
            Assert.Less(minAltEarly, 1.5f, "复位后前段应重新滑跑贴地, 实际最低 " + minAltEarly);
            Assert.Greater(c.Altitude, 3f, "14s 后再次爬升离地, 实际 " + c.Altitude);
            Debug.Log(string.Format(
                "[Reset] Loiter 接管 → 复位 → 解除自驾 → 重新滑跑后 {0:F1}m ✓", c.Altitude));
        }
    }
}
