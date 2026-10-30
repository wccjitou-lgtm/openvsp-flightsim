using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FlightSim.Tests
{
    /// <summary>
    /// Phase 7b 导入机型验收: 运行时 OBJ 解析 + 绝对路径 config 热替换。
    /// (管线本身的 python/AngelScript 侧验收在 Tests/test_import_vsp.py 与
    /// 端到端实测, 此处只测 Unity 运行时装配。)
    /// </summary>
    public class ImportPlayModeTests
    {
        private string _tmpDir;
        private GameObject _swapped;

        [SetUp]
        public void SetUp()
        {
            _tmpDir = Path.Combine(Application.temporaryCachePath,
                "ImportTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tmpDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (_swapped != null) Object.Destroy(_swapped);
            var go = GameObject.Find("TestUAV");
            if (go != null) Object.Destroy(go);
            if (_tmpDir != null && Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, true);
        }

        private string WriteObj()
        {
            string obj = Path.Combine(_tmpDir, "test.obj");
            File.WriteAllLines(obj, new[]
            {
                "# test obj",
                "v 0 0 0", "v 1 0 0", "v 0 1 0", "v 0 0 1",
                "f 1 2 3",           // 单三角
                "f 1 2 3 4",         // 四边形 → 扇形三角化 = 2 三角
            });
            return obj;
        }

        [UnityTest]
        public IEnumerator RuntimeObjLoader_ParsesVerticesAndTriangulates()
        {
            Mesh mesh = RuntimeObjLoader.LoadMesh(WriteObj());
            Assert.IsNotNull(mesh, "OBJ 应解析成功");
            Assert.AreEqual(4, mesh.vertexCount, "顶点数");
            Assert.AreEqual(3 * 3, mesh.triangles.Length, "1 三角 + 四边形扇形化 2 三角 = 3 三角");
            yield return null;
        }

        [UnityTest]
        public IEnumerator RuntimeObjLoader_MissingFileReturnsNull()
        {
            Assert.IsNull(RuntimeObjLoader.LoadMesh(
                Path.Combine(_tmpDir, "nope.obj")));
            yield return null;
        }

        [UnityTest]
        public IEnumerator AircraftSwapper_Swap_BuildsAircraftFromAbsolutePaths()
        {
            // config: 复制真实 TestUAV 配置到绝对路径 (证明走的是 ConfigAbsolutePath 分支)
            string cfg = Path.Combine(_tmpDir, "AircraftConfig.json");
            File.Copy(Path.Combine(Application.streamingAssetsPath, "AircraftConfig.json"), cfg);
            string obj = WriteObj();

            _swapped = AircraftSwapper.Swap(cfg, obj);
            Assert.IsNotNull(_swapped, "换机应返回新飞机 GO");
            var controller = _swapped.GetComponent<AircraftController>();
            Assert.IsNotNull(controller, "控制器已挂载");
            Assert.AreEqual(cfg.Replace('\\', '/'), controller.ConfigAbsolutePath.Replace('\\', '/'),
                "ConfigAbsolutePath 已生效 (Awake 前设置)");

            // 等待 Start/AircraftVisual 装配 + 几个物理步
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 3f) yield return null;

            Assert.IsNotNull(_swapped.transform.Find("ImportedModel"),
                "运行时 OBJ 应装配为 ImportedModel 子物体");
            Assert.IsFalse(float.IsNaN(controller.Altitude) || float.IsNaN(controller.BankDeg),
                "物理正常积分, 无 NaN");
            Assert.Greater(controller.State.Position.Z, -50f,
                "飞机存在 (NED 高度合理): " + controller.State.Position.Z);
            Debug.Log("[Import] 换机验收: Altitude=" + controller.Altitude.ToString("F2")
                      + "m, ImportedModel 已装配 ✓");
        }

        // ---- 机型库 (WorkshopLibrary.ScanLibrary): 免重算切换已导入机型 ----

        private static string MakeEntryDir(string root, string folderName)
        {
            string dir = Path.Combine(root, folderName);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "AircraftConfig.json"), "{}");
            return dir;
        }

        [UnityTest]
        public IEnumerator AircraftSwapper_SwapToShallowGear_KeepsWheelsOnGround()
        {
            // 回归 (2026-10-10, 用户实测"换机后模型跑到地底下"):
            // 轮位差补偿方向 — 旧机轮深 0.35m → 新机 0.03m 时, 新机 CG 出生点应
            // 下移 (NED z 增大 ≈0.32), 使轮子落在同一地面高度; 修复前方向相反,
            // 浅轮机会被埋进地下 0.6m+。
            var oldGo = new GameObject("OldPlane");
            var oldCtrl = oldGo.AddComponent<AircraftController>();   // Awake 同步装配默认配置
            yield return null;
            Assert.IsNotNull(oldCtrl.Plane, "前置: 默认机装配成功");
            Assert.IsNotNull(oldCtrl.Plane.Gear, "前置: 默认机有起落架");
            float oldWheelZ = 0f;
            foreach (var w in oldCtrl.Plane.Gear.Wheels)
                oldWheelZ = Mathf.Max(oldWheelZ, (float)w.Position.Z);
            Assert.Greater(oldWheelZ, 0.2f, "前置: 默认机轮深 ~0.35, 实测 " + oldWheelZ);
            oldCtrl.InitialPositionNed = new Vector3(0f, 0f, -oldWheelZ);   // 轮子恰好在地面

            // 浅轮机型配置: 复制默认配置, 轮深 0.35 → 0.03
            string cfg = Path.Combine(_tmpDir, "shallow.json");
            string text = File.ReadAllText(
                Path.Combine(Application.streamingAssetsPath, "AircraftConfig.json"));
            string patched = System.Text.RegularExpressions.Regex.Replace(text,
                "(position_m\"\\s*:\\s*\\[[^\\]]*?)0\\.35",
                m => m.Groups[1].Value + "0.03");
            Assert.AreNotEqual(text, patched, "前置: 轮深替换生效");
            File.WriteAllText(cfg, patched);

            _swapped = AircraftSwapper.Swap(cfg, "");
            var ctl = _swapped.GetComponent<AircraftController>();
            float newZ = ctl.InitialPositionNed.z;
            Assert.AreEqual(-oldWheelZ + (oldWheelZ - 0.03f), newZ, 1e-3f,
                "新机出生 NED z = 旧 z + 轮位差 (CG 下移, 轮子同高)");
            Assert.Greater(newZ, -0.05f, "浅轮新机 CG 出生点贴近地面 (不埋): " + newZ);
            Debug.Log("[Import] 浅轮换机出生高度: " + newZ.ToString("F3") + " (旧机 " +
                      (-oldWheelZ).ToString("F3") + ") ✓");
        }

        [Test]
        public void ScanLibrary_ParsesReportJson_UsesDisplayNameAndSpecs()
        {
            string dir = MakeEntryDir(_tmpDir, "uav_x");
            File.WriteAllText(Path.Combine(dir, "report.json"),
                "{\"name\":\"隼鸟\",\"mass_kg\":16.68,\"Sref_m2\":15.97,\"bref_m\":11.5," +
                "\"warnings\":[\"a\",\"b\"]}");

            var list = WorkshopLibrary.ScanLibrary(_tmpDir);

            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("隼鸟", list[0].DisplayName, "report.json 的 name 应覆盖文件夹名");
            Assert.IsTrue(list[0].HasReport);
            Assert.AreEqual(16.68f, list[0].MassKg, 0.001f);
            Assert.AreEqual(15.97f, list[0].SrefM2, 0.001f);
            Assert.AreEqual(11.5f, list[0].BrefM, 0.001f);
            Assert.AreEqual(2, list[0].WarningCount);
        }

        [Test]
        public void ScanLibrary_MissingReportJson_FallsBackToFolderName()
        {
            MakeEntryDir(_tmpDir, "no_report_here");

            var list = WorkshopLibrary.ScanLibrary(_tmpDir);

            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("no_report_here", list[0].DisplayName);
            Assert.IsFalse(list[0].HasReport);
        }

        [Test]
        public void ScanLibrary_WarningCount_CombinesWarningsAndSkippedControls()
        {
            string dir = MakeEntryDir(_tmpDir, "uav_y");
            File.WriteAllText(Path.Combine(dir, "report.json"),
                "{\"name\":\"y\",\"warnings\":[\"a\"],\"skipped_controls\":[\"relocated:Elevator\",\"x\"]}");

            var list = WorkshopLibrary.ScanLibrary(_tmpDir);

            Assert.AreEqual(1, list.Count);
            Assert.AreEqual(3, list[0].WarningCount, "1 条 warning + 2 条 skipped_controls = 3");
        }

        [Test]
        public void ReadReportOrNull_ParsesAllFields()
        {
            string dir = MakeEntryDir(_tmpDir, "uav_z");
            File.WriteAllText(Path.Combine(dir, "report.json"),
                "{\"name\":\"z\",\"mass_kg\":5,\"Sref_m2\":3.1,\"bref_m\":4.0,\"cref_m\":0.55," +
                "\"warnings\":[\"w1\"],\"skipped_controls\":[\"s1\"]}");

            var r = WorkshopLibrary.ReadReportOrNull(dir);

            Assert.IsNotNull(r);
            Assert.AreEqual("z", r.name);
            Assert.AreEqual(0.55f, r.cref_m, 0.001f);
            Assert.AreEqual(1, r.warnings.Length);
            Assert.AreEqual(1, r.skipped_controls.Length);
        }

        [Test]
        public void ReadReportOrNull_MissingFile_ReturnsNull()
        {
            string dir = MakeEntryDir(_tmpDir, "uav_no_report");
            Assert.IsNull(WorkshopLibrary.ReadReportOrNull(dir));
        }

        [Test]
        public void ReadReportOrNull_MalformedJson_ReturnsNullWithoutThrowing()
        {
            string dir = MakeEntryDir(_tmpDir, "uav_bad_json");
            File.WriteAllText(Path.Combine(dir, "report.json"), "{not valid json");

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("report\\.json 解析失败"));
            WorkshopLibrary.ReportJson r = null;
            Assert.DoesNotThrow(() => r = WorkshopLibrary.ReadReportOrNull(dir));
            Assert.IsNull(r);
        }

        [Test]
        public void ScanLibrary_SkipsDirsWithoutAircraftConfig()
        {
            string dir = Path.Combine(_tmpDir, "half_baked");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "report.json"), "{\"name\":\"x\"}");

            var list = WorkshopLibrary.ScanLibrary(_tmpDir);

            Assert.AreEqual(0, list.Count, "无 AircraftConfig.json 的目录不应进机型库");
        }

        [Test]
        public void ScanLibrary_MissingRoot_ReturnsEmptyList()
        {
            var list = WorkshopLibrary.ScanLibrary(Path.Combine(_tmpDir, "does_not_exist"));
            Assert.AreEqual(0, list.Count);
        }

        [Test]
        public void ScanLibrary_SortsByConfigMTimeDescending()
        {
            string older = MakeEntryDir(_tmpDir, "older");
            string newer = MakeEntryDir(_tmpDir, "newer");
            var t0 = System.DateTime.UtcNow.AddMinutes(-10);
            File.SetLastWriteTimeUtc(Path.Combine(older, "AircraftConfig.json"), t0);
            File.SetLastWriteTimeUtc(Path.Combine(newer, "AircraftConfig.json"), t0.AddMinutes(5));

            var list = WorkshopLibrary.ScanLibrary(_tmpDir);

            Assert.AreEqual(2, list.Count);
            Assert.AreEqual("newer", list[0].DisplayName, "最近修改的 AircraftConfig.json 应排最前");
            Assert.AreEqual("older", list[1].DisplayName);
        }
    }
}
