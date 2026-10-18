@echo off
rem Measures each RIFE TensorRT model on this PC so NOVA Motion can pick the best one that plays smoothly.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File setup\Tune.ps1
echo.
pause
