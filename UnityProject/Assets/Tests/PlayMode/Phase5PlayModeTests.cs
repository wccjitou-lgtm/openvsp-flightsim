using System.Collections;
using FlightDynamics;
using FlightDynamics.Math;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FlightSim.Tests
{
    /// <summary>
    /// Phase 5 验收 (开发提示词 10 节):
    ///   "三种相机模式可切换, HUD 数值与飞机真实状态一致"
    /// HUD 数据层 = AircraftController 遥测字段, 与核心库 State 逐一对照。
    /// </summary>
    public class Phase5PlayModeTests
    {
        private GameObject _aircraft;

        [SetUp]
        public void SetUp()
        {
            _aircraft = new GameObject("TestUAV");
            _aircraft.AddComponent<AircraftController>();
            _aircraft.AddComponent<AutoPilotInput>();
            _aircraft.AddComponent<FlightHUD>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_aircraft != null) Object.Destroy(_aircraft);
        }

        private AircraftController Controller
        { get { return _aircraft != null ? _aircraft.GetComponent<AircraftController>() : null; } }

        [UnityTest]
        public IEnumerator HUD_TelemetryMatchesRealState()
        {
            var c = Controller;
            Assert.NotNull(c);

            // 滑跑 6s, 状态离开初始值 (默认 CalmWind → WindAt 无副作用)
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 6f) yield return null;

            double airspeedExpected = c.State.Airspeed(Vec3.Zero);
            Assert.AreEqual((float)airspeedExpected, c.Airspeed, 0.02f, "空速 = 真空速 (无风)");
            Assert.AreEqual((float)c.State.Altitude, c.Altitude, 0.02f, "高度一致");
            Assert.AreEqual((float)(-c.State.VelocityNed.Z), c.VerticalSpeed, 0.02f,
                "垂直速度 = -V_ned.z (正=上升)");
            Assert.Less(c.WindSpeed, 0.01f, "无风场景风速应为 0");

            double[] e = c.State.Euler321();
            Assert.AreEqual((float)(e[1] * Mathd.Rad2Deg), c.PitchDeg, 0.02f, "俯仰一致");
            Assert.AreEqual((float)(e[0] * Mathd.Rad2Deg), c.BankDeg, 0.02f, "滚转一致");
            float hdgExpected = ((float)(e[2] * Mathd.Rad2Deg) + 360f) % 360f;
            Assert.AreEqual(hdgExpected, ((c.HeadingDeg + 360f) % 360f), 0.5f, "航向一致");

            Assert.Greater(c.GLoad, 0.5f, "过载 > 0.5");
            Assert.Less(c.GLoad, 3f, "过载 < 3 (滑跑/早期爬升合理范围)");
            Assert.AreEqual(1f, c.Throttle, 0.01f, "滑跑阶段自动驾驶满油门");
            Assert.Less(Mathf.Abs(c.ElevatorDeg), 40f, "舵面遥测有界");
            Assert.NotNull(c.Surfaces, "HUD 可读取共享舵面限位");
            Debug.Log(string.Format(
                "[Phase5] HUD 对照: IAS {0:F1} ALT {1:F1} VS {2:F2} HDG {3:F0} G {4:F2} 油门 {5:F0}%",
                c.Airspeed, c.Altitude, c.VerticalSpeed, c.HeadingDeg, c.GLoad, c.Throttle * 100f));
        }

        [UnityTest]
        public IEnumerator CameraModes_CycleChaseCockpitFree()
        {
            var camGo = new GameObject("P5Cam");
            camGo.AddComponent<Camera>();
            var rig = camGo.AddComponent<ChaseCamera>();
            rig.Target = _aircraft.transform;
            try
            {
                yield return null;   // 追尾一帧 (首帧吸附)

                Assert.AreEqual(ChaseCamera.CameraMode.Chase, rig.Mode, "初始为追尾");

                rig.CycleMode();
                Assert.AreEqual(ChaseCamera.CameraMode.Cockpit, rig.Mode, "V → 座舱");
                yield return null;
                Vector3 expectedPos = _aircraft.transform.position +
                    _aircraft.transform.rotation * rig.CockpitOffset;
                Assert.Less(Vector3.Distance(expectedPos, camGo.transform.position), 0.01f,
                    "座舱相机位置 = 机身 + 座舱偏移");
                Assert.Greater(Quaternion.Dot(camGo.transform.rotation,
                    _aircraft.transform.rotation), 0.999f, "座舱相机随飞机姿态");

                rig.CycleMode();
                Assert.AreEqual(ChaseCamera.CameraMode.Free, rig.Mode, "V → 自由观察");
                yield return null;
                Vector3 off = camGo.transform.position - _aircraft.transform.position;
                Assert.AreEqual(rig.FreeDistance, off.magnitude, 0.5f, "自由观察轨道半径 = FreeDistance");
                Vector3 toAircraft =
                    (_aircraft.transform.position - camGo.transform.position).normalized;
                Assert.Greater(Vector3.Dot(camGo.transform.forward, toAircraft), 0.99f,
                    "自由观察始终看向飞机");

                rig.CycleMode();
                Assert.AreEqual(ChaseCamera.CameraMode.Chase, rig.Mode, "V → 回到追尾 (三模式循环)");
            }
            finally
            {
                Object.Destroy(camGo);
            }
        }
    }
}
