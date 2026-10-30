using System.Collections.Generic;
using NUnit.Framework;

namespace FlightSim.Tests
{
    /// <summary>
    /// P1 部件覆盖生成测试 (W4 迁移): 原 VspImportUI.WriteOverrides 的用例意图保留 —
    /// 同名部件必须按 "geom:&lt;几何ID&gt;" 精确选择, 不得按名字双写。
    /// 迁移后直接测 WorkshopLibrary.WriteOverrides (静态化, 不再依赖 MonoBehaviour 反射)。
    /// </summary>
    public class P1WorkshopPlayModeTests
    {
        [Test]
        public void SameNameComponents_WriteOnlySelectedGeometryId()
        {
            // 两个同名 WingGeom: 各自按 geom:id 精确写入, 名字形式完全不出现
            var comps = new List<WorkshopLibrary.CompEdit>
            {
                new WorkshopLibrary.CompEdit
                {
                    Id = "WINGID", Name = "WingGeom", MassDefault = 2f,
                    Role = 1,   // 主翼
                },
                new WorkshopLibrary.CompEdit
                {
                    Id = "TAILID", Name = "WingGeom", MassDefault = 0.3f,
                    Role = 2,   // 平尾
                    Ctl = 3,    // 升降舵
                },
            };
            string json = WorkshopLibrary.WriteOverrides(comps, 0f, 0f, false);
            Assert.IsNotNull(json);
            StringAssert.Contains("\"geom:TAILID\"", json);
            StringAssert.Contains("\"geom:WINGID\"", json, "两个部件都被显式选择 (各写各的 geom:id)");
            StringAssert.DoesNotContain("\"WingGeom\":", json, "不得按名字写 (会双写同名部件)");
            StringAssert.Contains("\"control\": \"Elevator\"", json);

            // 未修改的部件列表 → 无覆盖 (null)
            var untouched = new List<WorkshopLibrary.CompEdit>
            {
                new WorkshopLibrary.CompEdit { Id = "A", Name = "Wing", MassDefault = 1f },
            };
            Assert.IsNull(WorkshopLibrary.WriteOverrides(untouched, 0f, 0f, false));

            // 全机级: 配重 + 起落架自动
            string json2 = WorkshopLibrary.WriteOverrides(untouched, 3f, 0.15f, true);
            Assert.IsNotNull(json2);
            StringAssert.Contains("\"ballast_kg\": 3", json2);
            StringAssert.Contains("\"ballast_pos_m\": [0.15, 0, 0]", json2);
            StringAssert.Contains("\"gear\": {\"auto\": true}", json2);
        }

        [Test]
        public void SanitizeInternal_MatchesPipeline()
        {
            // 与 Tools/import_vsp.py sanitize_internal 同构: ASCII 直通 (含混排里的 ASCII 部分),
            // 完全无 ASCII 时 md5 前 3 字节兜底
            Assert.AreEqual("TestUAV", WorkshopLibrary.SanitizeInternal("TestUAV"));
            Assert.AreEqual("-1", WorkshopLibrary.SanitizeInternal("机翼-1"), "混排只保留 ASCII 部分");
            Assert.AreEqual("uav_4b257f", WorkshopLibrary.SanitizeInternal("示例机"));
        }
    }
}
