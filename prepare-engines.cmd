@echo off
rem Prepares TensorRT engines for more video sizes, e.g.:  prepare-engines.cmd 720p 1440p 4K
setlocal
cd /d "%~dp0"
set SIZES=%*
if "%SIZES%"=="" set SIZES=720p 1440p 4K
powershell -NoProfile -ExecutionPolicy Bypass -File setup\Build-Engines.ps1 -Sizes "%SIZES: =,%"
echo.
pause
