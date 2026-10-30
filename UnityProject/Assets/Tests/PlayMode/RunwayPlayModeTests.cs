using System.Collections;
using FlightDynamics.Math;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FlightSim.Tests
{
    /// <summary>
    /// S1 机场验收: 跑道几何 (RunwayGeometry 坐标换算 / RunwayEnvironment 暴露与构建) +
    /// 读取几何的自动降落 (800m 跑道接地落在接地区、偏置跑道不再对准 Home) + PAPI 红白判据。
    /// </summary>
    public class RunwayPlayModeTests
    {
        private GameObject _aircraft;
        private GameObject _runwayGo;
        private GameObject _camGo;

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
            if (_runwayGo != null) Object.DestroyImmediate(_runwayGo);
            if (_camGo != null) Object.DestroyImmediate(_camGo);
        }

        private AircraftController C { get { return _aircraft.GetComponent<AircraftController>(); } }
        private ModePilot Pilot { get { return _aircraft.GetComponent<ModePilot>(); } }

        private RunwayEnvironment MakeRunway(Vector3 start, bool build)
        {
            _runwayGo = new GameObject("RunwayEnvironment");
            var env = _runwayGo.AddComponent<RunwayEnvironment>();
            env.BuildOnStart = false;   // Start 里不自动 Build; 需要时手动 Build
            env.RunwayStart = start;
            if (build) env.Build();
            return env;
        }

        // ---------------- RunwayGeometry 坐标换算 (纯逻辑) ----------------

        [Test]
        public void RunwayGeometry_AlongCross_HeadingNorth()
        {
            var g = new RunwayGeometry { ThresholdNed = new Vector2(10f, 5f), HeadingRad = 0f };
            g.AlongCross(new Vector2(110f, 8f), out float along, out float cross);
            Assert.AreEqual(100f, along, 1e-3f, "北向跑道: along = 北向差");
            Assert.AreEqual(3f, cross, 1e-3f, "跑道方向右侧 (东) 为正");
        }

        [Test]
        public void RunwayGeometry_AlongCross_HeadingEast_RightIsSouth()
        {
            var g = new RunwayGeometry { ThresholdNed = Vector2.zero, HeadingRad = Mathf.PI / 2f };
            g.AlongCross(new Vector2(5f, 100f), out float along, out float cross);
            Assert.AreEqual(100f, along, 1e-3f);
            Assert.AreEqual(-5f, cross, 1e-3f, "朝东时右侧是南, 偏北 5m = 左偏 -5");
        }

        [Test]
        public void RunwayGeometry_PointAlong_IsInverseOfAlongCross()
        {
            var g = new RunwayGeometry { ThresholdNed = new Vector2(-30f, 70f), HeadingRad = 0.7f };
            Vector2 p = g.PointAlong(123f, -4.5f);
            g.AlongCross(p, out float along, out float cross);
            Assert.AreEqual(123f, along, 1e-3f);
            Assert.AreEqual(-4.5f, cross, 1e-3f);
        }

        // ---------------- RunwayEnvironment 几何与构建 ----------------

        [Test]
        public void RunwayEnvironment_Geometry_Default800mNorthFacing()
        {
            var env = MakeRunway(Vector3.zero, build: false);
            var g = env.Geometry;
            Assert.AreEqual(800f, g.LengthM, "通航跑道 800m");
            Assert.AreEqual(0f, g.HeadingRad, "朝北使用");
            Assert.AreEqual(Vector2.zero, g.ThresholdNed);
            Assert.Greater(g.TouchdownZoneEndM, g.TouchdownDistM, "接地区终点应在瞄准点之后");
            Assert.Less(g.TouchdownZoneEndM, g.LengthM, "接地区应在跑道范围内");
        }

        [Test]
        public void RunwayEnvironment_Geometry_FollowsRunwayStart_AsNed()
        {
            var env = MakeRunway(new Vector3(250f, 3f, 100f), build: false);   // Unity: x 东, y 高, z 北
            var g = env.Geometry;
            Assert.AreEqual(new Vector2(100f, 250f), g.ThresholdNed, "NED = (北=z, 东=x)");
            Assert.AreEqual(3f, g.ElevationM, "海拔 = RunwayStart.y");
        }

        [Test]
        public void RunwayEnvironment_AirportBounds_CoversRunwayTaxiwayAndBuffers()
        {
            var env = MakeRunway(Vector3.zero, build: false);
            Rect b = env.AirportBounds;
            Assert.IsTrue(b.Contains(new Vector2(0f, 0f)), "含入口");
            Assert.IsTrue(b.Contains(new Vector2(0f, 800f)), "含末端");
            Assert.IsTrue(b.Contains(new Vector2(45f, 400f)), "含滑行道");
            Assert.IsTrue(b.Contains(new Vector2(140f, 100f)), "含机库");
            Assert.IsTrue(b.Contains(new Vector2(0f, -90f)), "含入口外缓冲");
            Assert.IsTrue(b.Contains(new Vector2(0f, 890f)), "含末端外缓冲");
        }

        [Test]
        public void RunwayEnvironment_Build_ProducesAirportFurnitureAndNoPinkMaterials()
        {
            var env = MakeRunway(Vector3.zero, build: true);
            var names = new System.Collections.Generic.HashSet<string>();
            foreach (Transform t in env.transform) names.Add(t.name);

            foreach (string n in new[] { "Runway", "Taxiway", "Apron", "TaxiConnector", "HangarBody",
                                         "TowerCab", "WindSock", "AimingPoint", "ThresholdBar",
                                         "EdgeLight", "ThresholdLight", "EndLight", "TaxiLight",
                                         "PapiUnit0", "PapiUnit3", "RunwaySign_36", "RunwaySign_18" })
                Assert.IsTrue(names.Contains(n), "缺少机场要素: " + n);
            Assert.IsFalse(names.Contains("RunwaySign_09") || names.Contains("RunwaySign_27"),
                "北向跑道不应再有 09/27 号牌");
            Assert.AreEqual(4, env.GetComponentsInChildren<PapiLight>().Length, "PAPI 四单元");

            foreach (var r in env.GetComponentsInChildren<Renderer>())
            {
                var m = r.sharedMaterial;
                Assert.IsNotNull(m, r.name + " 无材质");
                Assert.IsFalse(m.shader.name.Contains("InternalErrorShader"), r.name + " 呈粉色错误着色器");
            }
        }

        [Test]
        public void RunwayEnvironment_Build_RunwayNumbersArePaintedForBothEnds()
        {
            var env = MakeRunway(Vector3.zero, build: true);
            int num3 = 0, num6 = 0, num1 = 0, num8 = 0;
            foreach (Transform t in env.transform)
            {
                if (t.name == "RunwayNum3") num3++;
                else if (t.name == "RunwayNum6") num6++;
                else if (t.name == "RunwayNum1") num1++;
                else if (t.name == "RunwayNum8") num8++;
            }
            Assert.AreEqual(5, num3, "数字 3 = abcdg 五段");
            Assert.AreEqual(6, num6, "数字 6 = acdefg 六段");
            Assert.AreEqual(1, num1, "数字 1 = 单竖条");
            Assert.AreEqual(7, num8, "数字 8 = 全七段");
        }

        [Test]
        public void RunwayEnvironment_Trees_AvoidPapiRowAndWindsock()
        {
            var env = MakeRunway(Vector3.zero, build: false);
            env.TreeCount = 400;   // 加密, 让"碰巧没砸到"的概率可忽略
            env.Build();
            int trees = 0;
            foreach (Transform t in env.transform)
            {
                if (t.name != "Tree") continue;
                trees++;
                Assert.IsFalse(env.IsTreeForbidden(t.position.x, t.position.z),
                    "树落进禁区 (PAPI 灯排/风向袋): " + t.position);
            }
            Assert.Greater(trees, 300, "禁区只应剔除少数树, 实际 " + trees);
        }

        [Test]
        public void RunwayEnvironment_ForbiddenZone_CoversPapiUnitsAndWindsock()
        {
            var env = MakeRunway(Vector3.zero, build: false);
            for (int i = 0; i < 4; i++)
                Assert.IsTrue(env.IsTreeForbidden(-(env.RunwayWidth / 2f + 12f + i * 6f), env.TouchdownAimM),
                    "PAPI 单元 " + i + " 应在禁区内");
            Assert.IsTrue(env.IsTreeForbidden(-30f, 90f), "风向袋应在禁区内");
            Assert.IsFalse(env.IsTreeForbidden(-30f, 400f), "跑道中段西侧应允许种树");
        }

        // ---------------- PAPI ----------------

        [UnityTest]
        public IEnumerator PapiLight_WhiteAboveSetting_RedBelow()
        {
            var white = new Material(Shader.Find("Sprites/Default"));
            var red = new Material(white);
            var lampGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var unit = new GameObject("PapiTest");
            unit.transform.position = Vector3.zero;
            var papi = unit.AddComponent<PapiLight>();
            papi.SettingDeg = 3f;
            papi.LampRenderer = lampGo.GetComponent<Renderer>();
            papi.WhiteMat = white;
            papi.RedMat = red;

            _camGo = new GameObject("PapiCam");
            _camGo.tag = "MainCamera";
            _camGo.AddComponent<Camera>();

            _camGo.transform.position = new Vector3(0f, 10f, -100f);   // 仰角 5.7° > 3°
            yield return null;
            yield return null;
            Assert.AreSame(white, papi.LampRenderer.sharedMaterial, "高于设定角 → 白");

            _camGo.transform.position = new Vector3(0f, 2f, -100f);    // 仰角 1.1° < 3°
            yield return null;
            yield return null;
            Assert.AreSame(red, papi.LampRenderer.sharedMaterial, "低于设定角 → 红");

            Object.DestroyImmediate(lampGo);
            Object.DestroyImmediate(unit);
            Object.DestroyImmediate(white);
            Object.DestroyImmediate(red);
        }

        // ---------------- 读几何的自动降落 ----------------

        private IEnumerator PlaceAircraft(double north, double east, double alt, double speed, double headingRad)
        {
            var c = C;
            c.State.Position = new Vec3(north, east, -alt);
            c.State.VelocityBody = new Vec3(speed, 0, 0);
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

        // 降落结果 (协程不能返回值, 用字段传出)
        private float _tdAlong, _tdCross, _finalAlong;
        private bool _touched, _reachedRoll;
        private string _lastPhase = "";

        /// <summary>自动降落并记录首次接地点 (相对跑道入口的 along/cross) 与最终停止位置。</summary>
        private IEnumerator LandAndRecord(RunwayEnvironment env, float timeoutS)
        {
            var g = env.Geometry;
            _touched = false; _reachedRoll = false;
            Pilot.Engage(FlightMode.Land);
            float t0 = Time.realtimeSinceStartup;
            float nextLog = 0f;
            while (Time.realtimeSinceStartup - t0 < timeoutS)
            {
                yield return null;
                _lastPhase = Pilot.Phase;
                if (Time.realtimeSinceStartup - t0 >= nextLog)
                {
                    nextLog += 2f;
                    var p0 = new Vector2((float)C.State.Position.X, (float)C.State.Position.Y);
                    g.AlongCross(p0, out float al0, out float cr0);
                    Debug.Log(string.Format(
                        "[LandTrace] t={0:F0}s {1} along={2:F0} cross={3:F1} agl={4:F1} V={5:F1} vs={6:F1} pitch={7:F1} thr={8:F2} gnd={9}",
                        Time.realtimeSinceStartup - t0, Pilot.Phase, al0, cr0,
                        C.State.Altitude - g.ElevationM, C.Airspeed, C.VerticalSpeed, C.PitchDeg,
                        C.Throttle, C.OnGround));
                }
                var pos = new Vector2((float)C.State.Position.X, (float)C.State.Position.Y);
                g.AlongCross(pos, out float along, out float cross);
                _finalAlong = along;
                if (!_touched && C.OnGround) { _touched = true; _tdAlong = along; _tdCross = cross; }
                if (Pilot.Phase == "滑跑") _reachedRoll = true;
                if (C.IsCrashed) yield break;
                if (_reachedRoll && C.Airspeed < 1.0f) yield break;   // 已停稳
            }
        }

        [UnityTest]
        public IEnumerator Land_Runway800_TouchesDownInZoneNearCenterline()
        {
            var env = MakeRunway(Vector3.zero, build: false);
            // 入口前 450m、东偏 6m、高 40m (下滑道上方约 0), 机头朝北
            yield return PlaceAircraft(-450, 6, 40, 20, 0);

            yield return LandAndRecord(env, 100f);

            var g = env.Geometry;
            Assert.IsFalse(C.IsCrashed, "自动降落不坠毁");
            Assert.IsTrue(_touched, "应在限时内接地, 末态阶段 " + _lastPhase + " along=" + _finalAlong);
            Assert.GreaterOrEqual(_tdAlong, 0f, "不应短于入口接地, 实际 along=" + _tdAlong);
            Assert.LessOrEqual(_tdAlong, g.TouchdownZoneEndM,
                "应落在接地区内 (<=" + g.TouchdownZoneEndM + "m), 实际 along=" + _tdAlong);
            Assert.Less(Mathf.Abs(_tdCross), 7f, "接地点应靠近中线 (半宽 10m), 实际 cross=" + _tdCross);
            Assert.Less(_finalAlong, g.LengthM, "滑跑后应停在跑道内, 末端 along=" + _finalAlong);
            Debug.Log("[Runway800] 接地 along=" + _tdAlong.ToString("F0") + "m cross=" + _tdCross.ToString("F1")
                + "m, 末态 along=" + _finalAlong.ToString("F0") + "m 阶段 " + _lastPhase + " ✓");
        }

        [UnityTest]
        public IEnumerator Land_Runway_OffsetFarFromHome_AimsAtRunwayNotHome()
        {
            // Home 在原点, 跑道在东 250m、北 100m 处: 旧逻辑会对准 Home+(-80,0); 读几何后应对准这条跑道。
            var env = MakeRunway(new Vector3(250f, 0f, 100f), build: false);
            yield return PlaceAircraft(-350, 256, 40, 20, 0);   // 入口南 450m、东偏中线 6m

            yield return LandAndRecord(env, 100f);

            var g = env.Geometry;
            Assert.IsFalse(C.IsCrashed, "自动降落不坠毁");
            Assert.IsTrue(_touched, "应在限时内接地, 末态阶段 " + _lastPhase);
            Assert.GreaterOrEqual(_tdAlong, 0f, "接地 along=" + _tdAlong);
            Assert.LessOrEqual(_tdAlong, g.TouchdownZoneEndM, "接地 along=" + _tdAlong);
            Assert.Less(Mathf.Abs(_tdCross), 7f, "应靠近该跑道中线 (而非 Home), cross=" + _tdCross);
            Debug.Log("[RunwayOffset] 接地 along=" + _tdAlong.ToString("F0") + "m cross="
                + _tdCross.ToString("F1") + "m ✓");
        }
    }
}
