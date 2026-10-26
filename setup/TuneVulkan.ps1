# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
<#
.SYNOPSIS
  Tuning for PCs without NVIDIA TensorRT (Intel, AMD, older NVIDIA): measures RIFE on Vulkan
  at full, 75 % and half AI size, and MVTools, so the engine plays the best option that keeps up.

.DESCRIPTION
  Finds the fastest Vulkan device (laptops list the built-in graphics and the card), then times
  each installed RIFE model at each AI size on 1080p 25 -> 60, the whole engine. Writes the
  "vulkan" part of runtime\tuning.json and runtime\tune.log. Called by Tune.ps1.
#>
[CmdletBinding()]
param(
    [int]$Frames = 150,          # source frames per measurement (6 s of 25 fps video)
    [int]$TimeoutSec = 600
)
$ErrorActionPreference = "Stop"
$Root    = Split-Path -Parent $PSScriptRoot
$Runtime = Join-Path $Root "runtime"
$MpvDir  = Join-Path $Runtime "mpv"
$VSPipe  = Join-Path $MpvDir "VSPipe.exe"
$Models  = Join-Path $Runtime "models"
$Work    = Join-Path $Runtime ".cache\tune"
$LogFile = Join-Path $Runtime "tune.log"
# Best first, as in the engine
$Order   = @("rife-v4.26_ensembleFalse", "rife-v4.6_ensembleFalse", "rife-v4.25-lite_ensembleFalse", "rife-v4.22_lite_ensembleFalse")

if (-not (Test-Path $VSPipe)) { throw "Run build.cmd first (VSPipe.exe not found)" }
New-Item -ItemType Directory -Force -Path $Work | Out-Null
Copy-Item (Join-Path $Root "engine\novamotion.vpy") $Runtime -Force
Copy-Item (Join-Path $Root "engine\novamotion_trt.py") $Runtime -Force
Start-Transcript -Path $LogFile -Force | Out-Null

$gpus = @((Get-CimInstance Win32_VideoController).Name)
Write-Host "Tuning NOVA Motion for: $($gpus -join ', ')" -ForegroundColor Cyan
Write-Host "No NVIDIA TensorRT here, so this measures RIFE on Vulkan (any graphics) and MVTools (processor)."
Write-Host "Each test: 1080p, 25 -> 60 fps, the whole engine. Keep other heavy apps closed meanwhile."
Write-Host ""

$installed = @($Order | Where-Object { Test-Path (Join-Path $Models "$_\flownet.param") })

$vpy = Join-Path $Runtime "novamotion.vpy"
$bench = Join-Path $Work "tune-vulkan.vpy"
@"
import vapoursynth as vs
core = vs.core
clip = core.std.BlankClip(width=1920, height=1080, format=vs.YUV420P8, length=$Frames, fpsnum=25, fpsden=1, color=[90, 120, 140])
g = {'video_in': clip, 'container_fps': 25.0, 'display_fps': 60.0, '__file__': r'$vpy'}
exec(compile(open(r'$vpy', encoding='utf-8').read(), r'$vpy', 'exec'), g)
"@ | Set-Content -Encoding ASCII $bench

