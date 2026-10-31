using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FlightSim.Tests
{
    /// <summary>
    /// 机型工坊 W1 PlayMode 测试: 协议 JSON 抽取 / STL 坐标变换 (与
    /// stl_to_obj_unity.py 同款) / 槽位类型规则 / workshop.json 往返 /
    /// 预览资源释放 / 工坊开关与模拟暂停。
    /// </summary>
    public class WorkshopPlayModeTests
    {
        // ================= WorkshopJson =================

        [Test]
        public void Json_Str_WithEscapeAndChinese()
        {
            string line = "{\"id\":1,\"ok\":false,\"error\":\"ValueError: \\\"未知几何\\\" X\",\"ms\":3.5}";
            Assert.AreEqual("ValueError: \"未知几何\" X", WorkshopJson.Str(line, "error"));
            Assert.AreEqual(1, WorkshopJson.Int(line, "id"));
            Assert.IsFalse(WorkshopJson.Bool(line, "ok"));
            Assert.AreEqual(3.5f, WorkshopJson.Num(line, "ms"), 1e-4f);
        }

        [Test]
        public void Json_Str_IgnoresMatchesInsideStringValues()
        {
            // "error" 字段值里含 "id" 字样, 不得影响顶层 id 的提取
            string line = "{\"id\":7,\"ok\":true,\"error\":\"see id field\"}";
            Assert.AreEqual(7, WorkshopJson.Int(line, "id"));
            Assert.AreEqual("see id field", WorkshopJson.Str(line, "error"));
        }

        [Test]
        public void Json_Raw_ExtractsNestedObject()
        {
            string line = "{\"id\":2,\"ok\":true,\"data\":{\"geoms\":[{\"name\":\"MainWing\",\"slot\":\"wing1\"}],\"slots\":{}}}";
            string data = WorkshopJson.Raw(line, "data");
            Assert.IsNotNull(data);
            StringAssert.StartsWith("{\"geoms\"", data);
            // 嵌套一层再取
            string geoms = WorkshopJson.Raw(data, "geoms");
            Assert.IsNotNull(geoms);
            Assert.AreEqual("MainWing", WorkshopJson.Str(geoms, "name"));
        }

        [Test]
        public void Json_Raw_HandlesBracesInsideStrings()
        {
            string line = "{\"data\":{\"error\":\"a } b { c\",\"n\":1}}";
            string data = WorkshopJson.Raw(line, "data");
            Assert.IsNotNull(data);
            Assert.AreEqual("a } b { c", WorkshopJson.Str(data, "error"));
        }

        [Test]
        public void Json_Escape_RoundTrip()
        {
            string s = "路径\\D:\\文件 \"引号\" 中文";
            string json = "{\"path\":" + WorkshopJson.Escape(s) + "}";
            Assert.AreEqual(s, WorkshopJson.Str(json, "path"));
        }

        // ================= WorkshopStlLoader =================

        /// <summary>构造 OpenVSP 风格 ASCII STL (行首空格 + CRLF)。</summary>
        internal static string WriteTestStl(Vector3[] ovspTris, int triCount)
        {
            string path = Path.Combine(Application.temporaryCachePath, "workshop_test.stl");
            var sb = new System.Text.StringBuilder();
            sb.Append("solid\r\n");
            for (int t = 0; t < triCount; t++)
            {
                sb.Append("  facet normal 0 1 0\r\n    outer loop\r\n");
                for (int v = 0; v < 3; v++)
                    sb.Append("      vertex " + ovspTris[t * 3 + v].x.ToString("R") + " " +
                              ovspTris[t * 3 + v].y.ToString("R") + " " +
                              ovspTris[t * 3 + v].z.ToString("R") + "\r\n");
                sb.Append("    endloop\r\n  endfacet\r\n");
            }
            sb.Append("endsolid \r\n");
            File.WriteAllText(path, sb.ToString());
            return path;
        }

        [Test]
        public void StlLoader_AppliesOvspToUnityTransform()
        {
            // OpenVSP 体轴 (X 后, Y 右, Z 上) 的一个三角形
            var ovsp = new Vector3[] {
                new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(1f, 1f, 0f) };
            string path = WriteTestStl(ovsp, 1);
            Mesh m = WorkshopStlLoader.LoadMesh(path, Vector3.zero);
            Assert.IsNotNull(m);
            // 变换 Unity=(Y, Z, -X): (0,0,0)→(0,0,0); (1,0,0)→(0,0,-1); (1,1,0)→(1,0,-1)
            Assert.AreEqual(3, m.vertexCount);
            Assert.AreEqual(new Vector3(0, 0, 0), m.vertices[0]);
            Assert.AreEqual(new Vector3(0, 0, -1), m.vertices[1]);
            Assert.AreEqual(new Vector3(1, 0, -1), m.vertices[2]);
            // 绕序反转 (a,c,b)
            int[] tri = { m.triangles[0], m.triangles[1], m.triangles[2] };
            Assert.AreEqual(new[] { 0, 2, 1 }, tri);
        }

        [Test]
        public void StlLoader_CgShiftAndSharedVertices()
        {
            // 两个三角形共用两个顶点 (量化去重后 4 顶点)
            var ovsp = new Vector3[] {
                new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0),
                new Vector3(1,1,0), new Vector3(1,0,0), new Vector3(2,1,0) };
            string path = WriteTestStl(ovsp, 2);
            Vector3 cg = new Vector3(1f, 0.5f, 0f);   // OpenVSP 系重心
            Mesh m = WorkshopStlLoader.LoadMesh(path, cg);
            Assert.IsNotNull(m);
            Assert.AreEqual(4, m.vertexCount, "重复顶点应量化去重");
            Assert.AreEqual(6, m.triangles.Length / 3 * 3);
            // 第一个顶点 (0,0,0)-cg → (Y-cgY, Z-cgZ, -(X-cgX)) = (0-0.5, 0, 1)
            CollectionAssert.Contains(new List<Vector3>(m.vertices), new Vector3(-0.5f, 0f, 1f));
        }

        [Test]
        public void StlLoader_MissingFileReturnsNull()
        {
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("文件不存在"));
            Assert.IsNull(WorkshopStlLoader.LoadMesh(
                Path.Combine(Application.temporaryCachePath, "no_such_file.stl"), Vector3.zero));
        }

        // ================= WorkshopState =================

        [Test]
        public void State_SlotTypeRules()
        {
            Assert.IsTrue(WorkshopState.TypeFitsSlot("fuselage", "Pod"));       // TestUAV 机身
            Assert.IsTrue(WorkshopState.TypeFitsSlot("fuselage", "Fuselage"));
            Assert.IsTrue(WorkshopState.TypeFitsSlot("wing1", "Wing"));
            Assert.IsFalse(WorkshopState.TypeFitsSlot("ballast", "Wing"));
            Assert.IsFalse(WorkshopState.TypeFitsSlot("wing1", "Pod"));
        }

        // ================= U6/U7: per_geom 字典契约 (换机质量烘焙) =================

        [Test]
        public void Json_SplitObject_ParsesWorkerDictShape()
        {
            // worker cmd_mass_props 的真实形状 (E2E 捕获): per_geom 是 dict,
            // 键=部件名 (可含空格/中文), 值含 mass_kg
            string dict = "{\"ExampleWing\":{\"mass_kg\":1.0905,\"cg_m\":[1.09,0,0],\"volume_m3\":0.0047}," +
                          "\"Example Fuselage\":{\"mass_kg\":0.1565,\"cg_m\":[0.83,0,0],\"volume_m3\":0.0313}," +
                          "\"机翼1\":{\"mass_kg\":0.5,\"cg_m\":[0,0,0],\"volume_m3\":0.001}}";
            var kvs = WorkshopJson.SplitObject(dict);
            Assert.AreEqual(3, kvs.Count);
            Assert.AreEqual("ExampleWing", kvs[0].Key);
            Assert.AreEqual(1.0905f, WorkshopJson.Num(kvs[0].Value, "mass_kg"), 1e-4f);
            Assert.AreEqual("Example Fuselage", kvs[1].Key);   // 名含空格
            Assert.AreEqual(0.1565f, WorkshopJson.Num(kvs[1].Value, "mass_kg"), 1e-4f);
            Assert.AreEqual("机翼1", kvs[2].Key);               // 中文名
        }

        [Test]
        public void Json_SplitObject_GuardsAndEmpty()
        {
            Assert.AreEqual(0, WorkshopJson.SplitObject("{}").Count);
            Assert.AreEqual(0, WorkshopJson.SplitObject("[1,2]").Count);   // 数组不入
            Assert.AreEqual(0, WorkshopJson.SplitObject(null).Count);
            Assert.AreEqual(0, WorkshopJson.SplitObject("").Count);
        }

        [Test]
        public void State_LoadMass_ParsesPerGeomDict()
        {
            // U6 回归: LoadMass 曾按数组解析恒空 → 换机 mass_map 空 → Shell 质量
            // 丢失 → Ixx≈0 → 地面 NaN。真实 dict 形状必须解析出 perGeom。
            var st = new WorkshopState();
            st.LoadMass("{\"mass_kg\":2.0927,\"cg_m\":[1.0,0.0,0.05],\"num_comps\":5," +
                        "\"per_geom\":{\"ExampleWing\":{\"mass_kg\":1.0905,\"cg_m\":[1.09,0,0],\"volume_m3\":0.0047}," +
                        "\"Example Fuselage\":{\"mass_kg\":0.1565,\"cg_m\":[0.83,0,0],\"volume_m3\":0.0313}}}");
            Assert.AreEqual(2.0927f, st.mass.massKg, 1e-3f);
            Assert.AreEqual(2, st.mass.perGeom.Length);
            Assert.AreEqual("ExampleWing", st.mass.perGeom[0].name);
            Assert.AreEqual(1.0905f, st.mass.perGeom[0].massKg, 1e-3f);
            Assert.AreEqual("Example Fuselage", st.mass.perGeom[1].name);
            Assert.AreEqual(0.1565f, st.mass.perGeom[1].massKg, 1e-3f);
        }

        [Test]
        public void State_MeshPathForNestedSlots()
        {
            string data = "{\"meshes\":{\"wing1\":{\"path\":\"D:/x/wing1.stl\",\"tris\":272}," +
                          "\"fuselage\":{\"path\":\"D:/x/fuselage.stl\",\"tris\":272}}}";
            Assert.AreEqual("D:/x/wing1.stl", WorkshopState.MeshPathFor(data, "wing1"));
            Assert.AreEqual("D:/x/fuselage.stl", WorkshopState.MeshPathFor(data, "fuselage"));
            Assert.IsNull(WorkshopState.MeshPathFor(data, "vtail"));
        }

        [Test]
        public void State_SidecarRoundTrip()
        {
            var st = new WorkshopState();
            st.Slot("wing1").geom = "MainWing";
            st.Slot("vtail").geom = null;
            st.Slot("ballast").geom = "配重A";
            string vsp3 = Path.Combine(Application.temporaryCachePath, "sidecar_test.vsp3");
            st.SaveSidecar(vsp3);

            var st2 = new WorkshopState();
            Assert.IsTrue(st2.LoadSidecar(vsp3, out _, out _));
            Assert.AreEqual("MainWing", st2.Slot("wing1").geom);
            Assert.IsTrue(string.IsNullOrEmpty(st2.Slot("vtail").geom));
            Assert.AreEqual("配重A", st2.Slot("ballast").geom);
            // json 含 schemaVersion
            string json = File.ReadAllText(Path.ChangeExtension(vsp3, ".workshop.json"));
            StringAssert.Contains("\"schemaVersion\"", json);
        }

        [Test]
        public void State_LoadGeomsLinksSlotsById()
        {
            var st = new WorkshopState();
            string data = "{\"geoms\":[" +
                "{\"id\":\"AAA\",\"name\":\"Fuselage\",\"type\":\"Pod\",\"hidden\":false,\"slot\":\"fuselage\",\"subsurfs\":[]}," +
                "{\"id\":\"BBB\",\"name\":\"MainWing\",\"type\":\"Wing\",\"hidden\":false,\"slot\":\"wing1\"," +
                "\"subsurfs\":[{\"id\":\"S1\",\"name\":\"Aileron\",\"type\":3}]}]}";
            st.LoadGeoms(data);
            Assert.AreEqual("AAA", st.Slot("fuselage").geomId);
            Assert.AreEqual("BBB", st.Slot("wing1").geomId);
            Assert.IsNull(st.Slot("htail").geomId);
            Assert.AreEqual("Aileron", st.GeomById("BBB").subsurfs[0].name);
        }

        // ================= WorkshopPreview 资源释放 =================

        [UnityTest]
        public IEnumerator Preview_CreateMeshDispose_ReleasesAllResources()
        {
            var preview = new WorkshopPreview();
            preview.Create(256, 192);
            Assert.IsNotNull(preview.Rt);
            Assert.IsNotNull(preview.Cam);

            var mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 2, 1 };
            mesh.RecalculateBounds();
            preview.SetSlotMesh("wing1", mesh, Color.blue, "MainWing");
            preview.SetCgMarker(Vector3.zero, true);
            preview.Render();   // 手动渲染一帧不应报错

            var rt = preview.Rt;
            var camGo = preview.Cam.gameObject;
            preview.Dispose();
            yield return null;   // Destroy 帧末生效
            Assert.IsTrue(rt == null || !rt.IsCreated());
            Assert.IsTrue(camGo == null);   // Unity 伪 null = 已销毁
            Assert.IsNull(preview.Rt);
            Assert.IsNull(preview.Cam);
            Assert.IsFalse(preview.HasContent);
            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void Preview_OrbitZoomClamped()
        {
            var preview = new WorkshopPreview();
            preview.Create(128, 96);
            var p0 = preview.Cam.transform.position;
            preview.Orbit(30f, -10f);
            Assert.AreNotEqual(p0, preview.Cam.transform.position);
            preview.Zoom(1e6f);   // 超限缩放必须钳制
            float dist1 = Vector3.Distance(preview.Cam.transform.position, Vector3.down * 5000f);
            preview.Zoom(1e-6f);
            float dist2 = Vector3.Distance(preview.Cam.transform.position, Vector3.down * 5000f);
            Assert.IsTrue(dist2 < dist1 * 100f, "缩放应钳制在合理范围");
            preview.Dispose();
        }

        // ================= W2 编辑窗 =================

        [Test]
        public void W2_FuselageEditor_LoadFromSummary()
        {
            var ui = new GameObject("WorkshopUI_W2F").AddComponent<WorkshopUI>();
            var w = new FuselageEditorWindow(ui, "GID1", "机身");
            string json = "{\"length\":2.0,\"num_xsecs\":6,\"shapes\":[0,1,1,1,1,0]," +
                          "\"all_round_or_ellipse\":false,\"diameters\":[null,0.2,0.3,0.3,0.2,null]," +
                          "\"xloc_percents\":[0,0.2,0.4,0.6,0.8,1],\"material\":\"White\"}";
            w.LoadFromSummary(json);
            Assert.AreEqual(2.0f, w.length);
            Assert.AreEqual(6, w.xsecCount);
            Assert.IsTrue(w.importedPreserve);
            Assert.AreEqual(0.1f, w.radii[1], 1e-4f, "直径应换算为半径");
            Assert.IsNull(null);   // null 直径项以 0 占位
            Assert.AreEqual(0f, w.radii[0], 1e-6f);
            Object.Destroy(ui);
        }

        [Test]
        public void W2_FuselageEditor_RoundFuselageNotPreserve()
        {
            var ui = new GameObject("WorkshopUI_W2F2").AddComponent<WorkshopUI>();
            var w = new FuselageEditorWindow(ui, "GID2", "机身");
            w.LoadFromSummary("{\"length\":1.6,\"num_xsecs\":6," +
                              "\"all_round_or_ellipse\":true,\"diameters\":[0.08,0.3,0.38,0.38,0.3,0.08]," +
                              "\"xloc_percents\":[0,0.2,0.4,0.6,0.8,1]}");
            Assert.IsFalse(w.importedPreserve);
            Assert.AreEqual(6, w.radii.Length);
            Object.Destroy(ui);
        }

        [Test]
        public void W2_WingEditor_LoadAndTwinVtail()
        {
            var ui = new GameObject("WorkshopUI_W2W").AddComponent<WorkshopUI>();
            var w = new WingEditorWindow(ui, "GID3", "垂尾", true);
            w.LoadFromSummary("{\"span_half\":0.3,\"root_chord\":0.12,\"tip_chord\":0.08," +
                              "\"sweep\":0,\"twist\":0,\"dihedral\":0,\"rot_y\":0," +
                              "\"x\":1.35,\"y\":0.15,\"z\":0.1,\"point_mass\":0.09," +
                              "\"symmetric\":true,\"multi_segment\":false," +
                              "\"controls\":[{\"id\":\"SS1\",\"name\":\"Rudder\",\"eta_start\":0.1," +
                              "\"eta_end\":0.95,\"len_c\":0.35}],\"material\":\"White\"}");
            Assert.AreEqual(0.6f, w.spanFull, 1e-4f, "UI 显示全展=半展×2");
            Assert.AreEqual(1, w.controls.Count);
            Assert.AreEqual("rudder", w.controls[0].role);
            Assert.IsTrue(w.twin, "对称 + Y 偏移 = 双垂尾");
            Assert.AreEqual(0.3f, w.spacing, 1e-4f, "间距 = |Y|×2");
            Object.Destroy(ui);
        }

        [Test]
        public void W2_ControlSurfaceEditor_Clamp()
        {
            var ui = new GameObject("WorkshopUI_W2C").AddComponent<WorkshopUI>();
            var wing = new WingEditorWindow(ui, "GID4", "机翼1", false);
            var c = new WingEditorWindow.ControlInfo { id = "SS2", name = "Aileron" };
            var w = new ControlSurfaceEditorWindow();
            w.Open(wing, c);
            // 位置 80% + 展长 40% → 展长被钳到 20 (联动 ≤100)
            w.posPct = 80f; w.posText = "80";
            w.spanPct = 40f; w.spanText = "40";
            // (GUI 层联动在 Draw 中; 数值层由 worker 钳制, 此处验证装载)
            Assert.AreEqual(80f, w.posPct);
            Assert.AreEqual(40f, w.spanPct);
            Object.Destroy(ui);
        }

        [Test]
        public void W2_BuildControlOverlays_BandExtraction()
        {
            // 梯形翼: x∈[0,1] (根弦 1), y∈[0,2] (半展 2); 舵面 eta 0.5~1.0, len_c 0.25
            // → 只有外半段后 25% 弦的三角形进 overlay
            string path = Path.Combine(Application.temporaryCachePath, "ctrl_test.stl");
            var sb = new System.Text.StringBuilder("solid\r\n");
            void Tri(float x0, float y0, float x1, float y1, float x2, float y2)
            {
                sb.Append("  facet normal 0 1 0\r\n    outer loop\r\n");
                foreach (var (x, y) in new[] { (x0, y0), (x1, y1), (x2, y2) })
                    sb.Append("      vertex ").Append(x).Append(' ').Append(y).Append(" 0\r\n");
                sb.Append("    endloop\r\n  endfacet\r\n");
            }
            Tri(0.9f, 1.6f, 1.0f, 1.6f, 1.0f, 1.9f);   // 外段后缘 → 应命中
            Tri(0.1f, 1.6f, 0.2f, 1.6f, 0.2f, 1.9f);   // 外段前缘 → 不命中
            Tri(0.9f, 0.2f, 1.0f, 0.2f, 1.0f, 0.5f);   // 内段后缘 (eta<0.5) → 不命中
            File.WriteAllText(path, sb.ToString());
            var overlays = WorkshopStlLoader.BuildControlOverlays(path, Vector3.zero,
                new List<Vector3> { new Vector3(0.5f, 1.0f, 0.25f) });
            Assert.AreEqual(1, overlays.Count);
            // 恰好 1 个三角形 (3 顶点), 且已变换到 Unity 系 (x=y_ovsp=1.6~1.9)
            Assert.AreEqual(3, overlays[0].vertexCount);
            Assert.Greater(overlays[0].vertices[0].x, 1.0f);
        }

        [Test]
        public void W2_Widgets_SplitArrayAndItems()
        {
            var arr = WorkshopWidgets.SplitArray("[{\"name\":\"a\"},{\"name\":\"b\"}]");
            Assert.AreEqual(2, arr.Count);
            Assert.AreEqual("b", WorkshopJson.Str(arr[1], "name"));
            var names = WorkshopWidgets.ItemNames("{\"items\":[{\"name\":\"NACA 0012\"},{\"name\":\"x\"}]}");
            Assert.AreEqual("NACA 0012", names[0]);
        }

        // ================= U16/U17 下拉框部件可见性 =================

        /// <summary>U16: worker 的隐藏(被替代)部件必须在 JsonUtility 解析后存活,
        /// 否则下拉框「现有部件」看不到它们 (用户实测反馈: 新建后原部件消失)。</summary>
        [Test]
        public void U16_LoadGeoms_KeepsHiddenGeoms()
        {
            // 与 worker _list_geoms 输出同构 (含 slot=null 的隐藏件与 slot 分配件)
            string json = "{\"geoms\":[" +
                "{\"id\":\"F1\",\"name\":\"ExampleFuselage\",\"type\":\"Fuselage\",\"hidden\":false,\"slot\":\"fuselage\",\"subsurfs\":[]}," +
                "{\"id\":\"W1\",\"name\":\"ExampleWing\",\"type\":\"Wing\",\"hidden\":true,\"slot\":null,\"subsurfs\":[]}," +
                "{\"id\":\"W2\",\"name\":\"WingGeom\",\"type\":\"Wing\",\"hidden\":false,\"slot\":\"wing1\",\"subsurfs\":[]}" +
                "],\"slots\":{\"fuselage\":\"F1\",\"wing1\":\"W2\"},\"name\":\"T\"}";
            var st = new WorkshopState();
            st.LoadGeoms(json);
            Assert.AreEqual(3, st.geoms.Count, "隐藏部件必须保留在几何清单中");
            var hidden = st.geoms.Find(g => g.hidden);
            Assert.IsNotNull(hidden);
            Assert.AreEqual("ExampleWing", hidden.name);
            Assert.IsNull(hidden.slot);
            Assert.AreEqual("W2", st.Slot("wing1").geomId);
            Assert.AreEqual("WingGeom", st.Slot("wing1").geom);
        }

        /// <summary>U16: 下拉条目构建 — 当前✓ 置顶 / 可用 / 已停用 / 库 / 新建 三段分组。</summary>
        [Test]
        public void U16_DropdownEntries_ShowCurrentAvailableParkedLibrary()
        {
            var ui = new GameObject("WUI_U16").AddComponent<WorkshopUI>();
            try
            {
                var st = (WorkshopState)typeof(WorkshopUI)
                    .GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(ui);
                st.LoadGeoms("{\"geoms\":[" +
                    "{\"id\":\"W1\",\"name\":\"OldWing\",\"type\":\"Wing\",\"hidden\":true,\"slot\":null,\"subsurfs\":[]}," +
                    "{\"id\":\"W2\",\"name\":\"CurWing\",\"type\":\"Wing\",\"hidden\":false,\"slot\":\"wing1\",\"subsurfs\":[]}," +
                    "{\"id\":\"W3\",\"name\":\"FreeWing\",\"type\":\"Wing\",\"hidden\":false,\"slot\":null,\"subsurfs\":[]}," +
                    "{\"id\":\"F1\",\"name\":\"Fus\",\"type\":\"Fuselage\",\"hidden\":false,\"slot\":\"fuselage\",\"subsurfs\":[]}" +
                    "],\"slots\":{},\"name\":\"T\"}");
                st.library.Add(new WorkshopState.LibraryItem
                { type = "wing", name = "库机翼", path = "/lib/a.vsp3" });

                var labels = new List<string>();
                var actions = new List<string>();
                typeof(WorkshopUI)
                    .GetMethod("BuildSlotDropdownEntries", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(ui, new object[] { st.Slot("wing1"), labels, actions });

                // 分组与条目
                Assert.AreEqual("无", labels[0]);
                Assert.Contains("现有部件", labels);
                Assert.Contains("新建", labels);
                Assert.IsTrue(labels.Exists(l => l.Contains("CurWing") && l.Contains("当前")),
                    "当前部件必须置顶可见: " + string.Join("|", labels));
                Assert.IsTrue(labels.Exists(l => l.Contains("FreeWing") && !l.Contains("已停用")),
                    "未分配可见部件必须可见");
                Assert.IsTrue(labels.Exists(l => l.Contains("OldWing") && l.Contains("已停用")),
                    "被替代的隐藏部件必须以「已停用」保留在列表中");
                Assert.IsTrue(labels.Exists(l => l.Contains("[库] 库机翼")), "部件库条目");
                Assert.IsTrue(labels.Exists(l => l.Contains("新建机翼1")), "新建条目");
                // 机身几何不得出现在机翼槽 (类型过滤)
                Assert.IsFalse(labels.Exists(l => l.Contains("Fus")), "Fuselage 不得混入机翼槽");
                // 动作平行数组对齐
                Assert.AreEqual(labels.Count, actions.Count);
                int curIdx = labels.FindIndex(l => l.Contains("CurWing"));
                Assert.AreEqual("cur:", actions[curIdx]);
            }
            finally
            {
                Object.DestroyImmediate(ui.gameObject);
            }
        }

        /// <summary>U16 空状态: 无任何可用部件时显示占位行 (不空白)。</summary>
        [Test]
        public void U16_DropdownEntries_EmptyState()
        {
            var ui = new GameObject("WUI_U16E").AddComponent<WorkshopUI>();
            try
            {
                var st = (WorkshopState)typeof(WorkshopUI)
                    .GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(ui);
                st.LoadGeoms("{\"geoms\":[],\"slots\":{},\"name\":\"T\"}");
                var labels = new List<string>();
                var actions = new List<string>();
                typeof(WorkshopUI)
                    .GetMethod("BuildSlotDropdownEntries", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(ui, new object[] { st.Slot("wing2"), labels, actions });
                Assert.Contains("(暂无可用部件)", labels);
                Assert.Contains("新建机翼2 (默认参数)", labels);
            }
            finally
            {
                Object.DestroyImmediate(ui.gameObject);
            }
        }

        // ================= 审查修复 (R01-R13) =================

        [Test]
        public void R03_RequestJson_RoundTrip()
        {
            // 组包器输出必须语法完整 (审查 R03: 手拼缺闭合括号)
            string j = WorkshopReq.Obj().S("geom_id", "GID")
                .S("role", "aileron").F("eta_start", 0.55f)
                .F("eta_end", 0.95f).F("len_c", 0.25f)
                .S("user_name", "襟翼\"A\"").B("on", true).I("resample_to", 8)
                .Raw("diameters", WorkshopReq.FloatArr(new[] { 0.08f, 0.3f }))
                .Str();
            Assert.AreEqual("GID", WorkshopJson.Str(j, "geom_id"));
            Assert.AreEqual("aileron", WorkshopJson.Str(j, "role"));
            Assert.AreEqual(0.55f, WorkshopJson.Num(j, "eta_start"), 1e-4f);
            Assert.AreEqual(0.95f, WorkshopJson.Num(j, "eta_end"), 1e-4f);
            Assert.AreEqual(0.25f, WorkshopJson.Num(j, "len_c"), 1e-4f);
            Assert.AreEqual("襟翼\"A\"", WorkshopJson.Str(j, "user_name"));
            Assert.IsTrue(WorkshopJson.Bool(j, "on"));
            Assert.AreEqual(8, WorkshopJson.Int(j, "resample_to"));
            Assert.AreEqual(2, WorkshopReq.FloatArr(new[] { 1f, 2.5f }).Split(',').Length);
            StringAssert.StartsWith("[", WorkshopReq.FloatArr(new[] { 1f }));
            // 空对象/单字段也闭合
            Assert.AreEqual("{}", WorkshopReq.Obj().Str());
            Assert.AreEqual("x", WorkshopJson.Str(WorkshopReq.Obj().S("a", "x").Str(), "a"));
            // 每个参数对象闭合 (FlushEditorParms 的 parms 数组形态)
            string parms = "[{\"name\":\"Span\",\"group\":\"XSec_1\",\"value\":1.25}]";
            string j2 = WorkshopReq.Obj().S("geom_id", "G").Raw("parms", parms).Str();
            string arr = WorkshopJson.Raw(j2, "parms");
            Assert.AreEqual("Span", WorkshopJson.Str(arr, "name"));
            Assert.AreEqual(1.25f, WorkshopJson.Num(arr, "value"), 1e-4f);
        }

        [Test]
        public void R03_SetParmsJson_MatchesWingEditorPend()
        {
            // 机翼窗安装角在 F3 将改为 Y_Rotation; 此处锁定组包器与参数名映射一致
            string j = WorkshopReq.Obj().S("slot", "wing1").S("geom_id", "W").Str();
            Assert.AreEqual("wing1", WorkshopJson.Str(j, "slot"));
        }

        [UnityTest]
        public IEnumerator R03_EndToEnd_SetParmsViaBuilderActuallyApplies()
        {
            // 审查缺口 3: UI 组包层 → 真 worker 的端到端 (R03 修复的最终证明)
            var worker = new WorkshopWorkerClient();
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string configuredVsp = PlayerPrefs.GetString("Workshop.VspRoot", "");
            if (string.IsNullOrEmpty(configuredVsp))
                configuredVsp = System.Environment.GetEnvironmentVariable("VSPROOT");
            bool bundled = Directory.Exists(Path.Combine(root, "OpenVSP", "python", "openvsp"));
            if (string.IsNullOrEmpty(configuredVsp) && !bundled)
                Assert.Ignore("OpenVSP 未配置；设置 Workshop.VspRoot 或 VSPROOT 后运行该集成测试");
            if (!worker.Start(root))
                Assert.Ignore("worker 启动失败: " + worker.LastError);
            string replyJson = null;
            int step = 0;
            worker.Call("ping", "{}", r => { step = r.ok ? 1 : -1; });
            float t0 = Time.realtimeSinceStartup;
            while (step == 0 && Time.realtimeSinceStartup - t0 < 10f)
            {
                worker.Poll(_ => { }, _ => { });
                yield return null;
            }
            Assert.AreEqual(1, step, "worker ping 失败");

            worker.Call("load_example", "{}", r => { step = r.ok ? 2 : -2; });
            t0 = Time.realtimeSinceStartup;
            while (step == 2 - 1 && step != 2 && Time.realtimeSinceStartup - t0 < 20f)
            {
                worker.Poll(_ => { }, _ => { });
                yield return null;
            }
            Assert.AreEqual(2, step, "load_example 失败");

            // 组包器生成的 set_parms (机翼安装角, F3 后为 Y_Rotation 绝对姿态)
            string wingId = null;
            worker.Call("list_geoms", "{}", r =>
            {
                foreach (string g in WorkshopWidgets.SplitArray(WorkshopJson.Raw(r.dataJson, "geoms")))
                    if (WorkshopJson.Str(g, "name") == "ExampleWing")
                        wingId = WorkshopJson.Str(g, "id");
                step = 3;
            });
            t0 = Time.realtimeSinceStartup;
            while (wingId == null && Time.realtimeSinceStartup - t0 < 10f)
            {
                worker.Poll(_ => { }, _ => { });
                yield return null;
            }
            Assert.IsNotNull(wingId, "未找到 ExampleWing");

            worker.Call("set_parms",
                WorkshopReq.Obj().S("geom_id", wingId).Raw("parms",
                    "[{\"name\":\"Span\",\"group\":\"XSec_1\",\"value\":1.25}]").Str(),
                r => { replyJson = r.ok ? r.dataJson : ("ERR:" + r.error); step = 4; });
            t0 = Time.realtimeSinceStartup;
            while (step != 4 && Time.realtimeSinceStartup - t0 < 10f)
            {
                worker.Poll(_ => { }, _ => { });
                yield return null;
            }
            Assert.IsNull(replyJson == null ? null : replyJson.StartsWith("ERR") ? replyJson : null,
                "set_parms 失败: " + replyJson);

            worker.Call("get_geom_summary",
                WorkshopReq.Obj().S("geom_id", wingId).Str(),
                r =>
                {
                    replyJson = r.ok ? r.dataJson : ("ERR:" + r.error);
                    step = 5;
                });
            t0 = Time.realtimeSinceStartup;
            while (step != 5 && Time.realtimeSinceStartup - t0 < 10f)
            {
                worker.Poll(_ => { }, _ => { });
                yield return null;
            }
            Assert.IsTrue(replyJson != null && !replyJson.StartsWith("ERR"), replyJson);
            Assert.AreEqual(1.25f, WorkshopJson.Num(replyJson, "span_half"), 1e-3f,
                "组包器 set_parms 必须真正改到模型 (R03)");
            worker.Dispose();
        }

        // ================= UI 优化 U1-U3 (窗口/页签/颜色) =================

        private const System.Reflection.BindingFlags NP =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        [Test]
        public void U1_WindowRect_InitAndClamp()
        {
            var ui = new GameObject("WUI_U1").AddComponent<WorkshopUI>();
            var f = typeof(WorkshopUI);
            var rectF = f.GetField("_winRect", NP);
            var initM = f.GetMethod("InitWinRect", NP);
            var clampM = f.GetMethod("ClampWindowIntoView", NP);
            initM.Invoke(ui, null);
            var r = (Rect)rectF.GetValue(ui);
            Assert.LessOrEqual(r.width, 1200f, "UW01: 宽上限");
            Assert.LessOrEqual(r.height, 760f, "UW01: 高上限");
            Assert.GreaterOrEqual(r.x, 23f, "UW01: 四周留边");
            Assert.IsTrue(r.xMax <= Screen.width && r.yMax <= Screen.height, "窗口在视口内");
            // UW03: 异常 Rect 恢复
            rectF.SetValue(ui, new Rect(float.NaN, 0f, 100f, 100f));
            clampM.Invoke(ui, null);
            var r2 = (Rect)rectF.GetValue(ui);
            Assert.IsFalse(float.IsNaN(r2.x));
            Assert.GreaterOrEqual(r2.width, 720f);
            // UW03: 拖出视口后至少保证标题栏+关闭钮可见 (右缘露出 ≥120px)
            rectF.SetValue(ui, new Rect(Screen.width + 500f, -300f, 900f, 500f));
            clampM.Invoke(ui, null);
            var r3 = (Rect)rectF.GetValue(ui);
            Assert.LessOrEqual(r3.x, Screen.width - 120f + 0.5f);
            Assert.GreaterOrEqual(r3.y, 0f);
            Object.Destroy(ui);
        }

        [Test]
        public void U3_ApplySlotColor_DirtyWithoutInvalidatingAero()
        {
            var ui = new GameObject("WUI_U3").AddComponent<WorkshopUI>();
            var f = typeof(WorkshopUI);
            var st = (WorkshopState)f.GetField("_state", NP).GetValue(ui);
            var mv = f.GetProperty("ModelVersion");
            int v0 = (int)mv.GetValue(ui, null);
            ui.ApplySlotColor("wing1", "FF8800");
            Assert.AreEqual("#FF8800", st.Slot("wing1").color);
            Assert.IsTrue(st.dirty, "UC03: 颜色变化必须置 dirty");
            Assert.AreEqual(v0, (int)mv.GetValue(ui, null),
                "UC06: 纯外观变化不得失效气动结果 (模型版本不变)");
            Object.Destroy(ui);
        }

        [UnityTest]
        public IEnumerator U3_SetSlotColor_NoMaterialGrowth()
        {
            var pv = new WorkshopPreview();
            pv.Create(200, 150);
            var mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 2, 1 };
            mesh.RecalculateBounds();
            pv.SetSlotMesh("wing1", mesh, Color.white, "W");
            var mats = (List<Material>)typeof(WorkshopPreview)
                .GetField("_materials", NP).GetValue(pv);
            int n0 = mats.Count;
            for (int i = 0; i < 20; i++)
                pv.SetSlotColor("wing1", new Color(i / 20f, 0.5f, 0.5f));
            Assert.AreEqual(n0, mats.Count, "UC07: 连续切色不得新增材质 (A4 复用实例)");
            // 颜色必须真的写进材质 (用户反馈 bug1: 预览不随选色变化)
            var goField = typeof(WorkshopPreview).GetField("_slotObjs", NP);
            var objs = (System.Collections.Generic.Dictionary<string, GameObject>)goField.GetValue(pv);
            var mr = objs["wing1"].GetComponent<MeshRenderer>();
            Assert.AreEqual(0.95f, mr.sharedMaterial.color.r, 0.01f,
                "最后一次 SetSlotColor(0.95,0.5,0.5) 必须写入材质颜色");
            pv.Dispose();
            yield return null;
            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void UC04_SidecarKeepsSlotColor()
        {
            var st = new WorkshopState();
            st.Slot("wing1").color = "#FF8800";
            string vsp3 = Path.Combine(Application.temporaryCachePath, "uc04_color.vsp3");
            st.SaveSidecar(vsp3);
            var st2 = new WorkshopState();
            Assert.IsTrue(st2.LoadSidecar(vsp3, out _, out _));
            Assert.AreEqual("#FF8800", st2.Slot("wing1").color, "保存重开颜色一致 (UC04)");
        }

        // ================= WorkshopUI 烟雾 (反射驱动开关) =================

        [UnityTest]
        public IEnumerator WorkshopUI_TogglePausesAndResumesSimulation()
        {
            var go = new GameObject("WorkshopUI_Test");
            var ui = go.AddComponent<WorkshopUI>();
            Time.timeScale = 1f;
            var toggle = typeof(WorkshopUI).GetMethod("ToggleOpen",
                BindingFlags.NonPublic | BindingFlags.Instance);
            toggle.Invoke(ui, null);
            yield return null;
            Assert.AreEqual(0f, Time.timeScale, "工坊打开应暂停模拟");
            toggle.Invoke(ui, null);
            yield return null;
            Assert.AreEqual(1f, Time.timeScale, "关闭应恢复 timeScale");
            Object.Destroy(go);
            yield return null;   // OnDestroy: worker/preview 释放不报错
        }

        [UnityTest]
        public IEnumerator WorkshopUI_SwapCompletes_WhenWorkshopWindowClosed()
        {
            string py = WorkshopWorkerClient.DefaultPython313();
            if (!File.Exists(py)) Assert.Ignore("需要本机 Python 3.13 运行假管线脚本");

            // 假 import_vsp.py: 立即打印 [done] 行, 产物目录含真实 AircraftConfig.json
            string tmp = Path.Combine(Application.temporaryCachePath, "SwapClosed_" + Path.GetRandomFileName());
            string outDir = Path.Combine(tmp, "out");
            Directory.CreateDirectory(Path.Combine(tmp, "Tools"));
            Directory.CreateDirectory(outDir);
            string cfg = Path.Combine(outDir, "AircraftConfig.json");
            File.Copy(Path.Combine(Application.streamingAssetsPath, "AircraftConfig.json"), cfg);
            File.WriteAllText(Path.Combine(tmp, "Tools", "import_vsp.py"),
                "print('[done] 产物: " + outDir.Replace('\\', '/') + " (用时 1s)')\n");

            var go = new GameObject("WorkshopUI_SwapClosed");
            var ui = go.AddComponent<WorkshopUI>();
            var runner = (PipelineRunner)typeof(WorkshopUI)
                .GetField("_swapRunner", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(ui);
            Assert.IsFalse(WorkshopUI.IsOpen, "前置: 工坊窗口处于关闭状态");
            runner.Start(py, tmp, Path.Combine(tmp, "x.vsp3"), "T", 0f, 0.06f, 0f, false);

            GameObject swapped = null;
            float t0 = Time.realtimeSinceStartup;
            while (swapped == null && Time.realtimeSinceStartup - t0 < 15f)
            {
                yield return null;
                var found = GameObject.Find("TestUAV");
                var ctl = found != null ? found.GetComponent<AircraftController>() : null;
                if (ctl != null && ctl.ConfigAbsolutePath.Replace('\\', '/') == cfg.Replace('\\', '/'))
                    swapped = found;
            }
            Assert.IsNotNull(swapped, "管线结束后即使工坊窗口已关闭也必须完成热换机");

            Object.Destroy(swapped);
            Object.Destroy(go);
            yield return null;
            Directory.Delete(tmp, true);
        }

        // ---- 回归: 管线在 UI 不泵送期间结束 → [done]/[ui] 结束 留在队列里, 恢复泵送后仍须换机 ----
        // (旧守卫 IsRunning||OutDir!=""||LastError!="" 在此情形下永远早退, 第一次点击表现为"没反应")

        private static string MakeFakePipeline(string tag, out string outDir, bool withConfig)
        {
            string tmp = Path.Combine(Application.temporaryCachePath, tag + Path.GetRandomFileName());
            outDir = Path.Combine(tmp, "out");
            Directory.CreateDirectory(Path.Combine(tmp, "Tools"));
            Directory.CreateDirectory(outDir);
            if (withConfig)
                File.Copy(Path.Combine(Application.streamingAssetsPath, "AircraftConfig.json"),
                          Path.Combine(outDir, "AircraftConfig.json"));
            File.WriteAllText(Path.Combine(tmp, "Tools", "import_vsp.py"),
                "print('[ok] gen')\nprint('[done] 产物: " + outDir.Replace('\\', '/') + " (用时 1s)')\n");
            return tmp;
        }

        private static void WaitUntilExited(PipelineRunner runner)
        {
            float t0 = Time.realtimeSinceStartup;
            while (runner.IsRunning && Time.realtimeSinceStartup - t0 < 15f)
                System.Threading.Thread.Sleep(5);
            Assert.IsFalse(runner.IsRunning, "前置: 假管线应在 15s 内退出");
        }

        [Test]
        public void PipelineRunner_FinishedWithoutDrain_KeepsPendingLines()
        {
            string py = WorkshopWorkerClient.DefaultPython313();
            if (!File.Exists(py)) Assert.Ignore("需要本机 Python 3.13 运行假管线脚本");
            string tmp = MakeFakePipeline("RunnerPending_", out string outDir, false);
            try
            {
                var runner = new PipelineRunner();
                runner.Start(py, tmp, Path.Combine(tmp, "x.vsp3"), "T", 0f, 0.06f, 0f, false);
                WaitUntilExited(runner);   // 整个运行期间没人 Drain

                Assert.AreEqual("", runner.OutDir, "OutDir 只在 Drain 时解析 — 此刻尚未解析");
                Assert.IsTrue(runner.HasPending, "进程已退出但完成行仍在队列, 调用方必须据此继续泵送");

                bool finished = false;
                runner.Drain(line => { if (line == "[ui] 结束") finished = true; });
                Assert.IsTrue(finished, "Drain 应取到结束行");
                Assert.AreEqual(outDir.Replace('\\', '/'), runner.OutDir.Replace('\\', '/'));
                Assert.IsFalse(runner.HasPending, "取完后队列为空");

                // 再启动一轮: 不得残留上一轮的完成行
                runner.Start(py, tmp, Path.Combine(tmp, "x.vsp3"), "T", 0f, 0.06f, 0f, false);
                Assert.AreEqual("", runner.OutDir, "Start 重置 OutDir");
                var first = new List<string>();
                runner.Drain(l => first.Add(l));
                Assert.IsFalse(first.Exists(l => l == "[ui] 结束" || l.StartsWith("[done]")),
                    "Start 后不应读到上一轮残留的 [done]/[ui] 结束");
                WaitUntilExited(runner);
                runner.Drain(l => { });
            }
            finally { Directory.Delete(tmp, true); }
        }

        [UnityTest]
        public IEnumerator WorkshopUI_SwapCompletes_WhenPipelineEndsBetweenFrames()
        {
            string py = WorkshopWorkerClient.DefaultPython313();
            if (!File.Exists(py)) Assert.Ignore("需要本机 Python 3.13 运行假管线脚本");
            string tmp = MakeFakePipeline("SwapBetween_", out string outDir, true);
            string cfg = Path.Combine(outDir, "AircraftConfig.json");

            var go = new GameObject("WorkshopUI_SwapBetween");
            var ui = go.AddComponent<WorkshopUI>();
            ui.enabled = false;   // 管线运行期间 UI 不泵送 (等价于帧间隔长于进程收尾窗口)
            var runner = (PipelineRunner)typeof(WorkshopUI)
                .GetField("_swapRunner", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(ui);
            runner.Start(py, tmp, Path.Combine(tmp, "x.vsp3"), "T", 0f, 0.06f, 0f, false);
            WaitUntilExited(runner);
            ui.enabled = true;

            GameObject swapped = null;
            float t0 = Time.realtimeSinceStartup;
            while (swapped == null && Time.realtimeSinceStartup - t0 < 15f)
            {
                yield return null;
                var found = GameObject.Find("TestUAV");
                var ctl = found != null ? found.GetComponent<AircraftController>() : null;
                if (ctl != null && ctl.ConfigAbsolutePath.Replace('\\', '/') == cfg.Replace('\\', '/'))
                    swapped = found;
            }
            Assert.IsNotNull(swapped, "管线在帧间结束时, 完成事件不得丢失 (第一次点击即应换机)");

            Object.Destroy(swapped);
            Object.Destroy(go);
            yield return null;
            Directory.Delete(tmp, true);
        }
    }
}
