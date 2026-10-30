#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
test_parse_massprops.py — parse_massprops.py 的单元测试

Phase 0 验收标准 (开发提示词 3.4 节):
  针对已有的 Unnamed_MassProps.txt 验证解析器能正确提取已知值:
    Mass = 10.4577, Ixx = 27.237, ... (全部 11 个字段)

运行:
  cd openvsp-flightsim
  python -m unittest Tests.test_parse_massprops -v
"""

import sys
import unittest
from pathlib import Path

# 让测试既能从项目根目录跑, 也能从 Tests/ 目录跑
ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "Tools"))

from parse_massprops import parse_massprops  # noqa: E402

# 由 Tools/regen_massprops_fixture.as 生成的固定回归夹具。
SAMPLE = ROOT / "Tests" / "Fixtures" / "MassProps" / "Unnamed_MassProps.txt"

# ==== 已知值 (直接来自 Unnamed_MassProps.txt 的 Totals 行) ====
KNOWN = {
    "mass_kg": 1.506900802654146e01,
    "cg_m": [3.688671830248008e00, 6.968602534388641e-10, -1.053147453445080e-10],
    "Ixx": 1.139404050615583e02,
    "Iyy": 4.930124488766995e01,
    "Izz": 1.619167810967818e02,
    "Ixy": -2.425447638734984e-02,
    "Ixz": -1.081063241814184e-03,
    "Iyz": 2.663418680477260e-02,
    "volume_m3": 1.506900802654146e01,
}


@unittest.skipUnless(SAMPLE.is_file(), f"样例文件不存在: {SAMPLE}")
class TestParseUnnamedMassProps(unittest.TestCase):
    """Phase 0 验收: 解析 Unnamed_MassProps.txt 的已知值。"""

    @classmethod
    def setUpClass(cls):
        cls.result = parse_massprops(SAMPLE)

    def test_mass(self):
        """总质量 = 15.069 kg (夹具重生成后的 Totals 行值)"""
        self.assertAlmostEqual(self.result["totals"]["mass_kg"], KNOWN["mass_kg"], places=10)

    def test_cg(self):
        """重心三分量"""
        for got, want in zip(self.result["totals"]["cg_m"], KNOWN["cg_m"]):
            self.assertAlmostEqual(got, want, places=12)

    def test_inertia_diagonal(self):
        """转动惯量对角项 Ixx=113.94, Iyy=49.30, Izz=161.92"""
        inertia = self.result["totals"]["inertia_kgm2"]
        for key in ("Ixx", "Iyy", "Izz"):
            self.assertAlmostEqual(inertia[key], KNOWN[key], places=10,
                                   msg=f"{key} 不匹配")

    def test_inertia_cross(self):
        """惯量积 Ixy/Ixz/Iyz"""
        inertia = self.result["totals"]["inertia_kgm2"]
        for key in ("Ixy", "Ixz", "Iyz"):
            self.assertAlmostEqual(inertia[key], KNOWN[key], places=14,
                                   msg=f"{key} 不匹配")

    def test_volume(self):
        """总体积"""
        self.assertAlmostEqual(self.result["totals"]["volume_m3"], KNOWN["volume_m3"], places=10)

    def test_num_comps(self):
        """部件数 = 2 (PodGeom + WingGeom)"""
        self.assertEqual(self.result["num_comps"], 2)
        self.assertEqual(set(self.result["components"]), {"PodGeom", "WingGeom"})

    def test_component_rows_consistent(self):
        """部件行质量之和 == Totals 质量 (表格内部一致性)"""
        s = sum(c["Mass"] for c in self.result["components"].values())
        self.assertAlmostEqual(s, KNOWN["mass_kg"], places=10)

    def test_summary_block_matches_table(self):
        """文件顶部总计块与 Totals 行一致 (交叉校验)"""
        sb = self.result["summary_block"]
        self.assertAlmostEqual(sb["total_mass"], KNOWN["mass_kg"], places=10)
        for got, want in zip(sb["cg"], KNOWN["cg_m"]):
            self.assertAlmostEqual(got, want, places=12)
        self.assertAlmostEqual(sb["inertia_diag"][0], KNOWN["Ixx"], places=10)

    def test_units_recorded(self):
        """单位制被记录"""
        self.assertEqual(self.result["units"]["mass"], "kg")
        self.assertEqual(self.result["units"]["length"], "m")


class TestParseMassPropsErrors(unittest.TestCase):
    """异常路径测试 (不依赖样例文件)"""

    def test_missing_file(self):
        import parse_massprops as pm
        with self.assertRaises(FileNotFoundError):
            pm.parse_massprops(r"Z:\no\such\file.txt")

    def test_garbage_file(self):
        import tempfile
        import parse_massprops as pm
        with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False, encoding="utf-8") as f:
            f.write("完全不是 MassProps 格式的内容\n1 2 3\n")
            name = f.name
        try:
            with self.assertRaises(ValueError):
                pm.parse_massprops(name)
        finally:
            Path(name).unlink()


class TestComponentTableNameEdgeCases(unittest.TestCase):
    """U7: 部件名含空格 / 同名多行的解析 (换机质量烘焙依赖)。"""

    MASSPROPS_BODY = (
        "2 Num Comps\n"
        "1.5 1.0 0.0 0.0 Total Mass\n"
        "0.5 0.0 0.0 Center of Gravity\n"
        "0.1 0.2 0.3 Ixx, Iyy, Izz\n"
        "0.0 0.0 0.0 Ixy, Ixz, Iyz\n"
        "0.6 Volume\n"
        "Name Mass cgX cgY cgZ Ixx Iyy Izz Ixy Ixz Iyz Volume\n"
        "{rows}"
        "Name Mass cgX cgY cgZ Ixx Iyy Izz Ixy Ixz Iyz Volume\n"
        "Totals 1.5 1.0 0.0 0.0 0.1 0.2 0.3 0.0 0.0 0.0 0.6\n"
    )

    def _parse(self, rows: str):
        import tempfile
        import parse_massprops as pm
        with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False, encoding="utf-8") as f:
            f.write(self.MASSPROPS_BODY.format(rows=rows))
            name = f.name
        try:
            return pm.parse_massprops(name)["components"]
        finally:
            Path(name).unlink()

    def test_name_with_spaces(self):
        """部件名含空格 (如 "main wing L"): 行尾 11 数值列右对齐切分, 名不再截断整表。"""
        comps = self._parse(
            "main wing L 0.7 1.0 0.0 0.0 0.05 0.05 0.05 0 0 0 0.3\n"
            "PodGeom 0.8 1.0 0.0 0.0 0.05 0.05 0.05 0 0 0 0.3\n")
        self.assertIn("main wing L", comps)
        self.assertIn("PodGeom", comps)   # 空格名后的行不再被丢
        self.assertAlmostEqual(comps["main wing L"]["Mass"], 0.7, places=12)

    def test_duplicate_names_summed(self):
        """同名两行 (对称镜像片): 质量求和 — 烘焙 map 值即同名合计, worker 按份数分摊。"""
        comps = self._parse(
            "TwinPart 0.5 1.0 0.0 0.0 0.01 0.01 0.01 0 0 0 0.2\n"
            "TwinPart 0.5 3.0 0.0 0.0 0.01 0.01 0.01 0 0 0 0.2\n"
            "PodGeom 0.5 1.0 0.0 0.0 0.01 0.01 0.01 0 0 0 0.2\n")
        self.assertAlmostEqual(comps["TwinPart"]["Mass"], 1.0, places=12)
        self.assertAlmostEqual(comps["TwinPart"]["cgX"], 2.0, places=12)  # 质量加权

    def test_name_ending_with_number(self):
        """名以数字结尾 (如 "机翼 2"): 数字保留在名内, 不被吸进数值列。"""
        comps = self._parse(
            "机翼 2 0.7 1.0 0.0 0.0 0.05 0.05 0.05 0 0 0 0.3\n"
            "PodGeom 0.8 1.0 0.0 0.0 0.05 0.05 0.05 0 0 0 0.3\n")
        self.assertIn("机翼 2", comps)
        self.assertAlmostEqual(comps["机翼 2"]["Mass"], 0.7, places=12)


if __name__ == "__main__":
    unittest.main(verbosity=2)
