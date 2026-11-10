@echo off
rem Playback profile: drop a video on this file. It plays a few seconds in several settings
rem (full screen, muted, about 4 minutes in all) and writes runtime\profile\<time>\summary.txt.
setlocal
if "%~1"=="" (
  echo Drag a video file onto this file to profile its playback.
  pause & exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0playback-profile.ps1" -Video "%~1" -Only "%PROFILE_ONLY%"
echo.
pause
