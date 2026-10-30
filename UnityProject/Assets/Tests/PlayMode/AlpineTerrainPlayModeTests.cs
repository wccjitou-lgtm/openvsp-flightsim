using System.Collections;
using System.Linq;
using FlightDynamics;
using FlightDynamics.Math;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.TestTools;

namespace FlightSim.Tests
{
    /// <summary>
    /// 雪谷 C1 验收: 物理地形与 Unity Terrain 一致 / 台地 = y 0 / 无水 / 800 m 无风降落 (高原密度) /
    /// 高原性能对比 (海平面 vs 2000 m) / 谷中盘旋可飞性 / 切换后大气偏置设置与复位。
    /// 整个类共用一次 2049² 地形加载。
    /// </summary>
    public class AlpineTerrainPlayModeTests
    {
        private static GameObject _terrainGo;
        private static TerrainFromHeightmap _tf;
        private static ITerrainModel _physics;
        private GameObject _aircraft;

        [OneTimeSetUp]
        public void LoadAlpine()
        {
            TerrainRegistry.Clear();
            AtmosphereModel.AltitudeOffsetM = 0.0;
            _terrainGo = new GameObject("AlpineTerrainTest");
            _tf = _terrainGo.AddComponent<TerrainFromHeightmap>();
            _tf.RawFileName = SceneRegistry.Alpine.TerrainRaw;
            _tf.MetaFileName = SceneRegistry.Alpine.TerrainMeta;
            Assert.IsNotNull(_tf.LoadTerrain(), "雪谷地形应加载成功 (StreamingAssets/Terrain/alpine/ 缺文件?)");
            _physics = _tf.RegisteredTerrain;
            Physics.SyncTransforms();
        }

        [OneTimeTearDown]
        public void UnloadAlpine()
        {
            if (_terrainGo != null) Object.DestroyImmediate(_terrainGo);
            TerrainRegistry.Clear();
            AtmosphereModel.AltitudeOffsetM = 0.0;
        }

        [SetUp]
        public void SetUp()
        {
            AtmosphereModel.AltitudeOffsetM = 0.0;
            if (_terrainGo != null) TerrainRegistry.Set(_physics);
        }

        [TearDown]
        public void TearDown()
        {
            if (_aircraft != null) Object.DestroyImmediate(_aircraft);
            _aircraft = null;
            GameObject g;
            while ((g = GameObject.Find("TestUAV")) != null) Object.DestroyImmediate(g);
            AtmosphereModel.AltitudeOffsetM = 0.0;
            if (_terrainGo != null) TerrainRegistry.Set(_physics);
        }

        private GameObject MakeAircraft()
        {
            var go = new GameObject("TestUAV");
            go.AddComponent<AircraftController>();
            go.AddComponent<AutoPilotInput>();
            go.AddComponent<FlightGuard>();
            go.AddComponent<ModePilot>();
            return go;
        }

        // ---------------- 元数据 / 一致性 ----------------

        [Test]
        public void Meta_PlatformAtY0_MslRecorded_NoSea()
        {
            Assert.AreEqual(2049, _tf.SizePx);
            Assert.AreEqual(100f, _tf.AirportHeightM, 1e-3f);
            Assert.AreEqual(2000f, _tf.PlatformMslM, 1e-3f, "meta 记录台地真实海拔");
            Assert.AreEqual(SceneRegistry.Alpine.PlatformMslM, _tf.PlatformMslM, 1e-3f, "profile 与 meta 的海拔一致");
            Assert.IsFalse(_tf.HasSea);
            Assert.AreEqual(-100f, _terrainGo.transform.position.y, 1e-3f);
            Assert.AreSame(_tf.PhysicsTerrain, _tf.RegisteredTerrain, "无海: 登记的就是物理地形, 不包水面地板");
            Assert.IsFalse(TerrainRegistry.IsWater(400, 1200));
        }

        [Test]
        public void CoreTerrain_MatchesUnityTerrain_At60RandomPoints()
        {
            var terrain = _terrainGo.GetComponent<Terrain>();
            var rng = new System.Random(2026);
            float worst = 0f;
            for (int i = 0; i < 60; i++)
            {
                double north = (rng.NextDouble() * 2 - 1) * 1900.0;
                double east = (rng.NextDouble() * 2 - 1) * 1900.0;
                double core = _physics.HeightAt(north, east);
                float unity = terrain.SampleHeight(new Vector3((float)east, 0f, (float)north)) + terrain.transform.position.y;
                worst = Mathf.Max(worst, Mathf.Abs((float)core - unity));
                Assert.AreEqual(core, unity, 0.5, "点 " + i + " (N" + north.ToString("F0") + ", E" + east.ToString("F0") + ")");
            }
            Debug.Log("[AlpineTerrain] 核心库 vs Unity Terrain 60 点最大差 " + worst.ToString("F3") + " m");
        }

