using System.Collections;
using FlightDynamics.Math;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FlightSim.Tests
{
    /// <summary>
    /// 飞行模式自驾验收: RTL 转向飞回 / 盘旋持续转弯保持高度 / 自稳回平 /
    /// 降落 (无跑道分支) 盘旋下降 / 接管与解除交还。
    /// </summary>
    public class ModePilotPlayModeTests
    {
        private GameObject _aircraft;
        private GameObject _runway;

        [SetUp]
        public void SetUp()
        {
            _aircraft = new GameObject("TestUAV");
            _aircraft.AddComponent<AircraftController>();
            _aircraft.AddComponent<AutoPilotInput>();
            _aircraft.AddComponent<FlightGuard>();
            _aircraft.AddComponent<ModePilot>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_aircraft != null) Object.Destroy(_aircraft);
            // 跑道 GO 残留会把其他用例的 LAND 切到跑道分支, 必须清理
            if (_runway != null) Object.Destroy(_runway);
        }

        private AircraftController C
        { get { return _aircraft.GetComponent<AircraftController>(); } }

        private ModePilot Pilot
        { get { return _aircraft.GetComponent<ModePilot>(); } }

        /// <summary>把飞机摆到指定平飞状态 (等物理消化)。</summary>
        private IEnumerator PlaceAircraft(double north, double east, double altMps,
                                          double speedMps, double headingRad)
        {
            var c = C;
            c.State.Position = new Vec3(north, east, -altMps);
            c.State.VelocityBody = new Vec3(speedMps, 0, 0);
            c.State.Attitude = Quat.FromEuler321(0, 0, headingRad);
            double waited = 0;
            float t0 = Time.realtimeSinceStartup;
            while (waited < 0.6)
            {
                yield return null;
                waited += Time.realtimeSinceStartup - t0;
                t0 = Time.realtimeSinceStartup;
            }
        }

        private float HeadingErrToHome()
        {
            var s = C.State;
            float bear = Mathf.Atan2(-(float)s.Position.Y, -(float)s.Position.X);
            float h = (float)s.Euler321()[2];
            float e = bear - h;
            return Mathf.Abs(Mathf.Atan2(Mathf.Sin(e), Mathf.Cos(e)));
        }

        private float HorizDistToHome()
        {
            var s = C.State;
            return Mathf.Sqrt((float)s.Position.X * (float)s.Position.X
                            + (float)s.Position.Y * (float)s.Position.Y);
        }

        [UnityTest]
        public IEnumerator RTL_TurnsTowardHomeAndConverges()
        {
            yield return PlaceAircraft(700, 0, 200, 20, 0);   // 家北 700m, 机头背家
            Pilot.Engage(FlightMode.Rtl);
            Assert.AreEqual(FlightMode.Rtl, Pilot.Mode, "模式接管");

            // 本机实测持续转弯率 ~5-6°/s (弱航向稳定性的真实特性),
            // 180° 掉头需 ~30s; 45s 航向应朝家且距离明显收敛
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 45f) yield return null;
            Assert.Less(HeadingErrToHome(), Mathf.PI * 0.5f,
                "45s 航向应朝向起飞点 (误差 <90°), 实际 " + (HeadingErrToHome() * Mathf.Rad2Deg));
            float d45 = HorizDistToHome();
            Assert.Less(d45, 700f - 80f, "45s 距离应明显收敛 (正在飞回), 实际 " + d45);
            Debug.Log("[ModePilot] RTL 45s: 航向误差 " + (HeadingErrToHome() * Mathf.Rad2Deg).ToString("F0")
                + "° 距离 " + d45.ToString("F0") + "m ✓");
        }

        [UnityTest]
        public IEnumerator Loiter_SustainsTurnAndAltitude()
        {
            yield return PlaceAircraft(100, 0, 150, 20, 0);
            float h0 = (float)C.State.Euler321()[2];
            float alt0 = (float)C.State.Altitude;
            Pilot.Engage(FlightMode.Loiter);

            float t0 = Time.realtimeSinceStartup;
            float hdgPrev = h0; float totalTurn = 0; float maxBank = 0;
            while (Time.realtimeSinceStartup - t0 < 20f)
            {
                yield return null;
                float h = (float)C.State.Euler321()[2];
                totalTurn += Mathf.DeltaAngle(hdgPrev * Mathf.Rad2Deg, h * Mathf.Rad2Deg);
                hdgPrev = h;
                maxBank = Mathf.Max(maxBank, Mathf.Abs(C.BankDeg));
            }
            Assert.Greater(Mathf.Abs(totalTurn), 90f, "20s 应累计转弯 >90°, 实际 " + totalTurn);
            Assert.Greater(maxBank, 10f, "应建立盘旋坡度 >10°, 峰值 " + maxBank);
            Assert.Less(Mathf.Abs((float)C.State.Altitude - alt0), 60f, "高度保持 ±60m");
            Debug.Log("[ModePilot] Loiter 20s: 累计转弯 " + totalTurn.ToString("F0")
                + "° 坡度峰 " + maxBank.ToString("F0") + "° 高度漂移 "
                + ((float)C.State.Altitude - alt0).ToString("F1") + "m ✓");
        }

        [UnityTest]
        public IEnumerator Stab_LevelsWings()
        {
            yield return PlaceAircraft(0, 0, 120, 20, 0);
            // 制造 30° 坡度扰动
            C.State.Attitude = Quat.FromEuler321(30f * Mathf.Deg2Rad, 0, 0);
            Pilot.Engage(FlightMode.Stab);

            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 6f) yield return null;
            Assert.Less(Mathf.Abs(C.BankDeg), 6f, "6s 内机翼应回平 (|坡度|<6°), 实际 " + C.BankDeg);
            Debug.Log("[ModePilot] Stab: 坡度 30°→" + C.BankDeg.ToString("F1") + "° ✓");
        }

        // ================= U13: 自稳杆量穿透 =================

        [Test]
        public void Stab_StickToAttitude_Mapping()
        {
            // 满杆: ±30° 坡度 / 2° 基线 ±16° 俯仰 (U15: 原 ±12° 满杆只给 ~45% 舵量)
            var full = ControlLaws.StabStickToAttitude(1f, 1f);
            Assert.AreEqual(30f * Mathf.Deg2Rad, full.x, 1e-4f, "满右杆 → +30° 坡度");
            Assert.AreEqual(18f * Mathf.Deg2Rad, full.y, 1e-4f, "满拉杆 → 18° 俯仰目标");
            var back = ControlLaws.StabStickToAttitude(-1f, -1f);
            Assert.AreEqual(-30f * Mathf.Deg2Rad, back.x, 1e-4f);
            Assert.AreEqual(-14f * Mathf.Deg2Rad, back.y, 1e-4f, "满推杆 → -14°");
            var half = ControlLaws.StabStickToAttitude(0.5f, 0f);
            Assert.AreEqual(15f * Mathf.Deg2Rad, half.x, 1e-4f, "半杆线性");
            Assert.AreEqual(2f * Mathf.Deg2Rad, half.y, 1e-4f, "俯仰中立=2° 基线");
        }

        [Test]
        public void Stab_FullStick_ReachesHighElevatorAuthority()
        {
            // U15: 满杆 + 最大姿态误差 → 归一化舵量应 ≥70% (修复前 ~45%)
            float maxElev = 25f * Mathf.Deg2Rad;
            float target = ControlLaws.StabStickToAttitude(0f, 1f).y;   // 18°
            double e = ControlLaws.StabElevator(target, 0f, 0f, 1f, maxElev);
            double norm = e / maxElev;
            Assert.GreaterOrEqual(norm, 0.70, "满拉杆应达 ≥70% 升降舵权限, 实际 " + norm.ToString("F2"));
            // 杆中立 (松杆) 时无前馈, 纯增稳
            double e0 = ControlLaws.StabElevator(2f * Mathf.Deg2Rad, 0f, 0f, 0f, maxElev);
            Assert.Less(e0 / maxElev, 0.12, "松杆小误差 → 小舵量 (保持语义)");
            // 横滚前馈
            float maxAil = 20f * Mathf.Deg2Rad;
            double a = ControlLaws.StabAileron(0f, 30f * Mathf.Deg2Rad, 0f, 1f, maxAil);
            Assert.GreaterOrEqual(a / maxAil, 0.70, "满横滚杆应达 ≥70% 副翼权限");
        }

        [UnityTest]
        public IEnumerator Stab_EngageKeepsThrottleAndDisengageRestoresManual()
        {
            yield return PlaceAircraft(0, 0, 120, 20, 0);
            C.Throttle = 0.6f;
            Pilot.Engage(FlightMode.Stab);
            // 接管时油门从当前值起算 (U13: 手动语义, 不再锁死)
            for (int i = 0; i < 5; i++) yield return null;   // 数帧让 Poll 跑
            Assert.AreEqual(0.6f, C.Throttle, 0.05f, "STAB 接管应保持当前油门");
            Pilot.Disengage();
            yield return null;
            Assert.AreEqual(FlightMode.Manual, Pilot.Mode, "解除后回手动 (再按 4 等效)");
        }

        [UnityTest]
        public IEnumerator Land_NoRunway_DescendsInLoiter()
        {
            yield return PlaceAircraft(0, 0, 100, 20, 0);
            float alt0 = (float)C.State.Altitude;
            Pilot.Engage(FlightMode.Land);
            // Phase 在 Poll 里刷新 — 等物理帧消化
            double w = 0; float tw = Time.realtimeSinceStartup;
            while (Pilot.Phase == "" && w < 1.0)
            {
                yield return null;
                w += Time.realtimeSinceStartup - tw;
                tw = Time.realtimeSinceStartup;
            }
            Assert.AreEqual("盘旋下降", Pilot.Phase, "无跑道场景走盘旋下降分支");

            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 15f) yield return null;
            Assert.Less((float)C.State.Altitude, alt0 - 8f,
                "15s 应已明显下降 (-1.5m/s 目标), 实际 " + ((float)C.State.Altitude - alt0));
            Assert.IsFalse(C.IsCrashed, "缓降不应触发坠毁");
            Debug.Log("[ModePilot] Land 15s: " + alt0.ToString("F0") + "→"
                + ((float)C.State.Altitude).ToString("F0") + "m ✓");

            // 解除交还手动
            Pilot.Disengage();
            Assert.AreEqual(FlightMode.Manual, Pilot.Mode, "解除回手动");
            Assert.IsNull(C.OverrideInput, "接管指针清空");
        }

        [UnityTest]
        public IEnumerator Land_Runway_FinalTracksCenterlineWhenHomeOffset()
        {
            // 起飞点东移 250m: 跑道中线相对 Home 的真实横偏 = 0 (绝对东坐标 = 250)。
            // 五边横偏必须相对 Home 计算 —— 修复前用绝对东坐标读出 +250m 假横偏,
            // 修正坡度饱和左压 10°, 飞机被持续拉离中线 (~4m/s 侧漂)。
            var c = C;
            _runway = new GameObject("RunwayEnvironment");   // 触发 LAND 跑道分支
            c.InitialPositionNed = new Vector3(0f, 250f, -0.327f);
            yield return PlaceAircraft(-80, 250, 60, 20, 0);   // 入口点 = Home 南 80m 中线延长线上

            Pilot.Engage(FlightMode.Land);
            double w = 0; float tw = Time.realtimeSinceStartup;
            while (Pilot.Phase != "五边下滑" && w < 3.0)
            {
                yield return null;
                w += Time.realtimeSinceStartup - tw;
                tw = Time.realtimeSinceStartup;
            }
            Assert.AreEqual("五边下滑", Pilot.Phase, "入口上空 + 机头朝北应直接进入五边");

            // 15s 内相对横偏 (绝对东 - 250) 应保持有界; 修复前会发散到 ~50m+
            float maxCross = 0;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 15f)
            {
                yield return null;
                maxCross = Mathf.Max(maxCross, Mathf.Abs((float)c.State.Position.Y - 250f));
            }
            Assert.Less(maxCross, 30f,
                "中线延长线上五边, 相对横偏应有界 (绝对坐标假横偏会把飞机拉飞): 峰值 " + maxCross);
            Assert.IsFalse(c.IsCrashed, "五边过程不坠毁");
            Debug.Log("[ModePilot] Land 东移起飞点五边 15s: 相对横偏峰值 "
                + maxCross.ToString("F1") + "m ✓");
        }

        [UnityTest]
        public IEnumerator Land_Runway_FromEastOffset_ConvergesToCenterline()
        {
            // 人工试飞清单"从东侧横向偏移处触发降落"的自动化版:
            // 入口近旁东偏 60m、机头朝北 (distTh<70 && |heading|<20°, 直接进五边)
            // → 五边横偏修正向中线收敛 (东偏 60 → <30m)。
            // 注: 放在入口正北大偏距会先经历 90°+ 导航掉头, 本机 ~5°/s 转弯率
            // 25s 内进不了五边 (首版测试即因此超时)。
            var c = C;
            _runway = new GameObject("RunwayEnvironment");
            yield return PlaceAircraft(-100, 60, 60, 20, 0);   // 入口南 20m、东 60m

            Pilot.Engage(FlightMode.Land);
            double w = 0; float tw = Time.realtimeSinceStartup;
            while (Pilot.Phase != "五边下滑" && w < 5.0)
            {
                yield return null;
                w += Time.realtimeSinceStartup - tw;
                tw = Time.realtimeSinceStartup;
            }
            Assert.AreEqual("五边下滑", Pilot.Phase, "入口近旁 + 机头朝北应立即进入五边, 实际 " + Pilot.Phase);
            float eastAtPhase1 = (float)c.State.Position.Y;

            // 五边开始后 30s: 横偏应收敛 (60m → <30m), 流程推进到拉平/滑跑
            float minEast = float.MaxValue;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 30f)
            {
                yield return null;
                minEast = Mathf.Min(minEast, Mathf.Abs((float)c.State.Position.Y));
            }
            Assert.Less(minEast, 30f,
                "五边应向中线收敛 (东偏 60→<30m), 入口时 " + eastAtPhase1 + "m, 最小 " + minEast + "m");
            Assert.IsTrue(Pilot.Phase == "五边下滑" || Pilot.Phase == "拉平" || Pilot.Phase == "滑跑",
                "降落流程应推进, 实际 " + Pilot.Phase);
            Assert.IsFalse(c.IsCrashed, "自动降落不坠毁 (拉平下沉率 < 8m/s)");
            Debug.Log("[ModePilot] Land 东偏 60m 进五边: 入口横偏 "
                + eastAtPhase1.ToString("F0") + "m → 收敛至 " + minEast.ToString("F0")
                + "m, 阶段 " + Pilot.Phase + " ✓");
        }
    }
}
