#if UNITY_EDITOR
using System.IO;
using System.Linq;
using FlightSim;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FlightSim.EditorTools
{
    /// <summary>
    /// 场景渲染快照 (批处理, 无需 GUI/焦点):
    /// 逐场景打开 → 把相机摆到已知观察位 → Camera.Render 到 RenderTexture
    /// → 存 PNG。用于目检场景美术效果 (天空/雾/地形/跑道牌/树木)。
    /// Unity.exe -batchmode -projectPath ... -executeMethod FlightSim.EditorTools.SceneSnapshot.RenderAll -quit
    /// </summary>
    public static class SceneSnapshot
    {
        private static readonly (string path, string name, Vector3 camPos, Vector3 lookAt)[] Shots =
        {
            // 机场: 从跑道侧面高处看飞机+跑道
            ("Assets/Scenes/Flight_Phase2.unity", "airport",
             new Vector3(30f, 12f, -60f), new Vector3(0f, 1f, 0f)),
            // R2 三机位 (同场景, 近似 ChaseCamera 三模式): 追尾/座舱/自由
            ("Assets/Scenes/Flight_Phase2.unity", "airport_chase",
             new Vector3(0f, 5f, -18f), new Vector3(0f, 1.5f, 40f)),
            ("Assets/Scenes/Flight_Phase2.unity", "airport_cockpit",
             new Vector3(0f, 1.4f, 1.8f), new Vector3(0f, 2f, 120f)),
            ("Assets/Scenes/Flight_Phase2.unity", "airport_free",
             new Vector3(65f, 28f, -85f), new Vector3(0f, 2f, 60f)),
        };

        public static void RenderAll()
        {
            Directory.CreateDirectory("Logs/Snapshots");
            // 按场景分组: 同一场景只 Open+Build 一次 (重复 OpenScene 会卸载已建网格,
            // ScatterEnvironment 的静态变体缓存会拿到已销毁 Mesh → MissingReferenceException)
            foreach (var group in Shots.GroupBy(s => s.path))
            {
                EditorSceneManager.OpenScene(group.Key, OpenSceneMode.Single);
                BuildSceneContentsForSnapshot();   // R2: 手动 Build, 机场全景有内容

                // 地形状态诊断 (排查洋红渲染, 每场景一次)
                var terrain = Object.FindObjectOfType<Terrain>();
                if (terrain != null)
                {
                    var td = terrain.terrainData;
                    var layers = td != null ? td.terrainLayers : null;
                    int nullLayers = layers == null ? -1 : System.Array.IndexOf(layers, null);
                    string wsum = "n/a";
                    if (td != null && td.alphamapResolution > 0)
                    {
                        float[,,] am = td.GetAlphamaps(0, 0, td.alphamapResolution, td.alphamapResolution);
                        int r = td.alphamapResolution / 2;
                        wsum = string.Format("中心[{0:F2},{1:F2},{2:F2}] 角[{3:F2},{4:F2},{5:F2}]",
                            am[r, r, 0], am[r, r, 1], am[r, r, 2],
                            am[0, 0, 0], am[0, 0, 1], am[0, 0, 2]);
                    }
                    Debug.Log(string.Format(
                        "[Snapshot] {0}: layers={1} 首null={2} 权重: {3}",
                        group.Key, layers != null ? layers.Length.ToString() : "null", nullLayers, wsum));
                }

                foreach (var (path, name, camPos, lookAt) in group)
                {
                    var camGo = GameObject.Find("Main Camera");
                    var cam = camGo != null ? camGo.GetComponent<Camera>() : null;
                    if (cam == null) { Debug.LogError("[Snapshot] 无相机: " + path); continue; }

                    // 已知观察位 (脱离 ChaseCamera 的运行时跟随)
                    cam.transform.position = camPos;
                    cam.transform.LookAt(lookAt);
                    cam.enabled = false;   // 手动 Render

                    const int W = 1280, H = 720;
                    var rt = new RenderTexture(W, H, 24);
                    cam.targetTexture = rt;
                    // 预热帧: 同帧新建/改动的灯与材质在 HDRP LightLoop 可能未同步
                    cam.Render();
                    cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                    tex.Apply();
                    File.WriteAllBytes("Logs/Snapshots/" + name + ".png", tex.EncodeToPNG());

                    cam.targetTexture = null;
                    RenderTexture.active = null;
                    Object.DestroyImmediate(rt);
                    Object.DestroyImmediate(tex);
                    Debug.Log("[Snapshot] 已渲染: " + name);
                }
            }
            Debug.Log("[Snapshot] 全部完成: Logs/Snapshots/");
        }

        /// <summary>
        /// R2: 机场全景快照的内容构建 (与 RenderFeatureCloseups 同法) ——
        /// 跑道/撒布/动物/飞鸟都是运行时 Start 建的, 保存场景里只有组件, 不 Build 则
        /// 全景图只有天空。另做粉色防护: 遗留 Built-in Standard 材质换管线 Lit。
        /// </summary>
        private static void BuildSceneContentsForSnapshot()
        {
            // S2: 森林地形先建 (编辑模式没有 Start; 撒布/动物射线要打它)
            var terrainGo = GameObject.Find("ForestTerrain");
            if (terrainGo != null) terrainGo.GetComponent<TerrainFromHeightmap>().LoadTerrain();
            var env = GameObject.Find("RunwayEnvironment");
            if (env != null) env.GetComponent<RunwayEnvironment>().Build();
            Physics.SyncTransforms();   // 编辑模式: 新建碰撞体同步进物理场景, 否则撒布射线落空

            var scGo = GameObject.Find("ScatterEnvironment");
            if (scGo != null) scGo.GetComponent<ScatterEnvironment>().Build();
            var wlGo = GameObject.Find("WildlifeEnvironment");
            if (wlGo != null) wlGo.GetComponent<WildlifeEnvironment>().Build();
            var birdGo = GameObject.Find("BirdFlock");
            if (birdGo != null) birdGo.GetComponent<BirdFlock>().Build();

            // 粉色防护: 场景遗留 Standard 材质 (如 OBJ 模型导入材质) 在 HDRP 下呈洋红。
            // 运行时路径已由 AircraftVisual/RuntimeObjLoader 走 RenderMaterials 自愈,
            // 这里只保编辑模式快照本身不带粉色。
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m != null && m.shader != null && m.shader.name == "Standard")
                    {
                        mats[i] = RenderMaterials.Create(m.color, 0.35f, 0f, m.mainTexture);
                        changed = true;
                    }
                }
                if (changed) r.sharedMaterials = mats;
            }
        }

        /// <summary>
        /// 特征特写 (编辑模式, 无需 Play): 打开机场场景 → 手动 Build 跑道/撒布/动物/飞鸟,
        /// 再摆"标本园"(全部树/岩/灌木变体各一株, 真实 MeshRenderer, 批渲染可靠)
        /// → 逐机位渲染 PNG 目检网格/材质细节。
        /// Unity.exe -batchmode -nographics -projectPath ... -executeMethod FlightSim.EditorTools.SceneSnapshot.RenderFeatureCloseups -quit
        /// </summary>
        public static void RenderFeatureCloseups()
        {
            Directory.CreateDirectory("Logs/Snapshots");
            EditorSceneManager.OpenScene("Assets/Scenes/Flight_Phase2.unity", OpenSceneMode.Single);

            // 跑道 (地面碰撞体是后续撒布射线贴地的前提)
            var env = GameObject.Find("RunwayEnvironment");
            if (env != null) env.GetComponent<RunwayEnvironment>().Build();
            Physics.SyncTransforms();   // 编辑模式: 新建碰撞体同步进物理场景, 否则撒布射线落空

            // 撒布 (Build 内完成分块网格合并, 普通 MeshRenderer 立即可见)
            var scatterGo = new GameObject("ScatterSnapshot");
            var scatter = scatterGo.AddComponent<ScatterEnvironment>();
            scatter.AutoBuildOnStart = false;
            scatter.Build();

            // 动物 + 飞鸟 (真实 MeshRenderer, 编辑模式静态摆位)
            var wlGo = new GameObject("WildlifeSnapshot");
            var wl = wlGo.AddComponent<WildlifeEnvironment>();
            wl.AutoBuildOnStart = false;
            wl.Build();

            var birdGo = new GameObject("BirdSnapshot");
            var birds = birdGo.AddComponent<BirdFlock>();
            birds.AutoBuildOnStart = false;
            birds.Build();

            // ---- 标本园 (平地上排开全部变体, 目检网格/材质细节) ----
            SpecimenGarden();

            // ---- 机位 (全部在向阳侧: 太阳方位东南 → 相机摆南/东南, 朝北看) ----
            // 树行两镜 (前三变体 / 后三变体)
            RenderShot("closeup_trees_a", new Vector3(-152f, 4.5f, 627f), new Vector3(-151f, 2.8f, 640f));
            RenderShot("closeup_trees_b", new Vector3(-128f, 4.5f, 627f), new Vector3(-127f, 2.8f, 640f));
            // 岩石+灌木行
            RenderShot("closeup_rocks_bushes", new Vector3(-152f, 2.5f, 604f), new Vector3(-152f, 0.6f, 620f));
            // 动物: 各物种第一只
            ShootAnimal("closeup_giraffe", "Giraffe_0", 7f, 3.2f, 2.6f);
            ShootAnimal("closeup_lion_boar", "Lion_0", 5f, 1.6f, 0.8f);
            ShootAnimal("closeup_boar", "Boar_0", 4f, 1.3f, 0.5f);
            ShootAnimal("closeup_elephant", "Elephant_0", 8f, 3.5f, 2.2f);
            // 鸟 (空中盘旋初位)
            var bird = GameObject.Find("Bird_0");
            if (bird != null)
                RenderShot("closeup_bird",
                    bird.transform.position + new Vector3(2.2f, 0.15f, -2.2f),
                    bird.transform.position, 960, 540);
            // 撒布场全景 (跑道西侧, 分块网格普通渲染立即可见)
            var tree0 = GameObject.Find("Tree_0");
            if (tree0 != null)
                RenderShot("field_scatter",
                    tree0.transform.position + new Vector3(16f, 8f, -16f),
                    tree0.transform.position + new Vector3(0f, 2.5f, 0f));

            Debug.Log("[Snapshot] 特征特写全部完成: Logs/Snapshots/closeup_*.png + field_scatter.png");
        }

        /// <summary>找指定动物摆近景 (向阳侧机位: 东南)。dist=水平距离, camH=机高, lookH=注视高。</summary>
        private static void ShootAnimal(string name, string animalName, float dist, float camH, float lookH)
        {
            var a = GameObject.Find(animalName);
            if (a == null) { Debug.LogWarning("[Snapshot] 未找到 " + animalName); return; }
            Vector3 p = a.transform.position;
            RenderShot(name, p + new Vector3(dist, camH, -dist * 0.75f),
                p + new Vector3(0f, lookH, 0f), 960, 540);
        }

        /// <summary>标本园: 全部树/岩/灌木变体各一株, 排布在机场西侧平地 (z≈615~640)。</summary>
        private static void SpecimenGarden()
        {
            ScatterAssets.EnsureBuilt();
            var garden = new GameObject("SpecimenGarden");
            for (int i = 0; i < ScatterAssets.TreeVariants.Length; i++)
            {
                var go = new GameObject("SpecTree" + i);
                go.transform.SetParent(garden.transform, false);
                go.transform.position = new Vector3(-160f + i * 9f, 0f, 640f);
                go.AddComponent<MeshFilter>().sharedMesh = ScatterAssets.TreeVariants[i];
                go.AddComponent<MeshRenderer>().sharedMaterials = new[]
                {
                    ScatterAssets.TrunkMat,
                    ScatterAssets.CrownMats[i % ScatterAssets.CrownMats.Length],
                };
            }
            for (int i = 0; i < ScatterAssets.RockVariants.Length; i++)
            {
                var go = new GameObject("SpecRock" + i);
                go.transform.SetParent(garden.transform, false);
                go.transform.position = new Vector3(-160f + i * 6f,
                    ScatterAssets.RockSinkY[i] * 0.72f, 625f);
                go.transform.rotation = Quaternion.Euler(0f, i * 40f, 0f);
                go.AddComponent<MeshFilter>().sharedMesh = ScatterAssets.RockVariants[i];
                go.AddComponent<MeshRenderer>().sharedMaterial = ScatterAssets.RockMats[i % 2];
            }
            for (int i = 0; i < ScatterAssets.BushVariants.Length; i++)
            {
                var go = new GameObject("SpecBush" + i);
                go.transform.SetParent(garden.transform, false);
                go.transform.position = new Vector3(-160f + i * 5f,
                    ScatterAssets.BushSinkY[i] * 0.8f, 615f);
                go.AddComponent<MeshFilter>().sharedMesh = ScatterAssets.BushVariants[i];
                go.AddComponent<MeshRenderer>().sharedMaterials = new[]
                {
                    ScatterAssets.BushMats[i % 2], ScatterAssets.BushBaseMat,
                };
            }
        }

        /// <summary>
        /// S1 机场验收: 手动 Build 跑道环境, 渲染 8 个机位到 Logs/Snapshots/s1_airport/。
        /// Unity.exe -batchmode -projectPath ... -executeMethod FlightSim.EditorTools.SceneSnapshot.RenderAirport -quit
        /// (必须带真实图形设备, 不要加 -nographics)
        /// </summary>
        public static void RenderAirport()
        {
            Directory.CreateDirectory("Logs/Snapshots/s1_airport");
            EditorSceneManager.OpenScene("Assets/Scenes/Flight_Phase2.unity", OpenSceneMode.Single);
            BuildSceneContentsForSnapshot();

            var shots = new (string name, Vector3 pos, Vector3 look)[]
            {
                ("a_overview_high",   new Vector3(-230f, 230f, -120f), new Vector3(30f, 0f, 400f)),
                ("b_final_approach",  new Vector3(0f, 20f, -330f),     new Vector3(0f, 0f, 220f)),
                ("c_threshold_close", new Vector3(8f, 2.2f, -22f),     new Vector3(0f, 0f, 40f)),
                ("d_numbers_far_end", new Vector3(0f, 14f, 700f),      new Vector3(0f, 0f, 800f)),
                ("e_aiming_point",    new Vector3(-30f, 9f, 130f),     new Vector3(0f, 0f, 200f)),
                ("f_apron_hangars",   new Vector3(40f, 22f, 20f),      new Vector3(110f, 2f, 120f)),
                ("g_tower_taxiway",   new Vector3(10f, 8f, 340f),      new Vector3(60f, 6f, 430f)),
                ("h_papi_view",       new Vector3(-15f, 4f, -120f),    new Vector3(-30f, 0.5f, 180f)),
                // PAPI 判据验证: 同一单元组, 低于/在/高于 3.5° 下滑道三个观察点 (期望 4红 / 2红2白 / 4白)
                ("i_papi_low",        new Vector3(-31f, 1.2f, 140f),   new Vector3(-31f, 0.6f, 180f)),
                ("j_papi_on_slope",   new Vector3(-31f, 2.5f, 140f),   new Vector3(-31f, 0.6f, 180f)),
                ("k_papi_high",       new Vector3(-31f, 5.0f, 140f),   new Vector3(-31f, 0.6f, 180f)),
            };
            var env = GameObject.Find("RunwayEnvironment");
            var papis = env != null ? env.GetComponentsInChildren<PapiLight>() : new PapiLight[0];
            foreach (var sh in shots)
            {
                foreach (var pl in papis) pl.Refresh(sh.pos);   // 编辑模式无 LateUpdate, 手动按机位刷新
                if (sh.name.Contains("papi"))
                {
                    var st = new System.Text.StringBuilder();
                    foreach (var pl in papis.OrderBy(q => q.SettingDeg)) st.Append(pl.IsWhite ? 'W' : 'R');
                    Debug.Log("[Snapshot] " + sh.name + " PAPI 状态 (设定角由低到高): " + st);
                }
                RenderShot("s1_airport/" + sh.name, sh.pos, sh.look);
            }
            Debug.Log("[Snapshot] S1 机场验收完成: Logs/Snapshots/s1_airport/*.png");
        }

        /// <summary>
        /// B1 海岛验收: 打开场景 → 换成海岛环境 → 手动 Build 地形/跑道/撒布/飞鸟/附加物 → 渲染 9 个机位到 Logs/Snapshots/b1_island/。
        /// 编辑模式下 LoadTerrain 会把 TerrainData 落成 Assets/Terrain/island_TerrainData.asset, 渲染完删除 (场景不保存, 不需要)。
        /// 注意: ① 此阶段还没有水面, 海域看到的是海底沙地; ② 编辑模式下 Terrain 多层混合只渲染第 0 层 (沙色),
        /// 真实运行 (Play / PlayMode) 路径是对的 —— 看地表请用 IslandTerrainPlayModeTests.RenderSnapshots, 本方法只适合看几何。
        /// Unity.exe -batchmode -projectPath ... -executeMethod FlightSim.EditorTools.SceneSnapshot.RenderIsland -quit
        /// (必须带真实图形设备, 不要加 -nographics)
        /// </summary>
        public static void RenderIsland()
        {
            Directory.CreateDirectory("Logs/Snapshots/b1_island");
            EditorSceneManager.OpenScene("Assets/Scenes/Flight_Phase2.unity", OpenSceneMode.Single);
            Object.DestroyImmediate(GameObject.Find(EnvironmentBuilder.RootName));
            foreach (var r in Object.FindObjectsOfType<Transform>().Where(t => t.name == "__ScatterRender").ToArray())
                Object.DestroyImmediate(r.gameObject);
            TerrainRegistry.Clear();

            var h = EnvironmentBuilder.Build(SceneRegistry.Island);
            h.Terrain.LoadTerrain();
            h.Runway.Build();
            Physics.SyncTransforms();
            h.Scatter.Build();
            if (h.Birds != null) h.Birds.Build();
            if (h.Props != null) h.Props.Build();

            var shots = new (string name, Vector3 pos, Vector3 look)[]
            {
                ("a_overview_high",   new Vector3(-1300f, 750f, -1000f), new Vector3(300f, -20f, 300f)),
                ("b_airport_coast",   new Vector3(-120f, 70f, -220f),    new Vector3(300f, -5f, 420f)),
                ("c_beach_south",     new Vector3(250f, 25f, -480f),     new Vector3(390f, -9f, -280f)),
                ("d_cliff_northeast", new Vector3(120f, 70f, 880f),      new Vector3(400f, 10f, 1300f)),
                ("e_lighthouse",      new Vector3(250f, 40f, 1090f),     new Vector3(390f, 20f, 1220f)),
                ("f_pier",            new Vector3(330f, 14f, -340f),     new Vector3(420f, -8f, -285f)),
                ("g_final_approach",  new Vector3(0f, 40f, -420f),       new Vector3(0f, 0f, 300f)),
                ("h_top_down",        new Vector3(0f, 3300f, 1f),        new Vector3(0f, 0f, 0f)),
                ("i_east_sea_low",    new Vector3(520f, 120f, 300f),     new Vector3(1500f, -30f, 300f)),
            };
            foreach (var sh in shots) RenderShot("b1_island/" + sh.name, sh.pos, sh.look);

            AssetDatabase.DeleteAsset("Assets/Terrain/island_TerrainData.asset");
            Debug.Log("[Snapshot] B1 海岛验收完成: Logs/Snapshots/b1_island/*.png");
        }

        /// <summary>
        /// S2 地形验收: 手动 Build 场景, 渲染 6 个机位到 Logs/Snapshots/s2_terrain/ (高空俯瞰/斜视/河谷/机场台地/侧视)。
        /// Unity.exe -batchmode -projectPath ... -executeMethod FlightSim.EditorTools.SceneSnapshot.RenderTerrain -quit
        /// (必须带真实图形设备, 不要加 -nographics)
        /// </summary>
        public static void RenderTerrain()
        {
            Directory.CreateDirectory("Logs/Snapshots/s2_terrain");
            EditorSceneManager.OpenScene("Assets/Scenes/Flight_Phase2.unity", OpenSceneMode.Single);
            BuildSceneContentsForSnapshot();

            var shots = new (string name, Vector3 pos, Vector3 look)[]
            {
                ("a_top_down",      new Vector3(0f, 3200f, 1f),        new Vector3(0f, 0f, 0f)),
                ("b_oblique_south", new Vector3(0f, 900f, -2300f),     new Vector3(0f, -40f, 200f)),
                ("c_valley_low",    new Vector3(-250f, 260f, 1000f),   new Vector3(800f, 20f, 600f)),
                ("d_airport_air",   new Vector3(-220f, 160f, -260f),   new Vector3(40f, 0f, 380f)),
                ("e_river_bend",    new Vector3(500f, 140f, -300f),    new Vector3(900f, -10f, -800f)),
                ("f_ridge_side",    new Vector3(-1700f, 420f, -300f),  new Vector3(-600f, 0f, 200f)),
            };
            foreach (var sh in shots) RenderShot("s2_terrain/" + sh.name, sh.pos, sh.look);
            Debug.Log("[Snapshot] S2 地形验收完成: Logs/Snapshots/s2_terrain/*.png");
        }

        /// <summary>统一渲染一镜 (脱离 ChaseCamera; 手动 Render 到 RT 存 PNG)。</summary>
        private static void RenderShot(string name, Vector3 camPos, Vector3 lookAt,
            int w = 1280, int h = 720)
        {
            var camGo = GameObject.Find("Main Camera");
            var cam = camGo != null ? camGo.GetComponent<Camera>() : null;
            if (cam == null)
            {
                camGo = new GameObject("SnapshotCam");
                cam = camGo.AddComponent<Camera>();
            }
            var chase = camGo.GetComponent<ChaseCamera>();
            if (chase != null) chase.enabled = false;
            cam.transform.position = camPos;
            cam.transform.LookAt(lookAt);
            cam.enabled = false;

            var rt = new RenderTexture(w, h, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            File.WriteAllBytes("Logs/Snapshots/" + name + ".png", tex.EncodeToPNG());
            cam.targetTexture = null;
            RenderTexture.active = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
            Debug.Log("[Snapshot] 特写已渲染: " + name);
        }

        /// <summary>
        /// 跑道树带验收 (E-2 净空 65m): 手动 Build 跑道 + 撒布 (按场景搭建参数),
        /// 程序化统计跑道两侧树分布直方图 (验证 [45,58] 段不再双系统重种),
        /// 再渲染两机位 (顺跑道中线俯视 / 侧向高角全景) 供目检密度均匀性。
        /// Unity.exe -batchmode -projectPath ... -executeMethod FlightSim.EditorTools.SceneSnapshot.RenderRunwayTreeBands -quit
        /// (必须带真实图形设备, 不要加 -nographics)
        /// </summary>
        public static void RenderRunwayTreeBands()
        {
            Directory.CreateDirectory("Logs/Snapshots");
            EditorSceneManager.OpenScene("Assets/Scenes/Flight_Phase2.unity", OpenSceneMode.Single);

            // 与 FlightSimSceneSetup.CreateSceneInternal 相同的搭建参数
            var env = GameObject.Find("RunwayEnvironment");
            if (env != null) env.GetComponent<RunwayEnvironment>().Build();
            Physics.SyncTransforms();
            var scatterGo = new GameObject("ScatterSnapshot");
            var scatter = scatterGo.AddComponent<ScatterEnvironment>();
            scatter.AutoBuildOnStart = false;
            scatter.UseClearZone = true;
            var bounds = env != null ? env.GetComponent<RunwayEnvironment>().AirportBounds
                                     : new Rect(-65f, -25f, 130f, 490f);
            scatter.ClearCenter = bounds.center;
            scatter.ClearHalfSize = bounds.size * 0.5f;
            scatter.Build();

            // ---- 程序化密度统计: 净空矩形 (|x|<65 且 z∈[220±245]) 内不应有撒布树 ----
            // RunwayEnvironment 自种树: 名 "Tree", 预期 x∈[18,58] 26 棵
            // ScatterEnvironment 树: 子对象 Transform (Tree_N), 净空矩形内应为 0
            int[] runwayBands = new int[4];   // [0-15, 15-45, 45-65, >65]
            int[] scatterBands = new int[4];
            int scatterInClearRect = 0;
            if (env != null)
                foreach (Transform child in env.transform)
                    if (child.name == "Tree")
                        runwayBands[BandIdx(Mathf.Abs(child.position.x))]++;
            foreach (Transform child in scatterGo.transform)
                if (child.name.StartsWith("Tree"))
                {
                    scatterBands[BandIdx(Mathf.Abs(child.position.x))]++;
                    if (bounds.Contains(new Vector2(child.position.x, child.position.z)))
                        scatterInClearRect++;
                }
            Debug.Log(string.Format(
                "[Snapshot] 树带直方图 (|x| 分带, 全 z 范围): 跑道树 [0-15]={0} [15-45]={1} [45-65]={2} [>65]={3}; " +
                "撒布树 [0-15]={4} [15-45]={5} [45-65]={6} [>65]={7}。" +
                "机场占地矩形 (RunwayEnvironment.AirportBounds) 内撒布树 = {8} (应为 0)",
                runwayBands[0], runwayBands[1], runwayBands[2], runwayBands[3],
                scatterBands[0], scatterBands[1], scatterBands[2], scatterBands[3],
                scatterInClearRect));

            // ---- 机位: 顺跑道中线俯视 (看两侧树带对称/密度) ----
            RenderShot("treeband_centerline",
                new Vector3(0f, 26f, -20f), new Vector3(0f, 0f, 240f));
            // 侧向高角全景 (看净空条带 + 树带 + 外围撒布场过渡)
            RenderShot("treeband_overview",
                new Vector3(130f, 55f, 120f), new Vector3(0f, 0f, 240f));

            Debug.Log("[Snapshot] 树带验收完成: Logs/Snapshots/treeband_*.png");
        }

        private static int BandIdx(float absX)
        {
            if (absX < 15f) return 0;
            if (absX < 45f) return 1;
            if (absX < 65f) return 2;
            return 3;
        }

        /// <summary>
        /// 跑道牌特写: 编辑模式直接调 RunwayEnvironment.Build() 手动构建
        /// (批处理 Thread.Sleep 会卡死主循环导致 Play 初始化不完成),
        /// 相机摆在 09 号标识牌正前方 → 验证 TextMesh 文字朝向/可读性。
        /// </summary>
        public static void RenderPlaySigns()
        {
            Directory.CreateDirectory("Logs/Snapshots");
            EditorSceneManager.OpenScene("Assets/Scenes/Flight_Phase2.unity", OpenSceneMode.Single);

            var env = GameObject.Find("RunwayEnvironment");
            if (env == null) { Debug.LogError("[Snapshot] RunwayEnvironment 不存在"); return; }
            env.GetComponent<RunwayEnvironment>().Build();

            var sb = new System.Text.StringBuilder();
            foreach (Transform child in env.transform)
                sb.Append(child.name).Append("@").Append(child.position.ToString("F1")).Append(" ");
            Debug.Log("[Snapshot] 跑道对象: " + sb);

            try
            {
                                var camGo = GameObject.Find("Main Camera");
                var cam = camGo.GetComponent<Camera>();
                var chase = camGo.GetComponent<ChaseCamera>();
                if (chase != null) chase.enabled = false;
                // 36 牌: (13, 0, 6), 牌面朝 -z (南, 进近方向); 相机在牌南侧朝北看
                cam.transform.position = new Vector3(13f, 1.4f, 0.5f);
                cam.transform.LookAt(new Vector3(13f, 1.3f, 6f));
                cam.enabled = false;

                const int W = 640, H = 480;
                var rt = new RenderTexture(W, H, 24);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                File.WriteAllBytes("Logs/Snapshots/sign36.png", tex.EncodeToPNG());
                cam.targetTexture = null;
                RenderTexture.active = null;
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
                Debug.Log("[Snapshot] 36 号牌特写已渲染: Logs/Snapshots/sign36.png");
            }
            finally
            {
                // 不保存场景 (构建对象仅本进程有效)
            }
        }
    }
}
#endif
