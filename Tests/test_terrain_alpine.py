"""terrain_alpine.py 验收 (513² 小图): 确定性 / 台地常数 / 走廊净空 / 谷底宽度 >= 600 m 且走廊两侧 250 m 内无陡坡 /
山体相对台地最高 650~950 m / 谷底沿走廊缓升小于限制面 / 冰碛丘不进走廊 / 小溪是凹槽 / RAW 量化误差 < 0.05 m /
meta 与输出文件字段。"""
import json
import sys
import tempfile
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Tools"))
import terrain_airport as ta  # noqa: E402
import terrain_alpine as tal  # noqa: E402

SIZE, WORLD, SEED = 513, 4000.0, 31


class TerrainAlpineTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.h, cls.u = tal.generate(SIZE, WORLD, SEED, erosion_iters=6)
        cls.east, cls.north, cls.cell = ta.world_axes(SIZE, WORLD)
        cls.e = cls.east[0]
        cls.n = cls.north[:, 0]

    def _row(self, north_m):
        return int(np.argmin(np.abs(self.n - north_m)))

    def test_deterministic_same_seed_bytes_equal(self):
        h2, _ = tal.generate(SIZE, WORLD, SEED, erosion_iters=6)
        self.assertTrue(np.array_equal(self.h, h2))
        h3, _ = tal.generate(SIZE, WORLD, SEED + 1, erosion_iters=6)
        self.assertFalse(np.array_equal(self.h, h3), "换种子应得到不同地形")

    def test_platform_is_constant_at_platform_height(self):
        mask = ta.platform_mask(self.east, self.north, tal.PLATFORM)
        self.assertGreater(int(mask.sum()), 200)
        vals = self.h[mask]
        self.assertLess(float(vals.max() - vals.min()), 1e-3)
        self.assertAlmostEqual(float(vals.mean()), tal.PLATFORM_H, places=3)

    def test_corridor_never_above_clearance_surface(self):
        inside = np.abs(self.e) <= tal.CORRIDOR.half_width
        for r in range(SIZE):
            n = float(self.n[r])
            if tal.PLATFORM.north[0] <= n <= tal.PLATFORM.north[1]:
                continue
            cap = ta.corridor_cap(tal.PLATFORM, tal.CORRIDOR, n)
            self.assertLessEqual(float(self.h[r, inside].max()), cap + 0.05, "north=%.0f 走廊超出限制面" % n)

    def test_valley_floor_at_least_600m_wide(self):
        widths = []
        for n in np.arange(-1500.0, 1501.0, 100.0):
            r = self._row(n)
            xc, _ = tal.valley_geometry(np.float32(self.n[r]))
            rel = self.h[r] - tal.floor_profile(np.float32(self.n[r]))
            c = int(np.argmin(np.abs(self.e - float(xc))))
            lo = c
            while lo > 0 and rel[lo - 1] <= 15.0:
                lo -= 1
            hi = c
            while hi < SIZE - 1 and rel[hi + 1] <= 15.0:
                hi += 1
            widths.append(float(self.e[hi] - self.e[lo]))
        self.assertGreaterEqual(min(widths), 600.0, "最窄谷底 %.0f m (逐行 %s)" % (min(widths), widths))

    def test_walls_start_outside_250m_of_centerline_near_airport(self):
        for n in np.arange(0.0, 801.0, 100.0):
            r = self._row(n)
            band = np.abs(self.e) <= 250.0
            rel = self.h[r, band] - tal.PLATFORM_H
            self.assertLess(float(rel.max()), 8.0, "north=%.0f 离中线 250 m 内就起坡了: %.1f m" % (n, rel.max()))

    def test_relief_peaks_650_to_950_m_above_platform(self):
        rel_max = float(self.h.max() - tal.PLATFORM_H)
        self.assertGreater(rel_max, 650.0)
        self.assertLess(rel_max, 950.0)

    def test_floor_rises_gently_below_clearance_angles(self):
        n0, n1 = tal.PLATFORM.north
        self.assertLess(tal.FLOOR_SOUTH_DEG, tal.CORRIDOR.approach_deg)
        self.assertLess(tal.FLOOR_NORTH_DEG, tal.CORRIDOR.departure_deg)
        for n, lim in ((n0 - 1500.0, tal.CORRIDOR.approach_deg), (n1 + 1000.0, tal.CORRIDOR.departure_deg)):
            rise = float(tal.floor_profile(np.float32(n))) - tal.PLATFORM_H
            dist = abs(n - (n0 if n < n0 else n1))
            self.assertLess(np.degrees(np.arctan(rise / dist)), lim)

    def test_moraine_mounds_stay_out_of_corridor_sides(self):
        # 只测图端渐变带 (EDGE_FADE) 以内: 渐变带里整个地表会被拉向 EDGE_H
        for n in np.arange(-1200.0, 1201.0, 100.0):
            r = self._row(n)
            band = np.abs(self.e) <= 255.0
            rel = self.h[r, band] - tal.floor_profile(np.float32(self.n[r]))
            self.assertLess(float(rel.max()), 6.0, "north=%.0f 走廊两侧出现 %.1f m 的隆起" % (n, rel.max()))

    def test_creek_is_a_groove_in_the_floor(self):
        for n in (-1200.0, -600.0, 1200.0, 1500.0):
            r = self._row(n)
            xc, _ = tal.valley_geometry(np.float32(self.n[r]))
            ce = float(tal.creek_east(np.float32(self.n[r]), xc))
            c = int(np.argmin(np.abs(self.e - ce)))
            neighbours = np.concatenate([self.h[r, max(c - 14, 0):max(c - 8, 0)], self.h[r, c + 8:c + 14]])
            self.assertLess(float(self.h[r, c]), float(neighbours.mean()) - 0.8, "north=%.0f 溪床应比两岸低" % n)

    def test_raw_non_negative_and_edges_flat_at_edge_height(self):
        self.assertGreater(float(self.h.min()), 0.0)
        edge = np.concatenate([self.h[0, :], self.h[-1, :], self.h[:, 0], self.h[:, -1]])
        self.assertLess(float(np.abs(edge - tal.EDGE_H).max()), 1.0)

    def test_outputs_meta_and_quantization_error_below_5cm(self):
        with tempfile.TemporaryDirectory() as td:
            base = Path(td) / "alpine"
            meta = tal.write_outputs(base, self.h, self.u, SIZE, WORLD, SEED)
            self.assertEqual(meta["biome"], "alpine")
            self.assertEqual(meta["airport_height_m"], tal.PLATFORM_H)
            self.assertEqual(meta["platform_msl_m"], 2000.0)
            self.assertEqual(meta["edge_height_m"], tal.EDGE_H)
            self.assertNotIn("sea_level_m", meta, "雪谷没有海")
            on_disk = json.loads(base.with_suffix(".meta.json").read_text(encoding="utf-8"))
            self.assertEqual(on_disk["size_px"], SIZE)
            q = np.frombuffer(base.with_suffix(".raw").read_bytes(), dtype=">u2").reshape(SIZE, SIZE)
            back = q.astype(np.float64) / 65535.0 * meta["height_scale_m"]
            self.assertLess(float(np.abs(back - self.h).max()), 0.05)
            for suffix in (".slope.npy", ".relalt.npy"):
                self.assertTrue(Path(str(base) + suffix).exists(), suffix)


if __name__ == "__main__":
    unittest.main()
