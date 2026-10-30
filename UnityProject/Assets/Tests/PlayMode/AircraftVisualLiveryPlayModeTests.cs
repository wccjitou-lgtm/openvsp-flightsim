using System.Collections;
using System.IO;
using FlightSim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FlightSim.Tests
{
    /// <summary>
    /// 分部件 OBJ → AircraftVisual 涂装链路测试 (渲染升级 R4):
    /// 分组 OBJ 经 AircraftVisual.Start 装配后, 翼面类部件应涂军旗红、机身白;
    /// 老版单网格 OBJ 不命中关键字 → 全机身白 (容错)。
    /// </summary>
    public class AircraftVisualLiveryPlayModeTests
    {
        private string _dir;
        private const float Tol = 0.01f;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Application.temporaryCachePath, "r4_livery_tests");
            Directory.CreateDirectory(_dir);
        }

        private static GameObject BuildAircraft(string objPath)
        {
            var go = new GameObject("R4LiveryAircraft");
            go.AddComponent<AircraftController>();
            var visual = go.AddComponent<AircraftVisual>();
            visual.RuntimeObjPath = objPath;
            return go;
        }

        [UnityTest]
        public IEnumerator Livery_GroupedObj_WingsRedAndFuselageWhite()
        {
            string obj = Path.Combine(_dir, "grouped.obj");
            File.WriteAllText(obj,
                "o Fuselage\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 3 2\n" +
                "o MainWing\nv 2 0 0\nv 3 0 0\nv 2 1 0\nf 4 6 5\n");

            GameObject go = BuildAircraft(obj);
            yield return null;   // 等一帧让 AircraftVisual.Start 跑完
            try
            {
                var rends = go.GetComponentsInChildren<Renderer>();
                Assert.AreEqual(2, rends.Length, "分组 OBJ 应有 2 个 Renderer");
                foreach (var r in rends)
                {
                    Color c = r.material.color;
                    if (r.name == "MainWing")
                    {
                        Assert.AreEqual(0.82f, c.r, Tol, "MainWing 应为军旗红");
                        Assert.AreEqual(0.18f, c.g, Tol);
                    }
                    else
                    {
                        Assert.AreEqual(0.92f, c.r, Tol, "Fuselage 应为机身白");
                        Assert.AreEqual(0.94f, c.g, Tol);
                    }
                }
            }
            finally { Object.Destroy(go); }
        }

        [UnityTest]
        public IEnumerator Livery_UngroupedObj_AllBodyWhite()
        {
            string obj = Path.Combine(_dir, "single.obj");
            File.WriteAllText(obj, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 3 2\n");

            GameObject go = BuildAircraft(obj);
            yield return null;
            try
            {
                var rends = go.GetComponentsInChildren<Renderer>();
                Assert.AreEqual(1, rends.Length, "单网格 OBJ 应回退 1 个 Renderer");
                Color c = rends[0].material.color;
                Assert.AreEqual(0.92f, c.r, Tol, "单网格不命中关键字 → 机身白");
                Assert.AreEqual(0.94f, c.g, Tol);
            }
            finally { Object.Destroy(go); }
        }

        // ================= U10: 工坊每部件涂装 =================

        [Test]
        public void Livery_SanitizeGroupName_MatchesPythonRule()
        {
            // stl_to_obj_unity.sanitize_group_name 同规则: 非 [A-Za-z0-9_.-] → '_'
            Assert.AreEqual("Main_Wing", AircraftVisual.SanitizeGroupName("Main Wing"));
            Assert.AreEqual("BodyPod", AircraftVisual.SanitizeGroupName("BodyPod"));
            Assert.AreEqual("____", AircraftVisual.SanitizeGroupName("机身机翼"));
            Assert.AreEqual("A_1", AircraftVisual.SanitizeGroupName("A 1"));
            Assert.AreEqual("Part", AircraftVisual.SanitizeGroupName(""));
            Assert.AreEqual("Part", AircraftVisual.SanitizeGroupName(null));
        }

        [Test]
        public void Livery_TryParseHex_ValidAndInvalid()
        {
            Assert.IsTrue(AircraftVisual.TryParseHex("#4AA3FF", out Color c));
            Assert.AreEqual(0x4A / 255f, c.r, Tol);
            Assert.AreEqual(0xA3 / 255f, c.g, Tol);
            Assert.AreEqual(1f, c.b, Tol);
            Assert.IsTrue(AircraftVisual.TryParseHex("C8CDD4", out _), "无 # 前缀应接受");
            Assert.IsFalse(AircraftVisual.TryParseHex("#12345", out _), "长度错拒绝");
            Assert.IsFalse(AircraftVisual.TryParseHex("#GGGGGG", out _), "非十六进制拒绝");
            Assert.IsFalse(AircraftVisual.TryParseHex(null, out _));
        }

        [UnityTest]
        public IEnumerator Livery_WorkshopColors_ApplyPerPartAndFallbackBody()
        {
            // 工坊槽位颜色应逐部件上色 (键=OBJ 安全化组名); 无颜色的部件回落
            // 机身白; 无颜色表回落默认红白涂装 (前两个测试已覆盖)
            string obj = Path.Combine(_dir, "ws_colors.obj");
            File.WriteAllText(obj,
                "o BodyPod\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 3 2\n" +
                "o MainWing\nv 2 0 0\nv 3 0 0\nv 2 1 0\nf 4 6 5\n");

            GameObject go = BuildAircraft(obj);
            go.GetComponent<AircraftVisual>().PartColors = new System.Collections.Generic.Dictionary<string, Color>
            {
                { "MainWing", new Color32(0x4A, 0xA3, 0xFF, 255) },
            };
            yield return null;
            try
            {
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                {
                    Color c = r.material.color;
                    if (r.name == "MainWing")
                    {
                        Assert.AreEqual(0x4A / 255f, c.r, Tol, "MainWing 应用工坊蓝");
                        Assert.AreEqual(0xA3 / 255f, c.g, Tol);
                        Assert.AreEqual(1f, c.b, Tol);
                    }
                    else
                    {
                        Assert.AreEqual(0.92f, c.r, Tol, "无颜色部件回落机身白 (非军旗红)");
                    }
                }
            }
            finally { Object.Destroy(go); }
        }
    }
}
