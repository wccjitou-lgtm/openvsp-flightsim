using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 机型工坊 W1: ASCII STL → Unity Mesh 加载器。
    /// 坐标变换与 Tools/stl_to_obj_unity.py 逐位同款 (该脚本是轴映射的唯一权威):
    ///   Unity_x =  Y_ovsp - cgY   (右)
    ///   Unity_y =  Z_ovsp - cgZ   (上)
    ///   Unity_z = -(X_ovsp - cgX) (前)
    /// 左手系翻转 → 三角形绕序反转 (a, c, b)。
    /// OpenVSP 导出的 ASCII STL 行首带空格 + CRLF, 解析必须 Trim。
    /// 预览用途: 顶点按 1e-6 量化去重后共享 (法线整体重算)。
    /// </summary>
    public static class WorkshopStlLoader
    {
        /// <summary>加载 STL 为 Mesh; 失败 (文件缺失/无三角形) 返回 null 并告警。</summary>
        public static Mesh LoadMesh(string stlPath, Vector3 cgOvsp)
        {
            if (!File.Exists(stlPath))
            {
                Debug.LogWarning("[WorkshopStlLoader] 文件不存在: " + stlPath);
                return null;
            }

            // (量化键) → 顶点索引; 与 py 端 round(v*1e6) 量化一致
            var vertexMap = new Dictionary<Vector3Int, int>();
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var tri = new int[3];
            int triCount = 0;

            foreach (string raw in File.ReadAllLines(stlPath))
            {
                string line = raw.Trim();
                if (!line.StartsWith("vertex")) continue;
                string[] tok = line.Split(' ');
                if (tok.Length < 4) continue;
                if (!float.TryParse(tok[1], out float x) ||
                    !float.TryParse(tok[2], out float y) ||
                    !float.TryParse(tok[3], out float z)) continue;

                // OpenVSP 体轴 → Unity 轴 (重心平移由 cgOvsp 参数化, 预览传零向量)
                Vector3 u = new Vector3(y - cgOvsp.y, z - cgOvsp.z, -(x - cgOvsp.x));
                Vector3Int key = new Vector3Int(
                    Mathf.RoundToInt(u.x * 1e6f),
                    Mathf.RoundToInt(u.y * 1e6f),
                    Mathf.RoundToInt(u.z * 1e6f));
                if (!vertexMap.TryGetValue(key, out int idx))
                {
                    idx = vertices.Count;
                    vertexMap[key] = idx;
                    vertices.Add(u);
                }
                tri[triCount++] = idx;
                if (triCount == 3)
                {
                    // 左手系: 反转绕序保证法线朝外
                    triangles.Add(tri[0]);
                    triangles.Add(tri[2]);
                    triangles.Add(tri[1]);
                    triCount = 0;
                }
            }

            if (vertices.Count == 0 || triangles.Count == 0)
            {
                Debug.LogWarning("[WorkshopStlLoader] 无有效三角形: " + stlPath);
                return null;
            }

            var mesh = new Mesh { name = Path.GetFileNameWithoutExtension(stlPath) };
            if (vertices.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>OpenVSP 坐标点 → Unity 预览坐标 (标注/重心球共用同一变换)。</summary>
        public static Vector3 ToUnity(Vector3 ovsp, Vector3 cgOvsp)
        {
            return new Vector3(ovsp.y - cgOvsp.y, ovsp.z - cgOvsp.z, -(ovsp.x - cgOvsp.x));
        }

        /// <summary>
        /// 操纵面高亮网格 (W2): 从 STL 原(OVSP 系)顶点按舵面参数提取后缘条带三角形。
        /// 条带法自适应平面形 (后掠/扭转/垂尾旋转): 展向轴取包围盒较宽侧 (水平翼=y,
        /// 垂尾=z — 绕 X 旋转不改 x, 弦向恒为 x); 每条带后缘=带内最大 x, 舵面区=
        /// eta ∈ [etaStart,etaEnd] 且 x ≥ 后缘-len_c×当地弦。仅视觉辅助, 非精确分割。
        /// </summary>
        public static List<Mesh> BuildControlOverlays(string stlPath, Vector3 cgOvsp,
            IList<Vector3> etaStartEndLenC, int bandCount = 24)
        {
            var result = new List<Mesh>();
            if (!File.Exists(stlPath) || etaStartEndLenC == null || etaStartEndLenC.Count == 0)
                return result;

            // 读原始三角形 (OVSP 系)
            var tris = new List<Vector3[]>();   // 每项 3 顶点
            var verts = new List<Vector3>();
            foreach (string raw in File.ReadAllLines(stlPath))
            {
                string line = raw.Trim();
                if (!line.StartsWith("vertex")) continue;
                string[] tok = line.Split(' ');
                if (tok.Length < 4) continue;
                if (float.TryParse(tok[1], out float x) && float.TryParse(tok[2], out float y) &&
                    float.TryParse(tok[3], out float z))
                {
                    verts.Add(new Vector3(x, y, z));
                    if (verts.Count == 3) { tris.Add(verts.ToArray()); verts.Clear(); }
                }
            }
            if (tris.Count == 0) return result;

            // 展向轴判定 + 半展
            float yMin = float.MaxValue, yMax = float.MinValue, zMin = float.MaxValue, zMax = float.MinValue;
            foreach (var t in tris)
                foreach (var v in t)
                {
                    if (v.y < yMin) yMin = v.y; if (v.y > yMax) yMax = v.y;
                    if (v.z < zMin) zMin = v.z; if (v.z > zMax) zMax = v.z;
                }
            bool horizontal = Mathf.Abs(yMax - yMin) >= Mathf.Abs(zMax - zMin);
            float lo = horizontal ? yMin : zMin, hi = horizontal ? yMax : zMax;
            float span = Mathf.Max(1e-4f, Mathf.Max(Mathf.Abs(lo), Mathf.Abs(hi)));   // 对称翼单边建模
            // OVSP Wing 半展自原点起; 非对称 (双垂尾 Y 偏移) 时取最宽单侧
            float side = Mathf.Abs(hi) >= Mathf.Abs(lo) ? hi : lo;

            // 条带后缘/弦长
            var bandMaxX = new float[bandCount + 1];
            var bandMinX = new float[bandCount + 1];
            for (int i = 0; i <= bandCount; i++) { bandMaxX[i] = float.MinValue; bandMinX[i] = float.MaxValue; }
            foreach (var t in tris)
                foreach (var v in t)
                {
                    float eta = Mathf.Clamp(Mathf.Abs(horizontal ? v.y : v.z) / span, 0f, 1f);
                    int b = Mathf.Min(bandCount, Mathf.RoundToInt(eta * bandCount));
                    if (v.x > bandMaxX[b]) bandMaxX[b] = v.x;
                    if (v.x < bandMinX[b]) bandMinX[b] = v.x;
                }

            for (int c = 0; c < etaStartEndLenC.Count; c++)
            {
                float e0 = etaStartEndLenC[c].x, e1 = etaStartEndLenC[c].y, lc = etaStartEndLenC[c].z;
                var ov = new List<Vector3>();
                var ot = new List<int>();
                foreach (var t in tris)
                {
                    bool inZone = true;
                    for (int k = 0; k < 3; k++)
                    {
                        var v = t[k];
                        float eta = Mathf.Abs(horizontal ? v.y : v.z) / span;
                        if (eta < e0 - 0.02f || eta > e1 + 0.02f) { inZone = false; break; }
                        int b = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(eta) * bandCount), 0, bandCount);
                        if (bandMaxX[b] == float.MinValue) { inZone = false; break; }
                        float chord = Mathf.Max(1e-4f, bandMaxX[b] - bandMinX[b]);
                        // 绝对展向带内取该侧的后缘: 双垂尾两侧对称时 x 后缘一致
                        if (v.x < bandMaxX[b] - lc * chord - 1e-4f) { inZone = false; break; }
                    }
                    if (!inZone) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        Vector3 u = ToUnity(t[k], cgOvsp);
                        Vector3Int key = new Vector3Int(Mathf.RoundToInt(u.x * 1e6f),
                            Mathf.RoundToInt(u.y * 1e6f), Mathf.RoundToInt(u.z * 1e6f));
                        int idx = ov.Count;
                        ov.Add(u);
                        ot.Add(idx);
                    }
                }
                if (ot.Count >= 3)
                {
                    var m = new Mesh { name = "ControlOverlay" + c };
                    if (ov.Count > 65535) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                    m.SetVertices(ov);
                    m.SetTriangles(ot, 0);
                    m.RecalculateNormals();
                    m.RecalculateBounds();
                    result.Add(m);
                }
            }
            return result;
        }
    }
}
