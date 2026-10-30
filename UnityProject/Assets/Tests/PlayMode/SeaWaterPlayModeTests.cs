using System.Collections;
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
    /// 海岛 B2 验收: 水面即地板 (WaterFloorTerrain) / TerrainRegistry.IsWater / 触水判坠毁而不穿到海底 /
    /// 岸上正常着地不误判 / 海面渲染对象建销 (HDRP 水系统 + 平面退路) / 切换后水与 Volume Profile 复位。
    /// 整个类共用一次 2049² 海岛地形加载。
    /// </summary>
    public class SeaWaterPlayModeTests
    {
        private sealed class ZeroInput : MonoBehaviour, IFlightInput
        {
            public ControlInputs Poll() { return new ControlInputs(); }
            public void ResetState() { }
        }

        private sealed class FixedTerrain : ITerrainModel
        {
            private readonly double _h;
            public FixedTerrain(double h) { _h = h; }
            public double HeightAt(double north, double east) { return _h; }
        }

        private static GameObject _terrainGo;
        private static TerrainFromHeightmap _tf;
        private GameObject _aircraft;

        [OneTimeSetUp]
        public void LoadIsland()
        {
            TerrainRegistry.Clear();
            _terrainGo = new GameObject("SeaTestTerrain");
            _tf = _terrainGo.AddComponent<TerrainFromHeightmap>();
            _tf.RawFileName = SceneRegistry.Island.TerrainRaw;
            _tf.MetaFileName = SceneRegistry.Island.TerrainMeta;
            Assert.IsNotNull(_tf.LoadTerrain(), "海岛地形应加载成功");
            Physics.SyncTransforms();
        }

        [OneTimeTearDown]
        public void UnloadIsland()
        {
            if (_terrainGo != null) Object.DestroyImmediate(_terrainGo);
            TerrainRegistry.Clear();
        }

        [SetUp]
        public void SetUp() { if (_tf != null && _terrainGo != null) TerrainRegistry.Set(_tf.RegisteredTerrain); }

        [TearDown]
        public void TearDown()
        {
            GameObject g;
            while ((g = GameObject.Find("TestUAV")) != null) Object.DestroyImmediate(g);
            _aircraft = null;
            if (_terrainGo != null) TerrainRegistry.Set(_tf.RegisteredTerrain);
        }

        private static int CountWater() { return Object.FindObjectsOfType<WaterSurface>().Length; }

        private AircraftController MakeAircraft(Vec3 pos, double speed, double pitchRad)
        {
            _aircraft = new GameObject("TestUAV");
            var c = _aircraft.AddComponent<AircraftController>();
            _aircraft.AddComponent<ZeroInput>();
            c.State.Position = pos;
            c.State.VelocityBody = new Vec3(speed, 0, 0);
            c.State.Attitude = Quat.FromEuler321(0, pitchRad, 0);
            return c;
        }

        // ---------------- 地形层 ----------------

        [Test]
        public void WaterFloor_HeightIsMaxOfBedAndSea_IsWaterWhereBedBelowSea()
        {
            var below = new WaterFloorTerrain(new FixedTerrain(-30), -9);
            Assert.AreEqual(-9, below.HeightAt(0, 0), 1e-9, "海床低于海面 → 水面当地板");
            Assert.IsTrue(below.IsWater(0, 0));
            var above = new WaterFloorTerrain(new FixedTerrain(3), -9);
            Assert.AreEqual(3, above.HeightAt(0, 0), 1e-9, "陆地高于海面 → 用陆地高度");
            Assert.IsFalse(above.IsWater(0, 0));
        }

        [Test]
        public void RegisteredIslandTerrain_SeaIsFloor_LandIsUnchanged()
        {
            Assert.IsInstanceOf<WaterFloorTerrain>(_tf.RegisteredTerrain, "有海的地形登记时应包水面地板");
            Assert.AreSame(_tf.RegisteredTerrain, TerrainRegistry.Current);
            Assert.AreNotSame(_tf.RegisteredTerrain, _tf.PhysicsTerrain, "PhysicsTerrain 仍是海床 (供既有比对)");

            // 东侧外海 (岸线在 ~400 m, 1200 m 处是深水)
            double seaFloor = _tf.PhysicsTerrain.HeightAt(400, 1200);
            Assert.Less(seaFloor, _tf.SeaLevelY - 1.0, "该点海床应明显低于海面, 否则选点无区分力: " + seaFloor);
            Assert.AreEqual(_tf.SeaLevelY, TerrainRegistry.HeightAt(400, 1200), 1e-6, "水面即地板");
            Assert.IsTrue(TerrainRegistry.IsWater(400, 1200));

            Assert.AreEqual(0.0, TerrainRegistry.HeightAt(400, 0), 0.05, "跑道台地仍是 y 0");
            Assert.IsFalse(TerrainRegistry.IsWater(400, 0));
            Assert.AreEqual(_tf.PhysicsTerrain.HeightAt(400, 0), TerrainRegistry.HeightAt(400, 0), 1e-9, "陆地高度不受水面地板影响");
        }

        [Test]
        public void IsWater_FalseForFlatOrUnregistered()
        {
            TerrainRegistry.Set(FlatTerrain.Default);
            Assert.IsFalse(TerrainRegistry.IsWater(0, 0));
            TerrainRegistry.Set(new OffsetTerrain(new FixedTerrain(-500), 0));
            Assert.IsFalse(TerrainRegistry.IsWater(0, 0), "没有水面登记的地形再低也不算水");
            TerrainRegistry.Clear();
            Assert.IsFalse(TerrainRegistry.IsWater(0, 0));
        }

        // ---------------- 飞机 ----------------

        [UnityTest, Timeout(120000)]
        public IEnumerator Aircraft_DescendingOntoSea_CrashesAtWaterSurface_NotSeabed()
        {
            double seaY = _tf.SeaLevelY;   // -9
            // 外海上空 6 m (海面上方 15 m) 无动力滑翔, 略低头
            var c = MakeAircraft(new Vec3(400, 1200, -6), 22, -0.06);
            float lowest = float.MaxValue, t0 = Time.realtimeSinceStartup;
            while (!c.IsCrashed && Time.realtimeSinceStartup - t0 < 60f)
            {
                yield return null;
                lowest = Mathf.Min(lowest, c.Altitude);
            }
            Assert.IsTrue(c.IsCrashed, "应在限时内触水坠毁");
            Assert.AreEqual("坠入水面", c.CrashReason);
            Assert.GreaterOrEqual(lowest, seaY - 0.6, "不得穿过海面落到海底, 最低 " + lowest + " 海面 " + seaY);
            Assert.IsTrue(c.OnGround, "触水按接地处理");

            c.ResetToInitial();
            Assert.IsFalse(c.IsCrashed);
            Assert.AreEqual("", c.CrashReason, "复位清除坠毁原因");
        }

        [UnityTest, Timeout(120000)]
        public IEnumerator Aircraft_RestingOnBeachJustAboveWater_IsNotCrashed()
        {
            // 在跑道东侧找刚高出海面 0.8~2 m 的沙滩点 (北 400 一线)
            double beachEast = double.NaN;
            for (double e = 170; e < 900; e += 2)
            {
                double h = _tf.PhysicsTerrain.HeightAt(400, e);
                if (h > _tf.SeaLevelY + 0.8 && h < _tf.SeaLevelY + 2.0) { beachEast = e; break; }
            }
            Assert.IsFalse(double.IsNaN(beachEast), "应能在东侧找到沙滩点");
            double ground = _tf.PhysicsTerrain.HeightAt(400, beachEast);
            Assert.IsFalse(TerrainRegistry.IsWater(400, beachEast));

            var c = MakeAircraft(new Vec3(400, beachEast, -(ground + 0.327)), 0, 0);
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 2.5f) yield return null;
            Assert.IsTrue(c.OnGround, "停在沙滩上");
            Assert.IsFalse(c.IsCrashed, "岸上着地不应判成坠水: " + c.CrashReason);
        }

        // ---------------- 海面渲染对象 ----------------

        [UnityTest]
        public IEnumerator SeaWater_BuildsHdrpWaterSurfaceAtSeaLevel_AndCleansUp()
        {
            Assert.IsTrue(SeaWater.HdrpWaterEnabled != null && SeaWater.HdrpWaterEnabled(),
                "HDRP 管线资产应已开 supportWater (Assets/Rendering/HDRenderPipelineAsset*.asset)");
            int before = CountWater();

            var go = new GameObject("SeaTest");
            var sea = go.AddComponent<SeaWater>();
            sea.BuildOnStart = false;
            sea.SeaLevelY = _tf.SeaLevelY;
            sea.Build();
            Assert.IsTrue(sea.UsingHdrpWater, "水系统可用时应走 HDRP 水面");
            Assert.AreEqual(before + 1, CountWater());
            Assert.AreEqual(_tf.SeaLevelY, sea.Surface.transform.position.y, 1e-4f);
            var ws = sea.Surface.GetComponent<WaterSurface>();
            Assert.AreEqual(WaterSurfaceType.OceanSeaLake, ws.surfaceType);
            Assert.AreEqual(WaterGeometryType.Infinite, ws.geometryType);

            sea.Build();   // 幂等: 重建不累积
            yield return null;
            Assert.AreEqual(before + 1, CountWater(), "重复 Build 不应累积水面");

            Object.Destroy(go);
            yield return null;
            Assert.AreEqual(before, CountWater(), "销毁后水面应注销");
        }

        [UnityTest]
        public IEnumerator SeaWater_FallbackPlane_WhenHdrpWaterNotPreferred()
        {
            var go = new GameObject("SeaTestFallback");
            var sea = go.AddComponent<SeaWater>();
            sea.BuildOnStart = false;
            sea.PreferHdrpWater = false;
            sea.SeaLevelY = -9f;
            sea.Build();
            Assert.IsFalse(sea.UsingHdrpWater);
            Assert.IsNotNull(sea.Surface);
            Assert.IsNull(sea.Surface.GetComponent<Collider>(), "渲染平面不应有碰撞体 (物理走 WaterFloorTerrain)");
            Assert.AreEqual(-9f, sea.Surface.transform.position.y, 1e-4f);
            Object.Destroy(go);
            yield return null;
        }

        // ---------------- 场景切换 (放最后: 会释放类级共用地形) ----------------

        [UnityTest, Timeout(300000)]
        public IEnumerator ZZ_SwitchIslandThenForest_SeaAndVolumeAreSwappedAndRestored()
        {
            Object.DestroyImmediate(_terrainGo);
            _terrainGo = null;
            TerrainRegistry.Clear();

            var sceneProfile = ScriptableObject.CreateInstance<VolumeProfile>();
            var volGo = new GameObject("GlobalVolume");
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.sharedProfile = sceneProfile;

            _aircraft = new GameObject("TestUAV");
            _aircraft.AddComponent<AircraftController>();
            _aircraft.AddComponent<ZeroInput>();
            _aircraft.AddComponent<WindSettings>();
            _aircraft.AddComponent<FlightGuard>();
            _aircraft.AddComponent<ModePilot>();
            var swGo = new GameObject("SceneSwitcher");
            var sw = swGo.AddComponent<SceneSwitcher>();
            sw.PersistChoice = false;
            int baseline = CountWater();
            try
            {
                yield return null;
                yield return Run(sw, "island");
                Assert.IsNull(sw.LastError, sw.LastError);
                Assert.IsNotNull(sw.Handles.Sea, "海岛应有海面组件");
                Assert.IsNotNull(sw.Handles.Sea.Surface, "切换完成时海面应已建好");
                Assert.AreEqual(baseline + 1, CountWater());
                Assert.IsTrue(TerrainRegistry.IsWater(400, 1200), "海岛登记的地形应认得出水域");
                Assert.AreNotSame(sceneProfile, vol.sharedProfile, "海岛应换上自己的 Volume Profile");
                WaterRendering wr;
                Assert.IsTrue(vol.sharedProfile.TryGet(out wr) && wr.enable.value,
                    "海岛 Volume Profile 必须打开 WaterRendering (默认关, 不开水面不画)");
                var wind = _aircraft.GetComponent<WindSettings>();
                Assert.AreEqual(SceneRegistry.Island.WindSpeedMps, wind.WindSpeed, 1e-4f, "海岛默认海风");
                Assert.AreEqual(90f, SceneRegistry.Island.WindFromDeg, "海风来自东");

                yield return Run(sw, "forest");
                Assert.IsNull(sw.Handles.Sea, "森林没有海面");
                Assert.AreEqual(baseline, CountWater(), "离开海岛后水面应已销毁");
                Assert.IsFalse(TerrainRegistry.IsWater(400, 1200), "森林地形无水域");
                Assert.AreSame(sceneProfile, vol.sharedProfile, "回到森林应恢复场景自带 Volume Profile");
            }
            finally
            {
                Object.DestroyImmediate(swGo);
                Object.DestroyImmediate(volGo);
                Object.DestroyImmediate(sceneProfile);
                GameObject g;
                while ((g = GameObject.Find(EnvironmentBuilder.RootName)) != null) Object.DestroyImmediate(g);
                while ((g = GameObject.Find("__ScatterRender")) != null) Object.DestroyImmediate(g);
                TerrainRegistry.Clear();
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
            yield return null;   // 等 Destroy 生效
        }
    }
}