        [Test]
        public void TerrainCollider_RaycastHitsAgreeWithCoreHeight()
        {
            var rng = new System.Random(79);
            for (int i = 0; i < 20; i++)
            {
                double north = (rng.NextDouble() * 2 - 1) * 1800.0;
                double east = (rng.NextDouble() * 2 - 1) * 1800.0;
                Assert.IsTrue(Physics.Raycast(new Vector3((float)east, 1300f, (float)north), Vector3.down, out var hit, 2500f));
                Assert.AreEqual(_physics.HeightAt(north, east), hit.point.y, 0.5, "点 " + i);
            }
        }

        [Test]
        public void AirportPlatform_IsFlatAtY0_AndMountainsRiseHundredsOfMetres()
        {
            var terrain = _terrainGo.GetComponent<Terrain>();
            float minH = float.MaxValue, maxH = float.MinValue, minU = float.MaxValue, maxU = float.MinValue;
            for (double e = -80; e <= 170; e += 10)
                for (double n = -110; n <= 910; n += 20)
                {
                    float c = (float)_physics.HeightAt(n, e);
                    float u = terrain.SampleHeight(new Vector3((float)e, 0f, (float)n)) + terrain.transform.position.y;
                    minH = Mathf.Min(minH, c); maxH = Mathf.Max(maxH, c);
                    minU = Mathf.Min(minU, u); maxU = Mathf.Max(maxU, u);
                }
            // 16 位量化: 936 m 量程下一级 = 1.4 cm, 台地值与 100.000 最多差半级 (7 mm); 容差取一整级
            Assert.Less(maxH - minH, 0.01f);
            Assert.Less(maxU - minU, 0.01f);
            Assert.AreEqual(0f, (minH + maxH) / 2f, 0.02f);
            Assert.AreEqual(0f, (minU + maxU) / 2f, 0.02f);

            float top = 0f;
            for (double n = -1800; n <= 1800; n += 60)
                for (double e = -1800; e <= 1800; e += 60)
                    top = Mathf.Max(top, (float)_physics.HeightAt(n, e));
            Assert.Greater(top, 650f, "山体应高出台地数百米: " + top);
            Assert.Less(top, 950f);
        }

        [Test]
        public void FloorIsWideAndFlatAroundRunway_WallsStartBeyond250m()
        {
            for (double n = 0; n <= 800; n += 100)
                for (double e = -250; e <= 250; e += 25)
                    Assert.Less(_physics.HeightAt(n, e), 8.0, "走廊两侧 250 m 内不应起坡 (N" + n + ", E" + e + ")");
        }

        // ---------------- 飞机 ----------------

        [UnityTest]
        public IEnumerator Aircraft_RestsOnPlatform_AtAltitudeDensity()
        {
            AtmosphereModel.AltitudeOffsetM = 2000.0;
            _aircraft = MakeAircraft();
            var c = _aircraft.GetComponent<AircraftController>();
            c.State.Position = new Vec3(12, 0, -0.327);
            c.State.VelocityBody = new Vec3(0, 0, 0);
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 2.5f) yield return null;
            Assert.IsTrue(c.OnGround);
            Assert.Less(Mathf.Abs(c.Altitude - 0.33f), 0.4f);
            Assert.IsFalse(c.IsCrashed);
        }

