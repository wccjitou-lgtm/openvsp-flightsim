using System.Collections;
using FlightDynamics;
using FlightDynamics.Math;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FlightSim.Tests
{
    /// <summary>
    /// 坠毁检测 + 场地边界自动返航 验收:
    ///   1. 高速拍地 → IsCrashed, 物理冻结, Backspace 复位解除;
    ///   2. 飞出警戒环 → 自动转向起飞点, 距离收敛;
    ///   3. 正常任务不受影响 (回归: 全任务测试继续通过即无误报)。
    /// </summary>
    public class GuardPlayModeTests
    {
        private GameObject _aircraft;

        [SetUp]
        public void SetUp()
        {
            _aircraft = new GameObject("TestUAV");
            _aircraft.AddComponent<AircraftController>();
            _aircraft.AddComponent<AutoPilotInput>();
            _aircraft.AddComponent<DataLogger>();
            _aircraft.AddComponent<FlightGuard>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_aircraft != null) Object.Destroy(_aircraft);
            TerrainRegistry.Clear();
        }

        /// <summary>北向 north0 以北是一道坡 (每米升 slope, 封顶 cap), 其余为 0 的地形; 同时是返航时的"墙"。</summary>
        private sealed class RampTerrain : ITerrainModel
        {
            private readonly double _n0, _slope, _cap;
            public RampTerrain(double n0, double slope, double cap) { _n0 = n0; _slope = slope; _cap = cap; }
            public double HeightAt(double north, double east)
            {
                return north <= _n0 ? 0.0 : System.Math.Min((north - _n0) * _slope, _cap);
            }
        }

        private AircraftController Controller
        { get { return _aircraft != null ? _aircraft.GetComponent<AircraftController>() : null; } }

        [UnityTest]
        public IEnumerator Crash_HardImpact_FreezesAndResets()
        {
            var c = Controller;

            // 销毁自驾 (其俯仰增稳会从俯冲中改出); 控制器探测到组件销毁后
            // 回落键盘输入, 无按键 = 中立操纵 = 纯物理俯冲
            Object.Destroy(_aircraft.GetComponent<AutoPilotInput>());

            // 空中 60m, 25 m/s 直插地面
            c.State.Position = new Vec3(0, 0, -60);
            c.State.VelocityBody = new Vec3(2, 0, 25);   // 体轴 z 下 = 25 m/s 下沉 + 少量前飞

            float t0 = Time.realtimeSinceStartup;
            while (!c.IsCrashed && Time.realtimeSinceStartup - t0 < 8f) yield return null;
            Assert.IsTrue(c.IsCrashed, "拍地应判定坠毁 (下沉率 25 > 8 m/s)");

            // 物理冻结: 位置不再变化
            yield return new WaitForSeconds(0.5f);
            float alt1 = c.Altitude;
            float north1 = (float)c.State.Position.X;
            yield return new WaitForSeconds(0.5f);
            Assert.Less(Mathf.Abs(c.Altitude - alt1), 0.01f, "坠毁后高度冻结");
            Assert.Less(Mathf.Abs((float)c.State.Position.X - north1), 0.01f, "坠毁后位置冻结");

            // 复位解除 (轮询等待遥测刷新 — 高速帧率下某帧可能无物理步)
            c.ResetToInitial();
            Assert.IsFalse(c.IsCrashed, "Backspace 复位解除坠毁");
            double waitedR = 0;
            float tR = Time.realtimeSinceStartup;
            while (Mathf.Abs(c.Altitude - 0.327f) > 0.1f && waitedR < 1.0)
            {
                yield return null;
                waitedR += Time.realtimeSinceStartup - tR;
                tR = Time.realtimeSinceStartup;
            }
            Assert.Less(Mathf.Abs(c.Altitude - 0.327f), 0.1f, "回到跑道起点");

            // 复位后能正常重新滑跑 (不再坠毁)
            float t1 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t1 < 4f) yield return null;
            Assert.IsFalse(c.IsCrashed, "复位后正常滑跑不误报坠毁");
            Debug.Log("[Guard] 拍地坠毁 → 冻结 → 复位 → 重新滑跑 ✓");
        }

        [UnityTest]
        public IEnumerator Boundary_AutoTurnBackTowardHome()
        {
            var c = Controller;
            var guard = _aircraft.GetComponent<FlightGuard>();

            // 北飞 1150m (警戒环 1000m 外, 接管权重 ~0.75), 高度 300m, 20 m/s
            c.State.Position = new Vec3(1150, 0, -300);
            c.State.VelocityBody = new Vec3(20, 0, 0);   // 机头朝北 = 背对起飞点
            c.State.Attitude = Quat.FromEuler321(0, 0, 0);

            // 轮询等待物理步消化注入 (批处理高帧率下首帧可能无物理步)
            double waited = 0;
            float tp = Time.realtimeSinceStartup;
            while (guard.DistanceToHome < 1000f && waited < 2.0)
            {
                yield return null;
                waited += Time.realtimeSinceStartup - tp;
                tp = Time.realtimeSinceStartup;
            }

            float dist0 = guard.DistanceToHome;
            Assert.Greater(dist0, 1100f, "初始距离 1150m");
            Assert.IsTrue(guard.IsTurningBack, "越过警戒环应激活返航");

            // 15s: 航向应已明显转向起飞点 (标准转弯走大弧线, 距离先增后减)
            float headingErrToHome()
            {
                float bear = Mathf.Atan2(
                    -(float)c.State.Position.Y, -(float)c.State.Position.X);
                float h = c.HeadingDeg * Mathf.Deg2Rad;
                float e = bear - h;
                while (e > Mathf.PI) e -= 2f * Mathf.PI;
                while (e < -Mathf.PI) e += 2f * Mathf.PI;
                return Mathf.Abs(e);
            }
            float err0 = headingErrToHome();   // ≈ 180° (背对起飞点)

            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 15f) yield return null;
            float err15 = headingErrToHome();
            Assert.Less(err15, err0 - 0.6f,   // 至少转过来 ~34°
                "15s 航向应显著转向起飞点: 误差 " + (err0 * Mathf.Rad2Deg).ToString("F0")
                + "° → " + (err15 * Mathf.Rad2Deg).ToString("F0") + "°");
            float dist15 = guard.DistanceToHome;

            // 再 20s: 距离进入收敛 (已飞过弧线顶点)
            t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 20f) yield return null;
            Assert.Less(guard.DistanceToHome, dist15,
                "35s 后距离应已收敛 (返航生效): " + dist15.ToString("F0") + " → "
                + guard.DistanceToHome.ToString("F0"));
            Assert.IsFalse(c.IsCrashed, "返航过程不坠毁");
            Assert.IsFalse(float.IsNaN(guard.DistanceToHome), "无 NaN");
            Debug.Log(string.Format(
                "[Guard] 边界返航: 航向误差 {0:F0}°→{1:F0}°, 距离 {2:F0}→{3:F0}→{4:F0}m ✓",
                err0 * Mathf.Rad2Deg, err15 * Mathf.Rad2Deg,
                dist0, dist15, guard.DistanceToHome));
        }

        [UnityTest]
        public IEnumerator Boundary_WithModePilot_GuardBlendsWithoutDisengage()
        {
            // 组合场景固化: 自稳自驾 + 默认边界 (1200m)。固定语义 = AircraftController.
            // FixedUpdate 里 Poll 在前、Guard.Apply 在后: Guard 按 √ 权重混合
            // 副翼把飞机拉回场内 (自稳的航向保持只占剩余权重), 但不解除
            // ModePilot 的接管。几何/阈值复用 Boundary_AutoTurnBackTowardHome
            // (已验证的物理), 叠加"自驾全程保持接管"断言。
            var c = Controller;
            var guard = _aircraft.GetComponent<FlightGuard>();
            var pilot = _aircraft.AddComponent<ModePilot>();

            // 北飞 1150m (警戒环 1000m 外, 接管权重 ~0.75-1.0), 高度 300m, 20 m/s
            c.State.Position = new Vec3(1150, 0, -300);
            c.State.VelocityBody = new Vec3(20, 0, 0);
            c.State.Attitude = Quat.FromEuler321(0, 0, 0);
            double waited = 0; float tp = Time.realtimeSinceStartup;
            while (guard.DistanceToHome < 1100f && waited < 2.0)
            {
                yield return null;
                waited += Time.realtimeSinceStartup - tp;
                tp = Time.realtimeSinceStartup;
            }

            // 自稳接管 (航向保持朝北 = 与 Guard 返航指令对抗, 验证混合后 Guard 仍主导)
            pilot.Engage(FlightMode.Stab);
            Assert.AreEqual(FlightMode.Stab, pilot.Mode, "自稳模式接管");
            Assert.IsNotNull(c.OverrideInput, "接管指针生效");
            float alt0 = (float)c.State.Altitude;

            float headingErrToHome()
            {
                float bear = Mathf.Atan2(
                    -(float)c.State.Position.Y, -(float)c.State.Position.X);
                float h = c.HeadingDeg * Mathf.Deg2Rad;
                float e = bear - h;
                while (e > Mathf.PI) e -= 2f * Mathf.PI;
                while (e < -Mathf.PI) e += 2f * Mathf.PI;
                return Mathf.Abs(e);
            }
            float err0 = headingErrToHome();   // ≈ 180° (背对起飞点)

            // 15s: 航向应已明显转向起飞点 (Guard 权重压过自稳航向保持)
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 15f) yield return null;
            float err15 = headingErrToHome();
            Assert.Less(err15, err0 - 0.6f,   // 至少转过来 ~34°
                "15s 航向应显著转向起飞点 (Guard 主导混合): 误差 "
                + (err0 * Mathf.Rad2Deg).ToString("F0") + "° → " + (err15 * Mathf.Rad2Deg).ToString("F0") + "°");
            float dist15 = guard.DistanceToHome;

            // 再 20s: 距离进入收敛 (已飞过弧线顶点)
            t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 20f) yield return null;
            Assert.Less(guard.DistanceToHome, dist15,
                "35s 后距离应已收敛 (返航生效): " + dist15.ToString("F0") + " → "
                + guard.DistanceToHome.ToString("F0") + "m");

            // 全程关键语义: Guard 干预不解除自驾接管, Poll 持续运行
            Assert.AreEqual(FlightMode.Stab, pilot.Mode, "Guard 干预不应解除 ModePilot");
            Assert.AreEqual("自稳", pilot.Phase, "ModePilot.Poll 持续被调用 (U13: 阶段文案随杆量穿透更新)");
            Assert.IsNotNull(c.OverrideInput, "接管指针保持");
            Assert.Less(Mathf.Abs((float)c.State.Altitude - alt0), 60f,
                "自稳高度保持 ±60m, 实际 " + ((float)c.State.Altitude - alt0));
            Assert.IsFalse(c.IsCrashed, "组合控制不失控坠毁");
            Assert.IsFalse(float.IsNaN(guard.DistanceToHome), "无 NaN");
            Debug.Log(string.Format(
                "[Guard] 组合: Stab 保持接管, 航向误差 {0:F0}°→{1:F0}°, 距离 {2:F0}→{3:F0}m ✓",
                err0 * Mathf.Rad2Deg, err15 * Mathf.Rad2Deg, dist15, guard.DistanceToHome));
        }

        // ---------------- 返航时的地形避让 ----------------

        private struct RampRun { public float MinClearance, MaxAlt, EndDist; public bool Crashed, Climbed; }

        private IEnumerator FlyIntoRamp(bool avoidance, System.Action<RampRun> done)
        {
            var c = Controller;
            var guard = _aircraft.GetComponent<FlightGuard>();
            guard.TerrainAvoidance = avoidance;
            // 坡自北向 1060 m 起, 15% 坡度 (8.5°) 封顶 150 m: 警戒环 (1000 m) 外紧挨着的墙; 飞机 1040 m 处高 20 m 北飞
            TerrainRegistry.Set(new RampTerrain(1060, 0.15, 150));
            c.Plane.Gear.Terrain = TerrainRegistry.Current;
            c.State.Position = new Vec3(1040, 0, -20);
            c.State.VelocityBody = new Vec3(20, 0, 0);
            c.State.Attitude = Quat.FromEuler321(0, 0, 0);
            double waited = 0; float tp = Time.realtimeSinceStartup;
            while (guard.DistanceToHome < 1030f && waited < 2.0)
            {
                yield return null;
                waited += Time.realtimeSinceStartup - tp; tp = Time.realtimeSinceStartup;
            }
            var r = new RampRun { MinClearance = 999f };
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 40f)
            {
                yield return null;
                if (c.IsCrashed) { r.Crashed = true; break; }
                // 只量飞机在坡面范围内 (坡脚以北) 的离地高度; 坡外平地上的高度损失不算
                if (c.State.Position.X > 1060.0) r.MinClearance = Mathf.Min(r.MinClearance, c.AltitudeAgl);
                r.MaxAlt = Mathf.Max(r.MaxAlt, c.Altitude);
                if (guard.IsClimbingToClear) r.Climbed = true;
            }
            r.EndDist = guard.DistanceToHome;
            done(r);
        }

        [UnityTest, Timeout(180000)]
        public IEnumerator Boundary_TerrainAvoidance_ClimbsOverRampThatTurningAloneWouldHit()
        {
            RampRun off = default, on = default;
            yield return FlyIntoRamp(false, r => off = r);
            // 第二次：重置姿态/状态后再飞 (同一架机)
            _aircraft.GetComponent<AircraftController>().ResetToInitial();
            yield return null;
            yield return FlyIntoRamp(true, r => on = r);
            Debug.Log("[Guard] 坡道地形避让: 关闭 → 坠毁=" + off.Crashed + " 最小离地 " + off.MinClearance.ToString("F1") +
                      " m, 最大高度 " + off.MaxAlt.ToString("F0") + "; 开启 → 坠毁=" + on.Crashed + " 最小离地 " +
                      on.MinClearance.ToString("F1") + " m, 最大高度 " + on.MaxAlt.ToString("F0") + ", 爬升触发=" + on.Climbed);
            Assert.IsTrue(on.Climbed, "前方有坡且高度不足时应触发爬升避地形");
            Assert.IsFalse(on.Crashed, "开启地形避让不应坠毁");
            Assert.Greater(on.MinClearance, 2f, "开启后全程离地应 > 2 m, 实测 " + on.MinClearance);   // U16 新机型基线: 实测 3.33
            Assert.Greater(on.MinClearance, off.MinClearance, "开启应比关闭更安全 (关闭 " + off.MinClearance + " m)");
            Assert.Less(on.EndDist, 1500f, "仍在返航 (转弯弧线有限, 距离没有发散): " + on.EndDist);
        }

        [UnityTest]
        public IEnumerator Boundary_TerrainAvoidance_NoTerrain_DoesNotTouchElevatorOrThrottle()
        {
            // 平地场景 (无登记地形): 返航只动副翼, 升降舵/油门保持自驾原值 —— 旧行为不变
            var c = Controller;
            var guard = _aircraft.GetComponent<FlightGuard>();
            Assert.IsFalse(TerrainRegistry.HasTerrain);
            c.State.Position = new Vec3(1150, 0, -300);
            c.State.VelocityBody = new Vec3(20, 0, 0);
            c.State.Attitude = Quat.FromEuler321(0, 0, 0);
            float t0 = Time.realtimeSinceStartup;
            bool everClimbing = false;
            while (Time.realtimeSinceStartup - t0 < 6f)
            {
                yield return null;
                if (guard.IsClimbingToClear) everClimbing = true;
            }
            Assert.IsTrue(guard.IsTurningBack, "应在返航");
            Assert.IsFalse(everClimbing, "没有登记地形时不应触发爬升避地形");
        }

        [UnityTest]
        public IEnumerator Boundary_TerrainAvoidance_HighEnough_NoClimbCommand()
        {
            // 高度远高于前方地形: 照常只转向, 不爬升
            var c = Controller;
            var guard = _aircraft.GetComponent<FlightGuard>();
            TerrainRegistry.Set(new RampTerrain(1100, 0.08, 120));
            c.State.Position = new Vec3(1060, 0, -300);
            c.State.VelocityBody = new Vec3(20, 0, 0);
            c.State.Attitude = Quat.FromEuler321(0, 0, 0);
            float t0 = Time.realtimeSinceStartup;
            bool everClimbing = false;
            while (Time.realtimeSinceStartup - t0 < 5f)
            {
                yield return null;
                if (guard.IsClimbingToClear) everClimbing = true;
            }
            Assert.IsTrue(guard.IsTurningBack);
            Assert.IsFalse(everClimbing, "高度 300 m 远高于坡顶 120 m + 余量, 不应爬升");
            Assert.Greater(guard.TerrainAhead, 0f, "前视应看到坡");
        }
    }
}
