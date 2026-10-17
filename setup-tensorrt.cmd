@echo off
rem Optional: adds the RIFE TensorRT engine for NVIDIA RTX GPUs (one-time ~2-3 GB download).
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File setup\Get-TensorRT.ps1
echo.
pause
