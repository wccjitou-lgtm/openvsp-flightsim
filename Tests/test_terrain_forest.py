"""terrain_forest.py 验收 (小尺寸, 保证单测快): 确定性 / 台地常数 / 河床单调 / 瀑布落差 / RAW 格式。"""
import json
import sys
import tempfile
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Tools"))
import terrain_forest as tf  # noqa: E402

SIZE, WORLD, SEED = 257, 4000.0, 11


class TerrainForestTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.h, cls.river, cls.dist = tf.generate(SIZE, WORLD, SEED, erosion_iters=8)
        cls.cell = WORLD / (SIZE - 1)

    def _world_axes(self):
        half = WORLD / 2.0
        east = -half + np.arange(SIZE) * self.cell
        north = half - np.arange(SIZE) * self.cell
        return east, north

    def test_deterministic_bytes(self):
        with tempfile.TemporaryDirectory() as d:
            for sub in ("run_a", "run_b"):
                h, river, dist = tf.generate(SIZE, WORLD, SEED, erosion_iters=8)
                tf.write_outputs(Path(d) / sub / "t", h, river, dist, SIZE, WORLD, SEED)
            for ext in (".raw", ".meta.json", ".river.json"):
                self.assertEqual((Path(d) / "run_a" / ("t" + ext)).read_bytes(),
                                 (Path(d) / "run_b" / ("t" + ext)).read_bytes(), ext + " 同种子应逐字节一致")

    def test_different_seed_differs(self):
        h2, _, _ = tf.generate(SIZE, WORLD, SEED + 1, erosion_iters=8)
        self.assertGreater(float(np.abs(h2 - self.h).max()), 5.0)

    def test_airport_platform_is_exactly_flat(self):
        east, north = self._world_axes()
        e0, e1 = tf.AIRPORT_EAST
        n0, n1 = tf.AIRPORT_NORTH
        mask = ((east[None, :] >= e0) & (east[None, :] <= e1) &
                (north[:, None] >= n0) & (north[:, None] <= n1))
        self.assertGreater(int(mask.sum()), 20, "粗网格下台地内也应有足够采样点")
        vals = self.h[mask]
        self.assertEqual(float(vals.min()), float(vals.max()), "台地内高度必须是常数")
        self.assertAlmostEqual(float(vals[0]), tf.AIRPORT_H, places=4)

    def test_platform_flat_in_quantized_raw(self):
        with tempfile.TemporaryDirectory() as d:
            h, river, dist = tf.generate(SIZE, WORLD, SEED, erosion_iters=8)
            meta = tf.write_outputs(Path(d) / "t", h, river, dist, SIZE, WORLD, SEED)
            raw = np.frombuffer((Path(d) / "t.raw").read_bytes(), dtype=">u2").reshape(SIZE, SIZE)
            east, north = self._world_axes()
            e0, e1 = tf.AIRPORT_EAST
            n0, n1 = tf.AIRPORT_NORTH
            mask = ((east[None, :] >= e0) & (east[None, :] <= e1) &
                    (north[:, None] >= n0) & (north[:, None] <= n1))
            self.assertEqual(len(np.unique(raw[mask])), 1, "量化后台地仍应为同一个 uint16 值")
            self.assertEqual(raw.shape, (SIZE, SIZE))
            self.assertEqual(len((Path(d) / "t.raw").read_bytes()), SIZE * SIZE * 2)
            self.assertAlmostEqual(meta["airport_height_m"], tf.AIRPORT_H)

    def test_east_west_edges_fade_to_constant_height(self):
        # 东/西边缘整列 (河流只穿南北边) 应 = EDGE_H, 远景地面才能与之无缝衔接
        for col in (0, SIZE - 1):
            self.assertAlmostEqual(float(self.h[:, col].min()), tf.EDGE_H, places=3)
            self.assertAlmostEqual(float(self.h[:, col].max()), tf.EDGE_H, places=3)
        for row in (0, SIZE - 1):   # 南北边: 除河谷附近外也应为 EDGE_H
            away = self.dist[row, :] > 300.0
            self.assertTrue(bool(away.any()))
            self.assertAlmostEqual(float(self.h[row, away].min()), tf.EDGE_H, places=3)
            self.assertAlmostEqual(float(self.h[row, away].max()), tf.EDGE_H, places=3)

    def test_edge_fade_does_not_touch_airport_platform(self):
        east, north = self._world_axes()
        e0, e1 = tf.AIRPORT_EAST
        n0, n1 = tf.AIRPORT_NORTH
        mask = ((east[None, :] >= e0) & (east[None, :] <= e1) &
                (north[:, None] >= n0) & (north[:, None] <= n1))
        self.assertAlmostEqual(float(self.h[mask].min()), tf.AIRPORT_H, places=4)

    def test_approach_and_departure_corridors_stay_under_limit_surface(self):
        east, north = self._world_axes()
        n0, n1 = tf.AIRPORT_NORTH
        inside = np.abs(east) <= tf.CORRIDOR_HALF_W          # 走廊核心 (无羽化)
        south_rows = north < n0 - 1.0
        north_rows = north > n1 + 1.0
        for rows, deg, dist in ((south_rows, tf.APPROACH_DEG, n0 - north),
                                (north_rows, tf.DEPARTURE_DEG, north - n1)):
            sub = self.h[rows][:, inside]
            cap = tf.AIRPORT_H + dist[rows][:, None] * np.tan(np.radians(deg))
            self.assertLessEqual(float((sub - cap).max()), 0.01,
                                 "走廊内地形不得高于 %.1f° 限制面" % deg)

    def test_glidepath_clears_terrain_by_20m_on_approach(self):
        # 3.5° 下滑道 (瞄准点 along=180 处高 3m, 封顶 60m) 与走廊内地形的间隙
        east, north = self._world_axes()
        cols = np.abs(east) <= 30.0
        worst = 1e9
        for i, nr in enumerate(north):
            if nr > -100.0 or nr < -1400.0:
                continue
            glide = min(3.0 + np.tan(np.radians(3.5)) * (180.0 - nr), 60.0)
            worst = min(worst, glide - (float(self.h[i, cols].max()) - tf.AIRPORT_H))
        self.assertGreater(worst, 20.0, "进近走廊最小间隙 %.1f m" % worst)

    def test_river_bed_and_water_never_rise_downstream(self):
        bed, water = self.river["bed"], self.river["water"]
        self.assertTrue(np.all(np.diff(bed) <= 1e-6), "河床沿程不得上坡")
        self.assertTrue(np.all(np.diff(water) <= 1e-6), "水面沿程不得上坡")

    def test_waterfall_drop_in_range(self):
        s, bed = self.river["s"], self.river["bed"]
        sf = self.river["fall_s"]
        up = np.interp(sf - 30.0, s, bed)
        down = np.interp(sf + tf.FALL_LEN + 30.0, s, bed)
        drop = up - down
        self.assertGreaterEqual(drop, 15.0)
        self.assertLessEqual(drop, 32.0)

    def test_river_is_clear_of_airport(self):
        e0, e1 = tf.AIRPORT_EAST
        n0, n1 = tf.AIRPORT_NORTH
        east, north = self.river["east"], self.river["north"]
        near = ((east > e0 - 300) & (east < e1 + 300) & (north > n0 - 300) & (north < n1 + 300))
        self.assertFalse(bool(near.any()), "河道应距机场台地至少 300 m")

    def test_riverbed_cells_sit_below_surroundings(self):
        # 河中线格点应比 100~200 m 外的格点低 (河谷存在)
        d = self.dist
        centre = self.h[d < 6.0]
        banks = self.h[(d > 100.0) & (d < 200.0)]
        self.assertGreater(centre.size, 10)
        self.assertLess(float(np.median(centre)) + 5.0, float(np.median(banks)))

    def test_outputs_have_expected_files_and_shapes(self):
        with tempfile.TemporaryDirectory() as d:
            h, river, dist = tf.generate(SIZE, WORLD, SEED, erosion_iters=8)
            tf.write_outputs(Path(d) / "t", h, river, dist, SIZE, WORLD, SEED)
            slope = np.load(str(Path(d) / "t") + ".slope.npy")
            rd = np.load(str(Path(d) / "t") + ".riverdist.npy")
            self.assertEqual(slope.shape, (SIZE, SIZE))
            self.assertEqual(rd.shape, (SIZE, SIZE))
            u8 = np.frombuffer((Path(d) / "t.riverdist.u8").read_bytes(), dtype=np.uint8)
            self.assertEqual(u8.size, SIZE * SIZE, "Unity 读的河距图应为 size² 字节")
            ok = rd < 500.0
            self.assertLessEqual(float(np.abs(u8.reshape(SIZE, SIZE)[ok] * 2.0 - rd[ok]).max()), 1.01,
                                 "u8 量化误差应 <= 1 m (步长 2 m 取整)")
            rj = json.loads((Path(d) / "t.river.json").read_text(encoding="utf-8"))
            self.assertGreater(len(rj["points"]), 100)
            self.assertIn("water", rj["points"][0])
            self.assertTrue(0.0 <= float(slope.min()) and float(slope.max()) < 90.0)


if __name__ == "__main__":
    unittest.main()
