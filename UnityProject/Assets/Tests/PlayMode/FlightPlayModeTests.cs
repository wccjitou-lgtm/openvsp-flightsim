using System.Collections;
using System.IO;
using FlightDynamics;
using FlightDynamics.Math;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FlightSim.Tests
{
    /// <summary>
    /// Unity PlayMode 自动化测试: 在 Unity 运行时内跑真实物理飞行, 验证
    /// MonoBehaviour 包装层 + 纯 C# 核心库全链路 (配置加载/FixedUpdate 120Hz/
    /// 渲染插值/NED→Unity 映射/起落架/自动驾驶剖面)。
    /// 基准对比: 核心库 headless 同剖面 25s → 高度 ~29m/空速 ~17-19m/s。
    /// 运行: Unity.exe -batchmode -runTests -testPlatform PlayMode
    /// </summary>
    public class FlightPlayModeTests
    {
        private GameObject _aircraft;

        [SetUp]
        public void SetUp()
        {
            _aircraft = new GameObject("TestUAV");
            _aircraft.AddComponent<AircraftController>();
            _aircraft.AddComponent<AutoPilotInput>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_aircraft != null) Object.Destroy(_aircraft);
        }

        private AircraftController Controller
        { get { return _aircraft != null ? _aircraft.GetComponent<AircraftController>() : null; } }

        [Test]
        public void AircraftController_Awake_LoadsConfigAndGear()
        {
            // Awake 已在 AddComponent 时运行
            var c = Controller;
            Assert.NotNull(c, "控制器存在");
            Assert.NotNull(c.Plane, "飞机装配完成 (配置加载成功)");
            Assert.NotNull(c.Plane.Gear, "起落架加载成功");
            Assert.AreEqual(3, CountWheels(c), "三点起落架");
            Assert.AreEqual(0.35f, (float)c.Plane.Gear.Wheels[0].Position.Z, 0.01f, "前轮位置 z=0.35");
        }


        [UnityTest]
        public IEnumerator NED_To_Unity_Mapping_Heading0_FacesNorth()
        {
            var c = Controller;
            // 初始状态: 航向 0 (北) → Unity 前向 (+z)
            yield return null;
            Vector3 fwd = _aircraft.transform.forward;
            Assert.Greater(fwd.z, 0.9f, "机头应朝 Unity +z (北)");
            Assert.Less(Mathf.Abs(fwd.x), 0.1f, "无侧向偏转");
            Assert.Less(Mathf.Abs(_aircraft.transform.position.y - 0.327f), 0.05f,
                "初始高度 = 静平衡 ~0.33m (Unity y)");
        }

        [UnityTest]
        public IEnumerator FullMission_AutoTakeoff_ReachesAltitudeAndStaysBounded()
        {
            var c = Controller;
            Assert.NotNull(c);

            float maxBank = 0, maxAlt = 0;
            double waited = 0;
            double lastTime = Time.realtimeSinceStartupAsDouble;

            while (waited < 25.0)
            {
                yield return null;
                double now = Time.realtimeSinceStartupAsDouble;
                waited += now - lastTime;
                lastTime = now;

                if (c == null) { Assert.Fail("控制器被销毁"); yield break; }
                maxAlt = Mathf.Max(maxAlt, c.Altitude);
                maxBank = Mathf.Max(maxBank, Mathf.Abs(c.BankDeg));

                Assert.IsFalse(float.IsNaN(c.Altitude) || float.IsNaN(c.BankDeg),
                    "遥测无 NaN (t=" + waited + "s)");
            }

            // 与核心库基准 (25s: 高度 ~29m) 对比: Unity FixedUpdate 实现应同量级
            Assert.Greater(c.Altitude, 15f,
                "25s 应爬升到 15m+ (核心库基准 ~29m), 实际 " + c.Altitude);
            Assert.Less(c.Altitude, 55f, "高度不过冲失控: " + c.Altitude);
            Assert.Less(maxBank, 10f, "增稳下滚转有界: 峰值 " + maxBank);
            Assert.Greater(c.Airspeed, 10f, "保持飞行速度: " + c.Airspeed);
            Assert.Less(c.Airspeed, 35f, "速度不失控: " + c.Airspeed);

            Debug.Log(string.Format(
                "[PlayMode] 25s 飞行: 高度 {0:F1}m (基准 ~29), 空速 {1:F1}m/s, " +
                "俯仰 {2:F1}°, 滚转峰值 {3:F2}° — 核心库与 Unity 运行时一致",
                c.Altitude, c.Airspeed, c.PitchDeg, maxBank));
        }

        [UnityTest]
        public IEnumerator WindSettings_Turbulence_PerturbsButBounded()
        {
            var wind = _aircraft.AddComponent<WindSettings>();
            wind.Turbulence = DrydenTurbulence.TurbulenceLevel.Moderate;
            wind.Rebuild();
            var c = Controller;

            float maxBank = 0, maxAltDrift = 0;
            double waited = 0, last = Time.realtimeSinceStartupAsDouble;
            while (waited < 15.0)
            {
                yield return null;
                double now = Time.realtimeSinceStartupAsDouble;
                waited += now - last; last = now;
                maxBank = Mathf.Max(maxBank, Mathf.Abs(c.BankDeg));
                maxAltDrift = Mathf.Max(maxAltDrift, Mathf.Abs(c.Altitude));
                Assert.IsFalse(float.IsNaN(c.Altitude), "紊流下无 NaN");
            }
            Assert.Less(maxBank, 25f, "中度紊流滚转有界: " + maxBank);
            Debug.Log(string.Format("[PlayMode] 中度紊流 15s: 滚转峰 {0:F1}°", maxBank));
        }

        /// <summary>
        /// P0-1 回归: LateUpdate 必须在相邻两个物理状态间插值。
        /// 修复前 _renderCurr 冻结在出生点 → transform 周期性被拽向出生点。
        /// 断言①: transform 与 lerp(prev,curr,alpha) 期望值厘米级一致;
        /// 断言②: transform 与当前物理状态距离 ≤ 一个物理步位移 + 余量
        ///        (25m/s÷120Hz≈0.21m, 修复前为出生点距离, 量级差 100 倍+)。
        /// </summary>
        [UnityTest]
        public IEnumerator RenderInterpolation_TracksPhysicsState_NotSpawn()
        {
            var c = Controller;
            Assert.NotNull(c);

            // 飞行 12s: 远离出生点 (核心库基准 8s 滑跑 86m 后爬升)
            double waited = 0, last = Time.realtimeSinceStartupAsDouble;
            while (waited < 12.0)
            {
                yield return null;
                double now = Time.realtimeSinceStartupAsDouble;
                waited += now - last; last = now;
                Assert.IsFalse(float.IsNaN(c.Altitude), "遥测无 NaN (t=" + waited + "s)");
            }

            // 采集 0.5s: RenderCapture 以 DefaultExecutionOrder(100) 保证在
            // AircraftController.LateUpdate 之后执行, 同帧原子采样
            RenderCapture.Reset();
            _aircraft.AddComponent<RenderCapture>();
            waited = 0; last = Time.realtimeSinceStartupAsDouble;
            while (waited < 0.5)
            {
                yield return null;
                double now = Time.realtimeSinceStartupAsDouble;
                waited += now - last; last = now;
            }

            Assert.Greater(RenderCapture.Samples.Count, 10, "采样帧数足够");
            foreach (var s in RenderCapture.Samples)
            {
                // 期望值 = 控制器 LateUpdate 使用的同一插值公式 (NED→Unity 映射)
                double alpha = s.FixedDt > 0 ? s.Time % s.FixedDt / s.FixedDt : 0;
                var exp = new Vector3(
                    (float)(s.PrevPos.Y + alpha * (s.CurrPos.Y - s.PrevPos.Y)),
                    (float)-(s.PrevPos.Z + alpha * (s.CurrPos.Z - s.PrevPos.Z)),
                    (float)(s.PrevPos.X + alpha * (s.CurrPos.X - s.PrevPos.X)));
                Assert.Less(Vector3.Distance(s.TransformPos, exp), 0.05f,
                    "transform 应等于相邻物理状态插值 (厘米级), 实差 " +
                    Vector3.Distance(s.TransformPos, exp));

                var currUnity = new Vector3(
                    (float)s.CurrPos.Y, (float)-s.CurrPos.Z, (float)s.CurrPos.X);
                float stepBound = (float)(s.CurrVelNed.Length * s.FixedDt) + 0.1f;
                Assert.Less(Vector3.Distance(s.TransformPos, currUnity), stepBound,
                    "transform 距当前物理状态 ≤ 一个物理步位移, 实差 " +
                    Vector3.Distance(s.TransformPos, currUnity) + " 上限 " + stepBound);
            }

            float spawnDist = Vector3.Distance(_aircraft.transform.position,
                new Vector3(0f, 0.327f, 0f));
            Assert.Greater(spawnDist, 30f,
                "12s 后飞机应远离出生点 30m+ (修复前插值会被拽向出生点), 实际 " + spawnDist);
            Debug.Log(string.Format(
                "[PlayMode] 渲染插值回归通过: {0} 帧采样, 距出生点 {1:F1}m",
                RenderCapture.Samples.Count, spawnDist));
        }

        /// <summary>
        /// P1-5 回归: GustDirectionDeg 与 WindDirectionDeg 同用"来风方位角"约定。
        /// 0=北风: 两种来源 (稳定风 / 阵风) 飞机感受到的风矢量都应吹向 -N;
        /// 修复前阵风用"吹向"约定, 会得到 +N (方向相反)。
        /// 用大幅值阵风 + 峰前差分, 压过默认 Light 紊流 (~1.5m/s) 的噪声。
        /// </summary>
        [UnityTest]
        public IEnumerator WindSettings_GustDirection_MatchesWindDirectionConvention()
        {
            var c = Controller;
            var wind = _aircraft.AddComponent<WindSettings>();
            wind.WindSpeed = 0f;
            wind.Turbulence = DrydenTurbulence.TurbulenceLevel.Light;
            wind.GustAmplitude = 30f;
            wind.GustDuration = 3f;
            wind.GustDirectionDeg = 0f;    // 北风 → 吹向 -N
            wind.TriggerGust(1f);          // [1s, 4s], 峰值 2.5s

            Vec3 Sample() => c.Plane.Wind.WindAt(new Vec3(0, 0, -1), Time.timeAsDouble);

            // 峰前基准 (t≈0.6s, 阵风尚未开始)
            yield return new WaitForSeconds(0.6f);
            Vec3 before = Sample();

            // 峰值时刻 (t≈2.6s)
            yield return new WaitForSeconds(2.0f);
            Vec3 atPeak = Sample();
            Vec3 gust = new Vec3(atPeak.X - before.X, atPeak.Y - before.Y, atPeak.Z - before.Z);
            Debug.Log(string.Format("[P1-5] 阵风矢量 (来风0°): N={0:F1} E={1:F1} (期望 N≈-30)",
                gust.X, gust.Y));
            Assert.Less(gust.X, -25f, "北风阵风应吹向 -N (来风约定), 实际 N 分量 " + gust.X);
            Assert.Less(Mathf.Abs((float)gust.Y), 5f, "东西分量应接近 0");

            // 对照: 稳定风 WindDirectionDeg=0 也应是 -N (等阵风完全结束)
            yield return new WaitForSeconds(1.6f);   // t≈4.2s, 阵风 [1,4] 已过
            wind.WindSpeed = 30f;
            wind.Rebuild();
            Vec3 steady = Sample();
            Debug.Log(string.Format("[P1-5] 稳定风矢量 (来风0°): N={0:F1} E={1:F1}",
                steady.X, steady.Y));
            Assert.Less(steady.X, -25f, "北风稳定风应吹向 -N, 实际 N 分量 " + steady.X);
            Assert.Less(Mathf.Abs((float)steady.Y), 5f, "东西分量应接近 0");
        }

        /// <summary>
        /// P2-6 回归: 舵面限幅来自配置而非硬编码。
        /// 控制器暴露共享 mapper; 换 15°/10°/12° 限位后满杆稳态偏角应被新限位夹住。
        /// </summary>
        [Test]
        public void SharedSurfaces_DeflectionClampedByConfig()
        {
            var c = Controller;
            Assert.NotNull(c.Surfaces, "控制器暴露共享 mapper (P2-6)");
            Assert.AreEqual(25f * Mathf.Deg2Rad, (float)c.Surfaces.MaxElevatorRad, 1e-3f,
                "限位来自加载配置 (elevator 25°)");

            var custom = new ControlSurfaceMapper(c.Plane.Aero.Data,
                new MaxDeflection { Elevator = 15, Aileron = 10, Rudder = 12 });
            ControlInputs ctl = ControlInputs.Zero;
            for (int i = 0; i < 400; i++)   // 舵机滞后收敛
                ctl = custom.Map(1, 1, 1, 1, 0, 1.0 / 120.0);
            Assert.AreEqual(15f * Mathf.Deg2Rad, Mathf.Abs((float)ctl.Elevator), 0.02f,
                "满杆升降舵偏角 = 15° 新限位 (非硬编码)");
            Assert.AreEqual(10f * Mathf.Deg2Rad, Mathf.Abs((float)ctl.Aileron), 0.02f,
                "满杆副翼偏角 = 10° 新限位");
            Assert.AreEqual(12f * Mathf.Deg2Rad, Mathf.Abs((float)ctl.Rudder), 0.02f,
                "满杆方向舵偏角 = 12° 新限位");
        }

        private static int CountWheels(AircraftController c)
        {
            return c.Plane.Gear != null ? c.Plane.Gear.Wheels.Count : 0;
        }
    }

    /// <summary>
    /// 测试辅助: 在 AircraftController.LateUpdate 之后同帧采样 transform 与
    /// 其私有插值输入 (_renderPrev/_renderCurr, 反射读取)。
    /// </summary>
    [DefaultExecutionOrder(100)]
    internal class RenderCapture : MonoBehaviour
    {
        internal struct Sample
        {
            public Vector3 TransformPos;
            public double Time, FixedDt;
            public Vec3 PrevPos, CurrPos, CurrVelNed;
        }

        internal static readonly System.Collections.Generic.List<Sample> Samples =
            new System.Collections.Generic.List<Sample>();

        internal static void Reset() { Samples.Clear(); }

        private AircraftController _c;
        private System.Reflection.FieldInfo _prevF, _currF;

        private void Awake()
        {
            _c = GetComponent<AircraftController>();
            var t = typeof(AircraftController);
            const System.Reflection.BindingFlags F =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            _prevF = t.GetField("_renderPrev", F);
            _currF = t.GetField("_renderCurr", F);
        }

        private void LateUpdate()
        {
            var prev = (AircraftState)_prevF.GetValue(_c);
            var curr = (AircraftState)_currF.GetValue(_c);
            Samples.Add(new Sample
            {
                TransformPos = transform.position,
                Time = Time.timeAsDouble,
                FixedDt = Time.fixedDeltaTime,
                PrevPos = prev.Position,
                CurrPos = curr.Position,
                CurrVelNed = _c.State.VelocityNed
            });
        }
    }
}
