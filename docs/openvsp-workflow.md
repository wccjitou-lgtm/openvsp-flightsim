# OpenVSP workflow

[中文版本](zh-CN/openvsp-workflow.md)

## Configure the external tools

Install OpenVSP **3.51.3** and Python **3.13** with compatible bindings. Set the installation root before running the tools:

```bat
set VSPROOT=C:\Tools\OpenVSP-3.51.3
```

Do not copy the OpenVSP installation into the repository. Its executables, Python bindings, libraries, and data remain subject to their own licenses.

## Regenerate TestUAV reference data

From the repository root:

```bat
py -3.13 Tools/run_all_analyses.py
```

Or provide the path explicitly:

```bat
py -3.13 Tools/run_all_analyses.py --openvsp-dir "%VSPROOT%"
```

The pipeline builds `Data/TestUAV.vsp3`, calculates mass properties, and runs six VSPAERO cases: default stability, p/q/r-rate cases, beta sweep, and alpha sweep. It can take substantial time depending on the machine and solver settings.

The command writes and replaces generated files under `Data`. Run it in a dedicated worktree or review the resulting diff carefully. Do not commit solver scratch files, logs, `.vsptime`, `.adb`, `.vspaero`, or the other intermediate patterns excluded by `.gitignore`.

Convert the exported STL to the Unity OBJ layout when needed:

```bat
py -3.13 Tools/stl_to_obj_unity.py Data/TestUAV.stl Data/Meshes/TestUAV.obj --config Data/AircraftConfig.json
```

The configuration generator is `Tools/generate_aircraft_config.py`; its parser tests and the checked-in TestUAV reference files show the expected schema.

## Import another aircraft

Use a `.vsp3` model for which you have redistribution rights:

```bat
py -3.13 Tools/import_vsp.py --vsp path\to\MyAircraft.vsp3 --name MyAircraft --openvsp-dir "%VSPROOT%"
```

The default output is `VspImports/MyAircraft`. The `work` subdirectory contains regenerable solver intermediates and is ignored. Configuration and suitable OBJ output can be committed only after checking their provenance and removing local paths.

Geometry names should be ASCII alphanumeric. Underscores and non-ASCII names can break VSPAERO control-surface matching. Review mass properties, reference geometry, control axes, stability, trim, and numerical behavior before treating an imported model as flyable. The pipeline does not guarantee that every OpenVSP model produces a safe or useful flight model.

## Workshop operation

The F8 workshop edits a separate workshop model and invokes the same external tools. The worker uses Python 3.13 and OpenVSP bindings; aerodynamic runs use independent `vspscript` processes. Save user models outside tracked fixture directories unless they are intended as reviewed project assets.

OpenVSP airfoil data may be read from the user's installation. Redistribution of any copied airfoil file requires separate provenance and license confirmation; the OpenVSP executable license alone is not treated as proof for every data file.

## Data and license boundary

Original TestUAV and the aircraft models explicitly listed in `NOTICE` are project-original. VSPAERO outputs derived from those models are project data, but third-party geometry, airfoil coordinates, libraries, and solver installations do not become MIT by being processed or copied.

See `THIRD_PARTY_NOTICES.md` before adding any model, airfoil, texture, library, or binary.
