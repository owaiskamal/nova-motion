@echo off
rem Builds NovaMotion.exe into .\dist and checks the runtime is present.
setlocal
cd /d "%~dp0"
if not exist runtime\mpv\vapoursynth.dll (
  echo Setting up the runtime...
  powershell -NoProfile -ExecutionPolicy Bypass -File setup\Get-Runtime.ps1 || goto :fail
)
rem Keep the runtime's engine files in step with the repo (edit engine\, rebuild, play)
copy /y engine\novamotion.vpy runtime\ >nul
copy /y engine\novamotion_trt.py runtime\ >nul
xcopy /e /y /i /q engine\mpv runtime\mpv\portable_config >nul
rem A runtime carried over from the FlowFrame days must not load the old script too
if exist runtime\mpv\portable_config\scripts\flowframe.lua del runtime\mpv\portable_config\scripts\flowframe.lua

rem Need the .NET SDK (not just the runtime) to build
set HAS_SDK=
for /f "delims=" %%s in ('dotnet --list-sdks 2^>nul') do set HAS_SDK=1
if not defined HAS_SDK (
  echo The .NET 8 SDK is not installed - only the runtime is.
  echo Install it with:  winget install Microsoft.DotNet.SDK.8
  echo or download it from https://dotnet.microsoft.com/download/dotnet/8.0  ^(choose "SDK x64"^)
  echo Then run build.cmd again.
  goto :fail
)
echo Running the tests...
dotnet run --project app\NovaMotion.SmokeTest -c Release || (echo Smoke tests failed & goto :fail)
echo.
echo Building NOVA Motion into dist\ ... (the first build on a PC downloads .NET packages and can take a few minutes; progress goes to build.log)
dotnet publish app\NovaMotion.App -c Release -r win-x64 -o dist > build.log 2>&1 || (type build.log & echo. & echo Build failed - log saved to build.log & goto :fail)
echo.
echo Done. Start dist\NovaMotion.exe
pause
exit /b 0

:fail
echo.
echo Something went wrong - the messages above say what. Press any key to close.
pause >nul
exit /b 1
