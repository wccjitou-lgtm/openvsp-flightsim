"""Physical config validation shared by import and runtime loading."""

import copy
import importlib.util
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "Tools"))

AERO_FIELDS = (
    "CL0", "CLalpha", "CLq", "CD0", "k_induced", "Cm0", "Cmalpha", "Cmq",
    "CYbeta", "Clbeta", "Cnbeta", "CYp", "Clp", "Cnp", "CYr", "Clr", "Cnr",
)
CONTROLS = {
    "elevator": ("CLde", "Cmde"),
    "aileron": ("CYda", "Clda", "Cnda"),
    "rudder": ("CYdr", "Cldr", "Cndr"),
}


class AircraftValidationTests(unittest.TestCase):
    def setUp(self):
        # Keep a missing new implementation an explicit contract failure in RED.
        self.assertIsNotNone(
            importlib.util.find_spec("aircraft_validation"),
            "Tools/aircraft_validation.py must provide validate_aircraft_config",
        )
        from aircraft_validation import validate_aircraft_config
        self.validate = validate_aircraft_config
        self.good = json.loads((ROOT / "Data" / "AircraftConfig.json").read_text(encoding="utf-8"))

    def problems_at(self, cfg, path):
        problems = self.validate(cfg)
        self.assertIsInstance(problems, list)
        self.assertTrue(all(isinstance(item, str) for item in problems), problems)
        self.assertTrue(any(path in item for item in problems), (path, problems))
        return problems

    def mutate(self, path, value):
        cfg = copy.deepcopy(self.good)
        owner = cfg
        keys = path.split(".")
        for key in keys[:-1]:
            owner = owner[key]
        owner[keys[-1]] = value
        return cfg

    def test_generated_reference_config_passes_without_mutation(self):
        before = copy.deepcopy(self.good)
        self.assertEqual([], self.validate(self.good))
        self.assertEqual(before, self.good)

    def test_real_valid_generated_configs_pass(self):
        paths = [ROOT / "UnityProject/Assets/StreamingAssets/AircraftConfig.json",
                 ROOT / "VspImports/TestUAVImport/AircraftConfig.json"]
        for path in paths:
            with self.subTest(path=str(path)):
                self.assertEqual([], self.validate(json.loads(path.read_text(encoding="utf-8"))))

    def test_non_object_roots_return_errors(self):
        for value in (None, [], "config", 1, True):
            with self.subTest(value=value):
                self.problems_at(value, "config")

    def test_required_objects_reject_missing_and_wrong_types(self):
        paths = ("reference", "mass", "mass.inertia_kgm2", "aero", "aero.controls",
                 "aero.controls.elevator", "aero.controls.aileron", "aero.controls.rudder",
                 "engine")
        for path in paths:
            for value in (None, [], "object", 1, True):
                with self.subTest(path=path, value=value):
                    self.problems_at(self.mutate(path, value), path)
            cfg = self.mutate(path, {})
            owner = cfg
            keys = path.split(".")
            for key in keys[:-1]:
                owner = owner[key]
            del owner[keys[-1]]
            with self.subTest(path=path, missing=True):
                self.problems_at(cfg, path)

    def test_required_numeric_fields_cannot_be_missing(self):
        paths = ["reference." + key for key in ("Sref_m2", "bref_m", "cref_m")]
        paths += ["mass.mass_kg", "engine.maxThrust_N"]
        paths += ["mass.inertia_kgm2." + key for key in ("Ixx", "Iyy", "Izz", "Ixy", "Ixz", "Iyz")]
        paths += ["aero." + key for key in AERO_FIELDS]
        paths += ["aero.controls." + surface + "." + key
                  for surface, fields in CONTROLS.items() for key in (*fields, "maxDeflectionDeg")]
        for path in paths:
            cfg = copy.deepcopy(self.good)
            owner = cfg
            keys = path.split(".")
            for key in keys[:-1]:
                owner = owner[key]
            del owner[keys[-1]]
            with self.subTest(path=path):
                self.problems_at(cfg, path)

    def test_numeric_types_are_not_coerced(self):
        for path in ("mass.mass_kg", "reference.Sref_m2", "mass.inertia_kgm2.Ixx",
                     "aero.CL0", "aero.controls.aileron.CYda", "engine.maxThrust_N"):
            for value in (None, True, False, "12", [], {}):
                with self.subTest(path=path, value=value):
                    self.problems_at(self.mutate(path, value), path)

    def test_nonfinite_numbers_and_overflow_are_rejected(self):
        paths = ("mass.mass_kg", "reference.Sref_m2", "reference.bref_m", "reference.cref_m",
                 "mass.inertia_kgm2.Ixx", "mass.inertia_kgm2.Ixy", "aero.CL0",
                 "aero.Cnr", "aero.controls.elevator.Cmde", "aero.controls.rudder.Cldr",
                 "aero.controls.aileron.maxDeflectionDeg", "engine.maxThrust_N")
        for path in paths:
            for value in (float("nan"), float("inf"), -float("inf"), 10 ** 1000):
                with self.subTest(path=path, value=str(value)[:12]):
                    self.problems_at(self.mutate(path, value), path)

    def test_mass_reference_and_control_limits_are_positive(self):
        paths = ("mass.mass_kg", "reference.Sref_m2", "reference.bref_m", "reference.cref_m",
                 "aero.controls.elevator.maxDeflectionDeg",
                 "aero.controls.aileron.maxDeflectionDeg", "aero.controls.rudder.maxDeflectionDeg")
        for path in paths:
            for value in (0, -1):
                with self.subTest(path=path, value=value):
                    self.problems_at(self.mutate(path, value), path)

    def test_signed_aero_coefficients_may_be_zero(self):
        cfg = copy.deepcopy(self.good)
        for key in AERO_FIELDS:
            cfg["aero"][key] = 0
        for surface, fields in CONTROLS.items():
            for key in fields:
                if key not in ("Cmde", "Clda", "Cndr"):
                    cfg["aero"]["controls"][surface][key] = 0
        self.assertEqual([], self.validate(cfg))

    def test_primary_controls_are_nonzero_with_named_errors(self):
        for surface, key in (("elevator", "Cmde"), ("aileron", "Clda"), ("rudder", "Cndr")):
            path = "aero.controls." + surface + "." + key
            with self.subTest(path=path):
                problems = self.problems_at(self.mutate(path, 0), path)
                self.assertTrue(any("nonzero" in problem for problem in problems))
                self.assertEqual([], self.validate(self.mutate(path, -1e-12)))

    def test_thrust_and_drag_are_nonnegative(self):
        for path in ("engine.maxThrust_N", "aero.CD0", "aero.k_induced"):
            with self.subTest(path=path):
                self.problems_at(self.mutate(path, -1), path)
                self.assertEqual([], self.validate(self.mutate(path, 0)))

    def test_inertia_requires_positive_diagonal(self):
        for key in ("Ixx", "Iyy", "Izz"):
            for value in (0, -1):
                with self.subTest(key=key, value=value):
                    self.problems_at(self.mutate("mass.inertia_kgm2." + key, value),
                                     "mass.inertia_kgm2")

    def test_inertia_rejects_singular_and_indefinite_tensors(self):
        tensors = [
            dict(Ixx=1, Iyy=1, Izz=1, Ixy=1, Ixz=0, Iyz=0),
            dict(Ixx=1, Iyy=1, Izz=1, Ixy=2, Ixz=0, Iyz=0),
            dict(Ixx=1, Iyy=1, Izz=1, Ixy=0, Ixz=2, Iyz=0),
            dict(Ixx=1, Iyy=1, Izz=1, Ixy=0, Ixz=0, Iyz=2),
            # The tensor off-diagonals are NEGATIVE products of inertia.
            dict(Ixx=1, Iyy=1, Izz=1, Ixy=0.6, Ixz=0.6, Iyz=0.6),
        ]
        for tensor in tensors:
            with self.subTest(tensor=tensor):
                problems = self.problems_at(self.mutate("mass.inertia_kgm2", tensor),
                                            "mass.inertia_kgm2")
                self.assertTrue(any("positive definite" in problem for problem in problems))

    def test_positive_definite_signed_products_pass_at_different_scales(self):
        tensor = dict(Ixx=2, Iyy=3, Izz=4, Ixy=0.1, Ixz=-0.2, Iyz=0.3)
        for scale in (1e-50, 1, 1e50):
            with self.subTest(scale=scale):
                scaled = {key: value * scale for key, value in tensor.items()}
                self.assertEqual([], self.validate(self.mutate("mass.inertia_kgm2", scaled)))

    def test_cg_is_optional_but_when_present_must_be_three_finite_numbers(self):
        cfg = copy.deepcopy(self.good)
        del cfg["reference"]["cg_m"]
        self.assertEqual([], self.validate(cfg))
        for value in (None, [], [0, 0], [0, 0, 0, 0], "1,2,3",
                      [0, 0, float("nan")], [0, "0", 0], [True, 0, 0]):
            with self.subTest(value=value):
                self.problems_at(self.mutate("reference.cg_m", value), "reference.cg_m")

    def test_gear_is_optional_but_present_structure_must_be_valid(self):
        cfg = copy.deepcopy(self.good)
        del cfg["gear"]
        self.assertEqual([], self.validate(cfg))
        for value in (None, [], {}, "gear"):
            with self.subTest(value=value):
                self.problems_at(self.mutate("gear", value), "gear")
        for value in (None, {}, "wheels", [None], [[]]):
            with self.subTest(wheels=value):
                self.problems_at(self.mutate("gear.wheels", value), "gear.wheels")
        self.assertEqual([], self.validate(self.mutate("gear.wheels", [])))

    def test_wheel_positions_require_three_finite_signed_numbers(self):
        for value in (None, [], [0, 0], [0, 0, 0, 0], [0, float("inf"), 0],
                      [0, "0", 0], [False, 0, 0]):
            cfg = copy.deepcopy(self.good)
            cfg["gear"]["wheels"][0]["position_m"] = value
            with self.subTest(value=value):
                self.problems_at(cfg, "gear.wheels[0].position_m")
        cfg = copy.deepcopy(self.good)
        del cfg["gear"]["wheels"][0]["position_m"]
        self.problems_at(cfg, "gear.wheels[0].position_m")

    def test_optional_wheel_defaults_remain_compatible(self):
        cfg = copy.deepcopy(self.good)
        cfg["gear"] = {"wheels": [{"position_m": [-1, 0, 0.3]}]}
        self.assertEqual([], self.validate(cfg))

    def test_wheel_spring_and_damping_check_types_and_signs(self):
        for key in ("kN_per_m", "cNs_per_m"):
            for value in (-1, float("nan"), float("inf"), "900", True, None):
                cfg = copy.deepcopy(self.good)
                cfg["gear"]["wheels"][0][key] = value
                with self.subTest(key=key, value=value):
                    self.problems_at(cfg, "gear.wheels[0]." + key)
        cfg = copy.deepcopy(self.good)
        cfg["gear"]["wheels"][0]["kN_per_m"] = 0
        self.problems_at(cfg, "gear.wheels[0].kN_per_m")
        cfg["gear"]["wheels"][0]["kN_per_m"] = 900
        cfg["gear"]["wheels"][0]["cNs_per_m"] = 0
        self.assertEqual([], self.validate(cfg))

    def test_wheel_flags_require_booleans(self):
        for key in ("steerable", "brake"):
            for value in (1, "false", None, []):
                cfg = copy.deepcopy(self.good)
                cfg["gear"]["wheels"][0][key] = value
                with self.subTest(key=key, value=value):
                    self.problems_at(cfg, "gear.wheels[0]." + key)

    def test_optional_friction_rejects_bad_values(self):
        for value in (None, [], "friction"):
            with self.subTest(value=value):
                self.problems_at(self.mutate("gear.friction", value), "gear.friction")
        for key in ("mu_lateral", "mu_rolling", "mu_brake", "maxSteerDeg"):
            path = "gear.friction." + key
            for value in (-1, float("nan"), float("inf"), True, "1", None):
                with self.subTest(path=path, value=value):
                    self.problems_at(self.mutate(path, value), path)
            self.assertEqual([], self.validate(self.mutate(path, 0)))

    def test_unknown_metadata_is_ignored(self):
        cfg = copy.deepcopy(self.good)
        cfg["extension"] = {"futureField": None}
        cfg["meta"]["source"] = None
        self.assertEqual([], self.validate(cfg))

    def test_multiple_problems_are_reported_together(self):
        cfg = copy.deepcopy(self.good)
        cfg["mass"]["mass_kg"] = float("nan")
        cfg["engine"]["maxThrust_N"] = -1
        problems = self.validate(cfg)
        self.assertTrue(any("mass.mass_kg" in item for item in problems))
        self.assertTrue(any("engine.maxThrust_N" in item for item in problems))


if __name__ == "__main__":
    unittest.main()
