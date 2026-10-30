using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 野生动物 (长颈鹿/狮子/野猪/大象), 写实向精细化版:
    /// 程序化噪声网格 (胶囊躯干/分段颈/弯曲象鼻/眼耳角鬃毛尾簇) + 程序化皮毛纹理
    /// (长颈鹿斑块/野猪鬃纹/大象皱纹)。每种动物的网格/材质进程内只建一次,
    /// 全部个体共享 (AnimalAssets)。
    ///
    /// 结构: 根 GameObject (Giraffe_N 等, 命名不变供测试断言) +
    ///   Body (合并网格, 多 submesh) + HeadPivot/TailPivot (WanderAnimal 摆动) +
    ///   4 腿髋枢轴 (TaperedTrunk 锥形腿 + 蹄, 枢轴契约不变)。
    /// 游荡 AI/贴地/净空区/种子与旧版一致; 纯视觉装饰 (无碰撞, 不影响飞行物理)。
    /// </summary>
    public class WildlifeEnvironment : MonoBehaviour
    {
        [Tooltip("Start 时自动等地面就绪并生成 (测试可关, 走手动 Build)")]
        public bool AutoBuildOnStart = true;
        [Tooltip("撒布半径 m")]
        public float AreaRadius = 1000f;
        public int Seed = 17;

        [Header("数量")]
        public int GiraffeCount = 6;
        public int LionCount = 5;
        public int BoarCount = 8;
        public int ElephantCount = 4;

        [Header("净空区 (矩形, 与 ScatterEnvironment 一致)")]
        public bool UseClearZone = true;
        public Vector2 ClearCenter = new Vector2(0f, 220f);
        public Vector2 ClearHalfSize = new Vector2(45f, 245f);

        private void Start()
        {
            if (!AutoBuildOnStart) return;
            StartCoroutine(BuildWhenGroundReady());
        }

        private IEnumerator BuildWhenGroundReady()
        {
            float t = 0f;
            var probe = new Vector3(ClearCenter.x, 600f, ClearCenter.y);
            while (t < 2f)
            {
                if (Physics.Raycast(probe, Vector3.down, out _, 1200f))
                {
                    yield return new WaitForFixedUpdate();
                    Build();
                    yield break;
                }
                yield return null;
                t += Time.deltaTime;
            }
            Debug.LogWarning("[Wildlife] 2s 内未检测到地面碰撞体, 跳过生成");
        }

        /// <summary>生成全部动物 (幂等)。</summary>
        public void Build()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
                Destroy(transform.GetChild(i).gameObject);
            AnimalAssets.EnsureBuilt();

            var rng = new System.Random(Seed);
            int placed = 0;
            placed += Spawn(rng, GiraffeCount, "Giraffe", BuildGiraffe);
            placed += Spawn(rng, LionCount, "Lion", BuildLion);
            placed += Spawn(rng, BoarCount, "Boar", BuildBoar);
            placed += Spawn(rng, ElephantCount, "Elephant", BuildElephant);
            Debug.Log("[Wildlife] 已放养 " + placed + " 只动物 (种子 " + Seed + ")");
        }

        private int Spawn(System.Random rng, int count, string name,
                          System.Func<Transform, Transform[]> build)
        {
            int placed = 0, attempts = 0;
            while (placed < count && attempts++ < count * 40)
            {
                Vector3 p = SampleGround(rng);
                if (float.IsPositiveInfinity(p.x)) continue;

                var root = new GameObject(name + "_" + placed);
                root.transform.SetParent(transform, false);
                root.transform.position = p;
                var legs = build(root.transform);   // 返回四腿髋枢轴 (摆动动画)
                root.AddComponent<WanderAnimal>().Init(rng, p, legs,
                    root.transform.Find("HeadPivot"), root.transform.Find("TailPivot"));
                placed++;
            }
            return placed;
        }

        private Vector3 SampleGround(System.Random rng)
        {
            double ang = rng.NextDouble() * 2.0 * System.Math.PI;
            double r = System.Math.Sqrt(rng.NextDouble()) * (AreaRadius * 0.85);  // 留边
            float x = (float)(System.Math.Cos(ang) * r);
            float z = (float)(System.Math.Sin(ang) * r);
            if (UseClearZone &&
                Mathf.Abs(x - ClearCenter.x) < ClearHalfSize.x &&
                Mathf.Abs(z - ClearCenter.y) < ClearHalfSize.y)
                return Vector3.positiveInfinity;
            if (!Physics.Raycast(new Vector3(x, 600f, z), Vector3.down, out var hit, 1200f))
                return Vector3.positiveInfinity;
            return hit.point;
        }

        // ================= 四种动物装配 =================

        private Transform[] BuildGiraffe(Transform root)
        {
            var A = AnimalAssets.Get(AnimalAssets.Species.Giraffe);
            BodyPart(root, "Body", A.Body, A.BodyMats);
            PivotPart(root, "HeadPivot", A.HeadPivotPos, A.Head, A.HeadMats);
            PivotPart(root, "TailPivot", A.TailPivotPos, A.Tail, A.TailMats);
            return Legs(root, A.HipY, A.HipX, A.HipZ, A.Leg, A.LegMats);
        }

        private Transform[] BuildLion(Transform root)
        {
            var A = AnimalAssets.Get(AnimalAssets.Species.Lion);
            BodyPart(root, "Body", A.Body, A.BodyMats);
            PivotPart(root, "HeadPivot", A.HeadPivotPos, A.Head, A.HeadMats);
            PivotPart(root, "TailPivot", A.TailPivotPos, A.Tail, A.TailMats);
            return Legs(root, A.HipY, A.HipX, A.HipZ, A.Leg, A.LegMats);
        }

        private Transform[] BuildBoar(Transform root)
        {
            var A = AnimalAssets.Get(AnimalAssets.Species.Boar);
            BodyPart(root, "Body", A.Body, A.BodyMats);
            PivotPart(root, "HeadPivot", A.HeadPivotPos, A.Head, A.HeadMats);
            PivotPart(root, "TailPivot", A.TailPivotPos, A.Tail, A.TailMats);
            return Legs(root, A.HipY, A.HipX, A.HipZ, A.Leg, A.LegMats);
        }

        private Transform[] BuildElephant(Transform root)
        {
            var A = AnimalAssets.Get(AnimalAssets.Species.Elephant);
            BodyPart(root, "Body", A.Body, A.BodyMats);
            PivotPart(root, "HeadPivot", A.HeadPivotPos, A.Head, A.HeadMats);
            PivotPart(root, "TailPivot", A.TailPivotPos, A.Tail, A.TailMats);
            return Legs(root, A.HipY, A.HipX, A.HipZ, A.Leg, A.LegMats);
        }

        // ================= 装配工具 =================

        private static void BodyPart(Transform parent, string name, Mesh mesh, Material[] mats)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = mats;
        }

        /// <summary>摆动画枢轴 (头/尾): 枢轴 GO + 网格子对象, 返回枢轴。</summary>
        private static Transform PivotPart(Transform root, string name, Vector3 pivotPos,
            Mesh mesh, Material[] mats)
        {
            var pivot = new GameObject(name);
            pivot.transform.SetParent(root, false);
            pivot.transform.localPosition = pivotPos;
            BodyPart(pivot.transform, name.Replace("Pivot", ""), mesh, mats);
            return pivot.transform;
        }

        /// <summary>四条腿: 髋枢轴 + 共享锥形腿网格 (枢轴旋转即摆动)。返回枢轴数组。</summary>
        private Transform[] Legs(Transform root, float hipY, float x, float z,
            Mesh legMesh, Material[] mats)
        {
            var legs = new Transform[4];
            int i = 0;
            foreach (var (lx, lz) in new[] { (-x, z), (x, z), (-x, -z), (x, -z) })
            {
                var pivot = new GameObject("Leg" + i);
                pivot.transform.SetParent(root, false);
                pivot.transform.localPosition = new Vector3(lx, hipY, lz);
                var limb = new GameObject("limb");
                limb.transform.SetParent(pivot.transform, false);
                limb.AddComponent<MeshFilter>().sharedMesh = legMesh;
                limb.AddComponent<MeshRenderer>().sharedMaterials = mats;
                legs[i++] = pivot.transform;
            }
            return legs;
        }

        /// <summary>
        /// 四种动物的共享网格/材质 (进程内建一次)。网格全部原点对齐各自装配位:
        /// Body 原点在脚底中心; Head 原点在颈顶枢轴; Tail 原点在尾根枢轴; Leg 原点在髋 (向下伸展)。
        /// submesh 约定按物种数组顺序 (Body/Head/Tail/LegMats 与网格 submesh 一一对应)。
        /// </summary>
        public static class AnimalAssets
        {
            public enum Species { Giraffe = 0, Lion = 1, Boar = 2, Elephant = 3 }

            public sealed class Set
            {
                public Mesh Body, Head, Tail, Leg;
                public Material[] BodyMats, HeadMats, TailMats, LegMats;
                public Vector3 HeadPivotPos, TailPivotPos;
                public float HipY, HipX, HipZ;
            }

            private static Set[] _sets;
            public static Material IvoryMat, DarkMat, ManeMat;

            public static Set Get(Species s) { EnsureBuilt(); return _sets[(int)s]; }

            public static void EnsureBuilt()
            {
                if (_sets != null) return;

                // ---- 纹理 (灰度 × 材质色调) ----
                var giraffeTex = ProceduralTextures.Spots(256, 6, 0.16f, 61, 0.30f, 0.95f);
                var lionTex = ProceduralTextures.NoiseGray(128, 7f, 0.16f, 67);
                var boarTex = ProceduralTextures.Streaks(128, 2.5f, 9f, 0.22f, 71);
                var elephantTex = ProceduralTextures.Streaks(128, 3f, 11f, 0.18f, 79);

                // ---- 材质 (亮色阶, 背光面不死黑) ----
                var giraffeMat = Std("GiraffeFur", new Color(0.95f, 0.78f, 0.52f), giraffeTex, 0.12f);
                var lionMat = Std("LionFur", new Color(0.88f, 0.72f, 0.42f), lionTex, 0.10f);
                ManeMat = Std("Mane", new Color(0.50f, 0.35f, 0.18f), lionTex, 0.05f);
                var boarMat = Std("BoarFur", new Color(0.55f, 0.46f, 0.38f), boarTex, 0.08f);
                var elephantMat = Std("ElephantSkin", new Color(0.70f, 0.69f, 0.66f), elephantTex, 0.14f);
                IvoryMat = Std("Ivory", new Color(0.93f, 0.89f, 0.76f), null, 0.45f);
                DarkMat = Std("DarkAccent", new Color(0.16f, 0.14f, 0.12f), null, 0.10f);

                _sets = new[]
                {
                    BuildGiraffeSet(giraffeMat),
                    BuildLionSet(lionMat),
                    BuildBoarSet(boarMat),
                    BuildElephantSet(elephantMat),
                };
            }

            private static Material Std(string name, Color c, Texture2D tex, float gloss)
            {
                // R3: 带贴图的材质自动配同源法线
                var normal = tex != null ? ProceduralTextures.NormalFromHeight(tex, 0.7f) : null;
                var m = RenderMaterials.Create(c, gloss, 0f, tex, normal);
                m.name = name;
                return m;
            }

            // ---------- 部件简写 ----------

            private static Mesh Blob(float r, float sx, float sy, float sz, int seed,
                int subdiv = 1, float amp = 0.05f)
            {
                return ProceduralMeshes.PerturbedBlob(subdiv, r, 2.0f, amp,
                    new Vector3(sx, sy, sz), seed);
            }

            /// <summary>短锥段 (角/牙/吻/尾): 沿 +y 长 len。</summary>
            private static Mesh Seg(float len, float rBase, float rTip, int seed)
            {
                return ProceduralMeshes.TaperedTrunk(len, rBase, rTip, 7, 1,
                    0f, Vector2.zero, seed);
            }

            private static Matrix4x4 At(float x, float y, float z)
            {
                return Matrix4x4.TRS(new Vector3(x, y, z), Quaternion.identity, Vector3.one);
            }

            private static Matrix4x4 At(float x, float y, float z, Quaternion rot)
            {
                return Matrix4x4.TRS(new Vector3(x, y, z), rot, Vector3.one);
            }

            private sealed class Parts
            {
                public readonly List<Mesh> Meshes = new List<Mesh>();
                public readonly List<Matrix4x4> Locals = new List<Matrix4x4>();
                public readonly List<int> Groups = new List<int>();
                public void Add(Mesh m, Matrix4x4 l, int g) { Meshes.Add(m); Locals.Add(l); Groups.Add(g); }
                public Mesh Build(string name)
                {
                    return ProceduralMeshes.CombineParts(Meshes, Locals, Groups, name);
                }
            }

            /// <summary>锥形腿 (髋枢轴向下伸展) + 深色蹄。leg 组0 / hoof 组1。</summary>
            private static Mesh BuildLeg(float h, float r, int seed)
            {
                var p = new Parts();
                p.Add(Seg(h, r * 0.72f, r, seed), At(0f, -h, 0f), 0);
                p.Add(Seg(0.09f, r * 0.72f, r * 0.68f, seed + 1), At(0f, -h, 0f), 1);
                return p.Build("Leg" + seed);
            }

            // ---------- 长颈鹿 ----------

            private static Set BuildGiraffeSet(Material fur)
            {
                const int S = 110;
                // 体: 躯干胶囊 + 三段颈 (前倾逐级减小) + 颈背鬃毛球
                var body = new Parts();
                body.Add(Blob(1.0f, 0.55f, 0.62f, 0.95f, S, 2, 0.06f), At(0f, 1.9f, 0f), 0);

                Vector3 neckPos = new Vector3(0f, 2.30f, 0.60f);
                float[] segLen = { 0.95f, 0.85f, 0.55f };
                float[] tilt = { 24f, 12f, 5f };
                float[] r0 = { 0.21f, 0.18f, 0.15f };
                float[] r1 = { 0.18f, 0.15f, 0.13f };
                for (int i = 0; i < 3; i++)
                {
                    var rot = Quaternion.Euler(tilt[i], 0f, 0f);
                    body.Add(Seg(segLen[i], r0[i], r1[i], S + 10 + i), At(neckPos.x, neckPos.y, neckPos.z, rot), 0);
                    // 颈背短鬃 (深棕)
                    Vector3 mid = neckPos + rot * (Vector3.up * segLen[i] * 0.55f)
                        + rot * (Vector3.back * (r0[i] * 0.9f));
                    body.Add(Blob(0.09f, 0.45f, 1.6f, 0.7f, S + 40 + i),
                        At(mid.x, mid.y, mid.z, rot), 1);
                    neckPos += rot * (Vector3.up * segLen[i]);
                }
                var bodyMesh = body.Build("GiraffeBody");

                // 头 (枢轴 = 颈顶): 颅 + 吻锥 + 耳 + 眼 + 角 (比例放大)
                var head = new Parts();
                head.Add(Blob(0.24f, 0.75f, 0.85f, 1.15f, S + 60), At(0f, 0.12f, 0.07f), 0);
                head.Add(Seg(0.30f, 0.15f, 0.09f, S + 61),
                    At(0f, 0.04f, 0.19f, Quaternion.Euler(78f, 0f, 0f)), 0);   // 吻部前倾
                head.Add(Blob(0.085f, 1f, 0.5f, 0.9f, S + 62), At(-0.19f, 0.19f, -0.02f), 0);  // 耳L
                head.Add(Blob(0.085f, 1f, 0.5f, 0.9f, S + 63), At(0.19f, 0.19f, -0.02f), 0);   // 耳R
                head.Add(Seg(0.15f, 0.034f, 0.024f, S + 64), At(-0.08f, 0.28f, 0.02f), 0);     // 角L
                head.Add(Seg(0.15f, 0.034f, 0.024f, S + 65), At(0.08f, 0.28f, 0.02f), 0);      // 角R
                head.Add(Blob(0.042f, 1f, 1f, 1f, S + 66), At(-0.08f, 0.37f, 0.02f), 1);       // 角绒球L
                head.Add(Blob(0.042f, 1f, 1f, 1f, S + 67), At(0.08f, 0.37f, 0.02f), 1);        // 角绒球R
                head.Add(Blob(0.038f, 1f, 1f, 1f, S + 68), At(-0.13f, 0.14f, 0.19f), 1);       // 眼L
                head.Add(Blob(0.038f, 1f, 1f, 1f, S + 69), At(0.13f, 0.14f, 0.19f), 1);        // 眼R
                var headMesh = head.Build("GiraffeHead");

                // 尾 (枢轴 = 尾根): 下垂细尾 + 末端簇
                var tail = new Parts();
                tail.Add(Seg(0.85f, 0.028f, 0.014f, S + 80),
                    At(0f, -0.05f, -0.02f, Quaternion.Euler(168f, 0f, 0f)), 0);
                Vector3 tuftPos = new Vector3(0f, -0.05f, -0.02f)
                    + Quaternion.Euler(168f, 0f, 0f) * (Vector3.up * 0.85f);
                tail.Add(Blob(0.06f, 0.7f, 1.4f, 0.7f, S + 81), At(tuftPos.x, tuftPos.y, tuftPos.z), 1);
                var tailMesh = tail.Build("GiraffeTail");

                return new Set
                {
                    Body = bodyMesh, BodyMats = new[] { fur, ManeMat },
                    Head = headMesh, HeadMats = new[] { fur, DarkMat },
                    Tail = tailMesh, TailMats = new[] { fur, DarkMat },
                    Leg = BuildLeg(1.75f, 0.115f, S + 90), LegMats = new[] { fur, DarkMat },
                    HeadPivotPos = neckPos, TailPivotPos = new Vector3(0f, 2.05f, -0.92f),
                    HipY = 1.75f, HipX = 0.34f, HipZ = 0.62f,
                };
            }

            // ---------- 狮子 ----------

            private static Set BuildLionSet(Material fur)
            {
                const int S = 210;
                var body = new Parts();
                body.Add(Blob(0.75f, 0.45f, 0.50f, 1.0f, S, 2, 0.05f), At(0f, 0.82f, -0.05f), 0);
                // 鬃毛: 两层扰动球裹住头颈
                body.Add(Blob(0.50f, 0.85f, 0.90f, 0.75f, S + 1, 1, 0.10f), At(0f, 0.95f, 0.48f), 1);
                body.Add(Blob(0.40f, 0.95f, 0.95f, 0.60f, S + 2, 1, 0.12f), At(0f, 0.98f, 0.62f), 1);
                var bodyMesh = body.Build("LionBody");

                var head = new Parts();
                head.Add(Blob(0.26f, 0.80f, 0.85f, 0.95f, S + 60), At(0f, 0.02f, 0.05f), 0);
                head.Add(Blob(0.13f, 0.85f, 0.65f, 1.0f, S + 61), At(0f, -0.05f, 0.26f), 0);   // 吻
                head.Add(Blob(0.045f, 1f, 0.8f, 0.9f, S + 62), At(0f, -0.02f, 0.38f), 1);      // 鼻
                head.Add(Blob(0.065f, 1f, 1f, 0.5f, S + 63), At(-0.15f, 0.20f, 0.0f), 0);      // 耳L
                head.Add(Blob(0.065f, 1f, 1f, 0.5f, S + 64), At(0.15f, 0.20f, 0.0f), 0);       // 耳R
                head.Add(Blob(0.030f, 1f, 1f, 1f, S + 65), At(-0.10f, 0.08f, 0.22f), 1);       // 眼L
                head.Add(Blob(0.030f, 1f, 1f, 1f, S + 66), At(0.10f, 0.08f, 0.22f), 1);        // 眼R
                var headMesh = head.Build("LionHead");

                var tail = new Parts();
                tail.Add(Seg(0.62f, 0.026f, 0.012f, S + 80),
                    At(0f, 0f, 0f, Quaternion.Euler(150f, 0f, 0f)), 0);
                Vector3 tuftPos = Quaternion.Euler(150f, 0f, 0f) * (Vector3.up * 0.62f);
                tail.Add(Blob(0.055f, 0.7f, 1.5f, 0.7f, S + 81), At(tuftPos.x, tuftPos.y, tuftPos.z), 1);
                var tailMesh = tail.Build("LionTail");

                return new Set
                {
                    Body = bodyMesh, BodyMats = new[] { fur, ManeMat },
                    Head = headMesh, HeadMats = new[] { fur, DarkMat },
                    Tail = tailMesh, TailMats = new[] { fur, DarkMat },
                    Leg = BuildLeg(0.52f, 0.085f, S + 90), LegMats = new[] { fur, DarkMat },
                    HeadPivotPos = new Vector3(0f, 1.02f, 0.78f),
                    TailPivotPos = new Vector3(0f, 0.95f, -0.80f),
                    HipY = 0.52f, HipX = 0.26f, HipZ = 0.48f,
                };
            }

            // ---------- 野猪 ----------

            private static Set BuildBoarSet(Material fur)
            {
                const int S = 310;
                var body = new Parts();
                body.Add(Blob(0.62f, 0.50f, 0.52f, 1.0f, S, 2, 0.05f), At(0f, 0.62f, 0f), 0);
                // 脊背鬃刺 (一排扁小球, 深色)
                for (int i = 0; i < 4; i++)
                    body.Add(Blob(0.10f, 0.35f, 0.9f, 1.4f, S + 1 + i),
                        At(0f, 0.62f + 0.30f + 0.02f * i, 0.30f - i * 0.20f), 1);
                var bodyMesh = body.Build("BoarBody");

                var head = new Parts();
                head.Add(Blob(0.28f, 0.72f, 0.78f, 1.1f, S + 60), At(0f, 0f, 0.05f), 0);
                head.Add(Seg(0.22f, 0.13f, 0.10f, S + 61),
                    At(0f, -0.04f, 0.24f, Quaternion.Euler(82f, 0f, 0f)), 0);                    // 吻筒
                head.Add(Blob(0.085f, 1f, 0.85f, 0.6f, S + 62), At(0f, -0.09f, 0.45f), 1);     // 鼻盘
                head.Add(Seg(0.12f, 0.020f, 0.007f, S + 63),
                    At(-0.10f, -0.12f, 0.30f, Quaternion.Euler(52f, -12f, 0f)), 2);            // 獠牙L (前上卷)
                head.Add(Seg(0.12f, 0.020f, 0.007f, S + 64),
                    At(0.10f, -0.12f, 0.30f, Quaternion.Euler(52f, 12f, 0f)), 2);              // 獠牙R
                head.Add(Blob(0.06f, 0.5f, 1f, 0.9f, S + 65), At(-0.14f, 0.22f, -0.05f), 0);   // 耳L
                head.Add(Blob(0.06f, 0.5f, 1f, 0.9f, S + 66), At(0.14f, 0.22f, -0.05f), 0);    // 耳R
                head.Add(Blob(0.026f, 1f, 1f, 1f, S + 67), At(-0.10f, 0.06f, 0.24f), 1);       // 眼L
                head.Add(Blob(0.026f, 1f, 1f, 1f, S + 68), At(0.10f, 0.06f, 0.24f), 1);        // 眼R
                var headMesh = head.Build("BoarHead");

                var tail = new Parts();
                tail.Add(Seg(0.28f, 0.018f, 0.008f, S + 80),
                    At(0f, 0f, 0f, Quaternion.Euler(155f, 0f, 0f)), 0);
                var tailMesh = tail.Build("BoarTail");

                return new Set
                {
                    Body = bodyMesh, BodyMats = new[] { fur, DarkMat },
                    Head = headMesh, HeadMats = new[] { fur, DarkMat, IvoryMat },
                    Tail = tailMesh, TailMats = new[] { fur },
                    Leg = BuildLeg(0.38f, 0.062f, S + 90), LegMats = new[] { fur, DarkMat },
                    HeadPivotPos = new Vector3(0f, 0.66f, 0.52f),
                    TailPivotPos = new Vector3(0f, 0.72f, -0.58f),
                    HipY = 0.38f, HipX = 0.17f, HipZ = 0.36f,
                };
            }

            // ---------- 大象 ----------

            private static Set BuildElephantSet(Material skin)
            {
                const int S = 410;
                var body = new Parts();
                body.Add(Blob(1.5f, 0.72f, 0.75f, 1.0f, S, 2, 0.05f), At(0f, 2.2f, -0.2f), 0);
                body.Add(Blob(0.7f, 0.9f, 0.8f, 0.9f, S + 1, 1, 0.05f), At(0f, 2.0f, -1.3f), 0); // 臀部
                var bodyMesh = body.Build("ElephantBody");

                // 头 (枢轴处): 颅 + 大耳 + 四段弯曲象鼻 + 象牙 + 眼
                var head = new Parts();
                head.Add(Blob(0.62f, 0.85f, 0.95f, 0.90f, S + 60, 2, 0.05f), At(0f, 0f, 0.10f), 0);
                head.Add(Blob(0.50f, 0.26f, 1.0f, 0.78f, S + 61, 1, 0.08f),
                    At(-0.62f, 0.10f, -0.05f, Quaternion.Euler(0f, -12f, 0f)), 0);   // 耳L
                head.Add(Blob(0.50f, 0.26f, 1.0f, 0.78f, S + 62, 1, 0.08f),
                    At(0.62f, 0.10f, -0.05f, Quaternion.Euler(0f, 12f, 0f)), 0);     // 耳R
                // 象鼻: 从颅底前方向下弯曲 (前倾角逐级加大)
                Vector3 trunkPos = new Vector3(0f, -0.28f, 0.52f);
                float[] trunkTilt = { 100f, 122f, 148f, 168f };
                float[] trunkLen = { 0.48f, 0.44f, 0.40f, 0.36f };
                float[] trunkR0 = { 0.17f, 0.135f, 0.105f, 0.080f };
                float[] trunkR1 = { 0.135f, 0.105f, 0.080f, 0.055f };
                for (int i = 0; i < 4; i++)
                {
                    var rot = Quaternion.Euler(trunkTilt[i], 0f, 0f);
                    head.Add(Seg(trunkLen[i], trunkR0[i], trunkR1[i], S + 70 + i),
                        At(trunkPos.x, trunkPos.y, trunkPos.z, rot), 0);
                    trunkPos += rot * (Vector3.up * trunkLen[i]);
                }
                head.Add(Seg(0.52f, 0.050f, 0.012f, S + 80),
                    At(-0.24f, -0.38f, 0.42f, Quaternion.Euler(106f, -6f, 0f)), 1);   // 牙L (前下伸出)
                head.Add(Seg(0.52f, 0.050f, 0.012f, S + 81),
                    At(0.24f, -0.38f, 0.42f, Quaternion.Euler(106f, 6f, 0f)), 1);     // 牙R
                head.Add(Blob(0.042f, 1f, 1f, 1f, S + 82), At(-0.30f, 0.10f, 0.52f), 2);  // 眼L
                head.Add(Blob(0.042f, 1f, 1f, 1f, S + 83), At(0.30f, 0.10f, 0.52f), 2);   // 眼R
                var headMesh = head.Build("ElephantHead");

                var tail = new Parts();
                tail.Add(Seg(0.95f, 0.045f, 0.018f, S + 90),
                    At(0f, 0f, 0f, Quaternion.Euler(172f, 0f, 0f)), 0);
                Vector3 tuftPos = Quaternion.Euler(172f, 0f, 0f) * (Vector3.up * 0.95f);
                tail.Add(Blob(0.07f, 0.7f, 1.5f, 0.7f, S + 91), At(tuftPos.x, tuftPos.y, tuftPos.z), 1);
                var tailMesh = tail.Build("ElephantTail");

                return new Set
                {
                    Body = bodyMesh, BodyMats = new[] { skin },
                    Head = headMesh, HeadMats = new[] { skin, IvoryMat, DarkMat },
                    Tail = tailMesh, TailMats = new[] { skin, DarkMat },
                    Leg = BuildLeg(1.15f, 0.20f, S + 95), LegMats = new[] { skin, DarkMat },
                    HeadPivotPos = new Vector3(0f, 2.75f, 1.15f),
                    TailPivotPos = new Vector3(0f, 2.45f, -1.65f),
                    HipY = 1.15f, HipX = 0.55f, HipZ = 0.78f,
                };
            }
        }
    }

    /// <summary>
    /// 单只动物游荡 AI: 家半径内随机游走/驻足, 四腿摆动 + 头部环伺 (驻足时) +
    /// 尾巴持续摆动, 射线贴地。行为逻辑与旧版一致; 头/尾摆动为纯附加视觉。
    /// </summary>
    public class WanderAnimal : MonoBehaviour
    {
        private Vector2 _home;
        private float _heading, _targetHeading, _speed, _timer;
        private bool _moving;
        private float _wanderRadius, _turnRate, _groundY, _groundTimer;
        private Transform[] _legs;
        private Transform _head, _tail;
        private System.Random _rng;
        private float _phase;

        public void Init(System.Random rng, Vector3 spawn, Transform[] legs,
            Transform head = null, Transform tail = null)
        {
            _rng = rng;
            _home = new Vector2(spawn.x, spawn.z);
            _legs = legs;
            _head = head;
            _tail = tail;
            _heading = (float)(rng.NextDouble() * 360.0);
            _targetHeading = _heading;
            _wanderRadius = 40f + (float)rng.NextDouble() * 50f;
            _speed = 0.8f + (float)rng.NextDouble() * 1.2f;
            _turnRate = 30f + (float)rng.NextDouble() * 30f;   // deg/s
            _phase = (float)rng.NextDouble() * 10f;
            _groundY = spawn.y;
            PickNextState();
        }

        private void PickNextState()
        {
            // 60% 游走 (换向), 40% 驻足觅食
            _moving = _rng.Next(100) < 60;
            if (_moving)
            {
                float turn = (float)(_rng.NextDouble() * 160.0 - 80.0);
                _targetHeading = _heading + turn;
            }
            _timer = _moving ? 3f + (float)_rng.NextDouble() * 6f
                             : 2f + (float)_rng.NextDouble() * 4f;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            _timer -= dt;
            if (_timer <= 0f) PickNextState();

            // 平滑转向
            float dh = Mathf.DeltaAngle(_heading, _targetHeading);
            _heading += Mathf.Clamp(dh, -_turnRate * dt, _turnRate * dt);
            transform.rotation = Quaternion.Euler(0f, _heading, 0f);

            if (_moving)
            {
                transform.position += transform.forward * (_speed * dt);

                // 家半径约束: 越界即调头
                Vector2 off = new Vector2(transform.position.x - _home.x,
                                          transform.position.z - _home.y);
                if (off.magnitude > _wanderRadius)
                    _targetHeading = Mathf.Atan2(-off.y, -off.x) * Mathf.Rad2Deg;
            }

            // 贴地 (定期射线, 平滑贴合缓坡)
            _groundTimer -= dt;
            if (_groundTimer <= 0f)
            {
                _groundTimer = 0.3f;
                if (Physics.Raycast(transform.position + Vector3.up * 5f, Vector3.down,
                                    out var hit, 60f))
                    _groundY = hit.point.y;
            }
            Vector3 pos = transform.position;
            pos.y = Mathf.Lerp(pos.y, _groundY, Mathf.Clamp01(10f * dt));
            transform.position = pos;

            // 腿摆动 (行走摆幅大, 驻足微晃)
            float amp = _moving ? 22f : 2f;
            for (int i = 0; i < _legs.Length; i++)
                _legs[i].localRotation = Quaternion.Euler(
                    amp * Mathf.Sin(Time.time * 7f + _phase + i * Mathf.PI * 0.5f), 0f, 0f);

            // 头部: 行走时朝前, 驻足时缓慢环伺 (觅食感)
            if (_head != null)
            {
                float headYaw = _moving ? 0f : Mathf.Sin(Time.time * 0.6f + _phase) * 22f;
                float headPitch = _moving ? 0f : Mathf.Sin(Time.time * 0.43f + _phase * 1.7f) * 6f - 4f;
                _head.localRotation = Quaternion.Slerp(_head.localRotation,
                    Quaternion.Euler(headPitch, headYaw, 0f), Mathf.Clamp01(2.5f * dt));
            }

            // 尾巴持续摆动 (驱虫)
            if (_tail != null)
                _tail.localRotation = Quaternion.Euler(
                    Mathf.Sin(Time.time * 2.3f + _phase) * 9f, 0f,
                    Mathf.Sin(Time.time * 1.9f + _phase * 1.3f) * 12f);
        }
    }
}
