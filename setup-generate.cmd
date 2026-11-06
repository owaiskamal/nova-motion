@echo off
rem Adds generative detail for photos (SeedVR2): its own Python with PyTorch for CUDA, and the model.
rem About 8 GB; needs an NVIDIA graphics card with 8 GB or more.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File setup\Get-Generate.ps1 %*
echo.
pause
