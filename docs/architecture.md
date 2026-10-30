# Architecture

[中文版本](zh-CN/architecture.md)

## Data flow

```text
OpenVSP .vsp3 geometry
  -> AngelScript / MassProp / VSPAERO stability and polar sweeps
  -> Python parsers and regression
  -> AircraftConfig.json + component OBJ meshes
  -> standalone C# 6DOF core
  -> Unity aircraft, environment, HUD, workshop, and telemetry
```

VSPAERO runs offline. The Unity player consumes generated coefficients and does not call a CFD solver during flight.

## Flight-dynamics core

`FlightDynamics` contains the standalone double-precision implementation. Its Unity assembly has `noEngineReferences: true`, so the numerical core remains independent of `UnityEngine`.

Key responsibilities:

- `AircraftState.cs`: NED position, body velocities, angular rates, and quaternion attitude.
- `RigidBodyIntegrator.cs`: translational and rotational equations, quaternion kinematics, and RK4 integration.
- `AeroModel.cs`: linearized stability and control derivatives with engineering clamps.
- `AtmosphereModel.cs`: ISA atmosphere and airport elevation offset.
- `WindModel.cs` and `DrydenTurbulence.cs`: steady wind, shear, turbulence, and gusts.
- `GroundContactModel.cs` and `TerrainModel.cs`: simplified gear contact and heightmap terrain.
- `Trimmer.cs`: steady-flight trim.

Unity runs the flight update at 120 Hz, with four internal integration substeps. Unity `Rigidbody` is not used to advance the aircraft.

The same core sources are mirrored in `UnityProject/Assets/Scripts/FlightDynamics`. This duplication is intentional in the current release to support both standalone Roslyn builds and Unity. Keep the copies synchronized and run `Tools/check_repository.py`. The standalone `IO/AircraftConfigJson.cs` is not mirrored because Unity has its own configuration loader.

## Unity integration

- `Scripts/Aircraft`: controller, input mapping, automatic modes, and flight guards.
- `Scripts/DataPipeline`: JSON validation, loading, and coordinate conversion.
- `Scripts/Import`: runtime OBJ assembly and aircraft swapping.
- `Scripts/Workshop`: workshop UI, preview, worker protocol, and model state.
- `Scripts/Environment`: procedural terrain, runway, water, wind, vegetation, and wildlife.
- `Scripts/SceneSwitching`: scene profiles and runtime environment rebuilding.
- `Scripts/Camera`, `Scripts/UI`, `Scripts/Telemetry`: cameras, HUD, and CSV output.

The main scene is `Assets/Scenes/Flight_Phase2.unity`. Forest, island, and alpine are runtime environment profiles, not three separate `.unity` scenes. `SceneSwitcher` rebuilds the environment while retaining the aircraft, camera, and selected model.

The visual terrain and the flight-contact heightmap are built from the same source data. Default aircraft visuals fall back to StreamingAssets, so the scene does not depend on an editor-specific OBJ path.

## Python and OpenVSP tools

`Tools/run_all_analyses.py` regenerates the reference TestUAV data. `Tools/import_vsp.py` handles arbitrary suitable `.vsp3` imports. The workshop has two process paths:

1. a persistent Python worker using JSON Lines over stdio for model editing and export;
2. separate `vspscript` processes for VSPAERO cases so each result is preserved before the next run.

`Tools/workshop_common.py` resolves the repository root and OpenVSP location. OpenVSP is supplied by `VSPROOT`, `--openvsp-dir`, or an explicitly supported bundled layout; there is no machine-specific fallback.

## Scope boundaries

The aerodynamic model is local and linearized. Thrust, gear, tire friction, collision response, and some control-law parameters are simplified. These choices are suitable for research and iteration but do not establish certification-grade fidelity. See [OpenVSP workflow](openvsp-workflow.md) for data generation and [Testing](testing.md) for verification layers.
