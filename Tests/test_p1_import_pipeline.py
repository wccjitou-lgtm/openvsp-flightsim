"""P1 regressions for component identity, aerodynamic sets and final mesh CG."""

import argparse
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
import import_vsp as iv


class ComponentIdentityTests(unittest.TestCase):
    def test_inspect_keeps_ids_and_sums_each_geometry_mass(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp)
            work = out / "work"
            work.mkdir()
            (work / "plane_MassProps.txt").write_text(
                "Name Mass cgX cgY cgZ Ixx Iyy Izz Ixy Ixz Iyz Volume\n"
                "mass_WINGID 1.0 1 0 0 1 1 1 0 0 0 0.1\n"
                "mass_WINGID 1.0 1 0 0 1 1 1 0 0 0 0.1\n"
                "mass_TAILID 0.1 3 0 0 1 1 1 0 0 0 0.1\n"
                "mass_TAILID 0.2 3 0 0 1 1 1 0 0 0 0.1\n"
                "mass_TAILID_pm 0.05 3 0 0 0 0 0 0 0 0 0\n"
                "Totals 2.35 1 0 0 4 4 4 0 0 0 0.4\n",
                encoding="utf-8")
            log = (
                "[GEOM] WingGeom|Wing|2.5|0|0|0|0|16|16|1|0||WINGID\n"
                "[GEOM] WingGeom|Wing|0.7|0|3|0|0|16|16|1|0||TAILID\n"
                "[INSPECT_OK]\n")
            args = argparse.Namespace(vsp=Path("model.vsp3"), openvsp_dir=out)
            with patch.object(iv, "run_vspscript", return_value=(0, log)):
                self.assertEqual(iv.inspect_only(args, "plane", out, work), 0)
            rows = json.loads((out / "components.json").read_text(encoding="utf-8"))
            self.assertEqual([r.get("id") for r in rows], ["WINGID", "TAILID"])
            self.assertAlmostEqual(rows[0]["mass_kg"], 2.0)
            self.assertAlmostEqual(rows[1]["mass_kg"], 0.35)

    def test_weight_override_includes_point_mass_for_the_selected_id(self):
        from apply_overrides import apply_component_weights
        cfg = json.loads((ROOT / "Data/AircraftConfig.json").read_text(encoding="utf-8"))
        with tempfile.TemporaryDirectory() as tmp:
            mp = Path(tmp) / "mass.txt"
            mp.write_text(
                "Name Mass cgX cgY cgZ Ixx Iyy Izz Ixy Ixz Iyz Volume\n"
                "mass_WINGID 2 1 0 0 1 1 1 0 0 0 0.1\n"
                "mass_TAILID 0.3 3 0 0 1 1 1 0 0 0 0.1\n"
                "mass_TAILID_pm 0.2 3 0 0 0 0 0 0 0 0 0\n"
                "Totals 2.5 1 0 0 2 2 2 0 0 0 0.2\n", encoding="utf-8")
            weights = iv.component_weight_aliases(
                {"components": {"geom:TAILID": {"weight_kg": 1.0}}},
                {"WINGID": "WingGeom", "TAILID": "WingGeom"})
            apply_component_weights(cfg, mp, weights)
            self.assertAlmostEqual(cfg["mass"]["mass_kg"], 3.0)
            self.assertAlmostEqual(cfg["reference"]["cg_m"][0], 5.0 / 3)

    def test_id_override_uses_identity_not_display_name(self):
        script = iv.render_prep(
            Path("model.vsp3"), "plane", Path("/work"), True, 16, "thin",
            {"components": {"geom:TAILID": {"incidence_deg": 3}}})
        self.assertIn('OvMatches( gid, "geom:TAILID" )', script)
        self.assertNotIn('GetGeomName( gid ) != "geom:TAILID"', script)
        self.assertIn("matches != 1", script)

    def test_legacy_name_override_rejects_ambiguous_names(self):
        script = iv.render_prep(
            Path("model.vsp3"), "plane", Path("/work"), True, 16, "thin",
            {"components": {"WingGeom": {"control": "Elevator"}}})
        self.assertIn("matches != 1", script)
        self.assertIn("[PREP_FAIL] component selector", script)


class AerodynamicSetTests(unittest.TestCase):
    def test_ignored_geometry_removed_from_aero_set_not_mass_model(self):
        script = iv.render_prep(
            Path("model.vsp3"), "plane", Path("/work"), True, 16, "thin",
            {"components": {"geom:BODYID": {"ignore": True}}})
        self.assertIn("SetSetFlag( gid, 3, false )", script)
        self.assertNotIn("DeleteGeom( gid )", script)
        self.assertIn('ExportFile( NAME + ".stl", 3, EXPORT_STL )', script)

    def test_cases_preserve_filtered_sets_in_thin_and_thick_modes(self):
        for mode in ("thin", "thick"):
            with self.subTest(mode=mode):
                script = iv.render_case("plane", Path("/work"), 2, 0.06, 5,
                                        -6, 6, 5, -4, 10, 7, mode, mode)
                self.assertNotIn("SetSetFlag( ids[i], 3, true )", script)
                if mode == "thick":
                    self.assertIn("const int    GSET = 3;", script)

    def test_fixup_does_not_reactivate_ignored_geometry(self):
        script = iv.render_fixup("plane", Path("/work"), "", "Rudder")
        self.assertIn("GetSetFlag( ids[i], 3 )", script)