        [UnityTest, Timeout(240000)]
        public IEnumerator Land_On800mRunway_NoWind_AtPlateauDensity_TouchesDownInZone()
        {
            AtmosphereModel.AltitudeOffsetM = 2000.0;
            var runwayGo = new GameObject("RunwayEnvironment");
            var env = runwayGo.AddComponent<RunwayEnvironment>();
            env.BuildOnStart = false;
            env.BuildGroundPlane = false;
            try
            {
                _aircraft = MakeAircraft();
                var c = _aircraft.GetComponent<AircraftController>();
                c.State.Position = new Vec3(-450, 6, -40.0);
                c.State.VelocityBody = new Vec3(20, 0, 0);
                c.State.Attitude = Quat.FromEuler321(0, 0, 0);
                float tw = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - tw < 0.6f) yield return null;

                var g = env.Geometry;
                var pilot = _aircraft.GetComponent<ModePilot>();
                pilot.Engage(FlightMode.Land);
                bool touched = false, reachedRoll = false;
                float tdAlong = 0, tdCross = 0, minAgl = 1e9f, t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 100f)
                {
                    yield return null;
                    g.AlongCross(new Vector2((float)c.State.Position.X, (float)c.State.Position.Y), out float along, out float cross);
                    if (!touched && !c.OnGround && along < 0f) minAgl = Mathf.Min(minAgl, c.AltitudeAgl);
                    if (!touched && c.OnGround) { touched = true; tdAlong = along; tdCross = cross; }
                    if (pilot.Phase == "滑跑") reachedRoll = true;
                    if (c.IsCrashed) break;
                    if (reachedRoll && c.Airspeed < 1f) break;
                }
                Assert.IsFalse(c.IsCrashed, "雪谷高原上自动降落不坠毁: " + c.CrashReason);
                Assert.IsTrue(touched, "应在限时内接地");
                Assert.GreaterOrEqual(tdAlong, 0f);
                Assert.LessOrEqual(tdAlong, g.TouchdownZoneEndM, "应落在接地区内, along=" + tdAlong);
                Assert.Less(Mathf.Abs(tdCross), 7f, "cross=" + tdCross);
                Assert.Greater(minAgl, 1.2f, "进近最小 AGL " + minAgl);   // U16 新机型基线: 实测 1.77 (走廊阈值随之)
                Debug.Log("[AlpineTerrain] 雪谷自动降落(无风, 2000 m 密度): 接地 along=" + tdAlong.ToString("F0") + "m cross=" +
                          tdCross.ToString("F1") + "m, 进近最小 AGL " + minAgl.ToString("F1") + "m");
            }
            finally { Object.DestroyImmediate(runwayGo); }
        }

        /// <summary>一次满油门自动起飞的结果: 离地滑跑距离/时间、离地后 2~7 s 的平均爬升率、离地空速。</summary>
        private struct TakeoffResult { public float RollM, RollS, ClimbMps, LiftoffMps; public bool Crashed, Lifted; }

        private IEnumerator Takeoff(double offsetM, System.Action<TakeoffResult> done)
        {
            AtmosphereModel.AltitudeOffsetM = offsetM;
            var runwayGo = new GameObject("RunwayEnvironment");
            var env = runwayGo.AddComponent<RunwayEnvironment>();
            env.BuildOnStart = false;
            env.BuildGroundPlane = false;
            var res = new TakeoffResult();
            try
            {
                _aircraft = MakeAircraft();
                var c = _aircraft.GetComponent<AircraftController>();
                c.State.Position = new Vec3(12, 0, -0.327);
                c.State.VelocityBody = new Vec3(0, 0, 0);
                float tStart = Time.realtimeSinceStartup;
                float tLift = -1f, vsSum = 0f; int vsN = 0;
                bool wasOnGround = false;
                while (Time.realtimeSinceStartup - tStart < 60f)
                {
                    yield return null;
                    if (c.IsCrashed) { res.Crashed = true; break; }
                    if (c.OnGround) wasOnGround = true;
                    if (wasOnGround && !c.OnGround && tLift < 0f && c.Airspeed > 5f)
                    {
                        tLift = Time.realtimeSinceStartup;
                        res.Lifted = true;
                        res.RollM = (float)c.State.Position.X - 12f;
                        res.RollS = tLift - tStart;
                        res.LiftoffMps = c.Airspeed;
                    }
                    if (tLift > 0f)
                    {
                        float dt = Time.realtimeSinceStartup - tLift;
                        if (dt >= 2f && dt <= 7f) { vsSum += c.VerticalSpeed; vsN++; }
                        if (dt > 7f) break;
                    }
                }
                res.ClimbMps = vsN > 0 ? vsSum / vsN : 0f;
            }
            finally
            {
                if (_aircraft != null) Object.DestroyImmediate(_aircraft);
                _aircraft = null;
                Object.DestroyImmediate(runwayGo);
            }
            done(res);
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator Plateau_Performance_SeaLevelVsTwoThousandMetres_Table()
        {
            TakeoffResult sea = default, alt = default;
            yield return Takeoff(0.0, r => sea = r);
            yield return Takeoff(2000.0, r => alt = r);

            // 失速速度估计 (CLmax=1.5, 与 HUD 同口径), 按各自密度
            var probe = MakeAircraft();
            var plane = probe.GetComponent<AircraftController>().Plane;
            double mg = plane.Mass.MassKg * AtmosphereModel.G0, S = plane.Aero.Data.Sref;
            Object.DestroyImmediate(probe);
            AtmosphereModel.AltitudeOffsetM = 0.0;   // Takeoff() 结束时偏置停在 2000
            double rhoSea = AtmosphereModel.Density(0);
            AtmosphereModel.AltitudeOffsetM = 2000.0;
            double rhoAlt = AtmosphereModel.Density(0);
            AtmosphereModel.AltitudeOffsetM = 0.0;
            double vsSea = System.Math.Sqrt(2 * mg / (rhoSea * S * 1.5));
            double vsAlt = System.Math.Sqrt(2 * mg / (rhoAlt * S * 1.5));

            Debug.Log("[AlpineTerrain] 高原性能对比 (同机型 同油门 满油门; 推力不随密度衰减, 见任务书 D1):\n" +
                      "  项目                 海平面      2000 m\n" +
                      "  空气密度 kg/m3       " + rhoSea.ToString("F3") + "       " + rhoAlt.ToString("F3") + "\n" +
                      "  失速速度(估) m/s     " + vsSea.ToString("F1") + "         " + vsAlt.ToString("F1") + "\n" +
                      "  起飞滑跑距离 m       " + sea.RollM.ToString("F0") + "         " + alt.RollM.ToString("F0") + "  (抬轮 15 m/s 空速不变)\n" +
                      "  离地用时 s           " + sea.RollS.ToString("F1") + "         " + alt.RollS.ToString("F1") + "\n" +
                      "  离地空速 m/s         " + sea.LiftoffMps.ToString("F1") + "         " + alt.LiftoffMps.ToString("F1") + "\n" +
                      "  离地后爬升率 m/s     " + sea.ClimbMps.ToString("F2") + "         " + alt.ClimbMps.ToString("F2"));

            Assert.IsFalse(sea.Crashed || alt.Crashed, "两次起飞都不应坠毁");
            Assert.IsTrue(sea.Lifted && alt.Lifted, "两次都应在限时内离地");
            Assert.Less(rhoAlt / rhoSea, 0.83, "2000 m 密度应比海平面低 17% 以上");
            Assert.Greater(vsAlt / vsSea, 1.08, "失速速度应随密度升高约 10%");
            Assert.Greater(alt.RollM, sea.RollM, "高原起飞滑跑应更长: 海平面 " + sea.RollM + " m, 2000 m " + alt.RollM + " m");
        }

        [UnityTest, Timeout(240000)]
        public IEnumerator Loiter_InValley_CircleFitsInsideValleyFloor()
        {
            AtmosphereModel.AltitudeOffsetM = 2000.0;
            _aircraft = MakeAircraft();
            var c = _aircraft.GetComponent<AircraftController>();
            c.State.Position = new Vec3(0, 45, -60.0);          // 谷中线上方 60 m
            c.State.VelocityBody = new Vec3(22, 0, 0);
            c.State.Attitude = Quat.FromEuler321(0, 0, 0);
            yield return new WaitForSeconds(0.5f);
            var pilot = _aircraft.GetComponent<ModePilot>();
            pilot.Engage(FlightMode.Loiter);
            float minE = float.MaxValue, maxE = float.MinValue, minN = float.MaxValue, maxN = float.MinValue, minAgl = float.MaxValue;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 60f)
            {
                yield return null;
                if (c.IsCrashed) break;
                if (Time.realtimeSinceStartup - t0 < 12f) continue;   // 前 12 s 切入盘旋
                float n = (float)c.State.Position.X, e = (float)c.State.Position.Y;
                minE = Mathf.Min(minE, e); maxE = Mathf.Max(maxE, e);
                minN = Mathf.Min(minN, n); maxN = Mathf.Max(maxN, n);
                minAgl = Mathf.Min(minAgl, c.AltitudeAgl);
            }
            Assert.IsFalse(c.IsCrashed, "谷中盘旋不应坠毁: " + c.CrashReason);
            float diameterE = maxE - minE, diameterN = maxN - minN;
            float floorWidth = 780f;   // 谷底宽度下限 = 2 × (FLOOR_HALF_W - FLOOR_HALF_W_VAR) (terrain_alpine.py)
            Debug.Log("[AlpineTerrain] 谷中盘旋 (2000 m 密度, 倾斜 " + pilot.LoiterBankDeg + "°, 空速 " + c.Airspeed.ToString("F1") +
                      " m/s): 稳态轨迹东西跨度 " + diameterE.ToString("F0") + " m, 南北跨度 " + diameterN.ToString("F0") +
                      " m, 约占谷底宽度 " + (diameterE / floorWidth).ToString("P0") + ", 最小 AGL " + minAgl.ToString("F0") + " m");
            Assert.Less(diameterE, floorWidth, "盘旋圆应能放进谷底宽度, 实测直径 " + diameterE);
            Assert.Greater(minAgl, 15f, "盘旋中不应贴地");
        }

        // ---------------- 边界返航 + 地形避让 (雪谷真地形) ----------------

        private IEnumerator FlyToBoundary(bool avoidance, float headingDeg, float startNorth, float startEast,
                                          float aglM, System.Action<string, bool, float> done)
        {
            AtmosphereModel.AltitudeOffsetM = 2000.0;
            _aircraft = MakeAircraft();
            var c = _aircraft.GetComponent<AircraftController>();
            var guard = _aircraft.GetComponent<FlightGuard>();
            guard.BoundaryRadius = 1800f;
            guard.TerrainAvoidance = avoidance;
            float hdg = headingDeg * Mathf.Deg2Rad;
            float ground = (float)TerrainRegistry.HeightAt(startNorth, startEast);
            Object.Destroy(_aircraft.GetComponent<ModePilot>());
            Object.Destroy(_aircraft.GetComponent<AutoPilotInput>());
            yield return null;
            var pilot = _aircraft.AddComponent<ModePilot>();
            c.State.Position = new Vec3(startNorth, startEast, -(ground + aglM));
            c.State.VelocityBody = new Vec3(22, 0, 0);
            c.State.Attitude = Quat.FromEuler321(0, 0, hdg);
            yield return new WaitForSeconds(0.4f);
            pilot.Engage(FlightMode.Stab);
            float minAgl = 999f, minAglGuard = 999f, maxDist = 0f, t0 = Time.realtimeSinceStartup, lastTurn = -999f;
            bool crashed = false, climbed = false;
            while (Time.realtimeSinceStartup - t0 < 70f)
            {
                yield return null;
                if (guard.IsTurningBack) lastTurn = Time.realtimeSinceStartup;
                if (c.IsCrashed) { crashed = true; break; }
                minAgl = Mathf.Min(minAgl, c.AltitudeAgl);
                if (guard.IsTurningBack) minAglGuard = Mathf.Min(minAglGuard, c.AltitudeAgl);
                maxDist = Mathf.Max(maxDist, guard.DistanceToHome);
                if (guard.IsClimbingToClear) climbed = true;
            }
            // 归因: 返航接管期间或刚结束 5 s 内的坠毁算返航的问题; 之后是 Stab 自己按航向保持飞进谷壁 (返航已放手)
            bool crashedUnderGuard = crashed && Time.realtimeSinceStartup - lastTurn < 5f;
            done("航向 " + headingDeg + "° 起点 N" + startNorth + " E" + startEast + " AGL" + aglM + " 避让=" + avoidance +
                 ": 坠毁=" + crashed + (crashed ? "(" + c.CrashReason + ", 返航接管中/刚结束=" + crashedUnderGuard + ")" : "") +
                 " 全程最小AGL=" + minAgl.ToString("F1") + " 返航期间最小AGL=" + minAglGuard.ToString("F1") +
                 " 最远离起飞点=" + maxDist.ToString("F0") + " m 爬升触发=" + climbed + " 末距离 " + guard.DistanceToHome.ToString("F0"),
                 crashedUnderGuard, minAglGuard);
            Object.DestroyImmediate(_aircraft);
            _aircraft = null;
            yield return null;
        }

        [UnityTest, Timeout(400000)]
        public IEnumerator Guard_TerrainAvoidance_AtValleyBoundary_DoesNotCrash_NorthAndNortheast()
        {
            foreach (var cfg in new[] { new[] { 0f, 1550f, 0f }, new[] { 20f, 1450f, 100f } })
            {
                string lineOff = null, lineOn = null; bool crOff = false, crOn = false; float aglOff = 0, aglOn = 0;
                yield return FlyToBoundary(false, cfg[0], cfg[1], cfg[2], 40f, (l, c, a) => { lineOff = l; crOff = c; aglOff = a; });
                yield return FlyToBoundary(true, cfg[0], cfg[1], cfg[2], 40f, (l, c, a) => { lineOn = l; crOn = c; aglOn = a; });
                Debug.Log("[AlpineTerrain][Guard] " + lineOff);
                Debug.Log("[AlpineTerrain][Guard] " + lineOn);
                Assert.IsFalse(crOn, "开启地形避让, 返航接管期间不应坠毁: " + lineOn);
                Assert.Greater(aglOn, -0.5f, "开启后返航期间不坠毁 (允许轮子接地): " + lineOn);   // U16 新机型基线: 实测 -0.046 接地未坠毁
            }
        }

        // ---------------- C3 现状观察 (只记录, 不作断言) ----------------

        /// <summary>
        /// 观察: 自稳(Stab)保持航向直飞谷壁, 起落架触地 (OnGround) 能不能覆盖"撞坡"? 机体/翼尖有没有穿进地形?
        /// 记录: 是否坠毁及原因、撞击时空速/姿态、最小 AGL (负 = 穿地)、翼尖离地。按名字点选运行, 结果写进文档。
        /// </summary>
        [UnityTest, Explicit("C3 观察项, 只记录现状行为"), Timeout(240000)]
        public IEnumerator Observe_FlyIntoValleyWall_FromTheCorridorSide()
        {
            foreach (var headingDeg in new[] { -90f, -60f, -20f })
            {
                AtmosphereModel.AltitudeOffsetM = 2000.0;
                _aircraft = MakeAircraft();
                var c = _aircraft.GetComponent<AircraftController>();
                float hdg = headingDeg * Mathf.Deg2Rad;
                c.State.Position = new Vec3(400, 0, -40.0);
                c.State.VelocityBody = new Vec3(22, 0, 0);
                c.State.Attitude = Quat.FromEuler321(0, 0, hdg);
                yield return new WaitForSeconds(0.4f);
                var pilot = _aircraft.GetComponent<ModePilot>();
                pilot.Engage(FlightMode.Stab);
                float minAgl = float.MaxValue, t0 = Time.realtimeSinceStartup, impactSpeed = 0f, impactPitch = 0f, impactBank = 0f;
                double impactN = 0, impactE = 0, impactAlt = 0;
                bool crashed = false, anyGround = false;
                float firstGroundT = -1f;
                while (Time.realtimeSinceStartup - t0 < 90f)
                {
                    yield return null;
                    minAgl = Mathf.Min(minAgl, c.AltitudeAgl);
                    if (c.OnGround && !anyGround) { anyGround = true; firstGroundT = Time.realtimeSinceStartup - t0; }
                    if (c.IsCrashed)
                    {
                        crashed = true; impactSpeed = c.Airspeed; impactPitch = c.PitchDeg; impactBank = c.BankDeg;
                        impactN = c.State.Position.X; impactE = c.State.Position.Y; impactAlt = c.Altitude;
                        break;
                    }
                }
                double terrainAtEnd = TerrainRegistry.HeightAt(c.State.Position.X, c.State.Position.Y);
                Debug.Log("[AlpineTerrain][C3观察] 航向 " + headingDeg + "° Stab 直飞谷壁: 坠毁=" + crashed +
                          (crashed ? " 原因=" + c.CrashReason + " 空速=" + impactSpeed.ToString("F1") + " 俯仰=" + impactPitch.ToString("F0") +
                                     "° 滚转=" + impactBank.ToString("F0") + "° 位置 N" + impactN.ToString("F0") + " E" + impactE.ToString("F0") +
                                     " 高度 " + impactAlt.ToString("F1") + " (地形 " + terrainAtEnd.ToString("F1") + ")" : " (90 s 内未坠毁)") +
                          " | 最小AGL=" + minAgl.ToString("F1") + " m | 起落架触地=" + anyGround +
                          (anyGround ? "(t=" + firstGroundT.ToString("F1") + "s)" : "") +
                          " | 末高度 " + c.Altitude.ToString("F0") + " m 末AGL " + c.AltitudeAgl.ToString("F0") + " m");
                Object.DestroyImmediate(_aircraft);
                _aircraft = null;
                yield return null;
            }
        }

        // ---------------- 针叶林 ----------------

        [UnityTest, Timeout(120000)]
        public IEnumerator Conifers_StayBelowTreeline_OnGentleSlopes_AndThinOutWithAltitude()
        {
            var rwGo = new GameObject("RunwayEnvironment");
            var env = rwGo.AddComponent<RunwayEnvironment>();
            env.BuildOnStart = false; env.BuildGroundPlane = false; env.TreeCount = 0;
            var scGo = new GameObject("ConiferScatter");
            try
            {
                yield return null;
                var sc = scGo.AddComponent<ScatterEnvironment>();
                sc.AutoBuildOnStart = false;
                sc.AreaRadius = SceneRegistry.Alpine.BoundaryRadius;
                sc.TreeCount = 0; sc.RockCount = 0; sc.BushCount = 0;
                sc.ConiferCount = 800;
                sc.UseClearZone = false;
                sc.Build();

                int total = 0, low = 0, high = 0;
                float maxY = float.MinValue, minY = float.MaxValue;
                foreach (Transform t in scGo.transform)
                {
                    if (!t.name.StartsWith("Conifer_")) continue;
                    total++;
                    maxY = Mathf.Max(maxY, t.position.y);
                    minY = Mathf.Min(minY, t.position.y);
                    if (t.position.y < 40f) low++; else if (t.position.y > 110f) high++;
                    // 脚下坡度: 用物理地形差分 (±4 m)
                    double n = t.position.z, e = t.position.x;
                    double dzdx = (_physics.HeightAt(n, e + 4) - _physics.HeightAt(n, e - 4)) / 8.0;
                    double dzdn = (_physics.HeightAt(n + 4, e) - _physics.HeightAt(n - 4, e)) / 8.0;
                    double slope = System.Math.Atan(System.Math.Sqrt(dzdx * dzdx + dzdn * dzdn)) * 180.0 / System.Math.PI;
                    Assert.LessOrEqual(slope, sc.ConiferMaxSlopeDeg + 6.0, "针叶树不应种在陡坡: " + t.name + " 坡度 " + slope.ToString("F0"));
                }
                Debug.Log("[AlpineTerrain] 针叶树 " + total + " 棵, 高度范围 " + minY.ToString("F0") + ".." + maxY.ToString("F0") +
                          " m (谷底带 <40 m: " + low + ", 高坡带 >110 m: " + high + ")");
                Assert.Greater(total, 300, "应能种出足够数量的针叶树");
                Assert.LessOrEqual(maxY, sc.ConiferMaxRelM + 0.5f, "树线以上不应有树");
                Assert.GreaterOrEqual(minY, sc.ConiferMinRelM - 0.5f);
                Assert.Greater(low, high * 2, "越高越稀: 谷底带数量应远多于高坡带 (low=" + low + ", high=" + high + ")");
            }
            finally
            {
                Object.DestroyImmediate(scGo);
                Object.DestroyImmediate(rwGo);
                GameObject g;
                while ((g = GameObject.Find("__ScatterRender")) != null) Object.DestroyImmediate(g);
            }
        }

        // ---------------- 切换: 大气偏置的设置与复位 ----------------

        [UnityTest, Timeout(300000)]
        public IEnumerator ZZ_Switch_SetsAltitudeOffset_AndForestResetsIt()
        {
            Object.DestroyImmediate(_terrainGo);
            _terrainGo = null;
            TerrainRegistry.Clear();

            _aircraft = new GameObject("TestUAV");
            _aircraft.AddComponent<AircraftController>();
            _aircraft.AddComponent<AutoPilotInput>();
            _aircraft.AddComponent<WindSettings>();
            _aircraft.AddComponent<FlightGuard>();
            _aircraft.AddComponent<ModePilot>();
            var sceneProfile = ScriptableObject.CreateInstance<VolumeProfile>();
            var volGo = new GameObject("GlobalVolume");
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.sharedProfile = sceneProfile;
            var sunGo = new GameObject("Directional Light");
            var sunLight = sunGo.AddComponent<Light>();
            sunLight.type = LightType.Directional;
            sunLight.color = new Color(1.0f, 0.96f, 0.88f);
            sunGo.transform.rotation = Quaternion.Euler(38f, -40f, 0f);
            var swGo = new GameObject("SceneSwitcher");
            var sw = swGo.AddComponent<SceneSwitcher>();
            sw.PersistChoice = false;
            try
            {
                yield return null;
                yield return Run(sw, "alpine");
                Assert.IsNull(sw.LastError, sw.LastError);
                Assert.AreEqual(2000.0, AtmosphereModel.AltitudeOffsetM, 1e-9, "雪谷应把大气偏置设为台地海拔");
                Assert.AreEqual(1.0066, AtmosphereModel.Density(0), 2e-4, "跑道面处密度 = ISA 2000 m");
                Assert.IsNull(sw.Handles.Sea, "雪谷没有海");
                Assert.IsTrue(TerrainRegistry.HasTerrain);

                // C2: 太阳 / 风 / Volume / 生态
                Assert.AreNotSame(sceneProfile, vol.sharedProfile, "雪谷应换上自己的 Volume Profile");
                Assert.AreEqual("AlpineVolumeProfile", vol.sharedProfile.name);
                Assert.AreEqual(SceneRegistry.Alpine.SunPitchDeg, sunGo.transform.eulerAngles.x, 0.01f, "太阳俯角");
                Assert.AreEqual(SceneRegistry.Alpine.SunYawDeg, sunGo.transform.eulerAngles.y, 0.01f, "太阳朝向");
                Assert.AreEqual(SceneRegistry.Alpine.SunColor.Value.g, sunLight.color.g, 1e-4f, "太阳颜色");
                var wind = _aircraft.GetComponent<WindSettings>();
                Assert.AreEqual(8f, wind.WindSpeed, 1e-4f, "山谷风 8 m/s");
                Assert.AreEqual(0f, wind.WindDirectionDeg, 1e-4f, "北风 = 对朝北跑道逆风");
                Assert.IsNull(sw.Handles.Wildlife, "雪谷没有动物");
                Assert.IsNotNull(sw.Handles.Birds, "少量猛禽");
                Assert.AreEqual(1, sw.Handles.Birds.FlockCount);
                int conifers = 0;
                foreach (Transform t in sw.Handles.Scatter.transform) if (t.name.StartsWith("Conifer_")) conifers++;
                Assert.Greater(conifers, 400, "雪谷应有针叶林: " + conifers);

                yield return new WaitForSeconds(1f);
                var ctl = _aircraft.GetComponent<AircraftController>();
                Assert.IsTrue(ctl.OnGround, "飞机应停在雪谷台地上");
                Assert.IsFalse(ctl.IsCrashed);
                Assert.That(ctl.Altitude, Is.InRange(0.1f, 0.8f));

                yield return Run(sw, "forest");
                Assert.AreEqual(0.0, AtmosphereModel.AltitudeOffsetM, 1e-9, "回到森林应复位为海平面");
                Assert.AreEqual(1.225, AtmosphereModel.Density(0), 1e-6);
                Assert.AreSame(sceneProfile, vol.sharedProfile, "回到森林应恢复场景自带 Volume Profile");
                Assert.AreEqual(38f, sunGo.transform.eulerAngles.x, 0.01f, "太阳俯角复位");
                Assert.AreEqual(320f, sunGo.transform.eulerAngles.y, 0.01f, "太阳朝向复位 (-40° = 320°)");
                Assert.AreEqual(0.96f, sunLight.color.g, 1e-4f, "太阳颜色复位");
            }
            finally
            {
                Object.DestroyImmediate(swGo);
                Object.DestroyImmediate(volGo);
                Object.DestroyImmediate(sunGo);
                Object.DestroyImmediate(sceneProfile);
                GameObject g;
                while ((g = GameObject.Find(EnvironmentBuilder.RootName)) != null) Object.DestroyImmediate(g);
                while ((g = GameObject.Find("__ScatterRender")) != null) Object.DestroyImmediate(g);
                TerrainRegistry.Clear();
                AtmosphereModel.AltitudeOffsetM = 0.0;
            }
        }

        private static IEnumerator Run(SceneSwitcher sw, string id)
        {
            Assert.IsTrue(sw.SwitchTo(id), "SwitchTo(" + id + ")");
            float t0 = Time.realtimeSinceStartup;
            while (sw.IsLoading)
            {
                Assert.Less(Time.realtimeSinceStartup - t0, 120f, "切换 " + id + " 超时");
                yield return null;
            }
            yield return null;
        }

        // ---------------- 出图 (按名字点选才运行) ----------------

        /// <summary>
        /// 目检用出图 (运行时路径)。运行: -runTests -testPlatform PlayMode
        /// -testFilter "FlightSim.Tests.AlpineTerrainPlayModeTests.RenderSnapshots" (必须带图形设备, 不要 -nographics)。
        /// 输出 Logs/Snapshots/c1_alpine_play/*.png。
        /// </summary>
        [UnityTest, Explicit("出图工具, 不属于回归; 点名运行")]
        public IEnumerator RenderSnapshots()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../Logs/Snapshots/c1_alpine_play"));
            System.IO.Directory.CreateDirectory(dir);
            var created = new System.Collections.Generic.List<GameObject>();
            try
            {
                var vgo = new GameObject("SnapVolume"); created.Add(vgo);
                var vol = vgo.AddComponent<Volume>();
                vol.isGlobal = true;
                var vp = string.IsNullOrEmpty(SceneRegistry.Alpine.VolumeProfileResource)
                    ? null : Resources.Load<VolumeProfile>(SceneRegistry.Alpine.VolumeProfileResource);
#if UNITY_EDITOR
                if (vp == null) vp = UnityEditor.AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/Rendering/AirportVolumeProfile.asset");
#endif
                vol.sharedProfile = vp;

                var sunGo = new GameObject("SnapSun"); created.Add(sunGo);
                var sun = sunGo.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.color = SceneRegistry.Alpine.SunColor ?? Color.white;
                sunGo.transform.rotation = Quaternion.Euler(SceneRegistry.Alpine.SunPitchDeg, SceneRegistry.Alpine.SunYawDeg, 0f);
                var hd = sunGo.AddComponent<HDAdditionalLightData>();
                hd.EnableShadows(true);
                hd.SetIntensity(1.0e5f, LightUnit.Lux);

                var rwGo = new GameObject("SnapRunway"); created.Add(rwGo);
                var rw = rwGo.AddComponent<RunwayEnvironment>();
                rw.BuildOnStart = false; rw.BuildGroundPlane = false; rw.TreeCount = 0;
                rw.Build();
                Physics.SyncTransforms();
                yield return new WaitForFixedUpdate();

                var scGo = new GameObject("SnapScatter"); created.Add(scGo);
                var sc = scGo.AddComponent<ScatterEnvironment>();
                var p = SceneRegistry.Alpine;
                sc.AutoBuildOnStart = false; sc.AreaRadius = p.BoundaryRadius;
                sc.TreeCount = 0; sc.BushCount = 0; sc.RockCount = p.RockCount; sc.ConiferCount = p.ConiferCount;
                var ab = rw.AirportBounds;
                sc.UseClearZone = true; sc.ClearCenter = ab.center; sc.ClearHalfSize = ab.size * 0.5f;
                sc.Build();

                var camGo = new GameObject("SnapCam"); created.Add(camGo);
                var cam = camGo.AddComponent<Camera>();
                cam.farClipPlane = 12000f; cam.nearClipPlane = 0.3f; cam.fieldOfView = 60f;
                camGo.AddComponent<HDAdditionalCameraData>().antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
                cam.enabled = false;

                var shots = new (string name, Vector3 pos, Vector3 look)[]
                {
                    ("a_overview_high",  new Vector3(-1500f, 1500f, -2200f), new Vector3(0f, 100f, 300f)),
                    ("b_valley_from_south", new Vector3(40f, 150f, -1700f), new Vector3(40f, 120f, 900f)),
                    ("c_final_approach", new Vector3(40f, 25f, -520f),     new Vector3(40f, 0f, 300f)),
                    ("d_runway_threshold", new Vector3(0f, 5f, -40f),      new Vector3(0f, 3f, 400f)),
                    ("e_west_wall",      new Vector3(40f, 90f, 500f),      new Vector3(-900f, 350f, 700f)),
                    ("f_east_wall",      new Vector3(40f, 90f, 500f),      new Vector3(1000f, 380f, 600f)),
                    ("g_top_down",       new Vector3(0f, 3600f, 1f),       new Vector3(0f, 0f, 0f)),
                    ("h_creek",          new Vector3(320f, 30f, 600f),     new Vector3(280f, -90f, 760f)),
                    ("i_ridge_high",     new Vector3(-700f, 700f, 1300f),  new Vector3(40f, 100f, 300f)),
                };
                foreach (var sh in shots)
                {
                    cam.transform.position = sh.pos;
                    cam.transform.LookAt(sh.look);
                    var rt = new RenderTexture(1280, 720, 24);
                    cam.targetTexture = rt;
                    for (int i = 0; i < 4; i++) { cam.Render(); yield return null; }
                    cam.Render();
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                    tex.Apply();
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, sh.name + ".png"), tex.EncodeToPNG());
                    RenderTexture.active = prev;
                    cam.targetTexture = null;
                    Object.DestroyImmediate(rt);
                    Object.DestroyImmediate(tex);
                }
                Debug.Log("[AlpineTerrain] 出图完成: " + dir);
            }
            finally
            {
                foreach (var g in created) if (g != null) Object.DestroyImmediate(g);
                foreach (var t in Object.FindObjectsOfType<Transform>().Where(t => t.name == "__ScatterRender").ToArray())
                    Object.DestroyImmediate(t.gameObject);
            }
        }

    }
}
