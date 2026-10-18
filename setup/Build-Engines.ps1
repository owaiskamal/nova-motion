# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
<#
.SYNOPSIS
  Prepares RIFE TensorRT engines for chosen video sizes, so the first video at that size
  starts at once instead of pausing for a few minutes while TensorRT optimises.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File setup\Build-Engines.ps1 -Sizes 720p,1440p,4K
#>
[CmdletBinding()]
param(
    [string[]]$Sizes = @("1080p"),
    [string[]]$Models = @("v4.6")
)
$ErrorActionPreference = "Stop"
# Accept "720p,1440p" as one argument too (cmd and -File pass lists as a single string)
$Sizes  = @($Sizes  | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ })
$Models = @($Models | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ })
$Root    = Split-Path -Parent $PSScriptRoot
$Runtime = Join-Path $Root "runtime"
$MpvDir  = Join-Path $Runtime "mpv"
$VSPipe  = Join-Path $MpvDir "VSPipe.exe"
$OnnxDir = Join-Path $MpvDir "vs-plugins\models\rife"
$Work    = Join-Path $Runtime ".cache\engines"
$Known   = @{ "720p" = @(1280, 720); "1080p" = @(1920, 1080); "1440p" = @(2560, 1440); "4K" = @(3840, 2160); "2160p" = @(3840, 2160) }

if (-not (Test-Path $VSPipe)) { throw "Run build.cmd first (VSPipe.exe not found)" }
if (-not (Test-Path (Join-Path $MpvDir "vs-plugins\vstrt.dll"))) { throw "TensorRT is not installed. Run setup-tensorrt.cmd first." }
New-Item -ItemType Directory -Force -Path $Work | Out-Null
Copy-Item (Join-Path $Root "engine\novamotion_trt.py") $Runtime -Force

$failed = 0
foreach ($size in $Sizes) {
    if ($Known.ContainsKey($size)) { $w, $h = $Known[$size] }
    elseif ($size -match '^(\d+)x(\d+)$') { $w = [int]$Matches[1]; $h = [int]$Matches[2] }
    else { Write-Warning "Unknown size '$size' (use 720p, 1080p, 1440p, 4K or WIDTHxHEIGHT)"; continue }
    foreach ($m in $Models) {
        if (-not (Test-Path (Join-Path $OnnxDir "rife_$m.onnx"))) { Write-Warning "Model rife_$m.onnx not installed; skipped"; continue }
        $script = Join-Path $Work "build-$m-$w`x$h.vpy"
        @"
import sys
sys.path.insert(0, r'$Runtime')
from fractions import Fraction
import vapoursynth as vs
import novamotion_trt
core = vs.core
clip = core.std.BlankClip(width=$w, height=$h, format=vs.RGBH, length=8, fpsnum=25, fpsden=1)
novamotion_trt.interpolate(clip, Fraction(25), Fraction(60), 'rife-$m').set_output()
"@ | Set-Content -Encoding ASCII $script
        Write-Host "==> RIFE $m for $size ($w x $h). The first build takes a few minutes." -ForegroundColor Cyan
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $ErrorActionPreference = "Continue"
        & $VSPipe -e 4 $script NUL 2>&1 | ForEach-Object { Write-Host "    $_" }
        $code = $LASTEXITCODE
        $ErrorActionPreference = "Stop"
        if ($code -ne 0) { Write-Host "    failed (see above)" -ForegroundColor Red; $failed++ }
        else { Write-Host ("    ready in {0:N0} s" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green }
    }
}
if ($failed) { exit 1 }