class FinalMeshTests(unittest.TestCase):
    def test_pipeline_converts_mesh_after_ballast_updates_cg(self):
        baseline = json.loads((ROOT / "Data/AircraftConfig.json").read_text(
            encoding="utf-8"))
        seen = []
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp)
            source = out / "input.vsp3"
            source.write_text("solver boundary fixture", encoding="utf-8")
            overrides = out / "overrides.json"
            overrides.write_text(json.dumps({"ballast_kg": 3}), encoding="utf-8")

            def solver(_exe, script, work, _log):
                if "prep" in script.name:
                    (work / "plane_normalized.vsp3").write_text("fixture")
                    (work / "plane.stl").write_text(
                        "solid t\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 1\nendsolid t\n")
                    (work / "plane_MassProps.txt").write_text("fixture")
                    return 0, "[PREP_OK]\n[MASS] 14\n[CG] 1 0 0\n[SREF] 3\n[BREF] 4\n[CREF] 1\n"
                mode = (work / "case.txt").read_text()
                suffix = ".stab" if mode == "STAB_DEFAULT" else ".polar"
                (work / ("case" + suffix)).write_text("Aileron\nElevator\nRudder\n")
                return 0, "case DONE"

            def process(cmd, **_kwargs):
                if "generate_aircraft_config.py" in cmd[1]:
                    target = Path(cmd[cmd.index("--out") + 1])
                    target.write_text(json.dumps(copy.deepcopy(baseline)), encoding="utf-8")
                elif "stl_to_obj_unity.py" in cmd[1]:
                    config = json.loads(Path(cmd[cmd.index("--config") + 1]).read_text(encoding="utf-8"))
                    seen.append(config["reference"]["cg_m"])
                    Path(cmd[3]).write_text("v 0 0 0\n")
                return argparse.Namespace(returncode=0)

            with patch.object(iv, "run_vspscript", side_effect=solver), \
                    patch.object(iv.subprocess, "run", side_effect=process):
                rc = iv.main(["--vsp", str(source), "--name", "plane",
                              "--out-dir", str(out), "--overrides", str(overrides)])
            self.assertEqual(rc, 0)
            final = json.loads((out / "AircraftConfig.json").read_text(encoding="utf-8"))
            self.assertNotEqual(final["reference"]["cg_m"], baseline["reference"]["cg_m"])
            self.assertEqual(seen, [final["reference"]["cg_m"]])


class ArtifactIsolationTests(unittest.TestCase):
    """失败的一轮管线不得覆盖机型库里上一轮可用的产物 (config 与 model.obj/report.json 错配)。"""

    def _run(self, out, mutate_config):
        baseline = json.loads((ROOT / "Data/AircraftConfig.json").read_text(encoding="utf-8"))
        mutate_config(baseline)
        source = out / "input.vsp3"
        source.write_text("solver boundary fixture", encoding="utf-8")

        def solver(_exe, script, work, _log):
            if "prep" in script.name:
                (work / "plane_normalized.vsp3").write_text("fixture")
                (work / "plane.stl").write_text(
                    "solid t\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 1\nendsolid t\n")
                (work / "plane_MassProps.txt").write_text("fixture")
                return 0, "[PREP_OK]\n[MASS] 14\n[CG] 1 0 0\n[SREF] 3\n[BREF] 4\n[CREF] 1\n"
            mode = (work / "case.txt").read_text()
            suffix = ".stab" if mode == "STAB_DEFAULT" else ".polar"
            (work / ("case" + suffix)).write_text("Aileron\nElevator\nRudder\n")
            return 0, "case DONE"

        def process(cmd, **_kwargs):
            if "generate_aircraft_config.py" in cmd[1]:
                Path(cmd[cmd.index("--out") + 1]).write_text(
                    json.dumps(baseline), encoding="utf-8")
            elif "stl_to_obj_unity.py" in cmd[1]:
                Path(cmd[3]).write_text("v 9 9 9\n")
            return argparse.Namespace(returncode=0)

        with patch.object(iv, "run_vspscript", side_effect=solver), \
                patch.object(iv.subprocess, "run", side_effect=process), \
                contextlib.redirect_stdout(io.StringIO()):
            return iv.main(["--vsp", str(source), "--name", "plane", "--out-dir", str(out)])

    def test_failed_validation_keeps_previous_artifacts(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp)
            for name in ("AircraftConfig.json", "model.obj", "report.json"):
                (out / name).write_text("OLD " + name, encoding="utf-8")
            rc = self._run(out, lambda cfg: cfg["aero"].update(CD0=-0.01))
            self.assertEqual(rc, 5)
            for name in ("AircraftConfig.json", "model.obj", "report.json"):
                self.assertEqual((out / name).read_text(encoding="utf-8"), "OLD " + name,
                                 name + " 被失败的一轮覆盖")

    def test_successful_run_publishes_config_and_obj_together(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp)
            (out / "model.obj").write_text("OLD", encoding="utf-8")
            rc = self._run(out, lambda cfg: None)
            self.assertEqual(rc, 0)
            self.assertEqual((out / "model.obj").read_text(), "v 9 9 9\n")
            self.assertTrue(json.loads((out / "AircraftConfig.json").read_text(
                encoding="utf-8"))["aero"]["CD0"] >= 0)
            self.assertTrue((out / "report.json").is_file())


if __name__ == "__main__":
    unittest.main()
