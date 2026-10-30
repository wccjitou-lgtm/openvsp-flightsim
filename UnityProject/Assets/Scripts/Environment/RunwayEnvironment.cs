using System.Collections.Generic;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 通航机场环境 (S1): 800 m 跑道 + 平行滑行道 / 连接道 / 停机坪 + 塔台 / 机库 +
    /// 标线 (入口条纹 / 跑道号 / 瞄准点 / 中线 / 边线) + 自发光灯 (边灯 / 入口出口灯 /
    /// 滑行道灯) + PAPI + 风向袋。全部用基础图元与程序化材质, 无外部资源 (建筑为占位图元,
    /// 美术阶段可替换)。Unity 世界: +x = 东, +z = 北; 跑道沿 +z, 朝北使用 (36), 反向为 18。
    /// 所有布局坐标相对 RunwayStart (入口中线点)。
    /// 几何经 <see cref="Geometry"/> 暴露给 ModePilot 等读取; 净空/撒布范围见 <see cref="AirportBounds"/>。
    /// </summary>
    public class RunwayEnvironment : MonoBehaviour
    {
        [Header("跑道")]
        public float RunwayLength = 800f;
        public float RunwayWidth = 20f;
        public Vector3 RunwayStart = new Vector3(0f, 0f, 0f);   // 入口 (南端) 中线点; y = 跑道面海拔

        [Header("着陆引导")]
        [Tooltip("瞄准点距入口距离 m (瞄准点标线起点, 自驾下滑道对准点)")]
        public float TouchdownAimM = 180f;
        [Tooltip("接地区终点距入口距离 m")]
        public float TouchdownZoneEndM = 400f;
        [Tooltip("PAPI 与自驾共用的下滑角 (度)")]
        public float GlideAngleDeg = 3.5f;

        [Header("地面")]
        public float GroundSize = 2000f;
        [Tooltip("关闭则不建平面地面 (场景里有 Terrain 时, 跑道/建筑直接坐在地形机场台地上)")]
        public bool BuildGroundPlane = true;

        [Header("细节 (程序化图元, 无外部资源)")]
        public int TreeCount = 40;
        public int RandomSeed = 7;

        [Tooltip("关闭则不在 Start 自动 Build (测试里只要几何数据时用)")]
        public bool BuildOnStart = true;

        // ---- 布局常量 (相对 RunwayStart) ----
        private const float TaxiX = 45f, TaxiW = 10f;
        private const float ApronX0 = 50f, ApronX1 = 120f;
        private const float HangarX0 = 120f, HangarW = 24f, HangarL = 26f;
        private const float TowerX = 62f;
        // 草地要哑光: 0.9 以上会在掠射角镜面反射天空/云, 看起来像水面
        private const float GrassSmoothness = 0.12f;

        private readonly Dictionary<string, Material> _mats = new Dictionary<string, Material>();
        private readonly List<Material> _owned = new List<Material>();

        /// <summary>跑道几何 (NED, 供自驾读取)。每次按当前字段计算, 不缓存。</summary>
        public RunwayGeometry Geometry => new RunwayGeometry
        {
            ThresholdNed = new Vector2(RunwayStart.z, RunwayStart.x),
            HeadingRad = 0f,   // 图元沿 +z 构建, 恒朝北
            LengthM = RunwayLength,
            WidthM = RunwayWidth,
            ElevationM = RunwayStart.y,
            TouchdownDistM = TouchdownAimM,
            TouchdownZoneEndM = TouchdownZoneEndM,
        };

        /// <summary>机场占地 (Unity x/z 矩形, 含两端 100m 缓冲): 撒布/动物净空用。
        /// Rect.x/y = 最小 x/z, width/height = x/z 方向尺寸。</summary>
        public Rect AirportBounds
        {
            get
            {
                float x0 = RunwayStart.x - 70f, x1 = RunwayStart.x + 160f;
                float z0 = RunwayStart.z - 100f, z1 = RunwayStart.z + RunwayLength + 100f;
                return new Rect(x0, z0, x1 - x0, z1 - z0);
            }
        }

        private void Start()
        {
            if (BuildOnStart) Build();
        }

        public void Build()
        {
            SurfaceAssets.EnsureBuilt();
            _mats.Clear();

            if (BuildGroundPlane) BuildGround();
            BuildPavement();
            BuildRunwayMarkings();
            BuildTaxiMarkings();
            BuildLights();
            BuildPapi();
            BuildBuildings();
            BuildWindsock();

            // 跑道号标识牌 (两端, 面朝进近方向): 朝北使用 36, 反向 18
            MakeRunwaySign(P(RunwayWidth / 2f + 3f, 0f, 6f), "36", 0f);
            MakeRunwaySign(P(-RunwayWidth / 2f - 3f, 0f, RunwayLength - 6f), "18", 180f);

            PlantTrees();
        }

        // ================= 工具 =================

        /// <summary>布局坐标 (相对入口) → 世界坐标。</summary>
        private Vector3 P(float cx, float y, float cz) => RunwayStart + new Vector3(cx, y, cz);

        private Material Paint(Color c, float smooth = 0.5f)
        {
            string k = c.r + "," + c.g + "," + c.b + "," + smooth;
            if (!_mats.TryGetValue(k, out var m)) { m = RenderMaterials.Create(c, smooth); _mats[k] = m; }
            return m;
        }

        private Material Lamp(Color c)
        {
            string k = "lamp" + c.r + "," + c.g + "," + c.b;
            if (!_mats.TryGetValue(k, out var m)) { m = RenderMaterials.CreateEmissive(c); _mats[k] = m; }
            return m;
        }

        /// <summary>水平矩形平面 (cx/cz 为相对入口的中心, sx/sz 为 x/z 向尺寸)。</summary>
        private GameObject Flat(string name, float cx, float cz, float sx, float sz, float y, Material m)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Plane);
            go.name = name;
            go.transform.SetParent(transform, false);
            go.transform.localScale = new Vector3(sx / 10f, 1f, sz / 10f);
            go.transform.position = P(cx, y, cz);
            go.GetComponent<Renderer>().sharedMaterial = m;
            DestroyIfExists(go.GetComponent<Collider>());
            return go;
        }

        private GameObject Box(string name, Vector3 size, Vector3 center, Material m, bool keepCollider = false)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(transform, false);
            go.transform.localScale = size;
            go.transform.position = center;
            go.GetComponent<Renderer>().sharedMaterial = m;
            if (!keepCollider) DestroyIfExists(go.GetComponent<Collider>());
            return go;
        }

        // ================= 地面与路面 =================

        private void BuildGround()
        {
            // 远景深色外圈 (近浅远深层次; 叠在近景地面下方避免 z-fighting)
            var farGround = GameObject.CreatePrimitive(PrimitiveType.Plane);
            farGround.name = "GroundFar";
            farGround.transform.SetParent(transform, false);
            farGround.transform.localScale = new Vector3(GroundSize * 1.6f / 10f, 1f, GroundSize * 1.6f / 10f);
            farGround.transform.position = new Vector3(0f, -0.02f, 0f);
            SetSurface(farGround, new Color(0.24f, 0.34f, 0.20f),
                SurfaceAssets.Grass, SurfaceAssets.GrassNormal, GrassSmoothness, 480f);
            DestroyIfExists(farGround.GetComponent<Collider>());

            // 地面 (大平面, 保留碰撞体: 撒布/动物/飞鸟向下射线打它)
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(transform, false);
            ground.transform.localScale = new Vector3(GroundSize / 10f, 1f, GroundSize / 10f);   // Plane 默认 10m
            ground.transform.position = Vector3.zero;
            SetSurface(ground, new Color(0.36f, 0.50f, 0.29f),
                SurfaceAssets.Grass, SurfaceAssets.GrassNormal, GrassSmoothness, 320f);
        }

        /// <summary>带沥青纹理的铺装面 (纹理按 4m 一个周期各向同性平铺)。</summary>
        private GameObject Pave(string name, float cx, float cz, float sx, float sz, float y, Color c)
        {
            var go = Flat(name, cx, cz, sx, sz, y, null);
            SetSurface(go, c, SurfaceAssets.Asphalt, SurfaceAssets.AsphaltNormal, 0.4f,
                new Vector2(sx / 4f, sz / 4f));
            return go;
        }

        private void BuildPavement()
        {
            Color asphalt = new Color(0.22f, 0.22f, 0.24f);
            Pave("Runway", 0f, RunwayLength / 2f, RunwayWidth, RunwayLength, 0.03f, asphalt);

            // 平行滑行道 (东侧) + 三条连接道
            float zA = 0.05f * RunwayLength, zB = 0.5f * RunwayLength, zC = 0.95f * RunwayLength;
            float taxiZ0 = zA - TaxiW / 2f, taxiZ1 = zC + TaxiW / 2f;
            Pave("Taxiway", TaxiX, (taxiZ0 + taxiZ1) / 2f, TaxiW, taxiZ1 - taxiZ0, 0.02f, asphalt);
            float edge = RunwayWidth / 2f, taxiL = TaxiX - TaxiW / 2f;
            foreach (float z in new[] { zA, zB, zC })
                Pave("TaxiConnector", (edge + taxiL) / 2f, z, taxiL - edge, TaxiW, 0.02f, asphalt);

            // 停机坪 (混凝土色略浅)
            float az0 = 0.1f * RunwayLength, az1 = 0.2f * RunwayLength;
            Pave("Apron", (ApronX0 + ApronX1) / 2f, (az0 + az1) / 2f, ApronX1 - ApronX0, az1 - az0, 0.02f,
                new Color(0.42f, 0.42f, 0.43f));
        }

        // ================= 标线 =================

        private void BuildRunwayMarkings()
        {
            Material white = Paint(new Color(0.93f, 0.93f, 0.93f), 0.55f);
            float W = RunwayWidth, L = RunwayLength;
            const float y = 0.05f;

            // 边线 (连续)
            Flat("RunwayEdgeL", -(W / 2f - 0.7f), L / 2f, 0.4f, L - 1f, y, white);
            Flat("RunwayEdgeR", (W / 2f - 0.7f), L / 2f, 0.4f, L - 1f, y, white);

            // 入口条纹 (8 条, 两端)
            for (int i = 0; i < 8; i++)
            {
                float x = -7f + i * 2f;
                Flat("ThresholdBar", x, 16f, 1.2f, 24f, y, white);
                Flat("ThresholdBar", x, L - 16f, 1.2f, 24f, y, white);
            }

            // 跑道号 (入口条纹之后 8m 起, 9m 高): 朝北 36 (近端) / 18 (远端, 朝南读)
            DrawNumber("36", 0f, 36f, +1, white, y);
            DrawNumber("18", 0f, L - 36f, -1, white, y);

            // 中线虚线 (30m 线 / 20m 间隔), 避开两端号码区
            for (float z = 60f; z + 30f <= L - 60f + 0.01f; z += 50f)
                Flat("CenterLine", 0f, z + 15f, 0.6f, 30f, y, white);

            // 瞄准点标线: 两条粗条 (30m x 3m), 起点 = 瞄准点
            Flat("AimingPoint", -5.5f, TouchdownAimM + 15f, 3f, 30f, y, white);
            Flat("AimingPoint", 5.5f, TouchdownAimM + 15f, 3f, 30f, y, white);
        }

        private void BuildTaxiMarkings()
        {
            Material yellow = Paint(new Color(0.95f, 0.76f, 0.10f), 0.5f);
            float zA = 0.05f * RunwayLength, zB = 0.5f * RunwayLength, zC = 0.95f * RunwayLength;
            float edge = RunwayWidth / 2f, taxiL = TaxiX - TaxiW / 2f;
            const float y = 0.04f;

            // 滑行道中线 + 连接道中线
            Flat("TaxiCenterLine", TaxiX, (zA + zC) / 2f, 0.25f, zC - zA, y, yellow);
            foreach (float z in new[] { zA, zB, zC })
            {
                Flat("TaxiConnLine", (edge + TaxiX) / 2f, z, TaxiX - edge, 0.25f, y, yellow);

                // 等待线 (保持线): 靠跑道一侧两条实线 + 外侧一排虚线块, 距跑道边 6m
                float hx = edge + 6f;
                Flat("HoldShortSolid", hx, z, 0.25f, TaxiW, y, yellow);
                Flat("HoldShortSolid", hx + 0.6f, z, 0.25f, TaxiW, y, yellow);
                for (int i = -2; i <= 2; i++)
                    Flat("HoldShortDash", hx + 1.4f, z + i * 2f, 0.9f, 1f, y, yellow);
            }
        }

        /// <summary>
        /// 涂装数字 (七段式): text 逐字符横排, 居中于 centerX; baseZ 为数字底边 z;
        /// facing=+1 面向北 (自南向北读), -1 面向南。数字高 9m、宽 4.2m、笔画 0.9m。
        /// </summary>
        private void DrawNumber(string text, float centerX, float baseZ, int facing, Material m, float y)
        {
            const float H = 9f, Wd = 4.2f, S = 0.9f, gap = 1.6f;
            float total = text.Length * Wd + (text.Length - 1) * gap;
            for (int i = 0; i < text.Length; i++)
            {
                float cu = -total / 2f + i * (Wd + gap) + Wd / 2f;
                char d = text[i];
                void Seg(float u, float v, float su, float sv) =>
                    Flat("RunwayNum" + d, centerX + facing * u, baseZ + facing * v, su, sv, y, m);

                if (d == '1') { Seg(cu, H / 2f, S * 1.2f, H); continue; }
                // 位: a上 b右上 c右下 d下 e左下 f左上 g中
                int mask = SevenSeg[d - '0'];
                if ((mask & 1) != 0) Seg(cu, H - S / 2f, Wd, S);
                if ((mask & 2) != 0) Seg(cu + Wd / 2f - S / 2f, 3f * H / 4f, S, H / 2f);
                if ((mask & 4) != 0) Seg(cu + Wd / 2f - S / 2f, H / 4f, S, H / 2f);
                if ((mask & 8) != 0) Seg(cu, S / 2f, Wd, S);
                if ((mask & 16) != 0) Seg(cu - Wd / 2f + S / 2f, H / 4f, S, H / 2f);
                if ((mask & 32) != 0) Seg(cu - Wd / 2f + S / 2f, 3f * H / 4f, S, H / 2f);
                if ((mask & 64) != 0) Seg(cu, H / 2f, Wd, S);
            }
        }

        // 0..9 的七段掩码 (bit0=a ... bit6=g)
        private static readonly int[] SevenSeg = { 63, 6, 91, 79, 102, 109, 125, 7, 127, 111 };

        // ================= 灯光 (自发光, 无实时光源) =================

        private void BuildLights()
        {
            float W = RunwayWidth, L = RunwayLength;
            Material whiteLamp = Lamp(new Color(1f, 0.96f, 0.85f));
            Material green = Lamp(new Color(0.15f, 1f, 0.3f));
            Material red = Lamp(new Color(1f, 0.1f, 0.08f));
            Material blue = Lamp(new Color(0.15f, 0.35f, 1f));
            Vector3 sz = new Vector3(0.4f, 0.3f, 0.4f);

            // 跑道边灯 (每 50m, 距边 1.5m)
            for (float z = 0f; z <= L + 0.01f; z += 50f)
            {
                Box("EdgeLight", sz, P(-(W / 2f + 1.5f), 0.15f, z), whiteLamp);
                Box("EdgeLight", sz, P(W / 2f + 1.5f, 0.15f, z), whiteLamp);
            }
            // 入口灯 (近端绿) / 末端灯 (远端红), 横跨跑道宽
            for (int i = 0; i < 9; i++)
            {
                float x = -W / 2f + i * (W / 8f);
                Box("ThresholdLight", sz, P(x, 0.15f, -0.8f), green);
                Box("EndLight", sz, P(x, 0.15f, L + 0.8f), red);
            }
            // 滑行道边灯 (蓝, 每 40m)
            float zA = 0.05f * L, zC = 0.95f * L;
            for (float z = zA; z <= zC + 0.01f; z += 40f)
            {
                Box("TaxiLight", sz, P(TaxiX - TaxiW / 2f - 0.8f, 0.15f, z), blue);
                Box("TaxiLight", sz, P(TaxiX + TaxiW / 2f + 0.8f, 0.15f, z), blue);
            }
        }

        // ================= PAPI =================

        private void BuildPapi()
        {
            Material housing = Paint(new Color(0.18f, 0.18f, 0.20f), 0.3f);
            Material white = Lamp(new Color(1f, 1f, 1f));
            Material red = Lamp(new Color(1f, 0.1f, 0.08f));
            // 左侧 (进近时左手边), 由内向外: 设定角由低到高 (θ-0.5, θ-0.17, θ+0.17, θ+0.5)
            float[] offs = { -0.5f, -0.17f, 0.17f, 0.5f };
            for (int i = 0; i < 4; i++)
            {
                float x = -(RunwayWidth / 2f + 12f + i * 6f);
                var unit = new GameObject("PapiUnit" + i);
                unit.transform.SetParent(transform, false);
                unit.transform.position = P(x, 0f, TouchdownAimM);

                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.name = "Housing";
                box.transform.SetParent(unit.transform, false);
                box.transform.localScale = new Vector3(1.4f, 1.0f, 1.0f);
                box.transform.localPosition = new Vector3(0f, 0.5f, 0f);
                box.GetComponent<Renderer>().sharedMaterial = housing;
                DestroyIfExists(box.GetComponent<Collider>());

                var lamp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                lamp.name = "Lamp";
                lamp.transform.SetParent(unit.transform, false);
                lamp.transform.localScale = new Vector3(1.0f, 0.6f, 0.1f);
                lamp.transform.localPosition = new Vector3(0f, 0.55f, -0.55f);
                lamp.GetComponent<Renderer>().sharedMaterial = red;
                DestroyIfExists(lamp.GetComponent<Collider>());

                var papi = unit.AddComponent<PapiLight>();
                papi.SettingDeg = GlideAngleDeg + offs[i];
                papi.LampRenderer = lamp.GetComponent<Renderer>();
                papi.WhiteMat = white;
                papi.RedMat = red;
            }
        }

        // ================= 建筑 (占位图元) =================

        private void BuildBuildings()
        {
            float L = RunwayLength;
            Material wall = Paint(new Color(0.76f, 0.78f, 0.80f), 0.35f);
            Material roof = Paint(new Color(0.45f, 0.50f, 0.55f), 0.45f);
            Material glass = Paint(new Color(0.10f, 0.16f, 0.22f), 0.85f);
            Material concrete = Paint(new Color(0.62f, 0.62f, 0.60f), 0.2f);

            // 两座桶顶机库 (门朝西, 面向停机坪)
            foreach (float zc in new[] { 0.1225f * L + 0f, 0.1775f * L })
            {
                float cx = HangarX0 + HangarW / 2f;
                Box("HangarBody", new Vector3(HangarW, 4f, HangarL), P(cx, 2f, zc), wall);
                var barrel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                barrel.name = "HangarRoof";
                barrel.transform.SetParent(transform, false);
                barrel.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // 轴沿 z
                barrel.transform.localScale = new Vector3(HangarW, HangarL / 2f + 0.3f, 8f);   // 两端各探出 0.3m, 避免与方盒端面共面 z-fighting
                barrel.transform.position = P(cx, 4f, zc);
                barrel.GetComponent<Renderer>().sharedMaterial = roof;
                DestroyIfExists(barrel.GetComponent<Collider>());
                Box("HangarDoor", new Vector3(0.2f, 3.4f, HangarL - 4f),
                    P(HangarX0 - 0.05f, 1.8f, zc), glass);
            }

            // 塔台: 混凝土塔身 + 玻璃指挥室 + 屋顶
            float tz = 0.5f * L + 30f;
            Box("TowerShaft", new Vector3(4.5f, 9f, 4.5f), P(TowerX, 4.5f, tz), concrete);
            Box("TowerCab", new Vector3(9f, 3.2f, 9f), P(TowerX, 10.6f, tz), glass);
            Box("TowerRoof", new Vector3(10.5f, 0.5f, 10.5f), P(TowerX, 12.45f, tz), roof);
        }

        private void BuildWindsock()
        {
            // 风向袋 (跑道西侧近入口, 橙色)
            Vector3 b = P(-30f, 0f, 90f);
            var sock = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sock.name = "WindSock";
            sock.transform.SetParent(transform, false);
            sock.transform.localScale = Vector3.one * 1.2f;
            sock.transform.position = b + new Vector3(0f, 6f, 0f);
            SetColor(sock, new Color(1f, 0.5f, 0f));
            var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.name = "WindSockPole";
            pole.transform.SetParent(transform, false);
            pole.transform.localScale = new Vector3(0.15f, 3f, 0.15f);
            pole.transform.position = b + new Vector3(0f, 3f, 0f);
            SetColor(pole, new Color(0.75f, 0.75f, 0.78f));   // 默认 Primitive 材质是内置 Standard, HDRP 打包版里不存在 → 洋红
        }

        // ================= 标识牌 / 树 =================

        /// <summary>立式标识牌 (立柱 + 白底黑字), facingYawDeg=0 时朝 +z 方向 (跑道方向)。</summary>
        private void MakeRunwaySign(Vector3 basePos, string label, float facingYawDeg)
        {
            var sign = new GameObject("RunwaySign_" + label);
            sign.transform.SetParent(transform, false);
            sign.transform.position = basePos;
            sign.transform.rotation = Quaternion.Euler(0f, facingYawDeg, 0f);

            var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            post.name = "Post";
            post.transform.SetParent(sign.transform, false);
            post.transform.localScale = new Vector3(0.08f, 0.6f, 0.08f);
            post.transform.localPosition = new Vector3(0f, 0.6f, 0f);
            SetColor(post, new Color(0.3f, 0.3f, 0.3f));
            DestroyIfExists(post.GetComponent<Collider>());

            var board = GameObject.CreatePrimitive(PrimitiveType.Quad);
            board.name = "Board";
            board.transform.SetParent(sign.transform, false);
            board.transform.localPosition = new Vector3(0f, 1.3f, 0f);
            board.transform.localScale = new Vector3(1.4f, 1.0f, 1f);
            SetColor(board, Color.white, 0.5f);
            DestroyIfExists(board.GetComponent<Collider>());

            var textGo = new GameObject("Label");
            textGo.transform.SetParent(board.transform, false);
            textGo.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            var tm = textGo.AddComponent<TextMesh>();
            tm.text = label;
            tm.characterSize = 0.3f;
            tm.fontSize = 48;
            tm.color = Color.black;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
        }

        /// <summary>跑道西侧带状撒树 (东侧是滑行道/停机坪/建筑, 不种)。共享 ScatterAssets 树变体,
        /// 固定种子保证场景可重复。避开 PAPI 灯排与风向袋 (<see cref="IsTreeForbidden"/>)。</summary>
        private void PlantTrees()
        {
            ScatterAssets.EnsureBuilt();
            var rng = new System.Random(RandomSeed);
            for (int i = 0; i < TreeCount; i++)
            {
                float x = -(RunwayWidth / 2f + 12f + (float)rng.NextDouble() * 40f);
                float z = RunwayStart.z - 10f + (float)rng.NextDouble() * (RunwayLength + 20f);
                if (IsTreeForbidden(x, z - RunwayStart.z)) continue;
                MakeTree(new Vector3(RunwayStart.x + x, 0f, z), rng);
            }
        }

        /// <summary>树禁区 (相对入口的 x/z): PAPI 灯排 (含两侧余量) 与风向袋周围, 免得树挡灯或穿模。</summary>
        public bool IsTreeForbidden(float cx, float cz)
        {
            float papiX0 = -(RunwayWidth / 2f + 12f + 3f * 6f) - 6f;   // 最外侧单元再外 6m
            float papiX1 = -(RunwayWidth / 2f + 12f) + 6f;
            if (cx >= papiX0 && cx <= papiX1 && Mathf.Abs(cz - TouchdownAimM) < 14f) return true;
            return Mathf.Abs(cx + 30f) < 6f && Mathf.Abs(cz - 90f) < 6f;   // 风向袋 (-30, 90)
        }

        private void MakeTree(Vector3 pos, System.Random rng)
        {
            var tree = new GameObject("Tree");
            tree.transform.SetParent(transform, false);
            tree.transform.position = pos;
            tree.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
            tree.transform.localScale = Vector3.one * (0.9f + (float)rng.NextDouble() * 0.7f);

            int variant = rng.Next(ScatterAssets.TreeVariants.Length);
            tree.AddComponent<MeshFilter>().sharedMesh = ScatterAssets.TreeVariants[variant];
            tree.AddComponent<MeshRenderer>().sharedMaterials = new[]
            {
                ScatterAssets.TrunkMat,
                ScatterAssets.CrownMats[rng.Next(ScatterAssets.CrownMats.Length)],
            };
        }

        // ================= 材质 =================

        private void SetColor(GameObject go, Color c, float smoothness = 0.3f)
        {
            var renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = Own(RenderMaterials.Create(c, smoothness));
        }

        /// <summary>登记本组件创建的材质, OnDestroy 统一销毁 (场景切换会反复建/毁整个机场, 否则逐次泄漏)。</summary>
        private Material Own(Material m)
        {
            _owned.Add(m);
            return m;
        }

        private void OnDestroy()
        {
            foreach (var m in _mats.Values) if (m != null) Destroy(m);
            foreach (var m in _owned) if (m != null) Destroy(m);
            _mats.Clear();
            _owned.Clear();
        }

        /// <summary>
        /// 带表面细节的材质 (渲染升级 R3): albedo 灰度纹理 × color 调色 + 配套法线贴图
        /// (NormalFromHeight 从同一张 albedo 生成, 平铺周期一致) + 光滑度。
        /// tiling 同时作用于 albedo 与法线 (HDRP Lit 的 _NormalMap 有独立 ST, 需显式同值)。
        /// </summary>
        private void SetSurface(GameObject go, Color c, Texture2D albedo,
            Texture2D normal, float smoothness, float tiling)
        {
            SetSurface(go, c, albedo, normal, smoothness, new Vector2(tiling, tiling));
        }

        private void SetSurface(GameObject go, Color c, Texture2D albedo,
            Texture2D normal, float smoothness, Vector2 tiling)
        {
            var renderer = go.GetComponent<Renderer>();
            var mat = Own(RenderMaterials.Create(c, smoothness, 0f, albedo, normal));
            mat.mainTextureScale = tiling;
            if (mat.HasProperty("_NormalMap")) mat.SetTextureScale("_NormalMap", tiling);
            renderer.sharedMaterial = mat;
        }

        /// <summary>地面/跑道表面纹理 (渲染升级 R3): 程序化 albedo + 同源法线, 惰性一次构建。</summary>
        private static class SurfaceAssets
        {
            private static bool _built;
            public static Texture2D Grass, GrassNormal, Asphalt, AsphaltNormal;

            public static void EnsureBuilt()
            {
                if (_built) return;
                // 草地: 低频草丛明暗 (spotThreshold 压出草簇) + 法线
                Grass = ProceduralTextures.NoiseGray(256, 5.5f, 0.13f, 207, 0.62f, 0.30f);
                GrassNormal = ProceduralTextures.NormalFromHeight(Grass, 1.2f);
                // 沥青: 高频细颗粒 + 较强法线 (粗糙表面)
                Asphalt = ProceduralTextures.NoiseGray(256, 9f, 0.09f, 201);
                AsphaltNormal = ProceduralTextures.NormalFromHeight(Asphalt, 1.6f);
                _built = true;
            }
        }

        private static void DestroyIfExists(Object obj)
        {
            if (obj == null) return;
            // 编辑模式 (SceneSnapshot 手动 Build) 只能用 DestroyImmediate
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }
    }
}
