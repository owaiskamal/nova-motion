@echo off
rem Generative video upscaling test (SeedVR2): drop a video on this file.
rem Works from anywhere; it finds NOVA Studio's installed Generative part.
rem Optional: generative-video-test.cmd "video.mp4" --start 60 --seconds 3 --sizes 1080,2160
setlocal
set "RT="
for %%D in ("%~dp0runtime" "%~dp0..\runtime" "%USERPROFILE%\Documents\nova-motion\runtime" "%LOCALAPPDATA%\Programs\NOVA Motion\runtime" "%LOCALAPPDATA%\Programs\NOVA Studio\runtime") do (
  if not defined RT if exist "%%~D\generate\.venv\Scripts\python.exe" set "RT=%%~D"
)
if not defined RT (
  echo Add Generative on NOVA Studio's AI Models page first, then run this again.
  pause & exit /b 1
)
if "%~1"=="" (
  echo Drag a video file onto this file to test it.
  pause & exit /b 1
)
"%RT%\generate\.venv\Scripts\python.exe" "%~dp0generative_video_test.py" %* --runtime "%RT%"
echo.
pause
