@echo off
rem Adds generative detail for photos (SeedVR2): its own Python with PyTorch for CUDA, and the model.
rem About 6-8 GB of downloads; needs an NVIDIA graphics card with 4 GB or more.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File setup\Get-Generate.ps1 %*
echo.
pause
