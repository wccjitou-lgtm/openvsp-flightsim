using System.Collections;
using FlightDynamics;
using FlightDynamics.Math;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FlightSim.Tests
{
    /// <summary>
    /// 风况连续性 + 实时调节验收:
    ///   SteadyWindModel 缓变方向连续无跳变 / 关闭缓变方向恒定;
    ///   WindSettings.SetWeather 实时生效 (风速矢量立即切换, 紊流状态保持)。
    /// </summary>
    public class WindPanelPlayModeTests
    {
        [Test]
        public void SteadyWind_VeerOff_DirectionConstant()
        {
            var w = new SteadyWindModel(6.0, 90.0);   // 东风 (来风自东)
            for (double t = 0; t < 300; t += 0.5)
            {
                Vec3 v = w.WindAt(Vec3.Zero, t);
                Assert.AreEqual(0.0, v.X, 1e-9, "东风的北分量恒 0");
                Assert.AreEqual(-6.0, v.Y, 1e-9, "东来风吹向 -E, 恒定");
            }
        }

        [Test]
        public void SteadyWind_VeerOn_ContinuousNoJumps()
        {
            var w = new SteadyWindModel(8.0, 0.0)
            {
                Veer = true,
                VeerAmplitudeDeg = 18.0,
                VeerPeriodS = 150.0,
                VeerPhase = 0.7,
            };

            // 整个周期 0.25s 步采样: 相邻方向变化必须光滑 (≤ 摆动最大速率 + 余量)
            double maxRate = 18.0 * 2.0 * System.Math.PI / 150.0;   // ≈0.75 °/s
            double prev = w.DirectionAt(0);
            for (double t = 0.25; t <= 450; t += 0.25)
            {
                double cur = w.DirectionAt(t);
                double step = System.Math.Abs(cur - prev);
                Assert.LessOrEqual(step, maxRate * 0.25 + 0.05,
                    "方向跳变! t=" + t + " 步进 " + step + "°");
                prev = cur;
            }
            // 幅值范围: 全周期方向始终在 ±18° 内
            for (double t = 0; t <= 450; t += 0.25)
            {
                double d = w.DirectionAt(t);
                Assert.LessOrEqual(System.Math.Abs(d), 18.0 + 1e-6, "缓变幅值不超 ±18°");
            }
        }

        [UnityTest]
        public IEnumerator WindSettings_SetWeather_AppliesLive()
        {
            var go = new GameObject("TestUAV");
            go.AddComponent<AircraftController>();
            go.AddComponent<AutoPilotInput>();
            var wind = go.AddComponent<WindSettings>();
            yield return new WaitForFixedUpdate();
            yield return null;

            // 3 级: 6 m/s 来风 90° (东风); 紊流关 — 确定性断言矢量精确
            wind.SetWeather(6f, 90f, false, DrydenTurbulence.TurbulenceLevel.Light, false);
            yield return new WaitForFixedUpdate();
            Vec3 east = go.GetComponent<AircraftController>().Plane.Wind
                .WindAt(new Vec3(0, 0, -50), Time.timeAsDouble);
            Assert.AreEqual(0.0, east.X, 1e-6, "东风北分量 = 0");
            Assert.AreEqual(-6.0, east.Y, 1e-6, "东来风吹向 -E, 精确 6 m/s");

            // 实时改 0 级 (无风无紊流): 立即平静
            wind.SetWeather(0f, 0f, false, DrydenTurbulence.TurbulenceLevel.Light, false);
            yield return new WaitForFixedUpdate();
            yield return null;
            Vec3 calm = go.GetComponent<AircraftController>().Plane.Wind
                .WindAt(new Vec3(0, 0, -50), Time.timeAsDouble);
            Assert.Less(calm.Length, 0.05, "0 级应立即完全无风, 实际 " + calm.Length);

            Object.Destroy(go);
            Debug.Log("[WindPanel] SetWeather: 6m/s 东风均值 (0,-6) ✓ → 0 级立即静风 ✓");
        }
    }
}
