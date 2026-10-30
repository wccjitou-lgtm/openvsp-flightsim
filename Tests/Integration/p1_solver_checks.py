"""Explicit, local OpenVSP integration checks. Requires an unused output folder."""

import argparse
import csv
import hashlib
import json
import sys
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "Tools"))
import import_vsp as iv
from apply_overrides import parse_component_rows
from stl_to_obj_unity import read_ascii_stl


def geometries(path):
    return {g.findtext("ParmContainer/ID"): g
            for g in ET.parse(path).getroot().findall(".//Geom")
            if g.find("ParmContainer/ID") is not None}


def load(path):
    return json.loads(path.read_text(encoding="utf-8"))


def run(out):
    if out.exists():
        raise ValueError(f"Use a new output folder, not {out}")
    out.mkdir(parents=True)
    source = ROOT / "Data/TestUAV.vsp3"
    digest = hashlib.sha256(source.read_bytes()).hexdigest()
    tree = ET.parse(source)
    ids = {}
    for g in tree.getroot().findall(".//Geom"):
        name = g.find("ParmContainer/Name")
        if name is None:
            continue
        ids[name.text] = g.findtext("ParmContainer/ID")
        if name.text in ("MainWing", "HTail"):
            name.text = "SharedWing"
    fixture = out / "duplicate.vsp3"
    tree.write(fixture, encoding="utf-8", xml_declaration=True)
    common = ["--vsp", str(fixture), "--name", "P1Fixture", "--auto-controls"]

    inspect = out / "inspect"
    assert iv.main(common + ["--inspect-only", "--out-dir", str(inspect)]) == 0
    components = load(inspect / "components.json")
    assert len({c["id"] for c in components}) == 4
    wings = [c for c in components if c["name"] == "SharedWing"]
    assert len(wings) == 2 and wings[0]["id"] != wings[1]["id"]
    rows = parse_component_rows(next((inspect / "work").glob("*_MassProps.txt")))
    assert abs(sum(c["mass_kg"] for c in components) - sum(r["mass"] for r in rows)) < 3e-6
    print("[PASS] real inspect retains IDs and includes mirrored/point mass rows", flush=True)

    legacy = out / "legacy.json"
    legacy.write_text(json.dumps({"components": {"SharedWing": {"incidence_deg": 2}}}),
                      encoding="utf-8")
    assert iv.main(common + ["--overrides", str(legacy),
                             "--out-dir", str(out / "ambiguous")]) == 2
    assert not (out / "ambiguous/AircraftConfig.json").exists()
    print("[PASS] ambiguous legacy name rejected before solver execution", flush=True)

    base = out / "baseline"
    assert iv.main(common + ["--out-dir", str(base)]) == 0
    overrides = out / "overrides.json"
    overrides.write_text(json.dumps({
        "components": {
            "geom:" + ids["Fuselage"]: {"ignore": True},
            "geom:" + ids["HTail"]: {"weight_kg": 1.0, "incidence_deg": 2}
        },
        "ballast_kg": 3.0,
        "gear": {"auto": True},
    }), encoding="utf-8")
    edited = out / "edited"
    assert iv.main(common + ["--out-dir", str(edited), "--overrides", str(overrides)]) == 0
    cfg = load(edited / "AircraftConfig.json")
    assert iv.validate_config(edited / "AircraftConfig.json") == []

    normalized = geometries(edited / "work/P1Fixture_normalized.vsp3")
    assert len(normalized) == 4, "Ignoring aerodynamic geometry must not remove its mass."
    body_sets = [int(v.strip()) for v in normalized[ids["Fuselage"]]
                 .findtext("Geom/Set_List").split(",") if v.strip()]
    assert body_sets[3] == 0 and body_sets[4] == 1
    tag_files = list((edited / "work").glob("*.vkey"))
    assert tag_files, "The solver must emit its geometry tag map."
    for tag_file in tag_files:
        tags = csv.reader(tag_file.read_text(encoding="utf-8").splitlines())
        solver_ids = {row[4] for row in tags if len(row) == 9 and row[0].isdigit()}
        assert solver_ids and ids["Fuselage"] not in solver_ids
    tail_inc = float(normalized[ids["HTail"]].find(".//Y_Rel_Rotation").get("Value"))
    main_inc = float(normalized[ids["MainWing"]].find(".//Y_Rel_Rotation").get("Value"))
    assert abs(tail_inc - 2) < 1e-8 and abs(main_inc) < 1e-8
    base_tris = read_ascii_stl(base / "work/P1Fixture.stl")
    edited_tris = read_ascii_stl(edited / "work/P1Fixture.stl")
    assert len(edited_tris) < len(base_tris)
    assert (base / "work/P1Fixture_default.stab").read_bytes() != (
        edited / "work/P1Fixture_default.stab").read_bytes()
    print("[PASS] ID edit changes tail only; ignored body remains mass-only", flush=True)

    rows = parse_component_rows(edited / "work/prep_massprops.txt")
    tail_alias = "mass_" + ids["HTail"]
    retained = sum(r["mass"] for r in rows if r["name"] not in (tail_alias, tail_alias + "_pm"))
    assert abs(cfg["mass"]["mass_kg"] - (retained + 1.0 + 3.0)) < 1e-7
    cg = cfg["reference"]["cg_m"]
    expected = {(round(v[1] - cg[1], 6), round(v[2] - cg[2], 6), round(cg[0] - v[0], 6))
                for tri in edited_tris for v in tri}
    actual = {tuple(float(x) for x in line.split()[1:])
              for line in (edited / "model.obj").read_text(encoding="utf-8").splitlines()
              if line.startswith("v ")}
    assert actual.issubset(expected)
    assert len(actual) > 100
    assert cg != load(base / "AircraftConfig.json")["reference"]["cg_m"]
    assert hashlib.sha256(source.read_bytes()).hexdigest() == digest
    result = {
        "source_unchanged": True, "unique_component_ids": len(components),
        "baseline_triangles": len(base_tris), "edited_triangles": len(edited_tris),
        "final_mass_kg": cfg["mass"]["mass_kg"], "final_cg_m": cg,
        "obj_matches_final_cg": True, "tail_incidence_deg": tail_inc,
        "main_incidence_deg": main_inc, "aero_ignored_mass_retained": True,
    }
    (out / "verification.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print("[PASS] selected component weight, ballast and final-CG mesh verified", flush=True)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path, required=True)
    run(parser.parse_args().out.resolve())
