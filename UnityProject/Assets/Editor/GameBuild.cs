#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FlightSim.EditorTools
{
    /// <summary>
    /// 打包独立 Windows 程序 (不依赖 Unity 编辑器运行):
    ///   Unity.exe -batchmode -force-d3d12 -projectPath <UnityProject> -quit
    ///     -executeMethod FlightSim.EditorTools.GameBuild.BuildWindows -logFile <log>
    /// 产物: <仓库根>/Game/OpenVSPFlightSim.exe。
    /// 关键决策:
    ///   - 图形 API 固定 D3D12 (本机 D3D11 失焦黑屏, 与启动脚本 -force-d3d12 同理);
    ///   - 输出在仓库根下一层 (Game/), 使工坊的仓库根解析 (Application.dataPath/../..)
    ///     在打包版中仍指向 openvsp-flightsim —— 工坊/管线/机型库照常工作;
    ///   - 机型 OBJ / 地形 / 配置都在 StreamingAssets, 随包自动拷贝。
    /// </summary>
    public static class GameBuild
    {
        public static void BuildWindows()
        {
            PlayerSettings.productName = "OpenVSP 飞行模拟器";
            PlayerSettings.SetGraphicsAPIs(UnityEditor.BuildTarget.StandaloneWindows64,
                new[] { UnityEngine.Rendering.GraphicsDeviceType.Direct3D12 });
            WriteShaderRefMaterials();

            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string outDir = Path.Combine(root, "Game");
            Directory.CreateDirectory(outDir);
            string exe = Path.Combine(outDir, "OpenVSPFlightSim.exe");

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Flight_Phase2.unity" },
                locationPathName = exe,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            BuildReport report = BuildPipeline.BuildPlayer(opts);
            Debug.Log("[GameBuild] 结果: " + report.summary.result +
                      "  大小: " + (report.summary.totalSize / 1048576) + " MB  输出: " + exe);
            if (report.summary.result != BuildResult.Succeeded)
                EditorApplication.Exit(2);
        }

        /// <summary>
        /// 运行时 Shader.Find 的着色器必须被资产引用才会打进包 (本工程场景几乎全程序化,
        /// 无人引用 HDRP/TerrainLit 等 → 打包版地形洋红)。生成引用材质到 Resources 随包。
        /// </summary>
        private static void WriteShaderRefMaterials()
        {
            const string dir = "Assets/Resources/ShaderRefs";
            Directory.CreateDirectory(dir);
            foreach (var (name, shaderName) in new[]
                     { ("TerrainLit", "HDRP/TerrainLit"), ("Unlit", "HDRP/Unlit"),
                       ("Lit", "HDRP/Lit") })
            {
                var s = Shader.Find(shaderName);
                if (s == null) { Debug.LogWarning("[GameBuild] 着色器未找到: " + shaderName); continue; }
                string p = dir + "/" + name + ".mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(p);
                if (m == null || m.shader != s)
                {
                    AssetDatabase.DeleteAsset(p);
                    m = new Material(s);
                    AssetDatabase.CreateAsset(m, p);
                    Debug.Log("[GameBuild] 引用材质: " + p + " → " + shaderName);
                }
                // TerrainLit 的 mask/normal 变体必须随包: 地形贴层都带 mask 图 (A=光滑度),
                // 运行时地形引擎会给地形材质开 _MASKMAP/_NORMALMAP; 变体若被裁掉,
                // 打包版退回无 mask 路径, 光滑度失控 → 草地/雪地镜面反光 (2026-10-11 安装包回归)。
                if (name == "TerrainLit")
                {
                    m.EnableKeyword("_MASKMAP");
                    m.EnableKeyword("_NORMALMAP");
                    EditorUtility.SetDirty(m);
                }
            }
            AssetDatabase.SaveAssets();
        }
    }
}
#endif
