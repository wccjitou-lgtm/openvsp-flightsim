"""terrain_island.py 验收 (513² 小图): 确定性 / 台地常数且高出海面 9 m / 东岸与跑道平行且距中线 350~460 m /
沙滩缓坡 + 东北海蚀崖 / 四周为海 / 走廊净空 / RAW 非负 / coast.json 与 meta 字段。"""
import json
import sys
import tempfile
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Tools"))
import terrain_airport as ta  # noqa: E402
import terrain_island as ti  # noqa: E402

SIZE, WORLD, SEED = 513, 4000.0, 21


class TerrainIslandTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.h, cls.d = ti.generate(SIZE, WORLD, SEED, erosion_iters=6)
        cls.east, cls.north, cls.cell = ta.world_axes(SIZE, WORLD)
        cls.e = cls.east[0]
        cls.n = cls.north[:, 0]

    def _shore_east(self, north_m):
        r = int(np.argmin(np.abs(self.n - north_m)))
        start = int(np.searchsorted(self.e, 200.0))
        idx = np.nonzero(self.h[r, start:] < ti.SEA_H)[0]
        self.assertGreater(idx.size, 0, "north=%s 东侧应有海" % north_m)
        k = start + int(idx[0])
        return float(self.e[k])

    def test_deterministic_bytes(self):
        with tempfile.TemporaryDirectory() as t:
            for sub in ("a", "b"):
                h, d = ti.generate(SIZE, WORLD, SEED, erosion_iters=6)
                ti.write_outputs(Path(t) / sub / "i", h, d, SIZE, WORLD, SEED)
            for ext in (".raw", ".meta.json", ".coast.json"):
                self.assertEqual((Path(t) / "a" / ("i" + ext)).read_bytes(),
                                 (Path(t) / "b" / ("i" + ext)).read_bytes(), ext)
        h2, _ = ti.generate(SIZE, WORLD, SEED + 1, erosion_iters=6)
        self.assertGreater(float(np.abs(h2 - self.h).max()), 5.0)

    def test_platform_flat_and_nine_metres_above_sea(self):
        m = ta.platform_mask(self.east, self.north, ti.PLATFORM)
        v = self.h[m]
        self.assertGreater(int(m.sum()), 500)
        self.assertEqual(float(v.min()), float(v.max()))
        self.assertAlmostEqual(float(v[0]) - ti.SEA_H, 9.0, places=4)

    def test_raw_is_nonnegative_and_platform_flat_after_quantization(self):
        self.assertGreater(float(self.h.min()), 0.0)
        with tempfile.TemporaryDirectory() as t:
            ta.write_raw(Path(t) / "i.raw", self.h)
            raw = np.frombuffer((Path(t) / "i.raw").read_bytes(), dtype=">u2").reshape(SIZE, SIZE)
        m = ta.platform_mask(self.east, self.north, ti.PLATFORM)
        self.assertEqual(len(np.unique(raw[m])), 1)

    def test_east_coast_parallel_to_runway_350_to_460_m(self):
        # 跑道 north 0..800: 东岸线离中线 (east=0) 350~460 m, 且沿跑道方向起伏小 (近似平行)
        shores = [self._shore_east(n) for n in np.arange(0.0, 801.0, 100.0)]
        self.assertGreaterEqual(min(shores), 350.0, shores)
        self.assertLessEqual(max(shores), 460.0, shores)
        self.assertLess(max(shores) - min(shores), 100.0, "海岸应大致平行于跑道: %s" % shores)

    def test_sea_east_of_coast_and_around_island(self):
        self.assertLess(float(self.h[self.n.size // 2, int(self.e.size * 0.9)]), ti.SEA_H, "东侧远处是海")
        for line in (self.h[0, :], self.h[-1, :], self.h[:, 0], self.h[:, -1]):
            self.assertAlmostEqual(float(line.min()), ti.SEABED_H, places=3)
            self.assertAlmostEqual(float(line.max()), ti.SEABED_H, places=3)
        water = float((self.h < ti.SEA_H).mean())
        self.assertTrue(0.40 < water < 0.75, "水域占比 %.2f" % water)
        # 岛的西、北、南也有岸线: 沿中线向北/向南、沿北向 400 m 线向西都能走到海
        col = int(np.argmin(np.abs(self.e - 0.0)))
        self.assertTrue(bool((self.h[:, col][self.n > 1000] < ti.SEA_H).any()), "北侧有海")
        self.assertTrue(bool((self.h[:, col][self.n < -1000] < ti.SEA_H).any()), "南侧有海")
        r = int(np.argmin(np.abs(self.n - 400.0)))
        self.assertTrue(bool((self.h[r, self.e < -1000] < ti.SEA_H).any()), "西侧有海")

    def test_beach_gentle_south_and_cliff_northeast(self):
        sl = ti.slope_deg(self.h, self.cell)
        coast = ti.east_coast_polyline(self.h, SIZE, WORLD)
        def band(pt, w=50.0):
            r = int(np.argmin(np.abs(self.n - pt[0])))
            c0 = int(np.argmin(np.abs(self.e - (pt[1] - w))))
            c1 = int(np.argmin(np.abs(self.e - (pt[1] + 10.0))))
            return r, c0, c1
        beach = [p for p in coast if -300.0 <= p[0] <= 800.0]
        cliff = [p for p in coast if p[0] >= 1150.0]
        self.assertGreater(len(beach), 5)
        self.assertGreater(len(cliff), 3)
        for p in beach:
            r, c0, c1 = band(p)
            self.assertLess(float(sl[r, c0:c1].max()), 12.0, "沙滩缓坡 north=%s" % p[0])
        tops = []
        for p in cliff:
            r, c0, c1 = band(p, 60.0)
            tops.append(float(self.h[r, c0]) - ti.SEA_H)
        self.assertGreater(max(tops), 20.0, "东北应有 20 m 以上海蚀崖: %s" % tops)

    def test_hills_exist_inland_but_calm_near_airport(self):
        rel = self.h - ti.SEA_H
        self.assertGreater(float(rel.max()), 60.0)
        self.assertLess(float(rel.max()), 130.0)
        m = ta.platform_mask(self.east, self.north, ti.PLATFORM)
        self.assertLess(float(np.percentile(rel[(np.abs(self.east - 0.0) < 300.0) & (self.north > -300.0) &
                                                (self.north < 1000.0) & ~m], 95)), 25.0, "机场周围地势平缓")

    def test_corridor_land_under_limit_surface_and_sea_untouched(self):
        core = np.abs(self.e) <= ti.CORRIDOR.half_width
        for rows, dist, deg in ((self.n < ti.PLATFORM.north[0] - 1.0, ti.PLATFORM.north[0] - self.n,
                                 ti.CORRIDOR.approach_deg),
                                (self.n > ti.PLATFORM.north[1] + 1.0, self.n - ti.PLATFORM.north[1],
                                 ti.CORRIDOR.departure_deg)):
            cap = ti.PLATFORM.height + dist[rows][:, None] * np.tan(np.radians(deg))
            self.assertLessEqual(float((self.h[rows][:, core] - cap).max()), 0.01)

    def test_glidepath_clearance_over_land(self):
        cols = np.abs(self.e) <= 30.0
        worst = 1e9
        for i, nr in enumerate(self.n):
            if nr > -100.0 or nr < -1400.0:
                continue
            glide = min(3.0 + np.tan(np.radians(3.5)) * (180.0 - nr), 60.0)
            worst = min(worst, glide - (float(self.h[i, cols].max()) - ti.PLATFORM.height))
        self.assertGreater(worst, 20.0, "最小间隙 %.1f m" % worst)

    def test_outputs_and_metadata(self):
        with tempfile.TemporaryDirectory() as t:
            meta, cj = ti.write_outputs(Path(t) / "i", self.h, self.d, SIZE, WORLD, SEED)
            for ext in (".raw", ".meta.json", ".coast.json", ".slope.npy", ".coastdist.npy", ".preview.png"):
                self.assertTrue((Path(t) / ("i" + ext)).exists(), ext)
            self.assertEqual(len((Path(t) / "i.raw").read_bytes()), SIZE * SIZE * 2)
            m = json.loads((Path(t) / "i.meta.json").read_text(encoding="utf-8"))
        self.assertEqual(m["biome"], "island")
        self.assertEqual(m["sea_level_m"], ti.SEA_H)
        self.assertEqual(m["airport_height_m"], ti.SEA_H + 9.0)
        self.assertEqual(m["edge_height_m"], ti.SEABED_H)
        self.assertEqual(len(m["far_ground_rgb"]), 3)
        self.assertEqual(m["size_px"], SIZE)
        self.assertGreater(len(cj["east_coast"]), 20)
        lh, pier = cj["features"]["lighthouse"], cj["features"]["pier"]
        r = int(np.argmin(np.abs(self.n - lh["north"])))
        c = int(np.argmin(np.abs(self.e - lh["east"])))
        self.assertGreater(float(self.h[r, c]) - ti.SEA_H, 15.0, "灯塔立在崖顶上")
        r = int(np.argmin(np.abs(self.n - pier["north"])))
        c = int(np.argmin(np.abs(self.e - pier["east"])))
        self.assertLess(abs(float(self.h[r, c]) - ti.SEA_H), 3.0, "码头起点在岸线上")


if __name__ == "__main__":
    unittest.main()
