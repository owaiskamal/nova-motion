@echo off
rem Adds the light and newest RIFE models to an existing TensorRT setup (about 60 MB), then re-tunes.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File setup\Get-TensorRT.ps1 -ModelsOnly
echo.
pause
