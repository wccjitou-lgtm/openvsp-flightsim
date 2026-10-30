#!/bin/bash
# Build the standalone flight-dynamics library and test executables with VS2022 Roslyn.
set -e
cd "$(dirname "$0")/.."

if [ -n "${CSC_PATH:-}" ]; then
    CSC="$CSC_PATH"
else
    CSC=""
    for candidate in \
        "/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe" \
        "/c/Program Files/Microsoft Visual Studio/2022/Enterprise/MSBuild/Current/Bin/Roslyn/csc.exe" \
        "/c/Program Files/Microsoft Visual Studio/2022/Professional/MSBuild/Current/Bin/Roslyn/csc.exe" \
        "/c/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe"; do
        if [ -f "$candidate" ]; then
            CSC="$candidate"
            break
        fi
    done
fi
if [ -z "$CSC" ] || [ ! -f "$CSC" ]; then
    echo "Roslyn csc.exe not found. Install VS2022 BuildTools or set CSC_PATH." >&2
    exit 1
fi

mkdir -p bin

echo "[1/3] Building FlightDynamicsCore.dll ..."
"$CSC" -nologo -target:library -optimize+ -warn:4 \
    -out:bin/FlightDynamicsCore.dll \
    FlightDynamics/Math/Vec3.cs \
    FlightDynamics/Math/Quat.cs \
    FlightDynamics/Math/Matrix3.cs \
    FlightDynamics/AircraftState.cs \
    FlightDynamics/AtmosphereModel.cs \
    FlightDynamics/WindModel.cs \
    FlightDynamics/AeroModel.cs \
    FlightDynamics/GroundContactModel.cs \
    FlightDynamics/RigidBodyIntegrator.cs \
    FlightDynamics/Trimmer.cs \
    FlightDynamics/DrydenTurbulence.cs \
    FlightDynamics/TerrainModel.cs

echo "[2/3] Building FlightDynamics.Tests.exe ..."
"$CSC" -nologo -target:exe -optimize+ -warn:4 \
    -r:bin/FlightDynamicsCore.dll \
    -r:System.Web.Extensions.dll \
    -out:bin/FlightDynamics.Tests.exe \
    FlightDynamics/IO/AircraftConfigJson.cs \
    Tests/FlightDynamics.Tests/TestSuite.cs \
    Tests/FlightDynamics.Tests/Program.cs \
    Tests/FlightDynamics.Tests/MissionTests.cs \
    Tests/FlightDynamics.Tests/WindTests.cs \
    Tests/FlightDynamics.Tests/TerrainTests.cs

echo "[3/3] Building ConsoleFlightSim.exe ..."
"$CSC" -nologo -target:exe -optimize+ -warn:4 \
    -r:bin/FlightDynamicsCore.dll \
    -r:System.Web.Extensions.dll \
    -out:bin/ConsoleFlightSim.exe \
    FlightDynamics/IO/AircraftConfigJson.cs \
    Tests/FlightDynamics.Tests/ConsoleFlightSim.cs

if [ -f Data/AircraftConfig.json ]; then
    cp Data/AircraftConfig.json bin/AircraftConfig.json
fi

echo "Build complete: bin/"
