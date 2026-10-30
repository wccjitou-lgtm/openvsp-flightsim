#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""test_workshop_aero.py — W3 气动计算测试。

纯逻辑层: 全列解析/summarize 回归 (TestUAV 真 .polar fixture)/render_case 烘焙。
端到端层: 真跑一次示例机 (2 alpha 点, ~30s; 无 vspscript 则跳过)。
"""
from __future__ import annotations

import json
import math
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "Tools"))

import workshop_aero as wa  # noqa: E402


class TestPolarParsing(unittest.TestCase):
    def test_parse_polar_full_testuav(self):
        rows = wa.parse_polar_full(ROOT / "Data" / "TestUAV_alpha.polar")
        self.assertGreaterEqual(len(rows), 5)
        self.assertIn("Cm", rows[0], "全列解析应含 CMytot (Cm)")
        self.assertIn("LD", rows[0])
        alphas = [r["aoa_deg"] for r in rows]
        self.assertTrue(all(a2 >= a1 for a1, a2 in zip(alphas, alphas[1:])), "应按 aoa 排序")

    def test_summarize_regression(self):
        rows = wa.parse_polar_full(ROOT / "Data" / "TestUAV_alpha.polar")
        panel = {"cref": 0.25, "xref": 1.4597}
        d = wa.summarize(rows, panel)
        self.assertEqual(len(d["alpha"]), len(d["CL"]))
        # TestUAV 已知静稳定 (SM≈5.8%MAC, meta.neutralPoint 交叉基准) → dCm/dCL < 0
        self.assertLess(d["dCm_dCL"], 0.0, "TestUAV 应静稳定 (dCm/dCL<0)")
        self.assertGreater(d["Xnp_m"], panel["xref"], "Xnp 应在 CG 之后 (静稳定)")
        # CLα 合理 (TestUAV ~0.07-0.09 /deg)
        cl = d["CL"]; a = d["alpha"]
        cla = (cl[-1] - cl[0]) / (a[-1] - a[0])
        self.assertGreater(cla, 0.03)
        self.assertLess(cla, 0.15)

    def test_render_case_no_placeholders_and_unique_locals(self):
        panel = dict(wa._DEFAULTS)
        panel["controls"] = [{"name": "Elevator", "deflection_deg": 5.0}]
        src = wa.render_case(Path("D:/x/y.vsp3"), panel)
        self.assertNotIn("{{", src)
        # 四组扫描向量局部名唯一 (AngelScript 同作用域重定义会编译失败)
        self.assertNotIn('if ( !ReadVSPFile', src, "ReadVSPFile 返回 void 不可判断 (R4)")
        self.assertIn('"ReCref", s4', src, "ReCref 起始名无 Start 后缀")
        self.assertIn('CtlNames.insertLast( "Elevator" )', src)
        # R08: 组寻址必须用带 _N 的显示组名 (FindParm 法), 增益修正保留
        self.assertIn('"ControlSurfaceGroup_" + gi', src)
        self.assertIn('SetParmValUpdate( p1, -1.0 );', src, "铁律3 增益修正应烘焙")


class TestDefaults(unittest.TestCase):
    def test_defaults_match_reference_image(self):
        """空工程默认值 = 参考图数值 (提示词 §6)。"""
        d = wa._DEFAULTS
        self.assertEqual(d["alpha"], [0.0, 10.0, 3])
        self.assertEqual(d["beta"], [0.0, 0.0, 1])
        self.assertEqual(d["mach"], [0.0, 0.0, 1])
        self.assertEqual(d["recref"], [1e7, 2e7, 1])
        self.assertEqual(d["wake"]["iter"], 3)
        self.assertEqual(d["wake"]["nodes"], 8)
        self.assertEqual(d["wake"]["relax"], 1.0)
        self.assertEqual(d["wake"]["freeze"], 10000)
        self.assertEqual(d["wake"]["implicit_start"], 0)
        self.assertEqual(d["cull"]["frac"], 0.030)
        self.assertEqual(d["ncpu"], 4)
        self.assertEqual(d["sref"], 100.0)
        self.assertEqual(d["bref"], 1.0)
        self.assertEqual(d["cref"], 1.0)
        self.assertEqual(d["xref"], 10.105)


class TestEndToEnd(unittest.TestCase):
    """真跑示例机 (慢 ~30s; 无 vspscript 或无示例机生成条件则跳过)。"""

    def test_example_one_click_run(self):
        try:
            vsp_script = wa.wc.vsproot() / "vspscript.exe"
        except FileNotFoundError:
            self.skipTest("OpenVSP 未配置；设置 VSPROOT 后运行集成测试")
        example = ROOT / "work" / "w3_example.vsp3"
        if not vsp_script.is_file() or not example.is_file():
            self.skipTest("缺少 vspscript 或示例机 vsp3 (由 worker 冒烟生成)")
        work = ROOT / "work" / "w3_test_run"
        panel = ROOT / "work" / "w3_panel.json"
        panel.parent.mkdir(parents=True, exist_ok=True)
        panel.write_text(json.dumps({"alpha": [0.0, 8.0, 2]}), encoding="utf-8")
        import subprocess
        proc = subprocess.run(
            [sys.executable, str(ROOT / "Tools" / "workshop_aero.py"),
             "--vsp", str(example), "--panel", str(panel), "--work", str(work)],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
            timeout=900)
        self.assertEqual(proc.returncode, 0, proc.stdout[-2000:])
        self.assertIn("[done]", proc.stdout)
        result = json.loads((work / "aero_result.json").read_text(encoding="utf-8"))
        self.assertEqual(len(result["alpha"]), 2)
        cl = result["CL"]
        self.assertGreater(cl[-1], cl[0], "CL 应随迎角增大")


if __name__ == "__main__":
    unittest.main()
