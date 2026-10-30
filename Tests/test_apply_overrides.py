#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""test_apply_overrides.py — Phase 8a 生成阶段覆盖单测。"""

import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "Tools"))

import apply_overrides as ao


def base_cfg():
    return {
        "name": "t",
        "reference": {"Sref_m2": 15.97, "bref_m": 11.5, "cref_m": 1.4,
                      "cg_m": [4.635, 0.0, 0.342]},
        "mass": {"mass_kg": 16.678,
                 "inertia_kgm2": {"Ixx": 50.0, "Iyy": 120.0, "Izz": 150.0,
                                  "Ixy": 0.0, "Ixz": 0.0, "Iyz": 0.0}},
        "aero": {"CL0": 0.39, "CLalpha": 4.66, "Cm0": 0.02, "Cmalpha": 0.233,
                 "controls": {"elevator": {"CLde": -0.0003, "Cmde": 0.012}}},
        "gear": {"wheels": []},
    }


class BallastTests(unittest.TestCase):
    def test_ballast_moves_cg_forward_and_stabilizes(self):
        cfg = base_cfg()
        ao.apply_ballast(cfg, 3.0, [3.635, 0.0, 0.342])   # CG 前 1m 加 3kg
        self.assertAlmostEqual(cfg["mass"]["mass_kg"], 19.678, places=6)
        # 新 CG x = (16.678*4.635 + 3*3.635)/19.678 = 4.4826
        self.assertAlmostEqual(cfg["reference"]["cg_m"][0], 4.4826, places=3)
        # dx_ov = -0.1524 → Cmalpha += dx*CLa/cref = -0.1524*4.66/1.4 = -0.507
        self.assertAlmostEqual(cfg["aero"]["Cmalpha"], 0.233 - 0.507, places=2)
        self.assertLess(cfg["aero"]["Cmalpha"], 0, "3kg 前配重应转为静稳定")

    def test_ballast_increases_inertia(self):
        cfg = base_cfg()
        ao.apply_ballast(cfg, 3.0, [3.635, 0.0, 0.342])
        self.assertGreater(cfg["mass"]["inertia_kgm2"]["Iyy"], 120.0)

    def test_cg_override_shifts_derivatives(self):
        cfg = base_cfg()
        ao.apply_cg_override(cfg, [4.435, 0.0, 0.342])    # 前移 0.2m
        self.assertAlmostEqual(cfg["aero"]["Cmalpha"], 0.233 - 0.2 * 4.66 / 1.4,
                               places=6)
        self.assertAlmostEqual(cfg["reference"]["cg_m"][0], 4.435, places=6)


class GearTests(unittest.TestCase):
    def test_auto_gear_body_axes(self):
        cfg = base_cfg()
        bbox = {"min": [0.0, -6.4, -0.9], "max": [11.0, 6.4, 1.4]}   # ovsp: x0=头
        ao.auto_gear(cfg, bbox)
        w = {x["name"]: x["position_m"] for x in cfg["gear"]["wheels"]}
        # 体轴 x 前 = CG_ov - x_ov: nose 在机头附近 (ovsp x≈1.32) → 体轴 x≈+3.3
        self.assertAlmostEqual(w["nose"][0], 4.635 - 1.32, places=2)
        # 主轮略后于重心 (ovsp x=4.735) → 体轴 x = -0.1
        self.assertAlmostEqual(w["mainL"][0], -0.10, places=2)
        self.assertGreater(w["mainL"][1], 0)    # 左主轮 y>0 (体轴 y 右)
        self.assertLess(w["mainR"][1], 0)
        # 轮子在包围盒底再留净空 (体轴 z 下为正): cg_z - z0 + 净空 = 0.342+0.9+0.15
        self.assertAlmostEqual(w["nose"][2], 0.342 - (-0.9) + ao.GEAR_CLEARANCE_M, places=2)
        # 刚度恒定静沉陷 15mm (U8): k_main = m·g/(0.015×2.6), 与质量成正比,
        # 固有频率 √(g/sag) 与质量无关
        k = cfg["gear"]["wheels"][1]["kN_per_m"]
        self.assertAlmostEqual(k, 16.678 * 9.81 / (0.015 * 2.6), places=1)
        # 静沉陷校验: 三轮合成 m·g/k_total = 15mm
        kw = sum(w["kN_per_m"] for w in cfg["gear"]["wheels"])
        self.assertAlmostEqual(16.678 * 9.81 / kw, 0.015, places=4)


class BboxTests(unittest.TestCase):
    def test_parse_stl_bbox(self):
        import tempfile
        with tempfile.NamedTemporaryFile("w", suffix=".stl", delete=False,
                                         encoding="utf-8") as f:
            f.write("solid x\n")
            f.write("vertex 1.0 2.0 3.0\nvertex -1.5 0.0 0.5\nvertex 1.0 2.0 3.0\n")
            f.write("vertex 0.0 -2.5 1.0\n")
            name = f.name
        b = ao.parse_stl_bbox(Path(name))
        self.assertEqual(b["min"], [-1.5, -2.5, 0.5])
        self.assertEqual(b["max"], [1.0, 2.0, 3.0])
        Path(name).unlink()


class WeightTests(unittest.TestCase):
    def _mp(self, tmp):
        p = Path(tmp) / "mp.txt"
        p.write_text("""...Mass Properties...
1 Num Comps

Name Mass cgX cgY cgZ Ixx Iyy Izz Ixy Ixz Iyz Volume
WingGeom 2.000 5.0 0.0 0.3 1 1 1 0 0 0 0.02
Body 14.678 4.6 0.0 0.35 40 80 90 0 0 0 0.01
Totals 16.678 4.648 0.0 0.344 41 81 91 0 0 0 0.03
""", encoding="utf-8")
        return p

    def test_component_weights_recombine(self):
        import tempfile
        with tempfile.TemporaryDirectory() as tmp:
            cfg = base_cfg()
            ao.apply_component_weights(cfg, self._mp(tmp), {"WingGeom": 8.0})
            # 总重 = 8 + 14.678 = 22.678
            self.assertAlmostEqual(cfg["mass"]["mass_kg"], 22.678, places=3)
            # 新 CG x = (8*5.0 + 14.678*4.6)/22.678 = 4.741
            self.assertAlmostEqual(cfg["reference"]["cg_m"][0], 4.741, places=2)

    def test_parse_component_rows_ordered(self):
        import tempfile
        with tempfile.TemporaryDirectory() as tmp:
            rows = ao.parse_component_rows(self._mp(tmp))
            self.assertEqual([r["name"] for r in rows], ["WingGeom", "Body"])
            self.assertAlmostEqual(rows[0]["mass"], 2.0)
            self.assertAlmostEqual(rows[1]["cg"][0], 4.6)

    def test_unknown_component_weight_is_not_silently_ignored(self):
        import tempfile
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaisesRegex(ValueError, "missing"):
                ao.apply_component_weights(base_cfg(), self._mp(tmp), {"missing": 1.0})

    def test_invalid_component_weights_are_rejected(self):
        import tempfile
        with tempfile.TemporaryDirectory() as tmp:
            for weight in (-1.0, 0.0, float("nan"), float("inf")):
                with self.subTest(weight=weight), self.assertRaises(ValueError):
                    ao.apply_component_weights(base_cfg(), self._mp(tmp), {"WingGeom": weight})


if __name__ == "__main__":
    unittest.main()
