@echo off
rem Generative video upscaling test (SeedVR2): drop a video on this file.
rem Put this file and generative_video_test.py in the NOVA Studio folder (next to NovaMotion.exe).
rem Optional: generative-video-test.cmd "video.mp4" --start 60 --seconds 3 --sizes 1080,2160
setlocal
cd /d "%~dp0"
set "PY=%~dp0runtime\generate\.venv\Scripts\python.exe"
if not exist "%PY%" set "PY=%~dp0..\runtime\generate\.venv\Scripts\python.exe"
if not exist "%PY%" (
  echo Add Generative on NOVA Studio's AI Models page first, then run this again.
  pause & exit /b 1
)
if "%~1"=="" (
  echo Drag a video file onto this file to test it.
  pause & exit /b 1
)
"%PY%" "%~dp0generative_video_test.py" %*
echo.
pause
