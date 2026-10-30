using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 打包版渲染诊断 (排查洋红): 启动 15s 后扫描全部 Renderer,
    /// 报告 shader.isSupported == false 的材质 (洋红即此种)。
    /// 编辑器与打包版都会跑; 编辑器里全支持, 无输出即正常。
    /// </summary>
    public static class ShaderSupportProbe
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Run()
        {
            var go = new GameObject("_ShaderSupportProbe");
            go.AddComponent<Probe>().StartCoroutine(Probe.Routine());
        }

        private class Probe : MonoBehaviour
        {
            internal static System.Collections.IEnumerator Routine()
            {
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 15f) yield return null;
                int bad = 0;
                foreach (var r in Object.FindObjectsOfType<Renderer>(true))
                {
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null || m.shader == null) continue;
                        if (!m.shader.isSupported)
                        {
                            bad++;
                            string path = r.name;
                            for (var p = r.transform.parent; p != null; p = p.parent)
                                path = p.name + "/" + path;
                            Debug.LogWarning("[ShaderProbe] 不支持: " + m.shader.name +
                                             "  on " + path);
                        }
                    }
                }
                Debug.Log("[ShaderProbe] 扫描完成, 不支持材质数: " + bad);

                // 地形专项 (Terrain 不是 Renderer, 常规扫描覆盖不到)
                foreach (var t in Object.FindObjectsOfType<Terrain>(true))
                {
                    var mat = t.materialTemplate;
                    string ms = mat == null ? "(null)" :
                        mat.shader.name + " supported=" + mat.shader.isSupported;
                    var layers = t.terrainData != null ? t.terrainData.terrainLayers : null;
                    int withTex = 0;
                    if (layers != null)
                        foreach (var l in layers)
                            if (l != null && l.diffuseTexture != null) withTex++;
                    Debug.Log(string.Format(
                        "[ShaderProbe] 地形: {0} 材质={1} 贴层={2} 带纹理={3} alphamapTex={4}",
                        t.name, ms, layers != null ? layers.Length : -1, withTex,
                        t.terrainData != null && t.terrainData.alphamapTextures != null
                            ? t.terrainData.alphamapTextures.Length : -1));

                    // 镜面地面排查 (2026-10-11 打包版回归): 打印地形材质的关键字与
                    // 每贴层光滑度属性 —— mask 变体被裁剪时 _MASKMAP 缺失, 退回路径的光滑度
                    // 取自 _SmoothnessN × 贴图 alpha, 与 mask 图路径不一致。
                    if (mat != null)
                    {
                        Debug.Log(string.Format(
                            "[ShaderProbe] 地形材质 {0}: _MASKMAP={1} _NORMALMAP={2} _Smoothness0={3} _LayerHasMask0={4} _MaskMapRemapScale0={5} _Metallic0={6}",
                            t.name,
                            mat.IsKeywordEnabled("_MASKMAP"), mat.IsKeywordEnabled("_NORMALMAP"),
                            mat.HasProperty("_Smoothness0") ? mat.GetFloat("_Smoothness0").ToString("F2") : "n/a",
                            mat.HasProperty("_LayerHasMask0") ? mat.GetFloat("_LayerHasMask0").ToString("F0") : "n/a",
                            mat.HasProperty("_MaskMapRemapScale0") ? mat.GetVector("_MaskMapRemapScale0").ToString() : "n/a",
                            mat.HasProperty("_Metallic0") ? mat.GetFloat("_Metallic0").ToString("F2") : "n/a"));
                    }
                    if (layers != null)
                        foreach (var l in layers)
                            if (l != null)
                                Debug.Log(string.Format(
                                    "[ShaderProbe] 贴层 {0}: smoothness={1:F2} metallic={2:F2} mask={3} diffuseAlpha={4}",
                                    l.name, l.smoothness, l.metallic,
                                    l.maskMapTexture != null ? l.maskMapTexture.name : "(null)",
                                    l.diffuseTexture != null ? l.diffuseTexture.format.ToString() : "n/a"));
                }

                // 远景对象摸底 (洋红地平线排查): 尺寸/距离超阈值的 renderer 全部记录
                var cam = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
                foreach (var r in Object.FindObjectsOfType<Renderer>(true))
                {
                    bool far = (r.bounds.center - cam).magnitude > 2000f
                               || r.bounds.size.magnitude > 2000f;
                    if (!far) continue;
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null || m.shader == null) continue;
                        Debug.Log(string.Format(
                            "[ShaderProbe] 远景: {0}  shader={1} supported={2} color={3} dist={4:F0}m size={5:F0}m",
                            r.name, m.shader.name, m.shader.isSupported, m.color,
                            (r.bounds.center - cam).magnitude, r.bounds.size.magnitude));
                    }
                }
            }
        }
    }
}
