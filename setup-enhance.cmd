@echo off
rem Adds the AI models for Enhance (film and anime upscaling, clean-up): about 150 MB.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File setup\Get-EnhanceModels.ps1 %*
echo.
pause
