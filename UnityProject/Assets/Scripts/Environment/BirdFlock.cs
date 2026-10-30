using System.Collections.Generic;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 飞鸟群 (纯视觉, 不影响物理): 2~3 群鸟绕各自中心点盘旋 (soaring),
    /// 正弦扑翼 + 偶发滑翔, 转弯带坡度, 高度缓慢漂移; 定期向下射线,
    /// 保证不低于地形 + 最小离地高度 (山地场景安全)。固定种子, 确定性。
    /// 鸟身: 程序化网格 (纺锤躯干+头+喙+尾扇 合并单网格), 双翼肩枢轴独立摆动。
    /// </summary>
    public class BirdFlock : MonoBehaviour
    {
        [Tooltip("Start 时自动生成 (测试可关, 走手动 Build)")]
        public bool AutoBuildOnStart = true;
        [Tooltip("群数")]
        public int FlockCount = 2;
        [Tooltip("盘旋中心分布半径 m (以场景原点为圆心)")]
        public float AreaRadius = 900f;
        public int Seed = 23;

        [Tooltip("最小离地高度 m (地形上方净空, 山地场景防穿山)")]
        public float MinGroundClearance = 25f;

        private void Start()
        {
            if (AutoBuildOnStart) Build();   // 无需等地面 (空中生成)
        }

        /// <summary>生成全部鸟群 (幂等)。</summary>
        public void Build()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
                Destroy(transform.GetChild(i).gameObject);
            BirdAssets.EnsureBuilt();

            var rng = new System.Random(Seed);
            int total = 0;
            for (int f = 0; f < FlockCount; f++)
            {
                // 群中心 (圆内均匀, 留边)
                double ang = rng.NextDouble() * 2.0 * System.Math.PI;
                double r = System.Math.Sqrt(rng.NextDouble()) * (AreaRadius * 0.5);
                var center = new Vector3((float)(System.Math.Cos(ang) * r), 0f,
                    (float)(System.Math.Sin(ang) * r));
                float altitude = 30f + (float)rng.NextDouble() * 80f;     // 30~110m
                float orbitR = 60f + (float)rng.NextDouble() * 90f;       // 60~150m
                float speed = 8f + (float)rng.NextDouble() * 6f;          // 8~14 m/s 切向速度
                float omega = speed / orbitR;                             // rad/s
                float dirSign = rng.Next(2) == 0 ? 1f : -1f;

                int birds = 5 + rng.Next(5);   // 5~9
                for (int b = 0; b < birds; b++)
                {
                    var go = new GameObject("Bird_" + total);
                    go.transform.SetParent(transform, false);
                    var bird = go.AddComponent<SoaringBird>();
                    bird.Init(
                        center: center,
                        altitude: altitude + (float)(rng.NextDouble() * 12.0 - 6.0),
                        orbitRadius: orbitR + (float)(rng.NextDouble() * 20.0 - 10.0),
                        omega: omega,
                        dirSign: dirSign,
                        phase: (float)(rng.NextDouble() * 2.0 * System.Math.PI),
                        clearance: MinGroundClearance);
                    BuildBirdVisual(go.transform, bird);
                    total++;
                }
            }
            Debug.Log("[Birds] 已放飞 " + total + " 只鸟 (" + FlockCount + " 群, 种子 " + Seed + ")");
        }

        /// <summary>鸟体: Body 合并网格 + 双肩翼枢轴 (扑翼动画)。</summary>
        private static void BuildBirdVisual(Transform root, SoaringBird bird)
        {
            var body = new GameObject("Body");
            body.transform.SetParent(root, false);
            body.AddComponent<MeshFilter>().sharedMesh = BirdAssets.BodyMesh;
            body.AddComponent<MeshRenderer>().sharedMaterial = BirdAssets.BodyMat;

            var wl = new GameObject("WingL").transform;
            wl.SetParent(root, false);
            wl.localPosition = new Vector3(-0.09f, 0.03f, 0.04f);
            wl.localRotation = Quaternion.Euler(0f, 180f, 0f);   // 左翼镜像 (网格沿 +x)
            var wlm = new GameObject("mesh");
            wlm.transform.SetParent(wl, false);
            wlm.AddComponent<MeshFilter>().sharedMesh = BirdAssets.WingMesh;
            wlm.AddComponent<MeshRenderer>().sharedMaterial = BirdAssets.WingMat;

            var wr = new GameObject("WingR").transform;
            wr.SetParent(root, false);
            wr.localPosition = new Vector3(0.09f, 0.03f, 0.04f);
            var wrm = new GameObject("mesh");
            wrm.transform.SetParent(wr, false);
            wrm.AddComponent<MeshFilter>().sharedMesh = BirdAssets.WingMesh;
            wrm.AddComponent<MeshRenderer>().sharedMaterial = BirdAssets.WingMat;

            bird.SetWings(wl, wr);
        }

        /// <summary>飞鸟共享网格/材质 (进程内建一次)。</summary>
        public static class BirdAssets
        {
            public static Mesh BodyMesh, WingMesh;
            public static Material BodyMat, WingMat;
            private static bool _built;

            public static void EnsureBuilt()
            {
                if (_built) return;
                _built = true;

                // 躯干: 纺锤 + 头球 + 喙锥 + 尾扇 (合并单网格)
                var meshes = new List<Mesh>();
                var locals = new List<Matrix4x4>();
                var groups = new List<int>();
                meshes.Add(ProceduralMeshes.PerturbedBlob(1, 0.17f, 2.0f, 0.04f,
                    new Vector3(0.55f, 0.5f, 1.35f), 501));
                locals.Add(Matrix4x4.identity);
                groups.Add(0);
                meshes.Add(ProceduralMeshes.PerturbedBlob(1, 0.075f, 2.0f, 0.03f,
                    Vector3.one, 502));
                locals.Add(Matrix4x4.TRS(new Vector3(0f, 0.06f, 0.24f),
                    Quaternion.identity, Vector3.one));
                groups.Add(0);
                meshes.Add(ProceduralMeshes.TaperedTrunk(0.09f, 0.020f, 0.003f, 6, 1,
                    0f, Vector2.zero, 503));
                locals.Add(Matrix4x4.TRS(new Vector3(0f, 0.05f, 0.30f),
                    Quaternion.Euler(85f, 0f, 0f), Vector3.one));
                groups.Add(0);
                meshes.Add(ProceduralMeshes.PerturbedBlob(1, 0.10f, 2.0f, 0.03f,
                    new Vector3(1.1f, 0.12f, 1.2f), 504));
                locals.Add(Matrix4x4.TRS(new Vector3(0f, 0.01f, -0.26f),
                    Quaternion.identity, Vector3.one));
                groups.Add(0);
                BodyMesh = ProceduralMeshes.CombineParts(meshes, locals, groups, "BirdBody");

                // 翼: 沿 +x 的扁椭圆, 前缘略前掠 (展向 ~0.5m, 弦向 0.3m)
                WingMesh = ProceduralMeshes.PerturbedBlob(1, 0.35f, 2.2f, 0.05f,
                    new Vector3(1.35f, 0.07f, 0.45f), 505);
                WingMesh = ProceduralMeshes.CombineParts(
                    new List<Mesh> { WingMesh },
                    new List<Matrix4x4> { Matrix4x4.TRS(new Vector3(0.42f, 0f, 0f),
                        Quaternion.Euler(0f, -8f, 0f), Vector3.one) },
                    new List<int> { 0 }, "BirdWing");

                BodyMat = RenderMaterials.Create(new Color(0.30f, 0.28f, 0.25f), 0.08f);
                BodyMat.name = "BirdBody";
                WingMat = RenderMaterials.Create(new Color(0.36f, 0.34f, 0.30f), 0.06f);
                WingMat.name = "BirdWing";
            }
        }
    }

    /// <summary>
    /// 单只鸟盘旋飞行: 绕中心点圆轨道 + 高度缓漂 + 转弯压坡度 + 扑翼/滑翔交替;
    /// 定期向下射线保证离地净空 (地形上方)。全部确定性 (相位/参数来自种子序列)。
    /// </summary>
    public class SoaringBird : MonoBehaviour
    {
        public Vector3 Center { get; private set; }
        public float OrbitRadius { get; private set; }
        public float Altitude { get; private set; }

        private float _omega, _dirSign, _angle, _clearance;
        private float _flapPhase, _flapFreq, _glideSeed;
        private float _flapAmp;
        private float _groundY = float.NegativeInfinity, _groundTimer;
        private Transform _wingL, _wingR;

        public void Init(Vector3 center, float altitude, float orbitRadius,
            float omega, float dirSign, float phase, float clearance)
        {
            Center = center;
            Altitude = altitude;
            OrbitRadius = orbitRadius;
            _omega = omega;
            _dirSign = dirSign;
            _angle = phase;
            _clearance = clearance;
            _flapPhase = phase * 3.1f;
            _flapFreq = 2.4f + Mathf.Abs(Mathf.Sin(phase * 7.3f)) * 1.2f;   // 2.4~3.6 Hz
            _glideSeed = phase * 13.7f;
            _flapAmp = 38f;
            SnapToPath();
        }

        public void SetWings(Transform wingL, Transform wingR)
        {
            _wingL = wingL;
            _wingR = wingR;
        }

        private Vector3 PathPos(float angle, out Vector3 tangent)
        {
            // 圆轨道 (绕 Center, 逆时针为正); tangent = 运动方向
            float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
            var p = Center + new Vector3(c * OrbitRadius, 0f, s * OrbitRadius);
            tangent = new Vector3(-s, 0f, c) * _dirSign;
            return p;
        }

        private void SnapToPath()
        {
            Vector3 pos = PathPos(_angle, out _);
            pos.y = Altitude;
            transform.position = pos;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            float t = Time.time;

            _angle += _omega * _dirSign * dt;

            // 高度缓漂 (±5m, 周期 ~80s)
            float altDrift = Mathf.Sin(t * 0.08f + _flapPhase) * 5f;

            Vector3 pos = PathPos(_angle, out Vector3 tangent);
            pos.y = Altitude + altDrift;

            // 地形净空 (定期射线, 无地面时沿用路径高度)
            _groundTimer -= dt;
            if (_groundTimer <= 0f)
            {
                _groundTimer = 0.5f;
                // 射线起点必须高于地形最高点: 起伏地形上 pos+30m 可能已在山体内部而漏射
                Vector3 from = new Vector3(pos.x, Mathf.Max(pos.y + 30f, 600f), pos.z);
                if (Physics.Raycast(from, Vector3.down, out var hit, 1300f))
                    _groundY = hit.point.y;
            }
            if (!float.IsNegativeInfinity(_groundY))
                pos.y = Mathf.Max(pos.y, _groundY + _clearance);

            transform.position = pos;

            // 朝向 = 切向; 坡度压向圆心 (向心加速度对应坡度, 夸大 2.2 倍增强观感)
            float yaw = Mathf.Atan2(tangent.x, tangent.z) * Mathf.Rad2Deg;
            float v = _omega * OrbitRadius;
            float bank = Mathf.Clamp(Mathf.Atan2(v * v, 9.81f * OrbitRadius) * Mathf.Rad2Deg
                * 2.2f, 0f, 35f) * _dirSign;
            transform.rotation = Quaternion.Euler(0f, yaw, -bank);

            // 扑翼/滑翔交替: 滑翔相位 (慢变门控) 内振幅归零, 翼面保持 8° 上反角
            bool gliding = Mathf.Sin(t * 0.11f + _glideSeed) > 0.55f;
            float targetAmp = gliding ? 0f : 38f;
            _flapAmp = Mathf.Lerp(_flapAmp, targetAmp, Mathf.Clamp01(3f * dt));
            float flap = 8f + _flapAmp * Mathf.Sin(t * _flapFreq * 2f * Mathf.PI + _flapPhase);
            if (_wingR != null) _wingR.localRotation = Quaternion.Euler(0f, 0f, flap);
            if (_wingL != null) _wingL.localRotation = Quaternion.Euler(0f, 180f, flap);
        }
    }
}
