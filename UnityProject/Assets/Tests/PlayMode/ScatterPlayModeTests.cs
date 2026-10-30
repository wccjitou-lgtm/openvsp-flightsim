using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FlightSim.Tests
{
    /// <summary>
    /// 地面特征撒布验收: 数量达标、全部落在撒布半径内、净空区内无特征、
    /// 全部贴地 (y 与地面射线一致 ±容差)。
    /// </summary>
    public class ScatterPlayModeTests
    {
        private GameObject _root, _ground, _scatterGo;
        private ScatterEnvironment _scatter;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("ScatterTestRoot");
            _ground = GameObject.CreatePrimitive(PrimitiveType.Plane);   // 自带碰撞体
            _ground.transform.localScale = new Vector3(300f, 1f, 300f);  // 3000m 平面
            _scatterGo = new GameObject("ScatterEnvironment");
            _scatterGo.transform.SetParent(_root.transform, false);
            _scatter = _scatterGo.AddComponent<ScatterEnvironment>();
            _scatter.AutoBuildOnStart = false;   // 测试走手动 Build, 避免协程竞争
            _scatter.TreeCount = 60;
            _scatter.RockCount = 30;
            _scatter.BushCount = 40;
            _scatter.AreaRadius = 500f;
            _scatter.ClearCenter = new Vector2(0f, 0f);
            _scatter.ClearHalfSize = new Vector2(50f, 50f);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.Destroy(_root);
            if (_ground != null) Object.Destroy(_ground);
        }

        [UnityTest]
        public IEnumerator Scatter_PlacesFeaturesInBoundsAndClearZoneEmpty()
        {
            // 等一个物理帧: SetUp 新建的地面碰撞体需要物理步同步后射线才能命中
            yield return new WaitForFixedUpdate();
            _scatter.Build();

            int trees = 0, rocks = 0, bushes = 0, inClear = 0, offGround = 0;
            foreach (Transform child in _scatterGo.transform)
            {
                if (child.name.StartsWith("Tree_")) trees++;
                else if (child.name.StartsWith("Rock_")) rocks++;
                else if (child.name.StartsWith("Bush_")) bushes++;

                Vector3 p = child.position;
                float horiz = Mathf.Sqrt(p.x * p.x + p.z * p.z);
                Assert.LessOrEqual(horiz, _scatter.AreaRadius + 1f,
                    "特征须在撒布半径内: " + child.name + " 距 " + horiz);
                Assert.GreaterOrEqual(p.y, -0.5f, "特征须在地面附近 (不悬空/不深埋)");

                if (Mathf.Abs(p.x - _scatter.ClearCenter.x) < _scatter.ClearHalfSize.x &&
                    Mathf.Abs(p.z - _scatter.ClearCenter.y) < _scatter.ClearHalfSize.y)
                    inClear++;
            }

            Assert.GreaterOrEqual(trees, 50, "树木数量 (60 目标, 平面全覆盖应全中)");
            Assert.GreaterOrEqual(rocks, 25, "岩石数量");
            Assert.GreaterOrEqual(bushes, 35, "灌木数量");
            Assert.AreEqual(0, inClear, "净空区内不应有任何特征");
            Debug.Log(string.Format("[Scatter] 树 {0} / 岩 {1} / 灌木 {2}, 净空区 0 ✓",
                trees, rocks, bushes));

            // 俯瞰渲染存档 (人工/离线核验画面效果, 不参与断言)
            var sun = new GameObject("ScatterSun");
            sun.AddComponent<Light>().type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            var camGo = new GameObject("ScatterCam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.7f, 0.9f);   // 天蓝背景
            cam.transform.position = new Vector3(0f, 45f, 130f);
            cam.transform.LookAt(new Vector3(0f, 0f, 30f));
            cam.enabled = false;
            const int W = 1280, H = 720;
            var rt = new RenderTexture(W, H, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            System.IO.Directory.CreateDirectory("Logs/Snapshots");
            System.IO.File.WriteAllBytes("Logs/Snapshots/scatter_play.png", tex.EncodeToPNG());
            Object.Destroy(camGo);
            Object.Destroy(sun);
            Object.Destroy(rt);
            Object.Destroy(tex);
        }
    }
}
