using System.Collections.Generic;
using System.IO;
using FlightSim;
using NUnit.Framework;
using UnityEngine;

namespace FlightSim.Tests
{
    /// <summary>
    /// RuntimeObjLoader 分组支持测试 (渲染升级 R4):
    /// o/g 分组 → 每组一个子物体 + 独立 Renderer (名字=组名, 供 ApplyLivery 命名);
    /// 无分组 → 回退单网格 (旧行为)。
    /// </summary>
    public class RuntimeObjLoaderGroupTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Application.temporaryCachePath, "r4_loader_tests");
            Directory.CreateDirectory(_dir);
        }

        [Test]
        public void Loader_GroupedObj_CreatesOneRendererPerGroup()
        {
            // 仿 stl_to_obj_unity --parts 的输出: o 名 + 各组顶点段 + 全局索引面
            string obj = Path.Combine(_dir, "grouped.obj");
            File.WriteAllText(obj,
                "# test\n" +
                "o Fuselage\n" +
                "v 0 0 0\nv 1 0 0\nv 0 1 0\n" +
                "f 1 3 2\n" +
                "o MainWing\n" +
                "v 2 0 0\nv 3 0 0\nv 2 1 0\n" +
                "f 4 6 5\n");

            var parent = new GameObject();
            try
            {
                var child = RuntimeObjLoader.BuildChild(parent.transform, obj);
                Assert.IsNotNull(child, "分组 OBJ 应加载成功");
                var rends = child.GetComponentsInChildren<MeshRenderer>();
                Assert.AreEqual(2, rends.Length, "每组应一个 Renderer");
                var names = new HashSet<string> { rends[0].name, rends[1].name };
                Assert.IsTrue(names.Contains("Fuselage") && names.Contains("MainWing"),
                    "子物体名应等于组名: " + string.Join(",", names));
                foreach (var mf in child.GetComponentsInChildren<MeshFilter>())
                    Assert.AreEqual(3, mf.sharedMesh.vertexCount, "每组网格顶点应已紧消化");
            }
            finally { Object.DestroyImmediate(parent); }
        }

        [Test]
        public void Loader_UngroupedObj_FallsBackToSingleMesh()
        {
            string obj = Path.Combine(_dir, "single.obj");
            File.WriteAllText(obj, "# t\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 3 2\n");

            var parent = new GameObject();
            try
            {
                var child = RuntimeObjLoader.BuildChild(parent.transform, obj);
                Assert.IsNotNull(child);
                Assert.AreEqual(1, child.GetComponentsInChildren<MeshRenderer>().Length,
                    "无分组 OBJ 应回退单网格");
                Assert.AreEqual(3, child.GetComponentInChildren<MeshFilter>().sharedMesh.vertexCount);
            }
            finally { Object.DestroyImmediate(parent); }
        }

        [Test]
        public void Loader_MissingFile_ReturnsNull()
        {
            var parent = new GameObject();
            try
            {
                Assert.IsNull(RuntimeObjLoader.BuildChild(parent.transform,
                    Path.Combine(_dir, "nope.obj")));
            }
            finally { Object.DestroyImmediate(parent); }
        }
    }
}
