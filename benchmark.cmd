@echo off
rem Measures each engine at 1080p 25 -> 60 fps. Results: runtime\benchmark.log
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File setup\Benchmark.ps1
echo.
pause