$env:NOVAMOTION_NO_FALLBACK = "1"
$env:NOVAMOTION_NO_TUNING = "1"
function Measure-Option($Cfg) {
    $cfgPath = Join-Path $Work "engine-vulkan.json"
    $c = [ordered]@{ enabled = $true; target = 60; scene_detect = $true; dedup = $true; demo = $false }
    foreach ($k in $Cfg.Keys) { $c[$k] = $Cfg[$k] }
    [IO.File]::WriteAllText($cfgPath, ($c | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
    $env:NOVAMOTION_ENGINE_CONFIG = $cfgPath
    $psi = New-Object Diagnostics.ProcessStartInfo $VSPipe
    $psi.Arguments = "-p `"$bench`" NUL"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $MpvDir
    $p = [Diagnostics.Process]::Start($psi)
    $err = $p.StandardError.ReadToEndAsync()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not $p.WaitForExit(30000)) {
        if ($sw.Elapsed.TotalSeconds -ge $TimeoutSec) { $p.Kill(); Write-Host "    gave up after $([int]$sw.Elapsed.TotalMinutes) min"; return $null }
        Write-Host ("    still working, {0:mm\:ss}" -f $sw.Elapsed)
    }
    $m = [regex]::Match($err.Result, 'Output (\d+) frames in ([\d.]+) seconds \(([\d.]+) fps\)')
    if ($m.Success) { return [double]$m.Groups[3].Value }
    return $null
}

$options = [ordered]@{}
$device = 0
if ($installed) {
    # 1. Which Vulkan device: the light model at half size on each, until one doesn't exist
    $probe = $installed[-1]
    Write-Host "==> Graphics chips (Vulkan)" -ForegroundColor Cyan
    $best = -1
    foreach ($d in 0..3) {
        $fps = Measure-Option @{ engine = "rife"; rife_model = $probe; rife_gpu = $d; ai_scale = 0.5 }
        if ($null -eq $fps) { if ($d -gt 0) { break } else { Write-Host "    device 0 failed"; continue } }
        Write-Host ("    device {0}: {1:N1} fps" -f $d, $fps)
        if ($fps -gt $best) { $best = $fps; $device = $d }
    }
    if ($best -lt 0) { Write-Host "    Vulkan RIFE does not run on this PC." -ForegroundColor Yellow; $installed = @() }
    else { Write-Host "    using device $device" }

    # 2. Each model at each AI size, smallest first; a size is skipped once the one below it is far too slow
    Write-Host ""
    Write-Host "==> RIFE models and AI sizes" -ForegroundColor Cyan
    foreach ($m in $installed) {
        $prev = $null
        foreach ($s in 0.5, 0.75, 1.0) {
            if ($prev -and $prev -lt 25) { break }   # each step up is roughly 2x the work
            $fps = Measure-Option @{ engine = "rife"; rife_model = $m; rife_gpu = $device; ai_scale = $s }
            if ($null -eq $fps) { Write-Host "    $m at $([int]($s * 100))% failed"; break }
            $options["$m@$s"] = [math]::Round($fps, 1)
            Write-Host ("    {0,-32} {1,4}%  {2,6:N1} fps" -f $m, [int]($s * 100), $fps)
            $prev = $fps
        }
    }
}

Write-Host ""
Write-Host "==> MVTools (processor)" -ForegroundColor Cyan
$mv = Measure-Option @{ engine = "mvtools"; mv_quality = "balanced" }
if ($mv) { Write-Host ("    {0:N1} fps" -f $mv) } else { Write-Host "    failed" }
Remove-Item Env:\NOVAMOTION_ENGINE_CONFIG, Env:\NOVAMOTION_NO_FALLBACK, Env:\NOVAMOTION_NO_TUNING -ErrorAction SilentlyContinue

$result = [ordered]@{
    gpu      = ($gpus -join ", ")
    measured = (Get-Date).ToString("s")
    vulkan   = [ordered]@{ device = $device; source = "1920x1080"; options = $options }
    mvtools  = $(if ($mv) { [math]::Round($mv, 1) } else { $null })
}
[IO.File]::WriteAllText((Join-Path $Runtime "tuning.json"), ($result | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding $false))

Write-Host ""
$fit = $options.Keys | Where-Object { $options[$_] * 11 / 12 -ge 55 }
if ($fit) { Write-Host "Saved runtime\tuning.json. AI smoothing keeps up on this PC; NOVA Motion picks the best option per video." -ForegroundColor Green }
elseif ($mv -ge 60) { Write-Host "Saved runtime\tuning.json. The AI can't keep up at 1080p here, so NOVA Motion uses MVTools, which can." -ForegroundColor Yellow }
else { Write-Host "Saved runtime\tuning.json. This PC is slow for 1080p smoothing; 720p video will play best." -ForegroundColor Yellow }
Stop-Transcript | Out-Null
