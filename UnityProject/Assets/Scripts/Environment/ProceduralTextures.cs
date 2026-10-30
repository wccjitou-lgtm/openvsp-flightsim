using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 程序化纹理工具 (写实向建模的表面细节): 全部运行时生成、灰度为主 ——
    /// 色相由材质 color 提供 (同一张灰度纹理 × 不同材质色调 = 无限色变体,
    /// 保证材质可共享、GPU 实例化不断批)。
    ///   NoiseGray   多倍频值噪声灰度图 (叶面/岩石颗粒/通用表面起伏)
    ///   Streaks     拉伸条纹 (树皮竖纹/野猪鬃纹/大象皱纹)
    ///   Spots       斑块 (长颈鹿)
    /// 纹理小尺寸 (128/256) + Repeat 平铺, mipmaps 开。
    /// </summary>
    public static class ProceduralTextures
    {
        /// <summary>多倍频灰度噪声: 均值 ~0.78 (材质 color 接近所见即所得), contrast 控制对比; spotThreshold 处压暗成斑块。</summary>
        public static Texture2D NoiseGray(int size, float freq, float contrast, int seed,
            float spotThreshold = float.MaxValue, float spotDarken = 0.45f)
        {
            var tex = NewTexture(size);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // 边缘重复采样保证 Repeat 平铺无缝: 用环面坐标 (圆域映射)
                    float u = x / (float)size, v = y / (float)size;
                    float nx = Mathf.Cos(u * 2f * Mathf.PI) * 0.5f;
                    float ny = Mathf.Sin(u * 2f * Mathf.PI) * 0.5f;
                    float nz = Mathf.Cos(v * 2f * Mathf.PI) * 0.5f;
                    float n1 = ProceduralMeshes.Fbm(nx * freq, ny * freq, nz * freq, seed, 4);
                    float g = 0.78f + n1 * contrast;
                    float n2 = ProceduralMeshes.Fbm(nz * freq * 1.7f, nx * freq * 1.7f,
                        ny * freq * 1.7f, seed + 977, 3);
                    if (n2 > spotThreshold)
                        g *= 1f - spotDarken * Mathf.InverseLerp(spotThreshold, 1f, n2);
                    px[y * size + x] = new Color(g, g, g, 1f);
                }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        /// <summary>
        /// 沿 U 向的条纹 (stretch.y 越大条纹越细长): 树皮竖纹 (uv 已沿高度拉伸时)
        /// /野猪鬃纹/大象皱纹。明暗幅度 contrast。
        /// </summary>
        public static Texture2D Streaks(int size, float freqU, float freqV, float contrast, int seed)
        {
            var tex = NewTexture(size);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size, v = y / (float)size;
                    float nx = Mathf.Cos(u * 2f * Mathf.PI) * 0.5f;
                    float ny = Mathf.Sin(u * 2f * Mathf.PI) * 0.5f;
                    float nz = Mathf.Cos(v * 2f * Mathf.PI) * 0.5f;
                    float nw = Mathf.Sin(v * 2f * Mathf.PI) * 0.5f;
                    float n = ProceduralMeshes.Fbm(nx * freqU + nz * freqV,
                        ny * freqU + nw * freqV, nz * freqV * 0.7f, seed, 3);
                    float g = 0.80f + n * contrast;
                    px[y * size + x] = new Color(g, g, g, 1f);
                }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        /// <summary>
        /// 斑块纹 (长颈鹿): 胞状多边形斑块 —— 对格点随机偏移的最近距离场阈值化,
        /// 斑块内深、斑块间浅 (网纹)。patchScale=每纹理斑块密度, edge 软化边界。
        /// </summary>
        public static Texture2D Spots(int size, int cells, float edge, int seed,
            float spotDark = 0.30f, float baseBright = 0.92f)
        {
            // 胞点 (带随机抖动), 周期边界
            var pts = new Vector2[cells * cells];
            var rng = new System.Random(seed);
            for (int cy = 0; cy < cells; cy++)
                for (int cx = 0; cx < cells; cx++)
                    pts[cy * cells + cx] = new Vector2(
                        (cx + 0.15f + (float)rng.NextDouble() * 0.7f) / cells,
                        (cy + 0.15f + (float)rng.NextDouble() * 0.7f) / cells);

            var tex = NewTexture(size);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size, v = y / (float)size;
                    // 最近胞点距离 (周期)
                    float best = 10f;
                    for (int i = 0; i < pts.Length; i++)
                    {
                        float du = Mathf.Abs(pts[i].x - u); if (du > 0.5f) du = 1f - du;
                        float dv = Mathf.Abs(pts[i].y - v); if (dv > 0.5f) dv = 1f - dv;
                        float d = du * du + dv * dv;
                        if (d < best) best = d;
                    }
                    float dist = Mathf.Sqrt(best) * cells;   // 归一化: ~0 胞心 → ~0.7 边界
                    // 胞心浅 (底色), 边界深 (斑纹网线); 再反过来: 长颈鹿是深斑浅网
                    float g = Mathf.Lerp(spotDark, baseBright,
                        Mathf.SmoothStep(0f, 1f, (dist - 0.28f) / edge));
                    // 叠加细颗粒
                    g *= 0.92f + 0.08f * ProceduralMeshes.ValueNoise(u * 24f, v * 24f, 0f, seed);
                    px[y * size + x] = new Color(g, g, g, 1f);
                }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        /// <summary>
        /// 从灰度高度图生成法线贴图 (渲染升级 R3): Sobel 梯度 + 周期边界采样,
        /// 保持与源图一致的 Repeat 无缝平铺。strength 控制凹凸强度 (0.5~2 常用)。
        /// 输出为 linear 法线纹理 (RGB24); 源图须可读 (本项目程序化纹理均满足)。
        /// </summary>
        public static Texture2D NormalFromHeight(Texture2D height, float strength = 1f)
        {
            int size = height.width;
            var src = height.GetPixels();
            var tex = new Texture2D(size, size, TextureFormat.RGB24, true, true)
            {
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color[size * size];

            float L(int x, int y)
            {
                return src[((y + size) % size) * size + ((x + size) % size)].r;
            }

            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float tl = L(x - 1, y - 1), tc = L(x, y - 1), tr = L(x + 1, y - 1);
                    float ml = L(x - 1, y),                          mr = L(x + 1, y);
                    float bl = L(x - 1, y + 1), bc = L(x, y + 1), br = L(x + 1, y + 1);
                    float dx = (tr + 2f * mr + br) - (tl + 2f * ml + bl);
                    float dy = (bl + 2f * bc + br) - (tl + 2f * tc + tr);
                    var n = new Vector3(-dx * strength, -dy * strength, 1f).normalized;
                    px[y * size + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f,
                        n.z * 0.5f + 0.5f, 1f);
                }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        private static Texture2D NewTexture(int size)
        {
            return new Texture2D(size, size, TextureFormat.RGB24, true)
            {
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
        }
    }
}
