using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FlightSim.Tests
{
    /// <summary>
    /// 飞鸟群验收: 群数×每群 5~9 只数量齐全、绕各自中心盘旋 (轨道半径容差内)、
    /// 高度带合理、5s 内全部移动 (确实在飞)。
    /// </summary>
    public class BirdPlayModeTests
    {
        private GameObject _go;
        private BirdFlock _flock;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("BirdFlock");
            _flock = _go.AddComponent<BirdFlock>();
            _flock.AutoBuildOnStart = false;
            _flock.FlockCount = 2;
            _flock.AreaRadius = 600f;
            _flock.Seed = 23;
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.Destroy(_go);
        }

        [UnityTest]
        public IEnumerator Birds_SoarInFlocksAndStayAirborne()
        {
            _flock.Build();

            var birds = new System.Collections.Generic.List<SoaringBird>();
            foreach (Transform child in _go.transform)
            {
                Assert.IsTrue(child.name.StartsWith("Bird_"), "鸟命名 Bird_N: " + child.name);
                var b = child.GetComponent<SoaringBird>();
                Assert.NotNull(b, "鸟须挂 SoaringBird: " + child.name);
                birds.Add(b);
            }
            Assert.GreaterOrEqual(birds.Count, 10, "2 群 × 5~9 只, 至少 10 只");
            Assert.LessOrEqual(birds.Count, 18, "至多 18 只");

            foreach (var b in birds)
            {
                // 轨道半径容差 (个体偏移 ±10m + 余量)
                float flat = Vector2.Distance(
                    new Vector2(b.transform.position.x, b.transform.position.z),
                    new Vector2(b.Center.x, b.Center.z));
                Assert.AreEqual(b.OrbitRadius, flat, 15f,
                    "鸟须在轨道半径附近: " + b.name);
                // 高度带 (30~110 基线 ± 个体/漂移余量; 无地面 → 净空不生效)
                Assert.Greater(b.transform.position.y, 15f, "鸟须在空中: " + b.name);
                Assert.Less(b.transform.position.y, 130f, "鸟不应过高: " + b.name);
                // 翼枢轴已接线 (扑翼动画前提)
                Assert.IsNotNull(b.transform.Find("WingL"), "左翼枢轴: " + b.name);
                Assert.IsNotNull(b.transform.Find("WingR"), "右翼枢轴: " + b.name);
            }

            // 5s 内全部鸟移动 (盘旋切向速度 ≥8 m/s, 5s 至少 40m 弧长,
            // 但圆轨道可能接近回原位? 不会: 周期 = 2πr/v ≥ 27s ≫ 5s)
            var before = new Vector3[birds.Count];
            for (int i = 0; i < birds.Count; i++) before[i] = birds[i].transform.position;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 5f) yield return null;
            for (int i = 0; i < birds.Count; i++)
                Assert.Greater(Vector3.Distance(birds[i].transform.position, before[i]), 3f,
                    "5s 内鸟须在飞: " + birds[i].name);

            Debug.Log("[Birds] " + birds.Count + " 只鸟盘旋/高度/位移全部通过 ✓");
        }
    }
}
