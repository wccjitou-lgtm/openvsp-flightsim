# Getting started

[中文版本](zh-CN/getting-started.md)

## 1. Install the required tools

For the included TestUAV, install Unity **2022.3.62f3c1** on Windows x64. The project uses HDRP 14.0.12 and Direct3D 12. A real graphics device is required for rendering and PlayMode rendering tests.

For aircraft generation, also install OpenVSP **3.51.3** and Python **3.13** with matching OpenVSP Python bindings. Install the Python development dependencies only when needed:

```bash
py -3.13 -m pip install -r requirements-dev.txt
```

Use an ASCII-only checkout path when running OpenVSP tools; some OpenVSP script and mesh paths are not reliable with non-ASCII directories.

## 2. Fly TestUAV in Unity

1. Open `UnityProject` in Unity Hub.
2. Wait for package restore and script compilation.
3. Open `Assets/Scenes/Flight_Phase2.unity`.
4. Enter Play Mode. TestUAV starts on the forest runway with no wind by default.

The generated TestUAV configuration, OBJ model, and terrain assets are included. OpenVSP is not needed for this path.

You can also launch from the repository root:

```bat
set UNITY_EDITOR_PATH=C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe
launch_flightsim.bat
```

The example path must be replaced when your editor is installed elsewhere. The launcher creates `UnityProject/Logs` if necessary and opens the main scene with `-force-d3d12`.

## 3. Configure OpenVSP for the workshop

Set `VSPROOT` to the OpenVSP installation directory:

```bat
set VSPROOT=C:\Tools\OpenVSP-3.51.3
```

The directory must contain:

- `vspscript.exe` and the VSPAERO executables;
- `python/openvsp` and the other supplied Python packages;
- the OpenVSP airfoil library when using that optional source.

Start the workshop with F8. Its Python worker requires Python 3.13 and the OpenVSP bindings. You can configure the worker Python and OpenVSP paths through the workshop settings; command-line tools also accept `--openvsp-dir`.

## 4. Build the standalone player

Close other Unity instances using the project. From the repository root:

```bat
if not exist UnityProject\Logs mkdir UnityProject\Logs
"%UNITY_EDITOR_PATH%" -batchmode -force-d3d12 -projectPath "%CD%\UnityProject" -executeMethod FlightSim.EditorTools.GameBuild.BuildWindows -logFile "%CD%\UnityProject\Logs\build.log" -quit
```

The build entry point is `UnityProject/Assets/Editor/GameBuild.cs`. It builds only `Flight_Phase2.unity` for `StandaloneWindows64` and writes `Game/OpenVSPFlightSim.exe`.

Run the player:

```bat
Game\OpenVSPFlightSim.exe -scene forest
Game\OpenVSPFlightSim.exe -scene island
Game\OpenVSPFlightSim.exe -scene alpine
Game\OpenVSPFlightSim.exe -scene forest -fpsprobe -probeseconds 60
```

The standalone player includes default models, configuration, and terrain through StreamingAssets. Full workshop functionality additionally requires the repository `Tools`, `VspImports`, Python, and OpenVSP layout. No redistributable installer is currently provided.

## 5. Troubleshooting

### Unity cannot be found

Set `UNITY_EDITOR_PATH` to the actual `Editor\Unity.exe`; do not edit a machine-specific path into the repository.

### OpenVSP or the worker cannot start

Check that `VSPROOT` points to the installation root, not the `python` subdirectory. Verify Python 3.13 can import `openvsp` after the OpenVSP package and DLL paths are configured.

### Magenta terrain or missing shaders

Use the required HDRP package versions and D3D12. Build through `GameBuild.BuildWindows`, which creates explicit shader-reference materials for runtime-generated content.

### Tests cannot find local files

Run commands from the repository root. Do not rely on old `work`, `bin`, `Library`, or installer directories. The default Python tests and C# tests use tracked fixtures; OpenVSP integration tests skip explicitly when the dependency is not configured.

### Rendering tests fail in batch mode

Do not add `-nographics`. HDRP rendering tests require a real graphics device. Use `-force-d3d12` and inspect the Unity log for driver or device errors.

More validation commands are in [Testing](testing.md).
