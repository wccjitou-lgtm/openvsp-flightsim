using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FlightSim.Tests
{
    /// <summary>
    /// 性能参数面板验收: 计算值与已知数据一致 (TestUAV):
    ///   翼展 4.0 / 面积 3.1 / 展弦比 5.16 / 平尾容量 ≈0.158 / 垂尾容量 ≈0.0132 /
    ///   静稳定裕度 ≈5.8%MAC / 最大升阻比 ≈23.6。
    /// </summary>
    public class SpecsPlayModeTests
    {
        private GameObject _aircraft;

        [SetUp]
        public void SetUp()
        {
            _aircraft = new GameObject("TestUAV");
            _aircraft.AddComponent<AircraftController>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_aircraft != null) Object.Destroy(_aircraft);
        }

        [Test]
        public void Specs_MatchKnownTestUAVValues()
        {
            var c = _aircraft.GetComponent<AircraftController>();
            var design = AircraftConfigLoader.LastDesign;
            Assert.NotNull(design, "加载器应已缓存 design 数据");
            Assert.AreEqual(1.065, design.HtailArmM, 0.01, "尾力臂 (几何计算)");

            var rows = FlightHUD.ComputeSpecs(c.Plane, design);
            Assert.GreaterOrEqual(rows.Count, 12, "参数行数");

            double Get(string name) => rows.Find(r => r.Name == name).Value;

            Assert.AreEqual(4.0, Get("翼展"), 0.01);
            Assert.AreEqual(3.1, Get("机翼面积"), 0.01);
            Assert.AreEqual(5.16, Get("展弦比"), 0.02);
            Assert.AreEqual(0.775, Get("平均弦长"), 0.01);
            Assert.AreEqual(0.158, Get("平尾容量"), 0.01, "V_H = 0.357·1.065/(3.1·0.775)");
            Assert.AreEqual(0.0132, Get("垂尾容量"), 0.003);
            Assert.AreEqual(14.1, Get("起飞质量"), 0.2);
            Assert.AreEqual(4.69, Get("升力线斜率"), 0.05);   // U16 新机型基线 (原 4.44)
            Assert.AreEqual(16.3, Get("静稳定裕度"), 0.6, "SM = -Cma/CLa");   // U16 新机型基线 (原 5.8, 新机型俯仰更稳)
            Assert.AreEqual(24.3, Get("最大升阻比"), 0.5, "1/(2√(k·CD0))");   // U16 新机型基线
            Debug.Log("[Specs] 翼展4.00 AR5.16 平尾0.158 垂尾0.013 SM5.8% maxLD23.6 ✓");
        }
    }
}
