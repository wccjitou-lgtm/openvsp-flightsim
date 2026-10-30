using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 运行时 OBJ 加载器 (Phase 7b): 把 stl_to_obj_unity.py 输出的 OBJ
    /// 解析成 Mesh, 供导入机型运行时装配。
    /// 渲染升级 R4: 支持 `o`/`g` 分组 → 每组一个子物体 + 独立 Renderer
    /// (子物体名 = 组名, AircraftVisual.ApplyLivery 按名上色由此生效);
    /// 无分组或仅 1 个有效组时回退单网格 (与旧行为一致)。
    /// 其余容错不变: vn/vt/mtllib/usemtl 行忽略, 多边形面扇形三角化,
    /// 负索引按 OBJ 规则 (相对当前顶点计数) 解析, 顶点数超 65535 自动 32 位索引,
    /// 法线缺失时 RecalculateNormals。
    /// </summary>
    public static class RuntimeObjLoader
    {
        private static Material _defaultMat;

        /// <summary>加载 OBJ 并在 parent 下创建 ImportedModel 子物体; 失败返回 null。
        /// 带 ≥2 个有效分组的 OBJ → 每组一个子物体; 否则单网格单物体。</summary>
        public static GameObject BuildChild(Transform parent, string objPath)
        {
            var groups = LoadGrouped(objPath);
            if (groups != null && groups.Count >= 2)
            {
                var root = new GameObject("ImportedModel");
                root.transform.SetParent(parent, false);
                foreach (var g in groups)
                {
                    var child = new GameObject(g.Name);
                    child.transform.SetParent(root.transform, false);
                    child.AddComponent<MeshFilter>().sharedMesh = g.Mesh;
                    child.AddComponent<MeshRenderer>().sharedMaterial = GetDefaultMaterial();
                }
                Debug.Log(string.Format("[RuntimeObjLoader] {0}: {1} 个分组部件",
                    Path.GetFileName(objPath), groups.Count));
                return root;
            }

            Mesh mesh = LoadMesh(objPath);
            if (mesh == null) return null;

            var go = new GameObject("ImportedModel");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = GetDefaultMaterial();
            Debug.Log(string.Format("[RuntimeObjLoader] {0}: 顶点 {1}, 三角形 {2}",
                Path.GetFileName(objPath), mesh.vertexCount, mesh.triangles.Length / 3));
            return go;
        }

        /// <summary>分组解析结果: 组名 + 该组的独立 Mesh。</summary>
        public struct GroupMesh
        {
            public string Name;
            public Mesh Mesh;
        }

        /// <summary>
        /// 解析 OBJ 的 o/g 分组, 每组产出独立 (顶点紧凑化的) Mesh。
        /// 顶点表全局累计 (导出器面索引用全局编号), 每组收集自己的引用后重映射。
        /// 无分组/无有效组返回 null。
        /// </summary>
        public static List<GroupMesh> LoadGrouped(string objPath)
        {
            if (!File.Exists(objPath)) return null;

            var verts = new List<Vector3>();
            var groupNames = new List<string>();
            var groupTris = new List<List<int>>();
            int current = -1;

            foreach (string raw in File.ReadAllLines(objPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.StartsWith("v "))
                {
                    var p = Split3(line);
                    if (p.HasValue) verts.Add(p.Value);
                }
                else if (line.StartsWith("o ") || line.StartsWith("g "))
                {
                    groupNames.Add(line.Substring(2).Trim());
                    groupTris.Add(new List<int>());
                    current = groupNames.Count - 1;
                }
                else if (line.StartsWith("f "))
                {
                    if (current < 0)   // 分组前的面: 归入隐式组 (兼容非常规文件)
                    {
                        groupNames.Add("(default)");
                        groupTris.Add(new List<int>());
                        current = 0;
                    }
                    var tok = line.Substring(2).Split(
                        new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                    var idx = new List<int>();
                    foreach (string t in tok)
                    {
                        string vtok = t.Split('/')[0];
                        if (!int.TryParse(vtok, out int vi)) continue;
                        if (vi < 0) vi = verts.Count + vi + 1;
                        else vi = vi - 1;
                        if (vi >= 0 && vi < verts.Count) idx.Add(vi);
                    }
                    for (int i = 1; i + 1 < idx.Count; i++)
                    {
                        groupTris[current].Add(idx[0]);
                        groupTris[current].Add(idx[i]);
                        groupTris[current].Add(idx[i + 1]);
                    }
                }
            }

            var result = new List<GroupMesh>();
            for (int g = 0; g < groupNames.Count; g++)
            {
                if (groupTris[g].Count == 0) continue;
                // 顶点紧消化: 收集本组用到的全局索引并重映射
                var used = new Dictionary<int, int>();
                var localVerts = new List<Vector3>();
                var localTris = new int[groupTris[g].Count];
                for (int i = 0; i < groupTris[g].Count; i++)
                {
                    int gi = groupTris[g][i];
                    if (!used.TryGetValue(gi, out int li))
                    {
                        li = localVerts.Count;
                        used[gi] = li;
                        localVerts.Add(verts[gi]);
                    }
                    localTris[i] = li;
                }
                var mesh = new Mesh { name = groupNames[g] };
                if (localVerts.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(localVerts);
                mesh.SetTriangles(localTris, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                result.Add(new GroupMesh { Name = groupNames[g], Mesh = mesh });
            }
            return result.Count > 0 ? result : null;
        }

        /// <summary>解析 OBJ 文件为单个 Mesh (所有分组扁平合并); 失败 (文件缺失/无面) 返回 null。</summary>
        public static Mesh LoadMesh(string objPath)
        {
            if (!File.Exists(objPath))
            {
                Debug.LogWarning("[RuntimeObjLoader] 文件不存在: " + objPath);
                return null;
            }

            var verts = new List<Vector3>();
            var tris = new List<int>();
            var normals = new List<Vector3>();
            bool hasNormals = false;

            foreach (string raw in File.ReadAllLines(objPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.StartsWith("v "))            // 顶点
                {
                    var p = Split3(line);
                    if (p.HasValue) verts.Add(p.Value);
                }
                else if (line.StartsWith("vn "))      // 法线 (记录但缺失面法线时整体重算)
                {
                    var n = Split3(line);
                    if (n.HasValue) { normals.Add(n.Value); hasNormals = true; }
                }
                else if (line.StartsWith("f "))       // 面 (扇形三角化)
                {
                    var tok = line.Substring(2).Split(
                        new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                    var idx = new List<int>();
                    foreach (string t in tok)
                    {
                        // 格式 v, v/vt, v//vn, v/vt/vn; 索引 1 起, 负数相对当前计数
                        string vtok = t.Split('/')[0];
                        if (!int.TryParse(vtok, out int vi)) continue;
                        if (vi < 0) vi = verts.Count + vi + 1;
                        else vi = vi - 1;
                        if (vi >= 0 && vi < verts.Count) idx.Add(vi);
                    }
                    for (int i = 1; i + 1 < idx.Count; i++)
                    {
                        tris.Add(idx[0]); tris.Add(idx[i]); tris.Add(idx[i + 1]);
                    }
                }
                // vt / mtllib / usemtl 等忽略
            }

            if (verts.Count == 0 || tris.Count == 0)
            {
                Debug.LogWarning("[RuntimeObjLoader] 无有效网格数据: " + objPath);
                return null;
            }

            var mesh = new Mesh { name = Path.GetFileNameWithoutExtension(objPath) };
            if (verts.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            if (hasNormals && normals.Count == verts.Count) mesh.SetNormals(normals);
            else mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Vector3? Split3(string line)
        {
            var tok = line.Split(new[] { ' ', '\t' },
                System.StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length < 4) return null;
            if (!float.TryParse(tok[1], out float x) ||
                !float.TryParse(tok[2], out float y) ||
                !float.TryParse(tok[3], out float z)) return null;
            return new Vector3(x, y, z);
        }

        private static Material GetDefaultMaterial()
        {
            if (_defaultMat == null)
                _defaultMat = RenderMaterials.Create(new Color(0.92f, 0.94f, 0.96f));
            return _defaultMat;
        }
    }
}