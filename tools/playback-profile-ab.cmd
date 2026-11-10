@echo off
rem Longer A/B: NVIDIA draws the picture, or the built-in chip does. Drop a 24, 25 or 30 fps video
rem on this file (about 7 minutes: four runs of 90 seconds, in turn).
if "%~1"=="" ( echo Drag a video file onto this file. & pause & exit /b 1 )
for %%R in (1 2) do powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0playback-profile.ps1" -Video "%~1" -Seconds 90 -Only "auto,auto-builtin"
echo.
pause
