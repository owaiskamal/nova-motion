# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
<#
.SYNOPSIS
  Measures each NOVA Motion engine at 1080p 25 -> 60 fps outside the player.
  Writes runtime\benchmark.log. Real-time playback needs >= 60 fps here.
#>
[CmdletBinding()]
param(
    [string[]]$Engines = @("rife_trt", "rife", "mvtools"),
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
foreach ($e in $Engines) {
    $cfg = Join-Path $Work "engine-$e.json"
    @{ enabled = $true; engine = $e; target = "display"; rife_model = "rife-v4.6_ensembleFalse"; demo = $false } |
        ConvertTo-Json | Set-Content -Encoding ASCII $cfg
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
    } elseif ($p.HasExited) {
        $results += [pscustomobject]@{ Engine = $e; Fps = "failed" }
    }
    Write-Host ""
}
Remove-Item Env:\NOVAMOTION_ENGINE_CONFIG -ErrorAction SilentlyContinue
Write-Host "Summary (need 60+ fps for smooth 1080p playback):"
$results | Format-Table -AutoSize | Out-String | Write-Host
Stop-Transcript | Out-Null
