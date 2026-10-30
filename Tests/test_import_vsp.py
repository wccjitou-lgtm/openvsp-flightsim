#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""test_import_vsp.py — Phase 7a 编排器纯逻辑单测 (不跑 VSPAERO)。"""

import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "Tools"))

import import_vsp as iv


class RenderTests(unittest.TestCase):
    def test_sanitize_internal(self):
        self.assertEqual(iv.sanitize_internal("TestUAV"), "TestUAV")
        self.assertEqual(iv.sanitize_internal("My Plane-2"), "MyPlane-2")
        self.assertEqual(iv.sanitize_internal("弹簧刀"), iv.sanitize_internal("弹簧刀"))
        self.assertTrue(iv.sanitize_internal("弹簧刀").startswith("uav_"))

    def test_render_prep_bakes_params(self):
        s = iv.render_prep(Path("a.vsp3"), "MyPlane", Path("/w"), True, 16, "thin")
        self.assertIn('const string NAME = "MyPlane";', s)
        self.assertIn("const bool   AUTO_CONTROLS = true;", s)
        self.assertIn("const int    TESS = 16;", s)
        self.assertIn("SetSetFlag( ids[i], 3, true );", s)
        self.assertIn('SetVSP3FileName( WORK + "/" + NAME + "_normalized.vsp3" );', s)
        self.assertIn('FindParm( id, "Span", "XSec_1" )', s)   # 参数顺序回归
        self.assertNotIn("{{", s)

    def test_render_prep_thick_mode(self):
        s = iv.render_prep(Path("a.vsp3"), "B", Path("/w"), False, 0, "thick")
        self.assertIn("SetSetFlag( ids[i], 3, true );", s)
        self.assertIn("const int    TESS = 0;", s)
        self.assertIn("[PREP_FAIL] no SS_Control", s)

    def test_render_case_thin_and_thick(self):
        s = iv.render_case("X", Path("/w"), 2.0, 0.06, 5, -6, 6, 5, -4, 10, 7,
                           "thin", "thin")
        self.assertIn("const int    GSET = -1;", s)    # 薄面: 无厚面集合
        self.assertIn("const int    TSET = 3;", s)     # 薄面: 用户集3
        self.assertIn("const int    CGSET = 4;", s)    # Mass set retains ignored geometry.
        self.assertIn("const double BETA_END     = 6.0;", s)
        self.assertIn("const double ALPHA_END    = 10.0;", s)
        self.assertNotIn("{{", s)
        t = iv.render_case("X", Path("/w"), 2.0, 0.06, 5, -6, 6, 5, -4, 10, 7,
                           "thick", "thick")
        self.assertIn("const int    GSET = 3;", s := t)
        self.assertIn("const int    TSET = -1;", s)
        self.assertIn("const int    CGSET = 0;", s)

    def test_render_fixup(self):
        s = iv.render_fixup("X", Path("/w"), "Backwing", "Elevator")
        self.assertIn('const string GBASE = "Backwing";', s)
        self.assertIn('const string CTL  = "Elevator";', s)
        self.assertIn("relocated", s)
        self.assertNotIn("{{", s)

    def test_parse_fail_control(self):
        log = ("Could not find start of control surface D:/x/mthin.csf data "
               "in csf for control surface: mthin_normalizedBackwing_S_Surf0_Elevator \n")
        self.assertEqual(iv.parse_fail_control(log, "mthin_normalized"), ("Backwing", "Elevator"))
        self.assertIsNone(iv.parse_fail_control("random log line", "mthin_normalized"))

    def test_render_rejects_leftover_placeholder(self):
        with self.assertRaises(ValueError):
            iv.render("const A = {{A}};", {})


class ParseRangeTests(unittest.TestCase):
    def test_parse_range(self):
        self.assertEqual(iv.parse_range("-6,6,5"), (-6.0, 6.0, 5))
        self.assertEqual(iv.parse_range(" 0 , 10 , 3 "), (0.0, 10.0, 3))

    def test_parse_range_rejects_bad(self):
        import argparse
        with self.assertRaises(argparse.ArgumentTypeError):
            iv.parse_range("1,2")


class ValidateConfigTests(unittest.TestCase):
    def _write(self, tmp, cfg):
        p = Path(tmp) / "AircraftConfig.json"
        p.write_text(json.dumps(cfg), encoding="utf-8")
        return p

    GOOD = json.loads((Path(__file__).resolve().parent.parent /
                       "Data/AircraftConfig.json").read_text(encoding="utf-8"))

    def test_good_config_passes(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.assertEqual(iv.validate_config(self._write(tmp, self.GOOD)), [])

    def test_zero_control_fails_with_surface_named(self):
        cfg = json.loads(json.dumps(self.GOOD))
        cfg["aero"]["controls"]["elevator"]["Cmde"] = 0.0
        with tempfile.TemporaryDirectory() as tmp:
            problems = iv.validate_config(self._write(tmp, cfg))
        self.assertTrue(any("elevator.Cmde" in p for p in problems), problems)

    def test_missing_aero_key_fails(self):
        cfg = json.loads(json.dumps(self.GOOD))
        del cfg["aero"]["CLalpha"]
        with tempfile.TemporaryDirectory() as tmp:
            problems = iv.validate_config(self._write(tmp, cfg))
        self.assertTrue(any("CLalpha" in p for p in problems), problems)

    def test_nan_propagates_as_problem(self):
        cfg = json.loads(json.dumps(self.GOOD))
        cfg["aero"]["Cmalpha"] = float("nan")
        with tempfile.TemporaryDirectory() as tmp:
            problems = iv.validate_config(self._write(tmp, cfg))
        self.assertTrue(any("Cmalpha" in p for p in problems), problems)


class MarkerTests(unittest.TestCase):
    def test_extract_marker(self):
        log = "noise\n[MASS] 12.34\n[CG] 1.0 0 0.05\n"
        self.assertEqual(iv.extract_marker(log, "[MASS]"), "12.34")
        self.assertEqual(iv.extract_marker(log, "[CG]"), "1.0 0 0.05")
        self.assertIsNone(iv.extract_marker(log, "[NOPE]"))


if __name__ == "__main__":
    unittest.main()
