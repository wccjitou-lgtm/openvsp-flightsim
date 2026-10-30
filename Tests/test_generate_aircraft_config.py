"""Control-axis regressions using solver fixtures and the parser boundary only."""

import contextlib
import copy
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "Tools"))

import generate_aircraft_config as generator
from parse_vspaero_stab import parse_stab

DATA = ROOT / "Data"
STAB = DATA / "TestUAV_default.stab"
MASS = DATA / "TestUAV_MassProps.txt"


def assert_equal_float(actual, expected, path="config", rel=1e-12):
    """递归相等断言: dict 逐键 / list 逐位 / float 按相对容差 (跨解释器 libm
    末位差异), 其余类型精确相等。返回首个失配点便于定位。"""
    import math
    if isinstance(expected, dict):
        assert isinstance(actual, dict), f"{path}: 期望 dict 实为 {type(actual).__name__}"
        assert set(actual) == set(expected), \
            f"{path}: 键差 {set(expected) ^ set(actual)}"
        for k in expected:
            assert_equal_float(actual[k], expected[k], f"{path}.{k}", rel)
    elif isinstance(expected, list):
        assert isinstance(actual, list) and len(actual) == len(expected), \
            f"{path}: 列表长度 {len(actual) if isinstance(actual, list) else '?'} != {len(expected)}"
        for i, (a, e) in enumerate(zip(actual, expected)):
            assert_equal_float(a, e, f"{path}[{i}]", rel)
    elif isinstance(expected, float) or isinstance(expected, int) and isinstance(actual, float):
        assert isinstance(actual, (int, float)), f"{path}: 期望数值实为 {actual!r}"
        denom = max(1.0, abs(float(expected)))
        assert abs(float(actual) - float(expected)) / denom <= rel, \
            f"{path}: {actual!r} != {expected!r} (超相对容差 {rel:g})"
    else:
        assert actual == expected, f"{path}: {actual!r} != {expected!r}"


