# Testing

[中文版本](zh-CN/testing.md)

Run commands from the repository root. Record actual pass, failure, and skip counts in pull requests; do not copy totals from older development reports.

## Repository hygiene and core synchronization

```bash
py -3.13 -B Tools/check_repository.py
```

This check compares the two flight-dynamics source trees and rejects Python caches, oversized files, known developer paths, and common credential markers. It does not replace a full secret scan or manual asset-license review.

## Python tests

Install the direct development dependencies:

```bash
py -3.13 -m pip install -r requirements-dev.txt
py -3.13 -B -m unittest discover -s Tests -p "test_*.py"
```

The default suite uses tracked fixtures. Tests that require a real OpenVSP worker or solver skip explicitly when OpenVSP is not configured.

To include the integration tests, set `VSPROOT` and ensure Python 3.13 can load the OpenVSP bindings:

```bat
set VSPROOT=C:\Tools\OpenVSP-3.51.3
py -3.13 -B -m unittest discover -s Tests -p "test_*.py"
```

Integration tests can create ignored files under `work`. Use a disposable worktree for long solver runs and do not commit generated output without reviewing it.

## Standalone C# tests

Install Visual Studio 2022 BuildTools with Roslyn. The build script searches common VS2022 editions and also accepts `CSC_PATH`.

```bash
bash Tools/build_phase1.sh
(cd bin && ./FlightDynamics.Tests.exe)
(cd bin && ./ConsoleFlightSim.exe --headless 2)
```

The script builds `FlightDynamicsCore.dll`, `FlightDynamics.Tests.exe`, and `ConsoleFlightSim.exe`, then copies the default configuration into `bin`. Run the executables from `bin` so they can find that configuration.

## Unity PlayMode tests

Close other Unity instances using the project. Set `UNITY_EDITOR_PATH` and run:

```bat
"%UNITY_EDITOR_PATH%" -batchmode -force-d3d12 -projectPath "%CD%\UnityProject" -runTests -testPlatform PlayMode -testResults "%CD%\UnityProject\Logs\playmode.xml" -logFile "%CD%\UnityProject\Logs\playmode.log"
```

Do not add `-nographics`; HDRP rendering tests need a real graphics device. Do not add `-quit` to the test command. Inspect both the XML result and the editor log.

`WorkshopPreviewRenderDiagPlayModeTests` is marked `[Explicit]` and is excluded from the default suite. It is a pixel-level diagnostic that must be run interactively from the Unity Test Runner when investigating preview rendering. Some batch/remote HDRP environments return a completely black frame even with D3D12; a black frame in that environment is not, by itself, evidence of a preview-code regression. An interactive run is still required for visual verification.

## Manual release checks

From a fresh checkout without `Library`, `work`, or `bin`:

1. Import the Unity project and confirm there are no compile errors, missing scripts, or missing default assets.
2. Enter Play Mode and verify takeoff, reset, camera switching, and the three runtime environments.
3. Open F8 and run the worker and an OpenVSP case when those dependencies are available.
4. Build the Windows player with `GameBuild.BuildWindows` and start each `-scene` variant.
5. Check `docs/verification.md` for what has actually been verified on the current candidate, including anything not run.
