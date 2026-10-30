using System.Collections.Generic;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 程序化网格工具 (写实向场景建模的基础):
    ///   Icosphere      二十面体细分球 (顶点分布均匀, 适合噪声形变; 焊接共享顶点, 法线平滑)
    ///   PerturbedBlob  fbm 噪声径向扰动球 (树冠团/灌木/岩石/动物躯干共用)
    ///   TaperedTrunk   多段锥形树干 (顶端细、可微弯, 圆柱 UV)
    ///   CombineParts   多部件按 submesh 组合并为单网格 (部件局部矩阵仅 TRS)
    /// 全部纯代码生成, 无外部美术资源。网格由调用方缓存复用 (变体化 + GPU 实例化)。
    /// </summary>
    public static class ProceduralMeshes
    {
        // ================= 确定性值噪声 =================

        /// <summary>晶格哈希 → [-1, 1)。整数坐标, 全确定性 (不依赖 Unity 随机状态)。</summary>
        private static float Hash3(int x, int y, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + z * 1013904223 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x800000 - 1f;
            }
        }

        private static float Smooth(float t) { return t * t * (3f - 2f * t); }

        /// <summary>三线性值噪声, 输出 [-1, 1]。</summary>
        public static float ValueNoise(float x, float y, float z, int seed)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y), zi = Mathf.FloorToInt(z);
            float xf = x - xi, yf = y - yi, zf = z - zi;
            float u = Smooth(xf), v = Smooth(yf), w = Smooth(zf);

            float c000 = Hash3(xi, yi, zi, seed), c100 = Hash3(xi + 1, yi, zi, seed);
            float c010 = Hash3(xi, yi + 1, zi, seed), c110 = Hash3(xi + 1, yi + 1, zi, seed);
            float c001 = Hash3(xi, yi, zi + 1, seed), c101 = Hash3(xi + 1, yi, zi + 1, seed);
            float c011 = Hash3(xi, yi + 1, zi + 1, seed), c111 = Hash3(xi + 1, yi + 1, zi + 1, seed);

            float x00 = Mathf.Lerp(c000, c100, u), x10 = Mathf.Lerp(c010, c110, u);
            float x01 = Mathf.Lerp(c001, c101, u), x11 = Mathf.Lerp(c011, c111, u);
            return Mathf.Lerp(Mathf.Lerp(x00, x10, v), Mathf.Lerp(x01, x11, v), w);
        }

        /// <summary>分形叠加 (3 倍频, 增益减半), 输出约 [-1, 1]。</summary>
        public static float Fbm(float x, float y, float z, int seed, int octaves = 3)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += ValueNoise(x, y, z, seed + o * 131) * amp;
                norm += amp;
                amp *= 0.5f;
                x *= 2.03f; y *= 2.03f; z *= 2.03f;
            }
            return sum / norm;
        }

        // ================= 二十面体细分球 =================

        /// <summary>二十面体细分球 (半径 1, 球面 UV)。subdiv=1 → 162 顶点/320 面; 2 → 642/1280。</summary>
        public static Mesh Icosphere(int subdivisions)
        {
            const float t = 1.618033988749895f;
            var verts = new List<Vector3>
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
            };
            var tris = new List<int>
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11,
                1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9,
                4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            };

            for (int s = 0; s < subdivisions; s++)
            {
                var midCache = new Dictionary<long, int>();
                var newTris = new List<int>(tris.Count * 4);
                for (int i = 0; i < tris.Count; i += 3)
                {
                    int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                    int ab = Midpoint(verts, a, b, midCache);
                    int bc = Midpoint(verts, b, c, midCache);
                    int ca = Midpoint(verts, c, a, midCache);
                    newTris.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
                }
                tris = newTris;
            }

            for (int i = 0; i < verts.Count; i++)
                verts[i] = verts[i].normalized;

            var mesh = new Mesh { name = "Icosphere" + subdivisions };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.uv = SphericalUV(verts);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static int Midpoint(List<Vector3> verts, int a, int b, Dictionary<long, int> cache)
        {
            long key = a < b ? ((long)a << 32) + b : ((long)b << 32) + a;
            if (cache.TryGetValue(key, out int idx)) return idx;
            verts.Add((verts[a] + verts[b]) * 0.5f);
            cache[key] = verts.Count - 1;
            return verts.Count - 1;
        }

        private static Vector2[] SphericalUV(List<Vector3> verts)
        {
            var uv = new Vector2[verts.Count];
            for (int i = 0; i < verts.Count; i++)
            {
                Vector3 d = verts[i].normalized;
                uv[i] = new Vector2(
                    Mathf.Atan2(d.z, d.x) / (2f * Mathf.PI) + 0.5f,
                    d.y * 0.5f + 0.5f);
            }
            return uv;
        }

        // ================= 噪声扰动球 (树冠/灌木/岩石/躯干) =================

        /// <summary>
        /// 扰动球: 在 subdiv 级二十面球上做 fbm 径向扰动 + 三轴缩放。
        /// amp=扰动幅度 (相对半径); ridged=true 用脊状噪声 (岩石棱角感)。
        /// </summary>
        public static Mesh PerturbedBlob(int subdivisions, float radius, float freq,
            float amp, Vector3 squash, int seed, bool ridged = false)
        {
            var mesh = Icosphere(subdivisions);
            var verts = mesh.vertices;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 d = verts[i].normalized;
                float n = Fbm(d.x * freq + seed * 0.371f, d.y * freq + seed * 0.113f,
                              d.z * freq + seed * 0.537f, seed);
                if (ridged) n = 1f - 2f * Mathf.Abs(n);   // 脊状: 折痕棱角
                Vector3 p = d * (radius * (1f + amp * n));
                verts[i] = Vector3.Scale(p, squash);
            }
            mesh.vertices = verts;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.name = "Blob" + seed;
            return mesh;
        }

        // ================= 锥形树干 =================

        /// <summary>
        /// 多段锥形树干: 底 rBottom 顶 rTop, 中心线按 bendAmount 向 bendDir 二次弯曲,
        /// 半径带少量噪声起伏。圆柱 UV (v 沿高度)。底面封口, 顶面开口 (树冠遮挡)。
        /// </summary>
        public static Mesh TaperedTrunk(float height, float rBottom, float rTop,
            int radialSegs, int heightSegs, float bendAmount, Vector2 bendDir, int seed)
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            for (int j = 0; j <= heightSegs; j++)
            {
                float t = j / (float)heightSegs;
                float y = t * height;
                float ringR = Mathf.Lerp(rBottom, rTop, t)
                    * (1f + 0.06f * Fbm(0f, t * 3f, 0f, seed, 2));
                Vector2 center = bendDir * (bendAmount * t * t);
                for (int i = 0; i <= radialSegs; i++)   // 末列重复以闭合 UV 接缝
                {
                    float ang = i / (float)radialSegs * 2f * Mathf.PI;
                    float cx = Mathf.Cos(ang), sz = Mathf.Sin(ang);
                    float rr = ringR * (1f + 0.08f * ValueNoise(cx * 2f, t * 4f, sz * 2f, seed));
                    verts.Add(new Vector3(center.x + cx * rr, y, center.y + sz * rr));
                    normals.Add(new Vector3(cx, 0f, sz));
                    uvs.Add(new Vector2(i / (float)radialSegs, t));
                }
            }
            int stride = radialSegs + 1;
            for (int j = 0; j < heightSegs; j++)
                for (int i = 0; i < radialSegs; i++)
                {
                    int a = j * stride + i, b = a + 1, c = a + stride, d = c + 1;
                    tris.AddRange(new[] { a, c, b, b, c, d });
                }
            // 底面扇形封口
            int capCenter = verts.Count;
            verts.Add(new Vector3(0f, 0f, 0f));
            normals.Add(Vector3.down);
            uvs.Add(new Vector2(0.5f, 0f));
            for (int i = 0; i < radialSegs; i++)
                tris.AddRange(new[] { capCenter, i + 1, i });

            var mesh = new Mesh { name = "Trunk" };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // ================= 部件合并 =================

        /// <summary>
        /// 棕榈叶: 沿弧线的双面窄条 (起点在原点, 沿 +Z 伸出, 先上扬后下垂), 横截面 V 形 (中脊低两缘高),
        /// 宽度中段最宽、叶尖收窄。riseDeg = 起始仰角, droopDeg = 全长累计下垂角。
        /// </summary>
        public static Mesh PalmFrond(float length, float width, float riseDeg, float droopDeg, int segments)
        {
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var frontTris = new List<int>();
            var backTris = new List<int>();

            Vector2 pos = Vector2.zero;   // (z 向前, y 向上)
            float ds = length / segments;
            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments;
                float ang = (riseDeg - droopDeg * t * t) * Mathf.Deg2Rad;
                if (i > 0) pos += new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * ds;
                float w = width * Mathf.Pow(Mathf.Sin(Mathf.PI * Mathf.Clamp01(0.08f + 0.92f * t)), 0.6f) * 0.5f;
                float fold = w * 0.35f;
                Vector3 up = new Vector3(0f, Mathf.Cos(ang), -Mathf.Sin(ang)).normalized;
                verts.Add(new Vector3(-w, pos.y + fold, pos.x)); norms.Add(up); uvs.Add(new Vector2(0f, t));
                verts.Add(new Vector3(0f, pos.y, pos.x));        norms.Add(up); uvs.Add(new Vector2(0.5f, t));
                verts.Add(new Vector3(w, pos.y + fold, pos.x));  norms.Add(up); uvs.Add(new Vector2(1f, t));
            }
            for (int i = 0; i < segments; i++)
            {
                int a = i * 3, b = (i + 1) * 3;
                for (int k = 0; k < 2; k++)
                {
                    frontTris.Add(a + k); frontTris.Add(b + k); frontTris.Add(a + k + 1);
                    frontTris.Add(a + k + 1); frontTris.Add(b + k); frontTris.Add(b + k + 1);
                }
            }
            // 背面: 复制顶点 (法线取反) + 反绕序 —— 不依赖材质的双面开关
            int n0 = verts.Count;
            for (int i = 0; i < n0; i++) { verts.Add(verts[i]); norms.Add(-norms[i]); uvs.Add(uvs[i]); }
            for (int i = 0; i < frontTris.Count; i += 3)
            {
                backTris.Add(frontTris[i] + n0);
                backTris.Add(frontTris[i + 2] + n0);
                backTris.Add(frontTris[i + 1] + n0);
            }
            frontTris.AddRange(backTris);

            var mesh = new Mesh { name = "PalmFrond" };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(frontTris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// 锥体 (针叶树的一层枝叶): 底面圆心在原点、顶点在 +Y height 处; 底缘半径逐点抖动 (jag 为相对幅度) 出参差的枝尖。
        /// 带底面封口 (从下方看不透明)。
        /// </summary>
        public static Mesh Cone(float radius, float height, int segments, float jag, int seed)
        {
            var rng = new System.Random(seed);
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            verts.Add(new Vector3(0f, height, 0f)); uvs.Add(new Vector2(0.5f, 1f));
            for (int i = 0; i < segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                float r = radius * (1f + ((float)rng.NextDouble() * 2f - 1f) * jag);
                verts.Add(new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r));
                uvs.Add(new Vector2(i / (float)segments, 0f));
            }
            int baseCenter = verts.Count;
            verts.Add(Vector3.zero); uvs.Add(new Vector2(0.5f, 0f));
            for (int i = 0; i < segments; i++)
            {
                int a = 1 + i, b = 1 + (i + 1) % segments;
                tris.Add(0); tris.Add(b); tris.Add(a);                 // 侧面 (朝外)
                tris.Add(baseCenter); tris.Add(a); tris.Add(b);        // 底面 (朝下)
            }
            var mesh = new Mesh { name = "Cone" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// 把多个 (网格, 局部矩阵, submesh 组号) 部件合并为单网格。
        /// 源网格可以是多 submesh: 源 submesh s 映射到目标组 submeshGroups[p] + s
        /// (单 submesh 源部件行为与旧版一致)。同组号的部件进同一 submesh (共享材质)。
        /// 法线用逆转置矩阵变换, 支持非均匀缩放。顶点数超限自动切 32 位索引。
        /// </summary>
        public static Mesh CombineParts(IList<Mesh> meshes, IList<Matrix4x4> locals,
            IList<int> submeshGroups, string name)
        {
            int groupCount = 0;
            for (int i = 0; i < meshes.Count; i++)
                groupCount = Mathf.Max(groupCount,
                    submeshGroups[i] + meshes[i].subMeshCount);

            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var trisByGroup = new List<int>[groupCount];
            for (int g = 0; g < groupCount; g++) trisByGroup[g] = new List<int>();

            for (int p = 0; p < meshes.Count; p++)
            {
                var src = meshes[p];
                var local = locals[p];
                var normalMat = local.inverse.transpose;
                int baseIdx = verts.Count;
                var sv = src.vertices;
                var sn = src.normals;
                var su = src.uv;
                for (int i = 0; i < sv.Length; i++)
                {
                    verts.Add(local.MultiplyPoint3x4(sv[i]));
                    normals.Add(sn.Length == sv.Length
                        ? normalMat.MultiplyVector(sn[i]).normalized
                        : Vector3.up);
                    uvs.Add(su.Length == sv.Length ? su[i] : Vector2.zero);
                }
                for (int s = 0; s < src.subMeshCount; s++)
                {
                    var st = src.GetTriangles(s);
                    var list = trisByGroup[submeshGroups[p] + s];
                    for (int i = 0; i < st.Length; i++) list.Add(baseIdx + st[i]);
                }
            }

            var mesh = new Mesh { name = name };
            if (verts.Count > 65000)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = groupCount;
            for (int g = 0; g < groupCount; g++)
                mesh.SetTriangles(trisByGroup[g], g);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
