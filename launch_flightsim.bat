@echo off
setlocal
rem Launch the Unity editor with the main flight scene.
rem Set UNITY_EDITOR_PATH to the full path of Unity 2022.3.62f3c1 Editor\Unity.exe.
if not defined UNITY_EDITOR_PATH (
  echo UNITY_EDITOR_PATH is not set.
  echo Set it to your Unity Editor executable, then run this file again.
  exit /b 1
)
if not exist "%UNITY_EDITOR_PATH%" (
  echo Unity executable not found: %UNITY_EDITOR_PATH%
  exit /b 1
)
if not exist "%~dp0UnityProject\Logs" mkdir "%~dp0UnityProject\Logs"
start "" "%UNITY_EDITOR_PATH%" ^
  -projectPath "%~dp0UnityProject" ^
  -force-d3d12 ^
  -executeMethod FlightSim.EditorTools.SceneOpener.OpenAirportAndPlay ^
  -logFile "%~dp0UnityProject\Logs\Unity_Launch.log"
echo Unity is starting. Use Shift/Ctrl for throttle and K for the control guide.
timeout /t 5 >nul
