@echo off
rem Sets NOVA Motion up completely on this PC, or repairs what's missing, then builds dist\NovaMotion.exe.
rem Safe to run again any time: finished steps are skipped. Options: -Yes  -NoTensorRT  -NoTune  -NoShortcuts  -CheckOnly
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File setup\Setup.ps1 %*
if errorlevel 1 (
  echo.
  echo Setup stopped - the messages above say why. Fix that and run build.cmd again; finished steps are skipped.
)
echo.
pause
