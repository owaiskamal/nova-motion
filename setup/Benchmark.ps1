# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
<#
.SYNOPSIS
  Measures each NOVA Motion engine at 1080p 25 -> 60 fps outside the player.
  Writes runtime\benchmark.log. Real-time playback needs >= 60 fps here.
#>
[CmdletBinding()]
param(
    # rife_trt@0.5: TensorRT with the AI on a half-size copy (what GTX cards use).
    # rife@N: Vulkan RIFE on Vulkan device N; laptops list the built-in graphics too, so each is tried.
    # rife_half: Vulkan RIFE with the AI at half size, on the fastest device found above.
    [string[]]$Engines = @("rife_trt", "rife_trt@0.5", "rife@0", "rife@1", "rife@2", "rife_half", "mvtools"),
    [int]$Frames = 400,          # source frames (~16 s of 25 fps video)
    [int]$TimeoutSec = 300
)
$ErrorActionPreference = "Stop"
$Root    = Split-Path -Parent $PSScriptRoot
$Runtime = Join-Path $Root "runtime"
$MpvDir  = Join-Path $Runtime "mpv"
$VSPipe  = Join-Path $MpvDir "VSPipe.exe"
$Work    = Join-Path $Runtime ".cache\bench"
$LogFile = Join-Path $Runtime "benchmark.log"
New-Item -ItemType Directory -Force -Path $Work | Out-Null
Start-Transcript -Path $LogFile -Force | Out-Null

$gpu = ((Get-CimInstance Win32_VideoController).Name) -join ", "
Write-Host "GPU: $gpu"
Write-Host "Test: 1920x1080 YUV 25 fps -> 60 fps, $Frames source frames"
Write-Host ""

$vpy = Join-Path $Runtime "novamotion.vpy"
$bench = Join-Path $Work "bench.vpy"
@"
import vapoursynth as vs
core = vs.core
clip = core.std.BlankClip(width=1920, height=1080, format=vs.YUV420P8, length=$Frames, fpsnum=25, fpsden=1, color=[90, 120, 140])
g = {'video_in': clip, 'container_fps': 25.0, 'display_fps': 60.0, '__file__': r'$vpy'}
exec(compile(open(r'$vpy', encoding='utf-8').read(), r'$vpy', 'exec'), g)
"@ | Set-Content -Encoding ASCII $bench

$results = @()
$env:NOVAMOTION_NO_FALLBACK = "1"
$env:NOVAMOTION_NO_TUNING = "1"
$vulkanDone = $false
$vulkanBest = 0; $vulkanBestFps = -1
foreach ($e in $Engines) {
    $name, $opt = $e -split "@", 2
    if ($name -eq "rife" -and $vulkanDone) { continue }
    $c = [ordered]@{ enabled = $true; engine = $name; target = "display"; rife_model = "rife-v4.6_ensembleFalse";
                     trt_model = "rife-v4.6"; ai_scale = 1; demo = $false }
    if ($name -eq "rife_trt" -and $opt) { $c.ai_scale = [double]$opt }
    if ($name -eq "rife" -and $opt) { $c.rife_gpu = [int]$opt }
    if ($name -eq "rife_half") {
        if ($vulkanBestFps -lt 0) { continue }          # Vulkan RIFE didn't run at all
        $c.engine = "rife"; $c.rife_gpu = $vulkanBest; $c.ai_scale = 0.5; $e = "rife_half@$vulkanBest"
    }
    $cfg = Join-Path $Work ("engine-" + ($e -replace '[@.]', '_') + ".json")
    $c | ConvertTo-Json | Set-Content -Encoding ASCII $cfg
    $env:NOVAMOTION_ENGINE_CONFIG = $cfg
    Write-Host "==> $e" -ForegroundColor Cyan

    $psi = New-Object Diagnostics.ProcessStartInfo $VSPipe
    $psi.Arguments = "-p `"$bench`" NUL"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $MpvDir
    $p = [Diagnostics.Process]::Start($psi)
    $errTask = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        $p.Kill()
        Write-Host "    HUNG: no result after $TimeoutSec s (killed)" -ForegroundColor Red
        $results += [pscustomobject]@{ Engine = $e; Fps = "hung" }
    }
    $err = $errTask.Result
    # VSPipe redraws progress with carriage returns; keep only real lines
    $lines = $err -split "[`r`n]+" | Where-Object { $_ -and $_ -notmatch '^Frame: \d+/\d+' }
    $lines | ForEach-Object { Write-Host "    $_" }
    $m = [regex]::Match($err, 'Output (\d+) frames in ([\d.]+) seconds \(([\d.]+) fps\)')
    if ($m.Success) {
        $fps = [double]$m.Groups[3].Value
        $verdict = if ($fps -ge 66) { "real-time with headroom" } elseif ($fps -ge 60) { "just real-time" } else { "TOO SLOW for 60 fps" }
        Write-Host ("    => {0:N1} fps  ({1})" -f $fps, $verdict) -ForegroundColor $(if ($fps -ge 60) { "Green" } else { "Yellow" })
        $results += [pscustomobject]@{ Engine = $e; Fps = [math]::Round($fps, 1) }
        if ($name -eq "rife" -and $opt -and $fps -gt $vulkanBestFps) { $vulkanBest = [int]$opt; $vulkanBestFps = $fps }
    } elseif ($p.HasExited) {
        if ($name -eq "rife" -and $opt -and [int]$opt -gt 0) {
            # No such Vulkan device: the earlier ones were all there is
            Write-Host "    (no Vulkan device $opt)"; $vulkanDone = $true; continue
        }
        Write-Host "    => failed"
        $results += [pscustomobject]@{ Engine = $e; Fps = "failed" }
    }
    Write-Host ""
}
Remove-Item Env:\NOVAMOTION_ENGINE_CONFIG, Env:\NOVAMOTION_NO_FALLBACK, Env:\NOVAMOTION_NO_TUNING -ErrorAction SilentlyContinue
Write-Host "Summary (need 60+ fps for smooth 1080p playback):"
$results | Format-Table -AutoSize | Out-String | Write-Host
Stop-Transcript | Out-Null
