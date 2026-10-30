using System.Collections;
using FlightDynamics;
using FlightDynamics.Math;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FlightSim.Tests
{
    /// <summary>
    /// S2 森林地形验收: 核心库物理地形与 Unity Terrain 高度一致 / 机场台地恒定且 = y 0 /
    /// 物理地形登记并被新飞机继承 (含工坊换机) / AGL / 飞机停在台地上 / ModePilot 地形净空 /
    /// 换机后边界半径保持。整个类共用一次 2049² 地形加载 (最慢的部分)。
    /// </summary>
    public class ForestTerrainPlayModeTests
    {
        private static GameObject _terrainGo;
        private static TerrainFromHeightmap _tf;
        private static ITerrainModel _forestPhysics;

        private GameObject _aircraft;

        [OneTimeSetUp]
        public void LoadForestTerrain()
        {
            _terrainGo = new GameObject("ForestTerrainTest");
            _tf = _terrainGo.AddComponent<TerrainFromHeightmap>();
            _tf.RawFileName = "Terrain/forest/forest.raw";
            _tf.MetaFileName = "Terrain/forest/forest.meta.json";
            var data = _tf.LoadTerrain();
            Assert.IsNotNull(data, "森林地形应加载成功 (StreamingAssets/Terrain/forest/ 缺文件?)");
            _forestPhysics = _tf.PhysicsTerrain;
            Physics.SyncTransforms();
        }

        [OneTimeTearDown]
        public void UnloadForestTerrain()
        {
            if (_terrainGo != null) Object.DestroyImmediate(_terrainGo);
            TerrainRegistry.Clear();
        }

        [SetUp]
        public void SetUp()
        {
            TerrainRegistry.Set(_forestPhysics);   // 其他用例可能临时替换过
        }

        [TearDown]
        public void TearDown()
        {
            // 立即销毁: 同步 [Test] 之间不经过帧, 延迟 Destroy 会让上一个用例的 "TestUAV"
            // 还活着, AircraftSwapper 的 GameObject.Find("TestUAV") 会找到它 (之前就因此误失败)
            if (_aircraft != null) Object.DestroyImmediate(_aircraft);
            _aircraft = null;
            GameObject stale;
            while ((stale = GameObject.Find("TestUAV")) != null) Object.DestroyImmediate(stale);
            TerrainRegistry.Set(_forestPhysics);
        }

        private GameObject MakeAircraft(string name = "TestUAV")
        {
            var go = new GameObject(name);
            go.AddComponent<AircraftController>();
            go.AddComponent<AutoPilotInput>();
            go.AddComponent<FlightGuard>();
            go.AddComponent<ModePilot>();
            return go;
        }

        // ---------------- 元数据与一致性 ----------------

        [Test]
        public void Meta_IsReadAndAppliedToComponent()
        {
            Assert.AreEqual(2049, _tf.SizePx);
            Assert.AreEqual(4000f, _tf.WorldSizeM, 1e-3f);
            Assert.AreEqual(62f, _tf.AirportHeightM, 1e-3f, "meta.airport_height_m");
            Assert.AreEqual(-62f, _terrainGo.transform.position.y, 1e-3f, "地形整体下移台地高度");
            Assert.AreEqual(-2000f, _terrainGo.transform.position.x, 1e-3f);
            Assert.AreEqual(-2000f, _terrainGo.transform.position.z, 1e-3f);
        }

        [Test]
        public void CoreTerrain_MatchesUnityTerrain_At60RandomPoints()
        {
            var terrain = _terrainGo.GetComponent<Terrain>();
            Assert.IsNotNull(terrain);
            var rng = new System.Random(2024);
            float worst = 0f;
            for (int i = 0; i < 60; i++)
            {
                double north = (rng.NextDouble() * 2 - 1) * 1900.0;
                double east = (rng.NextDouble() * 2 - 1) * 1900.0;
                double core = _forestPhysics.HeightAt(north, east);
                float unity = terrain.SampleHeight(new Vector3((float)east, 0f, (float)north))
                              + terrain.transform.position.y;
                worst = Mathf.Max(worst, Mathf.Abs((float)core - unity));
                Assert.AreEqual(core, unity, 0.5, "点 " + i + " (N" + north.ToString("F0") + ", E" +
                                east.ToString("F0") + ") 核心库与 Unity Terrain 高度差应 < 0.5m");
            }
            Debug.Log("[ForestTerrain] 核心库 vs Unity Terrain 60 点最大差 " + worst.ToString("F3") + " m ✓");
        }

        [Test]
        public void TerrainCollider_RaycastHitsAgreeWithCoreHeight()
        {
            var rng = new System.Random(77);
            for (int i = 0; i < 20; i++)
            {
                double north = (rng.NextDouble() * 2 - 1) * 1800.0;
                double east = (rng.NextDouble() * 2 - 1) * 1800.0;
                Assert.IsTrue(Physics.Raycast(new Vector3((float)east, 600f, (float)north), Vector3.down,
                                              out var hit, 1300f), "射线应打到地形碰撞体");
                Assert.AreEqual(_forestPhysics.HeightAt(north, east), hit.point.y, 0.5,
                                "碰撞体表面与物理地形高度一致 (点 " + i + ")");
            }
        }

        [Test]
        public void AirportPlatform_IsFlatAndSitsAtRunwayLevelZero()
        {
            var terrain = _terrainGo.GetComponent<Terrain>();
            float minH = float.MaxValue, maxH = float.MinValue, minU = float.MaxValue, maxU = float.MinValue;
            for (double e = -80; e <= 170; e += 10)
                for (double n = -110; n <= 910; n += 20)
                {
                    float c = (float)_forestPhysics.HeightAt(n, e);
                    float u = terrain.SampleHeight(new Vector3((float)e, 0f, (float)n)) + terrain.transform.position.y;
                    minH = Mathf.Min(minH, c); maxH = Mathf.Max(maxH, c);
                    minU = Mathf.Min(minU, u); maxU = Mathf.Max(maxU, u);
                }
            Assert.Less(maxH - minH, 0.01f, "台地内物理高度应恒定");
            Assert.Less(maxU - minU, 0.01f, "台地内 Unity 地形高度应恒定");
            Assert.AreEqual(0f, (minH + maxH) / 2f, 0.01f, "台地面应在 y=0 (跑道面标高 0 的既有约定)");
            Assert.AreEqual(0f, (minU + maxU) / 2f, 0.01f, "Unity 侧台地面也在 y=0");
        }

        [Test]
        public void Terrain_HasRealRelief_NotAFlatField()
        {
            double min = double.MaxValue, max = double.MinValue;
            for (double n = -1800; n <= 1800; n += 60)
                for (double e = -1800; e <= 1800; e += 60)
                {
                    double h = _forestPhysics.HeightAt(n, e);
                    min = System.Math.Min(min, h); max = System.Math.Max(max, h);
                }
            Assert.Greater(max - min, 80.0, "全图高差应 > 80m, 实际 " + (max - min).ToString("F0"));
        }

        // ---------------- 登记与继承 ----------------

        [Test]
        public void NewAircraft_InheritsRegisteredTerrain()
        {
            _aircraft = MakeAircraft();
            var c = _aircraft.GetComponent<AircraftController>();
            Assert.AreSame(_forestPhysics, c.Plane.Gear.Terrain, "新飞机应继承已登记的物理地形");
        }

        [Test]
        public void NewAircraft_WithoutRegisteredTerrain_StaysFlat()
        {
            TerrainRegistry.Clear();
            _aircraft = MakeAircraft();
            var c = _aircraft.GetComponent<AircraftController>();
            Assert.AreSame(FlatTerrain.Default, c.Plane.Gear.Terrain, "无登记地形时保持平地 (旧行为)");
        }

        [Test]
        public void AircraftSwapper_NewAircraft_KeepsTerrainAndGuardBoundary()
        {
            _aircraft = MakeAircraft("TestUAV");
            _aircraft.GetComponent<FlightGuard>().BoundaryRadius = 1800f;
            _aircraft.GetComponent<FlightGuard>().BoundaryMargin = 250f;
            string cfg = System.IO.Path.Combine(Application.streamingAssetsPath, "AircraftConfig.json");

            var swapped = AircraftSwapper.Swap(cfg, "");

            Assert.AreNotSame(_aircraft, swapped);
            var ctrl = swapped.GetComponent<AircraftController>();
            Assert.AreSame(_forestPhysics, ctrl.Plane.Gear.Terrain, "换机后新飞机仍应有物理地形 (否则穿山)");
            var g = swapped.GetComponent<FlightGuard>();
            Assert.AreEqual(1800f, g.BoundaryRadius, "换机后边界半径保持, 不回默认 1200");
            Assert.AreEqual(250f, g.BoundaryMargin);
        }

        // ---------------- AGL 与停在台地 ----------------

        private static void FindHill(out double north, out double east, out double h)
        {
            north = east = h = 0;
            for (double n = -1500; n <= 1500; n += 100)
                for (double e = -1500; e <= 1500; e += 100)
                {
                    double v = _forestPhysics.HeightAt(n, e);
                    if (v > h) { h = v; north = n; east = e; }
                }
        }

        [UnityTest]
        public IEnumerator Altitude_Agl_IsRelativeToTerrainBelow()
        {
            FindHill(out double hn, out double he, out double hh);
            Assert.Greater(hh, 30.0, "应能找到 >30m 的山头, 最高 " + hh);
            _aircraft = MakeAircraft();
            var c = _aircraft.GetComponent<AircraftController>();
            c.State.Position = new Vec3(hn, he, -(hh + 50.0));
            c.State.VelocityBody = new Vec3(20, 0, 0);
            c.State.Attitude = Quat.FromEuler321(0, 0, 0);
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 0.4f) yield return null;

            float terrainBelow = (float)_forestPhysics.HeightAt(c.State.Position.X, c.State.Position.Y);
            Assert.AreEqual(c.Altitude - terrainBelow, c.AltitudeAgl, 0.05f, "AGL = 海拔 - 脚下地形高");
            Assert.Greater(c.Altitude, hh, "海拔应在山头之上 (山头 " + hh.ToString("F0") + "m)");
            Assert.Less(c.AltitudeAgl, c.Altitude + 1f);
        }

        [UnityTest]
        public IEnumerator Aircraft_StartsOnPlatform_RestsOnGroundAtRunwayLevel()
        {
            _aircraft = MakeAircraft();
            var c = _aircraft.GetComponent<AircraftController>();
            c.State.Position = new Vec3(12, 0, -0.327);
            c.State.VelocityBody = new Vec3(0, 0, 0);
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 2.5f) yield return null;

            Assert.IsTrue(c.OnGround, "台地上应接地 (物理地形 = 台地面 y 0)");
            Assert.Less(Mathf.Abs(c.Altitude - 0.33f), 0.4f, "轮子停在台地面: 机体 ~0.33m, 实际 " + c.Altitude);
            Assert.Less(Mathf.Abs(c.AltitudeAgl - c.Altitude), 0.05f, "台地处 AGL = 海拔");
            Assert.IsFalse(c.IsCrashed);
        }

        // ---------------- 真实森林地形上的自动降落 ----------------

        [UnityTest]
        public IEnumerator Land_OnForestTerrain_ApproachClearsHillsAndTouchesDownInZone()
        {
            // 跑道坐在地形机场台地上 (台地面 = y 0), 进近走廊经过净空面压平的地形。
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
                _aircraft.GetComponent<ModePilot>().Engage(FlightMode.Land);
                bool touched = false, reachedRoll = false;
                float tdAlong = 0, tdCross = 0, minAgl = 1e9f, t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 100f)
                {
                    yield return null;
                    var pos = new Vector2((float)c.State.Position.X, (float)c.State.Position.Y);
                    g.AlongCross(pos, out float along, out float cross);
                    if (!touched && !c.OnGround && along < 0f) minAgl = Mathf.Min(minAgl, c.AltitudeAgl);
                    if (!touched && c.OnGround) { touched = true; tdAlong = along; tdCross = cross; }
                    if (_aircraft.GetComponent<ModePilot>().Phase == "滑跑") reachedRoll = true;
                    if (c.IsCrashed) break;
                    if (reachedRoll && c.Airspeed < 1f) break;
                }
                Assert.IsFalse(c.IsCrashed, "森林地形上自动降落不坠毁");
                Assert.IsTrue(touched, "应在限时内接地");
                Assert.GreaterOrEqual(tdAlong, 0f, "接地不得短于入口, along=" + tdAlong);
                Assert.LessOrEqual(tdAlong, g.TouchdownZoneEndM, "应落在接地区内, along=" + tdAlong);
                Assert.Less(Mathf.Abs(tdCross), 7f, "接地点靠近中线, cross=" + tdCross);
                Assert.Greater(minAgl, 2.5f, "进近段离地高度最小值应 > 2.5m (走廊净空), 实际 " + minAgl);   // U16 新机型基线: 实测 3.66
                Debug.Log("[ForestTerrain] 森林地形自动降落: 接地 along=" + tdAlong.ToString("F0") + "m cross=" +
                          tdCross.ToString("F1") + "m, 进近最小 AGL " + minAgl.ToString("F1") + "m ✓");
            }
            finally
            {
                Object.DestroyImmediate(runwayGo);
            }
        }

        // ---------------- ModePilot 地形净空 ----------------

        /// <summary>合成山脊: 北向 250~500m 一道 120m 高 (起点正北, 前视 300/400m 采样点落在脊上)。</summary>
        private sealed class RidgeTerrain : ITerrainModel
        {
            public double HeightAt(double north, double east)
            {
                return north > 250.0 && north < 500.0 ? 120.0 : 0.0;
            }
        }

        /// <summary>STAB 保持航向正北 8s (飞 ~160m, 未到脊), 返回高度增益。ridge=false 为无地形对照。</summary>
        private IEnumerator StabNorthGain(bool ridge, System.Action<float, bool> result)
        {
            if (ridge) TerrainRegistry.Set(new RidgeTerrain()); else TerrainRegistry.Clear();
            _aircraft = MakeAircraft();
            var c = _aircraft.GetComponent<AircraftController>();
            c.State.Position = new Vec3(0, 0, -60.0);
            c.State.VelocityBody = new Vec3(20, 0, 0);
            c.State.Attitude = Quat.FromEuler321(0, 0, 0);
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 0.6f) yield return null;
            float alt0 = c.Altitude;

            _aircraft.GetComponent<ModePilot>().Engage(FlightMode.Stab);
            t0 = Time.realtimeSinceStartup;
            float maxAlt = alt0;
            while (Time.realtimeSinceStartup - t0 < 8f)
            {
                yield return null;
                maxAlt = Mathf.Max(maxAlt, c.Altitude);
            }
            result(maxAlt - alt0, c.IsCrashed);
        }

        [UnityTest]
        public IEnumerator Stab_ClimbsTowardRidgeAhead_ButNotWithoutTerrain()
        {
            float gainRidge = 0f, gainFlat = 0f; bool crashedRidge = false, crashedFlat = false;
            yield return StabNorthGain(true, (g, cr) => { gainRidge = g; crashedRidge = cr; });
            Object.DestroyImmediate(_aircraft); _aircraft = null;
            yield return StabNorthGain(false, (g, cr) => { gainFlat = g; crashedFlat = cr; });

            Debug.Log("[ForestTerrain] STAB 8s 高度增益: 前方有 120m 山脊 " + gainRidge.ToString("F1") +
                      "m, 无地形对照 " + gainFlat.ToString("F1") + "m");
            Assert.IsFalse(crashedRidge || crashedFlat, "两次都不应坠毁");
            Assert.Less(gainFlat, 5f, "无地形对照应基本保持高度, 实际增益 " + gainFlat);
            Assert.Greater(gainRidge, gainFlat + 4f,   // U16 新机型基线: 实测增益 6.15
                "前方 300~400m 有 120m 山脊时应明显爬升 (目标 150m): 山脊 " + gainRidge + "m vs 对照 " + gainFlat + "m");
        }

        [UnityTest]
        public IEnumerator Loiter_WithoutTerrain_DoesNotClimbForNothing()
        {
            TerrainRegistry.Clear();
            _aircraft = MakeAircraft();
            var c = _aircraft.GetComponent<AircraftController>();
            c.State.Position = new Vec3(0, 0, -60.0);
            c.State.VelocityBody = new Vec3(20, 0, 0);
            c.State.Attitude = Quat.FromEuler321(0, 0, 0);
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 0.6f) yield return null;
            float alt0 = c.Altitude;

            _aircraft.GetComponent<ModePilot>().Engage(FlightMode.Loiter);
            t0 = Time.realtimeSinceStartup;
            float maxAlt = alt0;
            while (Time.realtimeSinceStartup - t0 < 14f)
            {
                yield return null;
                maxAlt = Mathf.Max(maxAlt, c.Altitude);
            }
            Assert.Less(maxAlt, alt0 + 12f, "无地形登记时行为与旧版一致 (不抬升): 最高 " + maxAlt.ToString("F0"));
        }
    }
}
