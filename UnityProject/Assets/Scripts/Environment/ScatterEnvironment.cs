using System.Collections.Generic;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 地面特征撒布 (树木/岩石/灌木丛), 写实向精细化版:
    /// 以场景原点为圆心, 在飞行区 (半径可配, 与 FlightGuard.BoundaryRadius 对应) 内
    /// 确定性 (固定种子) 撒布。用向下射线贴地 —— 平坦地面 (Plane 碰撞体) 与山地
    /// (TerrainCollider) 通用; 射线未命中 (山地范围外/净空区) 的采样点跳过。
    /// 可配置矩形净空区 (跑道/停机坪不撒)。
    ///
    /// 渲染: 预生成 K 个程序化网格变体 (ScatterAssets), 撒布时把同 (类型×材质×
    /// 方位象限) 的全部实例直接合并进一个分块大网格 (CombineParts, 保留源 submesh
    /// 分层) —— 普通 MeshRenderer 渲染, 无 GPU 实例化依赖 (批处理/编辑器/真机行为
    /// 一致), 分块包围盒自动视锥剔除。每个特征保留同名空 GameObject 标记
    /// (Tree_N/Rock_N/Bush_N, 仅 Transform), 供 PlayMode 测试断言数量/半径/净空区/贴地。
    /// </summary>
    public class ScatterEnvironment : MonoBehaviour
    {
        [Tooltip("Start 时自动等地面就绪并撒布 (测试中可关掉走手动 Build)")]
        public bool AutoBuildOnStart = true;

        [Header("撒布区域")]
        [Tooltip("撒布半径 m (应 ≈ FlightGuard.BoundaryRadius)")]
        public float AreaRadius = 1100f;
        [Tooltip("随机种子 (固定值保证场景可重复)")]
        public int Seed = 13;

        [Header("数量")]
        public int TreeCount = 420;
        public int RockCount = 320;
        public int BushCount = 620;
        [Tooltip("棕榈数量 (海岛); 只种在海面以上的沙滩/低地带, 需要 TerrainRef")]
        public int PalmCount = 0;
        [Tooltip("针叶树数量 (雪谷); 只种在相对台地 [ConiferMinRelM, ConiferMaxRelM] 的缓坡上, 越接近树线越稀")]
        public int ConiferCount = 0;
        [Tooltip("针叶林下限/树线: 相对场景 y=0 (机场台地) 的高度 m")]
        public float ConiferMinRelM = -10f;
        public float ConiferMaxRelM = 210f;
        [Tooltip("针叶树只种在坡度小于此值 (度) 的地面")]
        public float ConiferMaxSlopeDeg = 30f;

        [Header("净空区 (矩形, 场景局部坐标; 跑道/停机坪)")]
        public bool UseClearZone = true;
        public Vector2 ClearCenter = new Vector2(0f, 220f);   // 跑道中点 (x, z)
        public Vector2 ClearHalfSize = new Vector2(45f, 260f);

        [Header("水域过滤 (海岛; 默认关闭)")]
        [Tooltip("提供海面高度的地形; 为空或地形无海则不过滤")]
        public TerrainFromHeightmap TerrainRef;
        [Tooltip("只在高于海面此高度 m 的陆地撒布 (沙滩上缘以上)")]
        public float MinAboveSeaM = 2.5f;

        [Tooltip("棕榈主带上沿: 海面以上多少米 (沙滩到草地过渡)")]
        public float PalmBandTopM = 9f;
        [Tooltip("主带以上的内陆点保留棕榈的概率")]
        [Range(0f, 1f)] public float PalmInlandChance = 0.1f;

        [Header("性能")]
        [Tooltip("单类型最大放置尝试次数 (×3 = 总上限)")]
        private const int MaxAttemptsPerType = 3000;

        /// <summary>一个分块的实例累积: (变体序号, 世界矩阵) 列表。</summary>
        private sealed class ChunkAcc
        {
            public readonly List<int> Variants = new List<int>();
            public readonly List<Matrix4x4> Mats = new List<Matrix4x4>();
        }

        private readonly Dictionary<int, ChunkAcc> _pending = new Dictionary<int, ChunkAcc>();
        private readonly List<Mesh> _chunkMeshes = new List<Mesh>();   // Clear 时销毁防泄漏
        private GameObject _renderRoot;   // 分块网格容器 (根级独立 GO, 不做子级:
                                          // PlayMode 测试按子级枚举特征标记)

        private void Start()
        {
            if (!AutoBuildOnStart) return;
            // 地面本身可能是运行时构建的 (RunwayEnvironment.Start 建 Plane,
            // 山地 TerrainCollider 随场景), 且新建碰撞体需物理步同步 ——
            // 轮询等地面就绪后再撒布, 否则射线全部落空
            StartCoroutine(BuildWhenGroundReady());
        }

        private System.Collections.IEnumerator BuildWhenGroundReady()
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
            Debug.LogWarning("[Scatter] 2s 内未检测到地面碰撞体, 跳过撒布");
        }

        /// <summary>生成全部特征 (幂等: 重复调用先清空)。</summary>
        public void Build()
        {
            Clear();
            ScatterAssets.EnsureBuilt();

            var rng = new System.Random(Seed);
            int placed = 0;
            placed += ScatterTrees(rng);
            placed += ScatterRocks(rng);
            placed += ScatterBushes(rng);
            placed += ScatterPalms(rng);
            placed += ScatterConifers(rng);
            int chunks = BakeChunks();
            Debug.Log("[Scatter] 已撒布 " + placed + " 个地面特征 (种子 " + Seed +
                      ", " + chunks + " 个分块网格)");
        }

        /// <summary>分块网格容器是根级独立 GO (不随本对象销毁), 场景切换销毁环境时必须一并清掉。</summary>
        private void OnDestroy() { Clear(); }

        public void Clear()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child);
                else DestroyImmediate(child);
            }
            if (_renderRoot != null)
            {
                if (Application.isPlaying) Destroy(_renderRoot);
                else DestroyImmediate(_renderRoot);
                _renderRoot = null;
            }
            foreach (var m in _chunkMeshes)
            {
                if (m == null) continue;
                if (Application.isPlaying) Destroy(m);
                else DestroyImmediate(m);
            }
            _chunkMeshes.Clear();
            _pending.Clear();
        }

        // ================= 撒布 =================

        private int ScatterTrees(System.Random rng)
        {
            int placed = 0, attempts = 0;
            while (placed < TreeCount && attempts++ < MaxAttemptsPerType)
            {
                Vector3 p = SampleGround(rng, 600f);
                if (!p.HasValue()) continue;
                float scale = 0.7f + (float)rng.NextDouble() * 0.9f;
                int variant = rng.Next(ScatterAssets.TreeVariants.Length);
                int mat = rng.Next(ScatterAssets.CrownMats.Length);
                float yaw = (float)rng.NextDouble() * 360f;

                Marker("Tree_" + placed, p);
                AddInstance(0, variant, mat, p, Quaternion.Euler(0f, yaw, 0f), scale);
                placed++;
            }
            return placed;
        }

        private int ScatterRocks(System.Random rng)
        {
            int placed = 0, attempts = 0;
            while (placed < RockCount && attempts++ < MaxAttemptsPerType)
            {
                Vector3 p = SampleGround(rng, 600f);
                if (!p.HasValue()) continue;
                float s = 0.4f + (float)rng.NextDouble() * 1.4f;
                int variant = rng.Next(ScatterAssets.RockVariants.Length);
                int mat = rng.Next(ScatterAssets.RockMats.Length);
                // 随机姿态: 偏航自由 + 轻微俯仰/侧倾 (岩石半埋更自然)
                var rot = Quaternion.Euler(
                    ((float)rng.NextDouble() - 0.5f) * 20f,
                    (float)rng.NextDouble() * 360f,
                    ((float)rng.NextDouble() - 0.5f) * 20f);
                float sink = ScatterAssets.RockSinkY[variant] * s * 0.72f;

                Marker("Rock_" + placed, p);
                AddInstance(1, variant, mat, p + Vector3.up * sink, rot, s);
                placed++;
            }
            return placed;
        }

        private int ScatterBushes(System.Random rng)
        {
            int placed = 0, attempts = 0;
            while (placed < BushCount && attempts++ < MaxAttemptsPerType)
            {
                Vector3 p = SampleGround(rng, 600f);
                if (!p.HasValue()) continue;
                float s = 0.5f + (float)rng.NextDouble() * 0.8f;
                int variant = rng.Next(ScatterAssets.BushVariants.Length);
                int mat = rng.Next(ScatterAssets.BushMats.Length);
                float yaw = (float)rng.NextDouble() * 360f;
                float sink = ScatterAssets.BushSinkY[variant] * s * 0.8f;

                Marker("Bush_" + placed, p);
                AddInstance(2, variant, mat, p + Vector3.up * sink,
                    Quaternion.Euler(0f, yaw, 0f), s);
                placed++;
            }
            return placed;
        }

        /// <summary>
        /// 棕榈: 集中在沙滩上缘到低地 (海面以上 MinAboveSeaM ~ PalmBandTopM), 内陆只留少量散株;
        /// 海岸带只占总面积一小部分, 所以尝试次数放大到 PalmCount 的 40 倍。
        /// </summary>
        private int ScatterPalms(System.Random rng)
        {
            if (PalmCount <= 0 || TerrainRef == null || !TerrainRef.HasSea) return 0;
            float hi = TerrainRef.SeaLevelY + PalmBandTopM;
            int placed = 0, attempts = 0, maxAttempts = PalmCount * 40;
            while (placed < PalmCount && attempts++ < maxAttempts)
            {
                Vector3 p = SampleGround(rng, 600f);   // 已排除海面 + MinAboveSeaM 以下
                if (!p.HasValue()) continue;
                if (p.y > hi && rng.NextDouble() > PalmInlandChance) continue;
                float scale = 0.8f + (float)rng.NextDouble() * 0.5f;
                int variant = rng.Next(ScatterAssets.PalmVariants.Length);
                float yaw = (float)rng.NextDouble() * 360f;

                Marker("Palm_" + placed, p);
                AddInstance(3, variant, 0, p, Quaternion.Euler(0f, yaw, 0f), scale);
                placed++;
            }
            return placed;
        }

        /// <summary>
        /// 针叶树 (雪谷): 按相对高度 + 坡度过滤 (用射线命中点的 y 和法线): 高度在 [ConiferMinRelM, ConiferMaxRelM],
        /// 坡度 < ConiferMaxSlopeDeg; 接受概率随高度线性降到树线处为 0, 所以谷底最密、往上渐稀、树线以上没有。
        /// 过滤会丢掉大部分采样点 (山体占 4 km 场地的大半), 尝试次数放大到数量的 60 倍。
        /// </summary>
        private int ScatterConifers(System.Random rng)
        {
            if (ConiferCount <= 0) return 0;
            int placed = 0, attempts = 0, maxAttempts = ConiferCount * 60;
            float span = Mathf.Max(1f, ConiferMaxRelM - ConiferMinRelM);
            while (placed < ConiferCount && attempts++ < maxAttempts)
            {
                Vector3 p = SampleGround(rng, 1300f, out Vector3 normal);
                if (!p.HasValue()) continue;
                if (p.y < ConiferMinRelM || p.y > ConiferMaxRelM) continue;
                if (Vector3.Angle(normal, Vector3.up) > ConiferMaxSlopeDeg) continue;
                float keep = 1f - Mathf.Clamp01((p.y - ConiferMinRelM) / span);
                if (rng.NextDouble() > keep) continue;
                float scale = 0.8f + (float)rng.NextDouble() * 0.9f;
                int variant = rng.Next(ScatterAssets.ConiferVariants.Length);
                int mat = rng.NextDouble() < 0.45 ? 1 : 0;
                float yaw = (float)rng.NextDouble() * 360f;

                Marker("Conifer_" + placed, p);
                AddInstance(4, variant, mat, p, Quaternion.Euler(0f, yaw, 0f), scale);
                placed++;
            }
            return placed;
        }

        /// <summary>测试断言用的空标记 (名字/位置语义与旧版一致)。</summary>
        private void Marker(string name, Vector3 pos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.position = pos;
        }

        /// <summary>登记一个实例到 (类型, 材质, 象限) 分块。</summary>
        private void AddInstance(int kind, int variant, int matIdx, Vector3 pos,
            Quaternion rot, float scale)
        {
            int quadrant = (pos.x >= 0f ? 1 : 0) | (pos.z >= 0f ? 2 : 0);
            int key = kind * 100 + matIdx * 10 + quadrant;
            if (!_pending.TryGetValue(key, out var acc))
            {
                acc = new ChunkAcc();
                _pending[key] = acc;
            }
            acc.Variants.Add(variant);
            acc.Mats.Add(Matrix4x4.TRS(pos, rot, Vector3.one * scale));
        }

        /// <summary>撒布完成: 每个分块合并成单一大网格 (保留源 submesh → 材质分层),
        /// 挂真实 MeshRenderer (自动视锥剔除)。分块挂在独立的根级容器 GO 下。
        /// 返回分块数。</summary>
        private int BakeChunks()
        {
            if (_renderRoot == null)
            {
                // 分块网格顶点已烘焙世界坐标, 容器须位于世界原点
                _renderRoot = new GameObject("__ScatterRender");
            }
            int built = 0;
            foreach (var kv in _pending)
            {
                int kind = kv.Key / 100;
                int matIdx = (kv.Key / 10) % 10;
                int quadrant = kv.Key % 10;
                var acc = kv.Value;

                var meshes = new List<Mesh>(acc.Variants.Count);
                var locals = new List<Matrix4x4>(acc.Variants.Count);
                var groups = new List<int>(acc.Variants.Count);
                Material[] mats;
                switch (kind)
                {
                    case 0:
                        foreach (var v in acc.Variants) meshes.Add(ScatterAssets.TreeVariants[v]);
                        mats = new[] { ScatterAssets.TrunkMat, ScatterAssets.CrownMats[matIdx] };
                        break;
                    case 1:
                        foreach (var v in acc.Variants) meshes.Add(ScatterAssets.RockVariants[v]);
                        mats = new[] { ScatterAssets.RockMats[matIdx] };
                        break;
                    case 4:
                        foreach (var v in acc.Variants) meshes.Add(ScatterAssets.ConiferVariants[v]);
                        mats = new[] { ScatterAssets.ConiferTrunkMat, ScatterAssets.ConiferNeedleMats[matIdx] };
                        break;
                    case 3:
                        foreach (var v in acc.Variants) meshes.Add(ScatterAssets.PalmVariants[v]);
                        mats = new[] { ScatterAssets.PalmTrunkMat, ScatterAssets.PalmFrondMat };
                        break;
                    default:
                        foreach (var v in acc.Variants) meshes.Add(ScatterAssets.BushVariants[v]);
                        mats = new[] { ScatterAssets.BushMats[matIdx], ScatterAssets.BushBaseMat };
                        break;
                }
                locals.AddRange(acc.Mats);
                for (int i = 0; i < acc.Mats.Count; i++) groups.Add(0);   // 源 submesh 直通

                string kindName = kind == 0 ? "Trees" : kind == 1 ? "Rocks" : kind == 3 ? "Palms" : kind == 4 ? "Conifers" : "Bushes";
                var chunkMesh = ProceduralMeshes.CombineParts(meshes, locals, groups,
                    "Chunk_" + kindName + "_q" + quadrant + "_m" + matIdx);
                _chunkMeshes.Add(chunkMesh);

                var go = new GameObject("Chunk_" + kindName + "_q" + quadrant + "_m" + matIdx);
                go.transform.SetParent(_renderRoot.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = chunkMesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = mats;
                built++;
            }
            return built;
        }

        private Vector3 SampleGround(System.Random rng, float rayFromHeight)
        {
            return SampleGround(rng, rayFromHeight, out _);
        }

        /// <summary>圆内均匀采样 + 射线贴地; 净空区内/射线未命中返回 无效。normal = 命中面法线。</summary>
        private Vector3 SampleGround(System.Random rng, float rayFromHeight, out Vector3 normal)
        {
            normal = Vector3.up;
            double ang = rng.NextDouble() * 2.0 * System.Math.PI;
            double r = System.Math.Sqrt(rng.NextDouble()) * AreaRadius;   // 圆内均匀
            float x = (float)(System.Math.Cos(ang) * r);
            float z = (float)(System.Math.Sin(ang) * r);

            if (UseClearZone &&
                Mathf.Abs(x - ClearCenter.x) < ClearHalfSize.x &&
                Mathf.Abs(z - ClearCenter.y) < ClearHalfSize.y)
                return Vector3.positiveInfinity;   // 净空区

            if (!Physics.Raycast(new Vector3(x, rayFromHeight, z), Vector3.down, out var hit, rayFromHeight + 600f))
                return Vector3.positiveInfinity;   // 无地面 (山地范围外)

            // 海岛: 低于海面 + 余量的点 (海底/浅滩) 不撒; TerrainRef 为空或无海则不过滤 (森林/旧行为)
            if (TerrainRef != null && TerrainRef.HasSea && hit.point.y < TerrainRef.SeaLevelY + MinAboveSeaM)
                return Vector3.positiveInfinity;

            normal = hit.normal;
            return hit.point;
        }
    }

    /// <summary>Vector3 有效性扩展 (SampleGround 未命中哨兵)。</summary>
    internal static class Vector3Valid
    {
        public static bool HasValue(this Vector3 v) => !float.IsPositiveInfinity(v.x);
    }

    /// <summary>
    /// 撒布特征共享资产 (变体网格 + 实例化材质), 进程内惰性构建一次。
    /// 树变体: submesh0=树干+分枝 (树皮纹), submesh1=3~5 团噪声树冠 (叶面噪点);
    /// 岩石变体: 1~3 块脊状扰动二十面体成簇 (颗粒纹);
    /// 灌木变体: submesh0=2~3 团压扁噪声球 (叶面噪点), submesh1=枯枝底。
    /// RunwayEnvironment 的跑道树与 SceneSnapshot 的特写标本也复用这里的资产。
    /// </summary>
    public static class ScatterAssets
    {
        public const int TreeVariantCount = 6;
        public const int RockVariantCount = 4;
        public const int BushVariantCount = 4;
        public const int PalmVariantCount = 4;
        public const int ConiferVariantCount = 4;

        public static Mesh[] TreeVariants { get; private set; }
        public static Mesh[] RockVariants { get; private set; }
        public static Mesh[] BushVariants { get; private set; }
        /// <summary>棕榈变体: submesh0=弯曲树干+果簇 (PalmTrunkMat), submesh1=放射状叶片 (PalmFrondMat)。</summary>
        public static Mesh[] PalmVariants { get; private set; }
        /// <summary>针叶树变体: submesh0=短树干 (ConiferTrunkMat), submesh1=4~5 层锥形枝叶 (ConiferNeedleMats)。</summary>
        public static Mesh[] ConiferVariants { get; private set; }
        /// <summary>岩石/灌木变体网格底缘深度 (-bounds.min.y), 放置时下嵌贴地用。</summary>
        public static float[] RockSinkY { get; private set; }
        public static float[] BushSinkY { get; private set; }

        public static Material TrunkMat { get; private set; }
        public static Material[] CrownMats { get; private set; }
        public static Material[] RockMats { get; private set; }
        public static Material[] BushMats { get; private set; }
        public static Material BushBaseMat { get; private set; }
        public static Material PalmTrunkMat { get; private set; }
        public static Material PalmFrondMat { get; private set; }
        public static Material ConiferTrunkMat { get; private set; }
        public static Material[] ConiferNeedleMats { get; private set; }

        private static bool _built;

        public static void EnsureBuilt()
        {
            if (_built) return;
            _built = true;

            // ---- 共享灰度纹理 (色相由材质 color 提供, 保证可共享复用) ----
            var leafTex = ProceduralTextures.NoiseGray(128, 6f, 0.22f, 31, 0.52f, 0.32f);
            var rockTex = ProceduralTextures.NoiseGray(128, 5f, 0.20f, 47, 0.58f, 0.30f);
            var barkTex = ProceduralTextures.Streaks(128, 7f, 1.2f, 0.24f, 53);

            // ---- 材质 (全部 Standard; 亮色阶保证背光面不死黑) ----
            TrunkMat = Std("Bark", new Color(0.58f, 0.44f, 0.31f), barkTex, 0.06f);
            CrownMats = new[]
            {
                Std("LeafDeep", new Color(0.36f, 0.56f, 0.24f), leafTex, 0.06f),
                Std("LeafMid", new Color(0.43f, 0.66f, 0.30f), leafTex, 0.06f),
                Std("LeafLight", new Color(0.50f, 0.74f, 0.35f), leafTex, 0.06f),
            };
            RockMats = new[]
            {
                Std("RockA", new Color(0.78f, 0.76f, 0.71f), rockTex, 0.18f),
                Std("RockB", new Color(0.64f, 0.62f, 0.58f), rockTex, 0.18f),
            };
            BushMats = new[]
            {
                Std("BushA", new Color(0.41f, 0.60f, 0.27f), leafTex, 0.06f),
                Std("BushB", new Color(0.47f, 0.67f, 0.32f), leafTex, 0.06f),
            };
            BushBaseMat = Std("BushBase", new Color(0.50f, 0.40f, 0.28f), barkTex, 0.05f);
            PalmTrunkMat = Std("PalmTrunk", new Color(0.80f, 0.72f, 0.58f), barkTex, 0.05f);
            PalmFrondMat = Std("PalmFrond", new Color(0.36f, 0.64f, 0.24f), leafTex, 0.10f);
            ConiferTrunkMat = Std("ConiferTrunk", new Color(0.36f, 0.28f, 0.21f), barkTex, 0.05f);
            ConiferNeedleMats = new[]
            {
                Std("ConiferDark", new Color(0.16f, 0.34f, 0.22f), leafTex, 0.06f),
                Std("ConiferSnowy", new Color(0.62f, 0.70f, 0.66f), leafTex, 0.10f),   // 挂雪的枝叶
            };

            // ---- 网格变体 ----
            TreeVariants = new Mesh[TreeVariantCount];
            for (int i = 0; i < TreeVariantCount; i++)
                TreeVariants[i] = BuildTreeVariant(1000 + i * 17);

            RockVariants = new Mesh[RockVariantCount];
            RockSinkY = new float[RockVariantCount];
            for (int i = 0; i < RockVariantCount; i++)
            {
                RockVariants[i] = BuildRockVariant(2000 + i * 29);
                RockSinkY[i] = -RockVariants[i].bounds.min.y;
            }

            ConiferVariants = new Mesh[ConiferVariantCount];
            for (int i = 0; i < ConiferVariantCount; i++)
                ConiferVariants[i] = BuildConiferVariant(5000 + i * 31);

            PalmVariants = new Mesh[PalmVariantCount];
            for (int i = 0; i < PalmVariantCount; i++)
                PalmVariants[i] = BuildPalmVariant(4000 + i * 23);

            BushVariants = new Mesh[BushVariantCount];
            BushSinkY = new float[BushVariantCount];
            for (int i = 0; i < BushVariantCount; i++)
            {
                BushVariants[i] = BuildBushVariant(3000 + i * 41);
                BushSinkY[i] = -BushVariants[i].bounds.min.y;
            }
        }

        private static Material Std(string name, Color c, Texture2D tex, float gloss)
        {
            // R3: 带贴图的材质自动配同源法线 (表面起伏感); 纯色材质不配
            var normal = tex != null ? ProceduralTextures.NormalFromHeight(tex, 0.7f) : null;
            var m = RenderMaterials.Create(c, gloss, 0f, tex, normal);
            m.name = name;
            // 注: 网格已按变体合并渲染, 不走 GPU 实例化批路径, 无需 enableInstancing
            return m;
        }

        // ================= 变体构建 =================

        /// <summary>树: 锥形微弯树干 + 1~2 分枝 + 3~5 团噪声树冠 (主团置顶, 侧团环绕)。</summary>
        private static Mesh BuildTreeVariant(int seed)
        {
            var rng = new System.Random(seed);
            float trunkH = 2.4f + (float)rng.NextDouble() * 1.2f;
            float bend = 0.08f + (float)rng.NextDouble() * 0.22f;
            float bendAng = (float)rng.NextDouble() * 2f * Mathf.PI;
            var bendDir = new Vector2(Mathf.Cos(bendAng), Mathf.Sin(bendAng));

            var meshes = new List<Mesh>();
            var locals = new List<Matrix4x4>();
            var groups = new List<int>();

            meshes.Add(ProceduralMeshes.TaperedTrunk(trunkH,
                0.15f + (float)rng.NextDouble() * 0.06f,
                0.06f + (float)rng.NextDouble() * 0.04f,
                9, 5, bend, bendDir, seed));
            locals.Add(Matrix4x4.identity);
            groups.Add(0);

            Vector3 topCenter = new Vector3(bendDir.x * bend, trunkH, bendDir.y * bend);

            // 树冠团位置 (主团 1 + 侧团 2~4)
            int sideCount = 2 + rng.Next(3);
            var crownPos = new List<Vector3> { topCenter + new Vector3(0f, 0.25f, 0f) };
            var crownRad = new List<float> { 1.15f + (float)rng.NextDouble() * 0.5f };
            float golden = 2.399963f;
            for (int i = 0; i < sideCount; i++)
            {
                float ang = i * golden + (float)rng.NextDouble() * 0.8f;
                float dist = 0.55f + (float)rng.NextDouble() * 0.5f;
                crownPos.Add(topCenter + new Vector3(
                    Mathf.Cos(ang) * dist,
                    -0.25f - (float)rng.NextDouble() * 0.75f,
                    Mathf.Sin(ang) * dist));
                crownRad.Add(0.55f + (float)rng.NextDouble() * 0.4f);
            }

            // 分枝: 从树干中段指向侧团 (进树干 submesh)
            for (int i = 1; i < crownPos.Count; i++)
            {
                float t = 0.5f + (float)rng.NextDouble() * 0.25f;
                Vector3 start = new Vector3(bendDir.x * bend * t * t, trunkH * t, bendDir.y * bend * t * t);
                Vector3 dir = crownPos[i] - start;
                float len = dir.magnitude * 0.85f;
                meshes.Add(ProceduralMeshes.TaperedTrunk(len, 0.045f, 0.018f, 6, 2,
                    0f, Vector2.zero, seed + i));
                locals.Add(Matrix4x4.TRS(start,
                    Quaternion.FromToRotation(Vector3.up, dir.normalized), Vector3.one));
                groups.Add(0);
            }

            // 树冠团 (噪声扰动球, 进树冠 submesh)
            for (int i = 0; i < crownPos.Count; i++)
            {
                var squash = new Vector3(
                    0.95f + (float)rng.NextDouble() * 0.2f,
                    0.68f + (float)rng.NextDouble() * 0.22f,
                    0.95f + (float)rng.NextDouble() * 0.2f);
                meshes.Add(ProceduralMeshes.PerturbedBlob(1, crownRad[i],
                    2.0f + (float)rng.NextDouble() * 1.2f,
                    0.24f + (float)rng.NextDouble() * 0.1f,
                    squash, seed + 500 + i));
                locals.Add(Matrix4x4.TRS(crownPos[i],
                    Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f), Vector3.one));
                groups.Add(1);
            }

            return ProceduralMeshes.CombineParts(meshes, locals, groups, "TreeV" + seed);
        }

        /// <summary>针叶树: 1.2 m 树干 + 4~5 层向上收窄的锥形枝叶 (层间微错位, 底缘参差), 总高约 6~7 m。</summary>
        private static Mesh BuildConiferVariant(int seed)
        {
            var rng = new System.Random(seed);
            var meshes = new List<Mesh>();
            var locals = new List<Matrix4x4>();
            var groups = new List<int>();

            meshes.Add(ProceduralMeshes.TaperedTrunk(1.6f, 0.16f, 0.09f, 7, 2, 0.02f, Vector2.right, seed));
            locals.Add(Matrix4x4.identity);
            groups.Add(0);

            int tiers = 4 + rng.Next(2);
            float y = 1.0f;
            float r = 1.9f + (float)rng.NextDouble() * 0.4f;
            float h = 2.0f;
            for (int i = 0; i < tiers; i++)
            {
                meshes.Add(ProceduralMeshes.Cone(r, h, 11, 0.16f, seed + 10 + i));
                locals.Add(Matrix4x4.TRS(
                    new Vector3(((float)rng.NextDouble() - 0.5f) * 0.12f, y, ((float)rng.NextDouble() - 0.5f) * 0.12f),
                    Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f), Vector3.one));
                groups.Add(1);
                y += h * 0.55f;
                r *= 0.76f;
                h *= 0.92f;
            }
            return ProceduralMeshes.CombineParts(meshes, locals, groups, "ConiferV" + seed);
        }

        /// <summary>棕榈: 7~9 m 弧形细树干 + 10~13 片放射下垂的长叶 + 冠心下一团果簇 (进树干 submesh)。</summary>
        private static Mesh BuildPalmVariant(int seed)
        {
            var rng = new System.Random(seed);
            float trunkH = 7.0f + (float)rng.NextDouble() * 2.5f;
            float bend = 0.9f + (float)rng.NextDouble() * 1.3f;
            float bendAng = (float)rng.NextDouble() * 2f * Mathf.PI;
            var bendDir = new Vector2(Mathf.Cos(bendAng), Mathf.Sin(bendAng));

            var meshes = new List<Mesh>();
            var locals = new List<Matrix4x4>();
            var groups = new List<int>();

            meshes.Add(ProceduralMeshes.TaperedTrunk(trunkH, 0.24f, 0.12f, 8, 8, bend, bendDir, seed));
            locals.Add(Matrix4x4.identity);
            groups.Add(0);

            Vector3 top = new Vector3(bendDir.x * bend, trunkH, bendDir.y * bend);
            int fronds = 12 + rng.Next(4);
            for (int i = 0; i < fronds; i++)
            {
                float yaw = i * (360f / fronds) + ((float)rng.NextDouble() - 0.5f) * 18f;
                float len = 4.2f + (float)rng.NextDouble() * 1.6f;
                float rise = 22f + (float)rng.NextDouble() * 30f;
                float droop = 60f + (float)rng.NextDouble() * 40f;
                meshes.Add(ProceduralMeshes.PalmFrond(len, 1.2f + (float)rng.NextDouble() * 0.4f, rise, droop, 8));
                locals.Add(Matrix4x4.TRS(top, Quaternion.Euler(0f, yaw, 0f), Vector3.one));
                groups.Add(1);
            }
            meshes.Add(ProceduralMeshes.PerturbedBlob(1, 0.42f, 2.0f, 0.2f, new Vector3(1f, 0.8f, 1f), seed + 7));
            locals.Add(Matrix4x4.TRS(top + new Vector3(0f, -0.35f, 0f), Quaternion.identity, Vector3.one));
            groups.Add(0);

            return ProceduralMeshes.CombineParts(meshes, locals, groups, "PalmV" + seed);
        }

        /// <summary>岩石: 主块 + 0~2 卫星块, 脊状噪声出棱角, 非均匀压扁半埋。</summary>
        private static Mesh BuildRockVariant(int seed)
        {
            var rng = new System.Random(seed);
            var meshes = new List<Mesh>();
            var locals = new List<Matrix4x4>();
            var groups = new List<int>();

            float mainR = 0.6f + (float)rng.NextDouble() * 0.45f;
            var squash = new Vector3(
                0.9f + (float)rng.NextDouble() * 0.4f,
                0.55f + (float)rng.NextDouble() * 0.25f,
                0.85f + (float)rng.NextDouble() * 0.35f);
            meshes.Add(ProceduralMeshes.PerturbedBlob(2, mainR,
                1.8f + (float)rng.NextDouble() * 0.8f,
                0.26f + (float)rng.NextDouble() * 0.12f, squash, seed, ridged: true));
            locals.Add(Matrix4x4.TRS(Vector3.zero,
                Quaternion.Euler((float)rng.NextDouble() * 360f, (float)rng.NextDouble() * 360f,
                    (float)rng.NextDouble() * 360f), Vector3.one));
            groups.Add(0);

            int sats = rng.Next(3);
            for (int i = 0; i < sats; i++)
            {
                float r = mainR * (0.35f + (float)rng.NextDouble() * 0.3f);
                float ang = (float)rng.NextDouble() * 2f * Mathf.PI;
                float dist = mainR * (0.7f + (float)rng.NextDouble() * 0.4f);
                meshes.Add(ProceduralMeshes.PerturbedBlob(1, r, 2.2f, 0.3f,
                    new Vector3(1f, 0.6f, 1f), seed + 100 + i, ridged: true));
                locals.Add(Matrix4x4.TRS(
                    new Vector3(Mathf.Cos(ang) * dist, r * 0.15f, Mathf.Sin(ang) * dist),
                    Quaternion.Euler(0f, (float)rng.NextDouble() * 360f,
                        ((float)rng.NextDouble() - 0.5f) * 30f), Vector3.one));
                groups.Add(0);
            }

            return ProceduralMeshes.CombineParts(meshes, locals, groups, "RockV" + seed);
        }

        /// <summary>灌木: 2~3 团压扁噪声球 ( foliage ) + 底部枯枝团 (submesh1)。</summary>
        private static Mesh BuildBushVariant(int seed)
        {
            var rng = new System.Random(seed);
            var meshes = new List<Mesh>();
            var locals = new List<Matrix4x4>();
            var groups = new List<int>();

            int blobs = 2 + rng.Next(2);
            for (int i = 0; i < blobs; i++)
            {
                float r = 0.45f + (float)rng.NextDouble() * 0.3f;
                float ang = i * 2.4f + (float)rng.NextDouble() * 0.7f;
                float dist = i == 0 ? 0f : 0.25f + (float)rng.NextDouble() * 0.3f;
                meshes.Add(ProceduralMeshes.PerturbedBlob(1, r,
                    2.4f + (float)rng.NextDouble() * 1.0f, 0.3f,
                    new Vector3(1f, 0.45f + (float)rng.NextDouble() * 0.17f, 1f),
                    seed + i));
                locals.Add(Matrix4x4.TRS(
                    new Vector3(Mathf.Cos(ang) * dist, r * 0.32f, Mathf.Sin(ang) * dist),
                    Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f), Vector3.one));
                groups.Add(0);
            }
            // 枯枝底 (中央扁球, 深棕, 隐约露出叶团底部)
            meshes.Add(ProceduralMeshes.PerturbedBlob(1, 0.3f, 2.2f, 0.3f,
                new Vector3(1f, 0.35f, 1f), seed + 90));
            locals.Add(Matrix4x4.TRS(new Vector3(0f, 0.08f, 0f), Quaternion.identity, Vector3.one));
            groups.Add(1);

            return ProceduralMeshes.CombineParts(meshes, locals, groups, "BushV" + seed);
        }
    }
}
