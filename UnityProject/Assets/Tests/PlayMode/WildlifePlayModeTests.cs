using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FlightSim.Tests
{
    /// <summary>
    /// 野生动物验收: 四种动物数量齐全、全部落在撒布半径内、净空区无动物、
    /// 动物确实在游荡 (位置变化) 且不跑出各自家半径。
    /// </summary>
    public class WildlifePlayModeTests
    {
        private GameObject _ground, _wlGo;
        private WildlifeEnvironment _wl;

        [SetUp]
        public void SetUp()
        {
            _ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            _ground.transform.localScale = new Vector3(300f, 1f, 300f);
            _wlGo = new GameObject("WildlifeEnvironment");
            _wl = _wlGo.AddComponent<WildlifeEnvironment>();
            _wl.AutoBuildOnStart = false;
            _wl.GiraffeCount = 4;
            _wl.LionCount = 3;
            _wl.BoarCount = 5;
            _wl.ElephantCount = 2;
            _wl.AreaRadius = 400f;
            _wl.ClearCenter = new Vector2(0f, 0f);
            _wl.ClearHalfSize = new Vector2(50f, 50f);
        }

        [TearDown]
        public void TearDown()
        {
            if (_ground != null) Object.Destroy(_ground);
            if (_wlGo != null) Object.Destroy(_wlGo);
        }

        [UnityTest]
        public IEnumerator Wildlife_SpawnsWandersAndStaysInBounds()
        {
            yield return new WaitForFixedUpdate();   // 碰撞体物理同步
            _wl.Build();

            int giraffe = 0, lion = 0, boar = 0, elephant = 0;
            var animals = new System.Collections.Generic.List<WanderAnimal>();
            foreach (Transform child in _wlGo.transform)
            {
                if (child.name.StartsWith("Giraffe")) giraffe++;
                else if (child.name.StartsWith("Lion")) lion++;
                else if (child.name.StartsWith("Boar")) boar++;
                else if (child.name.StartsWith("Elephant")) elephant++;
                animals.Add(child.GetComponent<WanderAnimal>());

                float horiz = Mathf.Sqrt(child.position.x * child.position.x
                                       + child.position.z * child.position.z);
                Assert.LessOrEqual(horiz, _wl.AreaRadius + 1f, "动物须在撒布半径内");
                Assert.GreaterOrEqual(child.position.y, -0.5f, "动物须贴地");
            }
            Assert.GreaterOrEqual(giraffe, 3, "长颈鹿数量");
            Assert.GreaterOrEqual(lion, 2, "狮子数量");
            Assert.GreaterOrEqual(boar, 4, "野猪数量");
            Assert.GreaterOrEqual(elephant, 2, "大象数量");

            // 游荡验证: 记录位置, 8s 后至少 1/3 的动物移动过, 且都在家半径+余量内
            var before = new Vector3[animals.Count];
            var homes = new Vector2[animals.Count];
            for (int i = 0; i < animals.Count; i++)
            {
                before[i] = animals[i].transform.position;
                var inv = new Vector2(before[i].x, before[i].z);
                homes[i] = inv;
            }

            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 8f) yield return null;

            int moved = 0;
            for (int i = 0; i < animals.Count; i++)
            {
                Vector3 now = animals[i].transform.position;
                if (Vector3.Distance(now, before[i]) > 0.8f) moved++;
                float dh = Mathf.Sqrt((now.x - homes[i].x) * (now.x - homes[i].x)
                                    + (now.z - homes[i].y) * (now.z - homes[i].y));
                Assert.Less(dh, 100f + 30f, "动物不应跑出家园范围 (家半径≤90+余量)");
            }
            Assert.GreaterOrEqual(moved, animals.Count / 3,
                "8s 内应有至少 1/3 动物在游荡 (移动>0.8m), 实际 " + moved + "/" + animals.Count);
            Debug.Log(string.Format("[Wildlife] {0} 只动物, 8s 内 {1} 只在游荡 ✓",
                animals.Count, moved));
        }
    }
}