class TestControlMapping(unittest.TestCase):
    def setUp(self):
        self.stab = parse_stab(STAB)

    def generate(self):
        with patch.object(generator, "parse_stab", return_value=self.stab):
            return generator.generate("TestUAV", MASS, STAB, None, None, None,
                                      "Meshes/TestUAV.stl")

    def add_group(self, key, name, source, values=None):
        self.stab["control_groups"][key] = name
        self.stab["derivative_columns"].append(key)
        for row in self.stab["derivatives"].values():
            row[key] = row[source]
        for coef, value in (values or {}).items():
            self.stab["derivatives"][coef][key] = value

    def test_tail_is_not_an_aileron(self):
        groups = {"ConGrp_2": "HTail_Elevator", "ConGrp_3": "VTail_Rudder"}
        self.assertIsNone(generator.find_control_group(groups, "aileron"))
        groups["ConGrp_1"] = "MainWing_Aileron"
        self.assertEqual(generator.find_control_group(groups, "aileron"), "ConGrp_1")

    def test_explicit_axis_names_and_aliases(self):
        for axis, names in {
            "aileron": ("LeftWing_Aileron", "RightWingAileron2", "wing-ail", "AIL"),
            "elevator": ("HTail_Elevator", "LeftWingElevator", "Elev_1", "ELEVATOR"),
            "rudder": ("VTail_Rudder", "FinRudder2", "rud", "RUDDER"),
        }.items():
            for name in names:
                with self.subTest(axis=axis, name=name):
                    self.assertEqual(
                        generator.find_control_group({"ConGrp_1": name}, axis),
                        "ConGrp_1",
                    )

    def test_embedded_substrings_do_not_define_axes(self):
        for name in ("Tail", "TrailingEdge", "Prevailing", "Elevation", "Rudderless"):
            for axis in ("aileron", "elevator", "rudder"):
                with self.subTest(name=name, axis=axis):
                    self.assertIsNone(
                        generator.find_control_group({"ConGrp_1": name}, axis)
                    )

    def test_single_group_helper_rejects_multiple_matches(self):
        with self.assertRaisesRegex(ValueError, "multiple.*aileron"):
            generator.find_control_group(
                {"ConGrp_1": "LeftWing_Aileron", "ConGrp_2": "RightWing_Aileron"},
                "aileron",
            )

    def test_reordered_groups_preserve_axis_values(self):
        self.stab["control_groups"] = dict(reversed(list(self.stab["control_groups"].items())))
        controls = self.generate()["aero"]["controls"]
        self.assertEqual(controls["aileron"]["Clda"], -0.1146078)
        self.assertEqual(controls["elevator"]["Cmde"], -0.6731879)

    def test_multiple_groups_sum_signed_derivatives_without_reapplying_gains(self):
        self.stab["control_groups"]["ConGrp_1"] = "LeftWing_Aileron"
        self.stab["control_groups"]["ConGrp_2"] = "LeftWing_Elevator"
        self.stab["derivatives"]["CMl"]["ConGrp_1"] = -0.3
        self.stab["derivatives"]["CS"]["ConGrp_1"] = 0.02
        self.stab["derivatives"]["CMn"]["ConGrp_1"] = -0.04
        self.add_group("ConGrp_4", "RightWing_Aileron", "ConGrp_1",
                       {"CMl": -0.2, "CS": -0.03, "CMn": 0.01})
        self.add_group("ConGrp_5", "RightWing_Elevator", "ConGrp_2",
                       {"CL": 0.25, "CMm": -0.35})
        self.add_group("ConGrp_6", "SecondFin_Rudder", "ConGrp_3")
        controls = self.generate()["aero"]["controls"]
        self.assertAlmostEqual(controls["aileron"]["Clda"], -0.5)
        self.assertAlmostEqual(controls["aileron"]["CYda"], -0.01)
        self.assertAlmostEqual(controls["aileron"]["Cnda"], -0.03)
        self.assertAlmostEqual(controls["elevator"]["CLde"], 0.7206901)
        self.assertAlmostEqual(controls["elevator"]["Cmde"], -1.0231879)
        self.assertAlmostEqual(controls["rudder"]["CYdr"], -0.3037276)
        self.assertAlmostEqual(controls["rudder"]["Cldr"], -0.02362900)
        self.assertAlmostEqual(controls["rudder"]["Cndr"], 0.1140122)

    def test_opposing_contributions_keep_their_signs(self):
        self.stab["derivatives"]["CMl"]["ConGrp_1"] = -0.3
        self.add_group("ConGrp_4", "RightWing_Aileron", "ConGrp_1", {"CMl": 0.1})
        self.assertAlmostEqual(self.generate()["aero"]["controls"]["aileron"]["Clda"], -0.2)

    def test_missing_axis_fails_instead_of_returning_partial_config(self):
        for key, axis in (("ConGrp_1", "aileron"), ("ConGrp_2", "elevator"),
                          ("ConGrp_3", "rudder")):
            with self.subTest(axis=axis):
                self.setUp()
                self.stab["control_groups"][key] = "Flap"
                with self.assertRaisesRegex(ValueError, f"Missing.*{axis}"):
                    self.generate()

    def test_ambiguous_axis_name_fails(self):
        for name in ("Wing_Elevator_Aileron", "Left_Elevon", "Fin_Rudder_Elevator"):
            with self.subTest(name=name):
                self.stab["control_groups"]["ConGrp_1"] = name
                with self.assertRaisesRegex(ValueError, "Ambiguous.*ConGrp_1"):
                    self.generate()

    def test_explicit_elevon_axis_labels_are_not_ambiguous(self):
        self.stab["control_groups"]["ConGrp_1"] = "LeftElevon_Aileron"
        self.stab["control_groups"]["ConGrp_2"] = "LeftElevon_Elevator"
        self.assertEqual(self.generate()["aero"]["controls"]["aileron"]["Clda"], -0.1146078)

    def test_incomplete_secondary_group_derivatives_fail(self):
        for axis, source, coefs in (
            ("aileron", "ConGrp_1", ("CS", "CMl", "CMn")),
            ("elevator", "ConGrp_2", ("CL", "CMm")),
            ("rudder", "ConGrp_3", ("CS", "CMl", "CMn")),
        ):
            for coef in coefs:
                with self.subTest(axis=axis, coef=coef):
                    self.setUp()
                    self.add_group("ConGrp_4", f"Second_{axis}", source)
                    del self.stab["derivatives"][coef]["ConGrp_4"]
                    with self.assertRaisesRegex(ValueError, f"{axis}.*ConGrp_4.*{coef}"):
                        self.generate()

    def test_lateral_controls_do_not_require_unused_lift_derivative(self):
        del self.stab["derivatives"]["CL"]["ConGrp_1"]
        self.assertEqual(self.generate()["aero"]["controls"]["aileron"]["Clda"], -0.1146078)

    def test_unmapped_derivative_column_fails(self):
        self.add_group("ConGrp_4", "Another_Aileron", "ConGrp_1")
        del self.stab["control_groups"]["ConGrp_4"]
        with self.assertRaisesRegex(ValueError, "Unmapped.*ConGrp_4"):
            self.generate()

    def test_nonfinite_derivatives_fail(self):
        for value in (float("nan"), float("inf"), -float("inf"), None):
            with self.subTest(value=value):
                self.stab["derivatives"]["CMl"]["ConGrp_1"] = value
                with self.assertRaisesRegex(ValueError, "aileron.*ConGrp_1.*CMl"):
                    self.generate()

    def test_no_effective_axis_authority_fails(self):
        for key, coef, axis in (("ConGrp_1", "CMl", "aileron"),
                               ("ConGrp_2", "CMm", "elevator"),
                               ("ConGrp_3", "CMn", "rudder")):
            with self.subTest(axis=axis):
                self.setUp()
                self.stab["derivatives"][coef][key] = 0.0
                with self.assertRaisesRegex(ValueError, f"Unusable.*{axis}"):
                    self.generate()

    def test_cancelled_axis_authority_fails_instead_of_taking_absolute_values(self):
        self.add_group("ConGrp_4", "RightWing_Aileron", "ConGrp_1",
                       {"CMl": -self.stab["derivatives"]["CMl"]["ConGrp_1"]})
        with self.assertRaisesRegex(ValueError, "Unusable.*aileron"):
            self.generate()

    def test_cli_reports_mapping_error_without_creating_config(self):
        self.stab["control_groups"]["ConGrp_1"] = "Flap"
        with tempfile.TemporaryDirectory() as tmp, \
                patch.object(generator, "parse_stab", return_value=self.stab), \
                contextlib.redirect_stderr(io.StringIO()) as errors:
            output = Path(tmp) / "AircraftConfig.json"
            code = generator.main(["--name", "TestUAV", "--massprops", str(MASS),
                                   "--stab", str(STAB), "--out", str(output)])
            self.assertEqual(code, 1)
            self.assertIn("aileron", errors.getvalue())
            self.assertFalse(output.exists())


