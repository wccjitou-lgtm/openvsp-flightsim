using System.Collections;
using System.Linq;
using FlightDynamics;
using FlightDynamics.Math;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FlightSim.Tests
{
    /// <summary>
    /// 场景切换框架验收 (P1): 重载无对象泄漏 / 飞机落在新跑道上 / 载入期间冻结 / 复位项 /
    /// 空中二次确认与地面直切 / 载入中与未就绪点击被忽略 / 静态状态 (TerrainRegistry) 复位。
    /// 用例里把"雪谷"临时换成无地形/无撒布的最小 profile (字段快照 → 清空 → TearDown 还原) 来覆盖
    /// "切到另一个场景"的路径 (真场景载入要 ~1 s+); 海岛/雪谷各有专项测试。
    /// </summary>
    public class SceneSwitchPlayModeTests
    {
        private sealed class IdleInput : MonoBehaviour, IFlightInput
        {
            public ControlInputs Poll() { return new ControlInputs { Brake = 1.0 }; }   // 零油门 + 刹车: 停在原地
            public void ResetState() { }
        }

        private readonly System.Collections.Generic.Dictionary<System.Reflection.FieldInfo, object> _alpineSaved =
            new System.Collections.Generic.Dictionary<System.Reflection.FieldInfo, object>();
        private GameObject _aircraft, _switcherGo;
        private SceneSwitcher _sw;
        private AircraftController _ctl;

        [SetUp]
        public void SetUp()
        {
            DestroyStale();
            TerrainRegistry.Clear();

            _alpineSaved.Clear();
            foreach (var f in typeof(SceneProfile).GetFields()) _alpineSaved[f] = f.GetValue(SceneRegistry.Alpine);
            var stub = SceneRegistry.Alpine;
            stub.Ready = false; stub.TerrainRaw = ""; stub.TerrainMeta = ""; stub.PlatformMslM = 0f;
            stub.TreeCount = stub.RockCount = stub.BushCount = stub.PalmCount = 0;
            stub.Wildlife = false; stub.BirdFlocks = 0; stub.Sea = false; stub.VolumeProfileResource = "";

            _aircraft = new GameObject("TestUAV");
            _ctl = _aircraft.AddComponent<AircraftController>();
            _aircraft.AddComponent<IdleInput>();
            _aircraft.AddComponent<WindSettings>();
            _aircraft.AddComponent<FlightGuard>();
            _aircraft.AddComponent<ModePilot>();

            _switcherGo = new GameObject("SceneSwitcher");
            _sw = _switcherGo.AddComponent<SceneSwitcher>();
            _sw.PersistChoice = false;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var kv in _alpineSaved) kv.Key.SetValue(SceneRegistry.Alpine, kv.Value);
            DestroyStale();
            TerrainRegistry.Clear();
        }

        // 同步 [Test] / UnityTest 之间 Destroy 不立即生效, 必须 DestroyImmediate (见 ForestTerrainPlayModeTests)
        private static void DestroyStale()
        {
            GameObject g;
            while ((g = GameObject.Find("TestUAV")) != null) Object.DestroyImmediate(g);
            while ((g = GameObject.Find("SceneSwitcher")) != null) Object.DestroyImmediate(g);
            while ((g = GameObject.Find(EnvironmentBuilder.RootName)) != null) Object.DestroyImmediate(g);
            while ((g = GameObject.Find("__ScatterRender")) != null) Object.DestroyImmediate(g);
        }

        private IEnumerator Switch(string id)
        {
            Assert.IsTrue(_sw.SwitchTo(id), "SwitchTo(" + id + ") 应被接受");
            float t0 = Time.realtimeSinceStartup;
            while (_sw.IsLoading)
            {
                Assert.Less(Time.realtimeSinceStartup - t0, 120f, "切换 " + id + " 超过 120 s");
                yield return null;
            }
        }

        private static string Snap(GameObject root)
        {
            return string.Format("transforms={0} scatterRender={1} meshes={2} materials={3} terrainData={4} textures={5}",
                root.GetComponentsInChildren<Transform>(true).Length,
                Object.FindObjectsOfType<Transform>().Count(t => t.name == "__ScatterRender"),
                Resources.FindObjectsOfTypeAll<Mesh>().Length,
                Resources.FindObjectsOfTypeAll<Material>().Length,
                Resources.FindObjectsOfTypeAll<TerrainData>().Length,
                Resources.FindObjectsOfTypeAll<Texture2D>().Length);
        }

        // ---------------- 重载与对象泄漏 ----------------

        [UnityTest, Timeout(300000)]
        public IEnumerator ReloadForest_NoObjectLeak_AndAircraftSitsOnRunway()
        {
            yield return null;   // 让 SceneSwitcher.Start 跑完
            yield return Switch("forest");
            Assert.AreSame(SceneRegistry.Forest, _sw.Current);
            Assert.IsNull(_sw.LastError, _sw.LastError);
            Assert.IsTrue(TerrainRegistry.HasTerrain, "森林地形应登记");
            var root1 = _sw.Handles.Root;
            string s1 = Snap(root1);
            float load1 = _sw.LastTotalS;

            yield return Switch("forest");
            Assert.IsTrue(root1 == null, "旧 EnvironmentRoot 应已销毁");
            string s2 = Snap(_sw.Handles.Root);
            Debug.Log("[SceneSwitchTest] 首次载入 " + load1.ToString("F2") + "s | " + s1);
            Debug.Log("[SceneSwitchTest] 重载 " + _sw.LastTotalS.ToString("F2") + "s (销毁 " + _sw.LastDestroyS.ToString("F2") +
                      " 地形 " + _sw.LastTerrainS.ToString("F2") + " 跑道 " + _sw.LastRunwayS.ToString("F2") +
                      " 撒布/动物 " + _sw.LastScatterS.ToString("F2") + ") | " + s2);
            Assert.AreEqual(s1, s2, "重载后对象/资源数量应与首次一致 (无泄漏)");

            yield return new WaitForSeconds(1f);   // 物理跑起来, 起落架落稳
            Assert.AreEqual(12f, _ctl.InitialPositionNed.x, 1e-3f, "起点 = 跑道入口内 12 m");
            Assert.AreEqual(0f, _ctl.InitialPositionNed.y, 1e-3f, "起点在中线上");
            Assert.IsTrue(_ctl.OnGround, "飞机应停在台地上");
            Assert.IsFalse(_ctl.IsCrashed);
            Assert.That(_ctl.Altitude, Is.InRange(0.1f, 0.8f), "台地面 = Unity y 0, 机身离地 ~0.33 m");
            Assert.AreEqual(_ctl.Altitude, _ctl.AltitudeAgl, 0.2f, "台地上 AGL ≈ 海拔");
        }

        // ---------------- 载入期间冻结 + 复位项 ----------------

        [UnityTest, Timeout(300000)]
        public IEnumerator Loading_FreezesAircraft_AndResetsPilotGuardWind()
        {
            yield return null;
            var pilot = _aircraft.GetComponent<ModePilot>();
            var guard = _aircraft.GetComponent<FlightGuard>();
            var wind = _aircraft.GetComponent<WindSettings>();
            yield return null;   // WindSettings.Start
            pilot.Engage(FlightMode.Rtl);
            guard.BoundaryRadius = 500f;
            wind.SetWeather(5f, 90f, true, DrydenTurbulence.TurbulenceLevel.Severe, true);
            Assert.AreEqual(FlightMode.Rtl, pilot.Mode);

            Assert.IsTrue(_sw.SwitchTo("forest"));
            yield return null;
            Assert.IsTrue(_sw.IsLoading, "载入中");
            Assert.IsTrue(_ctl.Frozen, "载入期间飞机物理应冻结");
            Assert.AreEqual(FlightMode.Manual, pilot.Mode, "切换解除自驾");
            while (_sw.IsLoading) yield return null;

            Assert.IsFalse(_ctl.Frozen, "载入完成后解冻");
            Assert.AreEqual(SceneRegistry.Forest.BoundaryRadius, guard.BoundaryRadius, "边界半径随场景");
            Assert.AreEqual(SceneRegistry.Forest.WindSpeedMps, wind.WindSpeed, 1e-4f, "风回到场景默认");
            Assert.IsFalse(wind.DirectionVeer);
            Assert.AreEqual(DrydenTurbulence.TurbulenceLevel.Light, wind.Turbulence);
        }

        // ---------------- 点击逻辑 ----------------

        [UnityTest, Timeout(300000)]
        public IEnumerator Click_AirNeedsConfirm_GroundSwitchesDirectly_StaticStateResets()
        {
            Assert.AreEqual(SceneSwitcher.ClickResult.Ignored, _sw.Click("alpine", 99f), "未就绪的场景忽略");
            SceneRegistry.Alpine.Ready = true;   // 无地形无撒布的最小 profile (海岛现已是真场景, 载入要 ~1 s+)
            yield return null;
            yield return Switch("forest");
            yield return new WaitForSeconds(1f);
            Assert.IsTrue(_ctl.OnGround);
            Assert.IsFalse(_sw.NeedsConfirm(), "地面不需要确认");

            // 升空: 把状态直接放到 100 m 高、25 m/s 前飞
            _ctl.State.Position = new Vec3(100, 0, -100);
            _ctl.State.VelocityBody = new Vec3(25, 0, 0);
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            Assert.IsFalse(_ctl.OnGround);
            Assert.IsTrue(_sw.NeedsConfirm(), "空中需要确认");

            float t = 100f;
            Assert.AreEqual(SceneSwitcher.ClickResult.Ignored, _sw.Click("forest", t), "点当前场景忽略");
            Assert.AreEqual(SceneSwitcher.ClickResult.Armed, _sw.Click("alpine", t));
            Assert.IsTrue(_sw.IsPending("alpine", t + 1f));
            Assert.IsFalse(_sw.IsPending("alpine", t + SceneSwitcher.ConfirmWindowS + 0.5f), "确认窗口 3 s 后失效");
            Assert.IsFalse(_sw.IsLoading, "第一次点击不切换");
            Assert.AreEqual(SceneSwitcher.ClickResult.Armed, _sw.Click("alpine", t + 5f), "窗口过期后再点 = 重新待确认");
            Assert.AreEqual(SceneSwitcher.ClickResult.Switching, _sw.Click("alpine", t + 6f));
            Assert.IsTrue(_sw.IsLoading);
            Assert.AreEqual(SceneSwitcher.ClickResult.Ignored, _sw.Click("forest", t + 6.1f), "载入中点击被忽略");
            while (_sw.IsLoading) yield return null;

            Assert.AreSame(SceneRegistry.Alpine, _sw.Current);
            Assert.IsFalse(TerrainRegistry.HasTerrain, "无地形的场景: 旧地形登记必须清掉");
            Assert.IsNull(_sw.PendingId);
            yield return new WaitForSeconds(1f);
            Assert.IsTrue(_ctl.OnGround, "切换后回到跑道平地上");

            // 地面: 直接切换, 不需要确认
            Assert.AreEqual(SceneSwitcher.ClickResult.Switching, _sw.Click("forest", t + 10f));
            while (_sw.IsLoading) yield return null;
            Assert.AreSame(SceneRegistry.Forest, _sw.Current);
            Assert.IsTrue(TerrainRegistry.HasTerrain, "切回森林后地形重新登记");
        }

        // ---------------- 真实海岛 ----------------

        [UnityTest, Timeout(600000)]
        public IEnumerator Island_RoundTripWithForest_BuildsPropsAndDoesNotLeak()
        {
            yield return null;
            yield return Switch("island");
            Assert.AreSame(SceneRegistry.Island, _sw.Current);
            Assert.IsNull(_sw.LastError, _sw.LastError);
            Assert.IsTrue(_sw.Handles.Terrain.HasSea);
            Assert.IsNotNull(_sw.Handles.Props, "海岛应有附加物组件");
            Assert.AreEqual(SceneRegistry.Island.BoundaryRadius, _aircraft.GetComponent<FlightGuard>().BoundaryRadius);
            float firstIslandS = _sw.LastTotalS;
            yield return new WaitForSeconds(1.5f);   // IslandProps / 起落架落稳
            Assert.IsNotNull(_sw.Handles.Props.transform.Find("Lighthouse"), "灯塔");
            Assert.IsNotNull(_sw.Handles.Props.transform.Find("Pier"), "码头");
            Assert.IsTrue(_ctl.OnGround, "飞机停在海岛台地上");
            Assert.That(_ctl.Altitude, Is.InRange(0.1f, 0.8f));
            string s1 = Snap(_sw.Handles.Root);

            yield return Switch("forest");
            Assert.IsTrue(_sw.Handles.Props == null, "森林没有海岛附加物");
            yield return Switch("island");
            yield return new WaitForSeconds(1.5f);
            string s2 = Snap(_sw.Handles.Root);
            Debug.Log("[SceneSwitchTest] 海岛首次载入 " + firstIslandS.ToString("F2") + "s, 往返后再载入 " +
                      _sw.LastTotalS.ToString("F2") + "s (地形 " + _sw.LastTerrainS.ToString("F2") + " 撒布 " +
                      _sw.LastScatterS.ToString("F2") + ") | " + s1 + " -> " + s2);
            Assert.AreEqual(s1, s2, "海岛 -> 森林 -> 海岛 后对象/资源数量应与首次一致 (无泄漏)");
        }

        [Test]
        public void SwitchTo_RejectsUnknownAndNotReady()
        {
            Assert.IsFalse(_sw.SwitchTo("alpine"), "雪谷未就绪");
            Assert.IsTrue(SceneRegistry.Island.Ready, "海岛 B1 起就绪");
            Assert.IsFalse(_sw.SwitchTo("nope"));
            Assert.IsFalse(_sw.SwitchTo(null));
            Assert.IsFalse(_sw.IsLoading);
        }

        [Test]
        public void Registry_ForestProfileMatchesLegacySceneSetupValues()
        {
            var f = SceneRegistry.Forest;
            Assert.AreEqual(1800f, f.BoundaryRadius);
            Assert.AreEqual(1100, f.TreeCount);
            Assert.AreEqual(860, f.RockCount);
            Assert.AreEqual(1660, f.BushCount);
            Assert.IsTrue(f.Wildlife);
            Assert.AreEqual(2, f.BirdFlocks);
            Assert.AreEqual("Terrain/forest/forest.raw", f.TerrainRaw);
            Assert.AreEqual(3, SceneRegistry.All.Count);
            Assert.AreSame(SceneRegistry.Forest, SceneRegistry.Default);
        }
    }
}
