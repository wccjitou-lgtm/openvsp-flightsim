# OpenVSP FlightSim

[中文说明](README.zh-CN.md)

OpenVSP FlightSim is a Windows flight-simulation research prototype that connects OpenVSP/VSPAERO aerodynamic data with a standalone C# six-degree-of-freedom flight-dynamics core and a Unity/HDRP visualization environment.

The runtime does **not** solve CFD. OpenVSP and VSPAERO generate offline stability and control derivatives, which the simulator uses in a linearized aerodynamic model.

## Highlights

- OpenVSP pipeline: `.vsp3` geometry, mass properties, VSPAERO sweeps, and `AircraftConfig.json` generation.
- Double-precision C# 6DOF core with RK4 integration and no `UnityEngine` dependency.
- ISA atmosphere, stability-derivative aerodynamics, simplified thrust and landing gear, steady wind, wind shear, Dryden turbulence, and discrete gusts.
- Three procedural environments switched at runtime: forest, island, and alpine valley.
- Keyboard controls, three camera modes, HUD, return-to-launch, orbit, stabilization, automatic landing, and CSV telemetry.
- F8 aircraft workshop for editing models, running VSPAERO, previewing results, and swapping aircraft.
- Python, standalone C#, and Unity PlayMode test suites.

## Current limitations

- Windows x64 and Direct3D 12 are the only currently supported runtime targets.
- Aerodynamics are linearized around selected operating points; this is not a full-envelope nonlinear or real-time CFD model.
- Thrust, landing gear, ground friction, and collision behavior are simplified engineering models.
- Automatic landing does not provide a complete crosswind crab/de-crab control law.
- The environments are procedural, not geographic reconstructions of real locations.
- PX4/MAVLink/HITL support is a future design topic, not an implemented feature.
- No installer or binary release is currently provided. The legacy installer is intentionally excluded while its runtime dependencies and redistribution notices are being redesigned.
- This is a research and development simulator, not a certified flight-training or airworthiness tool.

## Requirements

### Basic flight with the included TestUAV

- Windows 10/11 x64 with a Direct3D 12 capable GPU.
- Unity **2022.3.62f3c1**, matching `UnityProject/ProjectSettings/ProjectVersion.txt`.
- A Unity license and access to the Unity package registry.
- Unity packages listed in `UnityProject/Packages/manifest.json`, including HDRP 14.0.12 and Input System 1.7.0.

OpenVSP is **not** required to fly the included TestUAV because its generated configuration, model, and terrain assets are included.

### Aircraft generation and the full workshop

- OpenVSP **3.51.3**, including `vspscript.exe`, VSPAERO, and Python bindings.
- Python **3.13** compatible with the OpenVSP Python bindings.
- Python packages in `requirements-dev.txt` for terrain tools and the Python test suite.
- Visual Studio 2022 BuildTools with Roslyn `csc.exe` for standalone C# builds.

OpenVSP, Unity, Python, and their dependencies are external tools; they are not redistributed by this source repository.

## Quick start

1. Clone the repository and open `UnityProject` with the required Unity version.
2. Let Unity restore packages and compile scripts.
3. Open `UnityProject/Assets/Scenes/Flight_Phase2.unity` and enter Play Mode. The default is TestUAV on the forest runway.

Alternatively, configure the launcher from a terminal in the repository root:

```bat
set UNITY_EDITOR_PATH=C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe
launch_flightsim.bat
```

The path above is an example; set `UNITY_EDITOR_PATH` to your actual Unity executable. `启动飞行模拟.bat` forwards to the same launcher.

For OpenVSP features, also set:

```bat
set VSPROOT=C:\Tools\OpenVSP-3.51.3
```

`VSPROOT` must contain `vspscript.exe` and `python/openvsp`. Command-line tools also accept `--openvsp-dir` where documented.

See [Getting started](docs/getting-started.md) for player builds, environment setup, and troubleshooting.

## Controls

