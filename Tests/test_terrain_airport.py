"""terrain_airport.py 验收 (小尺寸): 台地常数 / 走廊净空 / 只降不升 / 边缘渐变 / 与标量参考一致 /
RAW 与 meta 同森林格式 / 确定性。"""
import sys
import tempfile
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Tools"))
import terrain_airport as ta  # noqa: E402
import terrain_forest as tf  # noqa: E402

SIZE, WORLD = 257, 4000.0
PLAT = ta.Platform()
COR = ta.Corridor()


def rough_field(seed=5, base=200.0, amp=120.0):
    """比走廊限制面和台地都高的粗糙地形, 逼各函数真正起作用。"""
    rng = np.random.default_rng(seed)
    return (base + amp * rng.random((SIZE, SIZE))).astype(np.float32)


def smooth(x):
    x = min(max(x, 0.0), 1.0)
    return x * x * (3.0 - 2.0 * x)


class TerrainAirportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.east, cls.north, cls.cell = ta.world_axes(SIZE, WORLD)
        cls.h0 = rough_field()

    def _full(self, h):
        h = ta.apply_corridor(h, self.east, self.north, PLAT, COR)
        return ta.apply_platform(h, self.east, self.north, PLAT)

    # ---- 台地 ----
    def test_platform_is_exactly_flat(self):
        h = self._full(self.h0)
        m = ta.platform_mask(self.east, self.north, PLAT)
        self.assertGreater(int(m.sum()), 500)
        self.assertEqual(float(h[m].min()), float(h[m].max()))
        self.assertAlmostEqual(float(h[m][0]), PLAT.height, places=4)

    def test_platform_flat_after_quantization(self):
        h = self._full(self.h0)
        with tempfile.TemporaryDirectory() as d:
            ta.write_raw(Path(d) / "t.raw", h)
            raw = np.frombuffer((Path(d) / "t.raw").read_bytes(), dtype=">u2").reshape(SIZE, SIZE)
        m = ta.platform_mask(self.east, self.north, PLAT)
        self.assertEqual(len(np.unique(raw[m])), 1)

    def test_platform_feather_blends_outward(self):
        h = self._full(self.h0)
        # 台地西缘外 10 m (列) 应介于台地高度与原地形之间, 外 feather 以外不变
        e0 = PLAT.east[0]
        row = int(np.argmin(np.abs(self.north[:, 0] - 400.0)))
        col_near = int(np.argmin(np.abs(self.east[0] - (e0 - 10.0))))
        col_far = int(np.argmin(np.abs(self.east[0] - (e0 - PLAT.feather - 60.0))))
        self.assertLess(float(h[row, col_near]), float(self.h0[row, col_near]))
        self.assertGreater(float(h[row, col_near]), PLAT.height)
        self.assertEqual(float(h[row, col_far]), float(self.h0[row, col_far]))

    # ---- 走廊 ----
    def test_corridor_core_under_limit_surface(self):
        h = ta.apply_corridor(self.h0, self.east, self.north, PLAT, COR)
        core = np.abs(self.east[0]) <= COR.half_width
        for rows, deg, dist in ((self.north[:, 0] < PLAT.north[0] - 1.0, COR.approach_deg,
                                 PLAT.north[0] - self.north[:, 0]),
                                (self.north[:, 0] > PLAT.north[1] + 1.0, COR.departure_deg,
                                 self.north[:, 0] - PLAT.north[1])):
            cap = PLAT.height + dist[rows][:, None] * np.tan(np.radians(deg))
            self.assertLessEqual(float((h[rows][:, core] - cap).max()), 0.01)

    def test_corridor_only_lowers_never_raises(self):
        low = np.full((SIZE, SIZE), 5.0, np.float32)    # 全是水域/谷底: 远低于限制面
        out = ta.apply_corridor(low, self.east, self.north, PLAT, COR)
        self.assertTrue(np.array_equal(out, low))
        out2 = ta.apply_corridor(self.h0, self.east, self.north, PLAT, COR)
        self.assertLessEqual(float((out2 - self.h0).max()), 0.0)

    def test_corridor_leaves_far_flanks_and_platform_rows_alone(self):
        out = ta.apply_corridor(self.h0, self.east, self.north, PLAT, COR)
        flank = np.abs(self.east[0]) > COR.half_width + COR.feather + 1.0
        self.assertTrue(np.array_equal(out[:, flank], self.h0[:, flank]), "走廊羽化带之外不得改动")
        rows = (self.north[:, 0] >= PLAT.north[0]) & (self.north[:, 0] <= PLAT.north[1])
        self.assertTrue(np.array_equal(out[rows], self.h0[rows]), "台地南北范围内行不属于走廊")

    def test_glidepath_clearance_at_least_20m(self):
        h = self._full(self.h0)   # 与森林测试同口径: 走廊 + 台地都应用后的最终高度
        cols = np.abs(self.east[0]) <= 30.0
        worst = 1e9
        for i, nr in enumerate(self.north[:, 0]):
            if nr > -100.0 or nr < -1400.0:
                continue
            glide = min(3.0 + np.tan(np.radians(3.5)) * (180.0 - nr), 60.0)
            worst = min(worst, glide - (float(h[i, cols].max()) - PLAT.height))
        self.assertGreater(worst, 20.0, "3.5° 下滑道最小间隙 %.1f m" % worst)

    def test_matches_scalar_reference_at_sample_points(self):
        h = self._full(self.h0)
        rng = np.random.default_rng(3)
        for _ in range(300):
            r, c = int(rng.integers(0, SIZE)), int(rng.integers(0, SIZE))
            e, n, v = float(self.east[0, c]), float(self.north[r, 0]), float(self.h0[r, c])
            wc = 1.0 - smooth(max(abs(e) - COR.half_width, 0.0) / COR.feather)
            for dist, deg in ((max(PLAT.north[0] - n, 0.0), COR.approach_deg),
                              (max(n - PLAT.north[1], 0.0), COR.departure_deg)):
                cap = PLAT.height + dist * np.tan(np.radians(deg))
                w = wc if dist > 0.0 else 0.0
                if v > cap:
                    v = v * (1.0 - w) + cap * w
            dx = max(PLAT.east[0] - e, e - PLAT.east[1], 0.0)
            dz = max(PLAT.north[0] - n, n - PLAT.north[1], 0.0)
            w = 1.0 - smooth(np.hypot(dx, dz) / PLAT.feather)
            ref = PLAT.height if (dx == 0.0 and dz == 0.0) else v * (1.0 - w) + PLAT.height * w
            self.assertAlmostEqual(float(h[r, c]), float(ref), delta=2e-3, msg="点 (%d,%d)" % (r, c))

    def test_corridor_cap_helper_matches_array_math(self):
        self.assertEqual(ta.corridor_cap(PLAT, COR, 400.0), PLAT.height)
        self.assertAlmostEqual(ta.corridor_cap(PLAT, COR, -1110.0),
                               PLAT.height + 1000.0 * np.tan(np.radians(COR.approach_deg)), places=6)
        self.assertAlmostEqual(ta.corridor_cap(PLAT, COR, 1910.0),
                               PLAT.height + 1000.0 * np.tan(np.radians(COR.departure_deg)), places=6)

    # ---- 边缘 ----
    def test_edge_fade_reaches_constant_and_spares_center(self):
        h = ta.apply_edge_fade(self.h0, self.east, self.north, WORLD, edge_h=12.0, fade=500.0)
        for sl in (h[0, :], h[-1, :], h[:, 0], h[:, -1]):
            self.assertEqual(float(sl.min()), 12.0)
            self.assertEqual(float(sl.max()), 12.0)
        c = SIZE // 2
        self.assertEqual(float(h[c, c]), float(self.h0[c, c]), "离边缘 > fade 的中心不受影响")

    def test_edge_value_may_be_below_land(self):
        h = ta.apply_edge_fade(self.h0, self.east, self.north, WORLD, edge_h=1.0)   # 海岛: 边缘取海底
        self.assertEqual(float(h[0, 0]), 1.0)

    # ---- 通用性质 ----
    def test_inputs_not_mutated_and_float32(self):
        before = self.h0.copy()
        for out in (ta.apply_corridor(self.h0, self.east, self.north, PLAT, COR),
                    ta.apply_platform(self.h0, self.east, self.north, PLAT),
                    ta.apply_edge_fade(self.h0, self.east, self.north, WORLD, 10.0)):
            self.assertEqual(out.dtype, np.float32)
            self.assertEqual(out.shape, (SIZE, SIZE))
        self.assertTrue(np.array_equal(before, self.h0))

    def test_deterministic_bytes(self):
        a = self._full(rough_field(9))
        b = self._full(rough_field(9))
        self.assertEqual(a.tobytes(), b.tobytes())
        self.assertNotEqual(a.tobytes(), self._full(rough_field(10)).tobytes())

    # ---- 与森林输出格式一致 ----
    def test_write_raw_matches_forest_raw_bytes_and_scale(self):
        h, river, dist = tf.generate(SIZE, WORLD, 11, erosion_iters=4)
        with tempfile.TemporaryDirectory() as d:
            meta_f = tf.write_outputs(Path(d) / "f", h, river, dist, SIZE, WORLD, 11)
            scale = ta.write_raw(Path(d) / "a.raw", h)
            self.assertEqual((Path(d) / "f.raw").read_bytes(), (Path(d) / "a.raw").read_bytes())
            self.assertEqual(scale, meta_f["height_scale_m"])

    def test_make_meta_has_every_field_unity_reads_and_forest_values(self):
        h, river, dist = tf.generate(SIZE, WORLD, 11, erosion_iters=4)
        with tempfile.TemporaryDirectory() as d:
            meta_f = tf.write_outputs(Path(d) / "f", h, river, dist, SIZE, WORLD, 11)
        m = ta.make_meta("x", SIZE, WORLD, meta_f["height_scale_m"], 11,
                         ta.Platform(tf.AIRPORT_EAST, tf.AIRPORT_NORTH, tf.AIRPORT_H, tf.AIRPORT_FEATHER),
                         ta.Corridor(tf.CORRIDOR_HALF_W, tf.CORRIDOR_FEATHER, tf.APPROACH_DEG, tf.DEPARTURE_DEG),
                         tf.EDGE_H, biome="forest")
        for k in ("size_px", "world_size_m", "height_scale_m", "sea_level_offset_m", "seed", "airport_height_m",
                  "edge_height_m", "corridor", "airport_platform", "row_order", "byte_order", "axis_convention"):
            self.assertEqual(m[k], meta_f[k], k)
        self.assertEqual(m["biome"], "forest")
        self.assertNotIn("sea_level_m", m, "无海场景不写 sea_level_m")
        self.assertEqual(ta.make_meta("x", 9, 1.0, 1.0, 1, PLAT, COR, 0.0, "island", sea_level_m=3.5)["sea_level_m"], 3.5)


if __name__ == "__main__":
    unittest.main()