class TestRealControlFixtures(unittest.TestCase):
    def test_default_config_unchanged(self):
        # U16: 2026-10-10 重建机型为用户有意开发, 采纳为新基线
        # (输入与配置自洽, 再生成比对恢复; md5 守护同步更新)。
        expected = json.loads((DATA / "AircraftConfig.json").read_text(encoding="utf-8"))
        with contextlib.redirect_stdout(io.StringIO()):
            actual = generator.generate(
                "TestUAV", MASS, STAB, DATA / "TestUAV_alpha.polar",
                DATA / "TestUAV_beta.polar", expected["engine"]["maxThrust_N"],
                expected["geometryMeshPath"],
            )
        # The fixture used relative input paths; this test is cwd-independent.
        actual["meta"]["source"] = copy.deepcopy(expected["meta"]["source"])
        # 浮点按相对容差比较 (跨解释器 libm 末位差异); U16 基线=用户重建机型
        assert_equal_float(actual, expected, path="config")


def write_polar(path, rows, with_cdw=True):
    """最小 .polar 夹具: rows = [(AoA_deg, CLtot, CDtot, CDwtot)]; 解析按列名取值。"""
    cols = "Beta Mach AoA CLtot CDtot" + (" CDwtot" if with_cdw else "")
    lines = [cols]
    for aoa, cl, cd, cdw in rows:
        vals = [0.0, 0.06, aoa, cl, cd] + ([cdw] if with_cdw else [])
        lines.append(" ".join(str(v) for v in vals))
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