| Input | Action |
|---|---|
| Shift / Ctrl | Increase / decrease throttle |
| W / S or Up / Down | Pitch |
| A / D or Left / Right | Roll |
| Q / E | Yaw |
| B | Brake |
| R / F | Pitch trim |
| 1 / 2 / 3 / 4 | Return to launch / land / orbit / stabilize |
| 0 | Disengage automatic mode |
| V | Cycle chase, cockpit, and free cameras |
| F8 | Open the aircraft workshop |
| K / H | Control guide / hide HUD |
| F9 | Toggle telemetry recording |
| Backspace | Reset the aircraft |
| Esc | Pause menu |

## Build and test

Build a standalone Windows player through `FlightSim.EditorTools.GameBuild.BuildWindows`; detailed commands are in [Getting started](docs/getting-started.md).

```bat
Game\OpenVSPFlightSim.exe -scene forest
Game\OpenVSPFlightSim.exe -scene island
Game\OpenVSPFlightSim.exe -scene alpine
```

Run the default repository checks:

```bash
py -3.13 -m pip install -r requirements-dev.txt
py -3.13 -B -m unittest discover -s Tests -p "test_*.py"
bash Tools/build_phase1.sh
(cd bin && ./FlightDynamics.Tests.exe)
py -3.13 -B Tools/check_repository.py
```

OpenVSP integration tests require `VSPROOT`. Unity PlayMode tests require the Unity editor and a real graphics device. See [Testing](docs/testing.md) and the latest [verification record](docs/verification.md); historical test totals are not treated as current results.

## Repository layout

```text
FlightDynamics/                    Standalone C# flight-dynamics core
UnityProject/
  Assets/Scripts/                  Unity integration and synchronized core copy
  Assets/Editor/                   Scene and Windows build tools
  Assets/Tests/PlayMode/           Unity NUnit tests
  Assets/StreamingAssets/          Runtime model, configuration, and terrain data
Tools/                             OpenVSP, terrain, build, and validation scripts
Tests/                             Python tests, C# tests, and fixed fixtures
Data/                              TestUAV source model and generated reference data
VspImports/                        Redistribution-cleared aircraft library
.github/                           Issue/PR templates and CI
docs/                              English and Chinese developer documentation
```

The standalone `FlightDynamics` sources are intentionally mirrored under `UnityProject/Assets/Scripts/FlightDynamics`. `Tools/check_repository.py` rejects accidental drift; the standalone-only JSON loader is the documented exception.

## Documentation

- [Getting started](docs/getting-started.md) · [入门指南](docs/zh-CN/getting-started.md)
- [Architecture](docs/architecture.md) · [架构说明](docs/zh-CN/architecture.md)
- [OpenVSP workflow](docs/openvsp-workflow.md) · [OpenVSP 工作流](docs/zh-CN/openvsp-workflow.md)
- [Testing](docs/testing.md) · [测试指南](docs/zh-CN/testing.md)
- [Contributing](CONTRIBUTING.md)
- [Roadmap](ROADMAP.md)
- [Third-party notices](THIRD_PARTY_NOTICES.md)

## Contributing and support

Please read [CONTRIBUTING.md](CONTRIBUTING.md) before submitting a pull request. Changes should stay focused, include relevant tests, preserve the synchronized core copies, and avoid committing generated files, local paths, or assets without redistribution rights.

Use GitHub Issues for bugs and feature requests, and Discussions for design questions. See [SUPPORT.md](SUPPORT.md) and [SECURITY.md](SECURITY.md) for support and private vulnerability reporting.

## License

Copyright (c) 2026 wccjitou. Original project code, documentation, original aircraft models, and procedural assets are available under the [MIT License](LICENSE), subject to the ownership statements in [NOTICE](NOTICE).

Third-party tools and data retain their own licenses. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). OpenVSP FlightSim is an independent project and is not affiliated with or endorsed by NASA or the OpenVSP project.
