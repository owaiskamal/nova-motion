@echo off
rem Adds what Generate's "Animate a picture" needs: the depth model and three Python packages (about 150 MB).
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File setup\Get-StudioParts.ps1 %*
echo.
pause