# 实测 (2026-10-09 机型工坊换机失败): 近场 CDtot 全为负, 远场 CDwtot = CDo + CDiw 正常
NEAR_FIELD_NEGATIVE = [(0.0, 0.17516, -0.00491, 0.00762),
                       (5.0, 0.61538, -0.01005, 0.02075),
                       (10.0, 1.04678, -0.00889, 0.04774)]


class TestDragPolarFallback(unittest.TestCase):
    def _generate(self, rows, with_cdw=True):
        with tempfile.TemporaryDirectory() as tmp:
            polar = Path(tmp) / "alpha.polar"
            write_polar(polar, rows, with_cdw)
            with contextlib.redirect_stdout(io.StringIO()):
                return generator.generate("T", MASS, STAB, polar, None, None, "m.stl")

    def test_nonphysical_near_field_falls_back_to_far_field(self):
        cfg = self._generate(NEAR_FIELD_NEGATIVE)
        self.assertGreaterEqual(cfg["aero"]["CD0"], 0.0)
        self.assertGreaterEqual(cfg["aero"]["k_induced"], 0.0)
        cases = [{"CL": cl, "CDw": cdw, "beta_deg": 0.0} for _, cl, _, cdw in NEAR_FIELD_NEGATIVE]
        cd0, k = generator.fit_drag_polar(cases, key="CDw")
        self.assertAlmostEqual(cfg["aero"]["CD0"], cd0)
        self.assertAlmostEqual(cfg["aero"]["k_induced"], k)
        self.assertEqual(cfg["meta"]["dragPolar"]["source"], "CDwtot")
        self.assertLess(cfg["meta"]["dragPolar"]["nearFieldCD0"], 0.0)

    def test_physical_near_field_is_kept_and_not_flagged(self):
        rows = [(0.0, 0.1, 0.0100, 0.0200), (5.0, 0.6, 0.0300, 0.0500), (10.0, 1.0, 0.0700, 0.0900)]
        cfg = self._generate(rows)
        self.assertNotIn("dragPolar", cfg["meta"])
        cases = [{"CL": cl, "CD": cd, "beta_deg": 0.0} for _, cl, cd, _ in rows]
        cd0, k = generator.fit_drag_polar(cases)
        self.assertAlmostEqual(cfg["aero"]["CD0"], cd0)
        self.assertAlmostEqual(cfg["aero"]["k_induced"], k)

    def test_no_fallback_when_far_field_column_missing(self):
        cfg = self._generate(NEAR_FIELD_NEGATIVE, with_cdw=False)
        self.assertLess(cfg["aero"]["CD0"], 0.0)   # 维持原值, 由管线校验器拒收
        self.assertNotIn("dragPolar", cfg["meta"])

    def test_no_fallback_when_far_field_also_nonphysical(self):
        rows = [(a, cl, cd, cd) for a, cl, cd, _ in NEAR_FIELD_NEGATIVE]
        cfg = self._generate(rows)
        self.assertLess(cfg["aero"]["CD0"], 0.0)
        self.assertNotIn("dragPolar", cfg["meta"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
