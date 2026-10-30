using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FlightSim.Tests
{
    /// <summary>
    /// U11: 工坊预览实时着色链 — 用户报告"选部件颜色后实时外形展示不更新"。
    /// 覆盖两层: ①WorkshopPreview.SetSlotColor 直接改材质; ②WorkshopUI.
    /// ApplySlotColor (选色器入口) → 预览对象材质变化的端到端链。
    /// </summary>
    public class WorkshopPreviewColorPlayModeTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _workshop;

        [SetUp]
        public void SetUp()
        {
            foreach (var old in Object.FindObjectsOfType<WorkshopUI>())
                Object.DestroyImmediate(old.gameObject);
            _workshop = new GameObject("WorkshopUI_U11");
            var ui = _workshop.AddComponent<WorkshopUI>();
            // 打开工坊 (ToggleOpen 私有; 触发 Update 里的 F8 分支等价)
            typeof(WorkshopUI).GetMethod("ToggleOpen", Private).Invoke(ui, null);
        }

        [TearDown]
        public void TearDown()
        {
            if (_workshop != null) Object.DestroyImmediate(_workshop);
            typeof(WorkshopUI).GetProperty("IsOpen").SetValue(null, false);
        }

        private static Mesh TriangleMesh()
        {
            var m = new Mesh { name = "U11Tri" };
            m.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            m.triangles = new[] { 0, 2, 1 };
            m.RecalculateNormals();
            return m;
        }

        private WorkshopPreview PreviewOf(WorkshopUI ui)
        {
            return (WorkshopPreview)typeof(WorkshopUI).GetField("_preview", Private).GetValue(ui);
        }

        [Test]
        public void Preview_SetSlotColor_UpdatesBaseColor()
        {
            var prev = new WorkshopPreview();
            try
            {
                prev.Create(128, 128);
                var mesh = TriangleMesh();
                prev.SetSlotMesh("wing1", mesh, new Color32(0x4A, 0xA3, 0xFF, 255), "MainWing");

                // 直接改色 (选色器入口最终调这里)
                prev.SetSlotColor("wing1", new Color32(0xFF, 0x00, 0x00, 255));
                // 经场景找预览子对象 (y=-5000 隔离区)
                var go = GameObject.Find("MainWing");
                Assert.IsNotNull(go, "预览槽对象应存在");
                var r = go.GetComponent<MeshRenderer>();
                Assert.IsNotNull(r);
                Color c = r.sharedMaterial.color;
                Assert.AreEqual(1f, c.r, 0.01f, "SetSlotColor 后主色应变红");
                Assert.AreEqual(0f, c.g, 0.01f);
                Assert.AreEqual(0f, c.b, 0.01f);
            }
            finally { prev.Dispose(); }
        }

        [UnityTest]
        public IEnumerator UI_ApplySlotColor_UpdatesPreviewMaterialLive()
        {
            var ui = _workshop.GetComponent<WorkshopUI>();
            var prev = PreviewOf(ui);
            Assert.IsNotNull(prev, "工坊打开后预览应已创建");

            // 注入一个槽位网格 (模拟网格刷新完成的状态)
            var mesh = TriangleMesh();
            prev.SetSlotMesh("wing1", mesh, new Color32(0x4A, 0xA3, 0xFF, 255), "MainWing");

            // 选色器应用颜色的入口 (公有, 弹层直接调用)
            ui.ApplySlotColor("wing1", "FF8000");

            var go = GameObject.Find("MainWing");
            Assert.IsNotNull(go);
            var r = go.GetComponent<MeshRenderer>();
            Color c = r.sharedMaterial.color;
            Assert.AreEqual(1f, c.r, 0.01f, "ApplySlotColor 应实时更新预览材质 (橙)");
            Assert.AreEqual(0x80 / 255f, c.g, 0.02f);
            Assert.AreEqual(0f, c.b, 0.01f);
            yield break;
        }

        [Test]
        public void UI_ApplySlotColor_OnUnpopulatedPreview_SchedulesMeshRefresh()
        {
            // U11: 预览还没装载该槽网格时 (换机/重开工坊后), 选色不再静默空操作 —
            // 应触发网格刷新让颜色以新值呈现
            var ui = _workshop.GetComponent<WorkshopUI>();
            var prev = PreviewOf(ui);
            Assert.IsFalse(prev.HasSlot("wing1"), "前置: 该槽预览未装载");

            ui.ApplySlotColor("wing1", "FF0000");

            var need = (bool)typeof(WorkshopUI).GetField("_needMeshRefresh", Private).GetValue(ui);
            Assert.IsTrue(need, "未装载槽位选色应触发网格刷新");
        }

        [Test]
        public void UI_ToggleOpen_WithModel_SchedulesPreviewRefresh()
        {
            // U11: 重开工坊 (预览被 Create 重建为空) 且模型非空 → 自动装载网格
            var ui = _workshop.GetComponent<WorkshopUI>();
            var stateField = typeof(WorkshopUI).GetField("_state", Private);
            var state = stateField.GetValue(ui);
            var geomsList = (System.Collections.IList)typeof(WorkshopState)
                .GetField("geoms").GetValue(state);
            if (geomsList.Count == 0)
                geomsList.Add(System.Activator.CreateInstance(typeof(WorkshopState.GeomInfo)));
            typeof(WorkshopUI).GetMethod("ToggleOpen", Private).Invoke(ui, null);   // 关
            Assert.IsFalse(WorkshopUI.IsOpen);
            typeof(WorkshopUI).GetMethod("ToggleOpen", Private).Invoke(ui, null);   // 开
            var need = (bool)typeof(WorkshopUI).GetField("_needMeshRefresh", Private).GetValue(ui);
            Assert.IsTrue(need, "重开工坊且模型非空应自动刷新预览");
        }
    }
}
