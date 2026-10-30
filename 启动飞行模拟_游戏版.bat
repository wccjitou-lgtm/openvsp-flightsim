@echo off
setlocal
rem Launch the standalone player built by FlightSim.EditorTools.GameBuild.BuildWindows.
set "GAME_EXE=%~dp0Game\OpenVSPFlightSim.exe"
if not exist "%GAME_EXE%" (
  echo Standalone player not found: %GAME_EXE%
  echo Build it first; see docs/getting-started.md.
  pause
  exit /b 1
)
start "" "%GAME_EXE%" %*
