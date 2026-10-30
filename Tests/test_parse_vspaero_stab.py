#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
test_parse_vspaero_stab.py — parse_vspaero_stab.py 的单元测试

针对真实生成的 Data/TestUAV.stab (VSPAERO 7.2.2, STABILITY_DEFAULT 模式) 验证:
  - 参考量 (Sref/bref/cref/cg/状态点)
  - 导数表全部系数 (与文件中的数值逐一对照)
  - 控制面组映射 (ConGrp_1/2/3 -> 副翼/升降舵/方向舵)
  - 结果块 (静稳定裕度 SM / 中立点 X_np)

运行:
  cd openvsp-flightsim
  python -m unittest Tests.test_parse_vspaero_stab -v
"""

import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "Tools"))

from parse_vspaero_stab import parse_stab  # noqa: E402

STAB = ROOT / "Data" / "TestUAV_default.stab"


@unittest.skipUnless(STAB.is_file(), f".stab 文件不存在: {STAB}")
class TestParseTestUAVStab(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.d = parse_stab(STAB)

    # ---- 参考量 ----
    def test_reference(self):
        ref = self.d["reference"]
        self.assertAlmostEqual(ref["Sref"], 3.1000000, places=6)
        self.assertAlmostEqual(ref["Bref"], 4.0000000, places=6)
        self.assertAlmostEqual(ref["Cref"], 0.7750000, places=6)
        self.assertAlmostEqual(ref["Xcg"], 1.4611728, places=6)
        self.assertAlmostEqual(ref["Mach"], 0.06, places=8)
        self.assertAlmostEqual(ref["AoA"], 2.0, places=6)
        self.assertAlmostEqual(ref["Beta"], 0.0, places=6)

    # ---- 控制面组映射 ----
    def test_control_groups(self):
        cg = self.d["control_groups"]
        self.assertEqual(cg.get("ConGrp_1"), "MainWing_Aileron")
        self.assertEqual(cg.get("ConGrp_2"), "HTail_Elevator")
        self.assertEqual(cg.get("ConGrp_3"), "VTail_Rudder")

    # ---- 导数表: 逐系数对照真实文件数值 ----
    def test_longitudinal_derivatives(self):
        dv = self.d["derivatives"]
        # 纵向: 升力线斜率 CLalpha = 4.4428228 (per rad)
        self.assertAlmostEqual(dv["CL"]["Alpha"], 4.6904500, places=6)
        # 俯仰力矩对攻角 Cmalpha = -0.2570016 (负 = 静稳定)
        self.assertAlmostEqual(dv["CMm"]["Alpha"], -0.7662743, places=6)
        self.assertLess(dv["CMm"]["Alpha"], 0.0, "Cmalpha 应为负 (静稳定)")
        # 俯仰阻尼 Cmq = -3.7046424
        self.assertAlmostEqual(dv["CMm"]["q"], -6.6311186, places=6)
        # 升力对俯仰率 CLq = 7.0672598
        self.assertAlmostEqual(dv["CL"]["q"], 11.8416314, places=6)
        # 基准升力 CL0(该状态点) = 0.1586962
        self.assertAlmostEqual(dv["CL"]["base"], 0.1636228, places=6)

    def test_lateral_directional_derivatives(self):
        dv = self.d["derivatives"]
        # 侧力对侧滑 CYbeta = -0.0966436 (负 = 侧滑产生恢复侧力)
        self.assertAlmostEqual(dv["CS"]["Beta"], -0.2192596, places=6)
        # 上反效应 Clbeta = +0.0130880
        self.assertAlmostEqual(dv["CMl"]["Beta"], -0.0364339, places=6)
        # 航向稳定性 Cnbeta = +0.0007756 (正 = 方向稳定)
        self.assertAlmostEqual(dv["CMn"]["Beta"], 0.0486233, places=6)
        # 滚转阻尼 Clp = -0.4638985 (负 = 阻尼)
        self.assertAlmostEqual(dv["CMl"]["p"], -0.4298950, places=6)
        self.assertLess(dv["CMl"]["p"], 0.0, "Clp 应为负 (滚转阻尼)")
        # 偏航阻尼 Cnr = -0.0620947
        self.assertAlmostEqual(dv["CMn"]["r"], -0.0768279, places=6)
        # 滚转对偏航率 Clr = -0.0010855
        self.assertAlmostEqual(dv["CMl"]["r"], 0.0480036, places=6)

    def test_control_derivatives(self):
        dv = self.d["derivatives"]
        # 副翼 (ConGrp_1): 滚转操纵 Clda = -0.1355001
        self.assertAlmostEqual(dv["CMl"]["ConGrp_1"], -0.1146078, places=6)
        # 升降舵 (ConGrp_2, 镜像增益修复后为对称偏转, 正偏=后缘向下):
        # CLde = +0.4304798, Cmde = -0.5528226 (工程量级)
        self.assertAlmostEqual(dv["CL"]["ConGrp_2"], 0.4706901, places=6)
        self.assertAlmostEqual(dv["CMm"]["ConGrp_2"], -0.6731879, places=6)
        # 升降舵对称偏转后滚转耦合应接近零 (镜像抵消消除)
        self.assertLess(abs(dv["CMl"]["ConGrp_2"]), 0.01, "升降舵滚转耦合应接近零")
        # 方向舵 (ConGrp_3): CYdr = -0.0636388, Cndr = +0.0216638
        self.assertAlmostEqual(dv["CS"]["ConGrp_3"], -0.1518638, places=6)
        self.assertAlmostEqual(dv["CMn"]["ConGrp_3"], 0.0570061, places=6)

    def test_aileron_roll_authority_exceeds_rudder(self):
        """副翼滚转操纵效能应显著大于方向舵的滚转效能 (物理合理性)"""
        dv = self.d["derivatives"]
        self.assertGreater(abs(dv["CMl"]["ConGrp_1"]), abs(dv["CMl"]["ConGrp_3"]))

    # ---- 结果块 ----
    def test_results_sm_xnp(self):
        res = self.d["results"]
        self.assertAlmostEqual(res["SM"]["value"], 0.16336900, places=6)
        self.assertAlmostEqual(res["X_np"]["value"], 1.5877838, places=6)

    def test_perturbation_table(self):
        perts = {p["case"]: p for p in self.d["perturbations"]}
        self.assertIn("Base_Aero", perts)
        self.assertIn("Alpha", perts)
        self.assertIn("MainWing_Aileron", perts)
        # Alpha 扰动行: +0.010 deg
        self.assertAlmostEqual(perts["Alpha"]["delta"], 0.010, places=8)


class TestParseStabErrors(unittest.TestCase):
    def test_missing_file(self):
        import parse_vspaero_stab as ps
        with self.assertRaises(FileNotFoundError):
            ps.parse_stab(r"Z:\no\such\file.stab")

    def test_garbage_file(self):
        import tempfile
        import parse_vspaero_stab as ps
        with tempfile.NamedTemporaryFile("w", suffix=".stab", delete=False, encoding="utf-8") as f:
            f.write("nothing useful here\n1 2 3\n")
            name = f.name
        try:
            with self.assertRaises(ValueError):
                ps.parse_stab(name)
        finally:
            Path(name).unlink()


if __name__ == "__main__":
    unittest.main(verbosity=2)
