# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
<#
.SYNOPSIS
  Measures every installed RIFE TensorRT model on this PC and saves the results, so the
  engine can pick the best-looking model that still plays smoothly for each video.

.DESCRIPTION
  For each model: builds its TensorRT engine for 1080p if needed (a few minutes the first
  time), then times the whole engine (colour conversion, frame analysis, RIFE) at
  1080p 25 -> 60 fps. Also tries 1 to 4 parallel TensorRT streams and keeps the fastest.
  Writes runtime\tuning.json (read by the engine) and runtime\tune.log.
#>
[CmdletBinding()]
param(
    [int]$Frames = 300,          # source frames per measurement (12 s of 25 fps video)
    [int]$TimeoutSec = 900
)
$ErrorActionPreference = "Stop"
$Root    = Split-Path -Parent $PSScriptRoot
$Runtime = Join-Path $Root "runtime"
$MpvDir  = Join-Path $Runtime "mpv"
$VSPipe  = Join-Path $MpvDir "VSPipe.exe"
$OnnxDir = Join-Path $MpvDir "vs-plugins\models\rife"
$Work    = Join-Path $Runtime ".cache\tune"
$LogFile = Join-Path $Runtime "tune.log"
$Order   = @("v4_26", "v4_25", "v4_6", "v4_25_lite", "v4_22_lite")   # best first, as in the engine

if (-not (Test-Path $VSPipe)) { throw "Run build.cmd first (VSPipe.exe not found)" }
if (-not (Test-Path (Join-Path $MpvDir "vs-plugins\vstrt.dll"))) { throw "TensorRT is not installed. Run setup-tensorrt.cmd first." }
New-Item -ItemType Directory -Force -Path $Work | Out-Null
Copy-Item (Join-Path $Root "engine\novamotion.vpy") $Runtime -Force
Copy-Item (Join-Path $Root "engine\novamotion_trt.py") $Runtime -Force
Start-Transcript -Path $LogFile -Force | Out-Null

$gpu = ((Get-CimInstance Win32_VideoController).Name | Where-Object { $_ -match "NVIDIA" } | Select-Object -First 1)
Write-Host "Tuning NOVA Motion for: $gpu" -ForegroundColor Cyan
Write-Host "Each model: 1080p, 25 -> 60 fps, the whole engine. Keep other GPU work closed meanwhile."
Write-Host ""

# Installed models, best first: rife_v4.22_lite.onnx -> v4_22_lite
$models = @()
foreach ($m in $Order) {
    $file = "rife_" + ($m -replace '^v4_', 'v4.') + ".onnx"
    if (Test-Path (Join-Path $OnnxDir $file)) { $models += $m }
}
if (-not $models) { throw "No RIFE ONNX models found in $OnnxDir" }

$vpy = Join-Path $Runtime "novamotion.vpy"
$bench = Join-Path $Work "tune.vpy"
@"
import vapoursynth as vs
core = vs.core
clip = core.std.BlankClip(width=1920, height=1080, format=vs.YUV420P8, length=$Frames, fpsnum=25, fpsden=1, color=[90, 120, 140])
g = {'video_in': clip, 'container_fps': 25.0, 'display_fps': 60.0, '__file__': r'$vpy'}
exec(compile(open(r'$vpy', encoding='utf-8').read(), r'$vpy', 'exec'), g)
"@ | Set-Content -Encoding ASCII $bench

function Measure-Engine([string]$Model, $Streams, [int]$Count = 0) {
    $cfg = Join-Path $Work "engine.json"
    $c = [ordered]@{ enabled = $true; engine = "rife_trt"; target = 60; trt_model = "rife-$Model"; ai_scale = 1;
                     scene_detect = $true; dedup = $true; demo = $false }
    if ($Streams) { $c.trt_streams = $Streams }
    $c | ConvertTo-Json | Set-Content -Encoding ASCII $cfg
    $env:NOVAMOTION_ENGINE_CONFIG = $cfg
    $psi = New-Object Diagnostics.ProcessStartInfo $VSPipe
    $psi.Arguments = if ($Count -gt 0) { "-e $Count -p `"$bench`" NUL" } else { "-p `"$bench`" NUL" }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $MpvDir
    $p = [Diagnostics.Process]::Start($psi)
    $err = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { $p.Kill(); return $null }
    $m = [regex]::Match($err.Result, 'Output (\d+) frames in ([\d.]+) seconds \(([\d.]+) fps\)')
    if ($m.Success) { return [double]$m.Groups[3].Value }
    $err.Result -split "[`r`n]+" | Where-Object { $_ -and $_ -notmatch '^Frame: ' } | Select-Object -Last 6 | ForEach-Object { Write-Host "    $_" }
    return $null
}

# 1. Build each engine once (the first build of a model takes a few minutes)
foreach ($m in $models) {
    Write-Host "==> Preparing $m (first time: a few minutes)" -ForegroundColor Cyan
    $null = Measure-Engine $m $null 8
}

# 2. Parallel TensorRT streams, on the default model
$probe = if ($models -contains "v4_6") { "v4_6" } else { $models[0] }
Write-Host ""
Write-Host "==> Parallel streams ($probe)" -ForegroundColor Cyan
$best = 2; $bestFps = 0
foreach ($s in 1..4) {
    $fps = Measure-Engine $probe $s
    if ($fps) {
        Write-Host ("    {0} stream(s): {1:N1} fps" -f $s, $fps)
        if ($fps -gt $bestFps * 1.03) { $best = $s; $bestFps = $fps }   # more streams only if clearly faster
    }
}
Write-Host "    using $best"

# 3. Every model with the best stream count
Write-Host ""
Write-Host "==> Models" -ForegroundColor Cyan
$sizes = [ordered]@{}
foreach ($m in $models) {
    $fps = Measure-Engine $m $best
    if ($fps) {
        $sizes[$m] = [math]::Round($fps, 1)
        # What it can keep up with, with the same safety margin the engine uses
        $per = $fps * 11 / 12
        # AI frames each second at 60 fps, plus the engine's 1.4x headroom: 24 fps -> 48, 25 -> 55, 30 -> 30
        $need = @{ 24 = 48 * 1.4; 25 = 55 * 1.4; 30 = 30 * 1.4 }
        $ok = @(24, 25, 30) | Where-Object { $per -ge $need[$_] }
        Write-Host ("    {0,-11} {1,6:N1} fps   smooth for: {2}" -f $m, $fps, $(if ($ok) { ($ok | ForEach-Object { "$_ fps" }) -join ", " } else { "lighter videos only" }))
    } else {
        Write-Host "    $m failed (see above)" -ForegroundColor Yellow
    }
}

$result = [ordered]@{
    gpu      = $gpu
    measured = (Get-Date).ToString("s")
    streams  = $best
    sizes    = [ordered]@{ "1920x1088" = $sizes }
}
$result | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $Runtime "tuning.json")
Remove-Item Env:\NOVAMOTION_ENGINE_CONFIG -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "Saved runtime\tuning.json. NOVA Motion now picks the best model this PC can play smoothly." -ForegroundColor Green
Stop-Transcript | Out-Null
