# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
<#
.SYNOPSIS
  Sets NOVA Motion up completely on this PC, or repairs what's missing. Run by build.cmd.

.DESCRIPTION
  Every step checks first and skips what is already done, so running it again is quick:
    1. .NET 8 SDK (offers to install it with winget)
    2. Player runtime: mpv, VapourSynth, RIFE (Vulkan) with its models, MVTools, yt-dlp
    3. NVIDIA TensorRT on cards that support it (RTX, GTX 16), and its RIFE models
    4. Tests, then the app build into dist\
    5. Tuning for this PC (TensorRT, or Vulkan RIFE and MVTools), redone when outdated or the
       graphics changed
    6. Start menu and desktop shortcuts
    7. A summary of what's ready
#>
[CmdletBinding()]
param(
    [switch]$Yes,           # answer every question with the default (yes)
    [switch]$NoTensorRT,    # never install TensorRT
    [switch]$NoTune,        # don't tune now (tune.cmd later)
    [switch]$NoShortcuts,
    [switch]$CheckOnly      # change nothing: only report what is and isn't set up
)
$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"
$Root    = Split-Path -Parent $PSScriptRoot
$Runtime = Join-Path $Root "runtime"
$MpvDir  = Join-Path $Runtime "mpv"
$Plugins = Join-Path $MpvDir "vs-plugins"
$Dist    = Join-Path $Root "dist"
$TuningVersion = 2
$RifeModels = @("rife-v4.6_ensembleFalse", "rife-v4.26_ensembleFalse", "rife-v4.25-lite_ensembleFalse", "rife-v4.22_lite_ensembleFalse")
$TrtModels  = @("rife_v4.6.onnx", "rife_v4.22_lite.onnx", "rife_v4.25_lite.onnx", "rife_v4.26.onnx")
Set-Location $Root

function Step([string]$t) { Write-Host ""; Write-Host "==> $t" -ForegroundColor Cyan }
function Ok([string]$t)   { Write-Host "    [ok] $t" -ForegroundColor Green }
function Note([string]$t) { Write-Host "    $t" }
function Warn([string]$t) { Write-Host "    [!] $t" -ForegroundColor Yellow }
function Ask([string]$q) {
    if ($Yes) { Note "$q yes (-Yes)"; return $true }
    $a = Read-Host "    $q [Y/n]"
    return ($a -eq "" -or $a -match '^(y|yes)$')
}
function Run-Script([string]$name, [string[]]$a = @()) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $name) @a
    return $LASTEXITCODE
}

# ---------------------------------------------------------------- what's there
function Get-State {
    $gpuNames = @((Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue).Name)
    $nvidia = $gpuNames | Where-Object { $_ -match 'NVIDIA' } | Select-Object -First 1
    $tuning = $null
    $tuningPath = Join-Path $Runtime "tuning.json"
    if (Test-Path $tuningPath) { try { $tuning = Get-Content $tuningPath -Raw | ConvertFrom-Json } catch { } }
    $trtInstalled = (Test-Path (Join-Path $Plugins "vstrt.dll")) -and (Test-Path (Join-Path $MpvDir "vsmlrt.py"))
    $onnxDir = Join-Path $Plugins "models\rife"
    [pscustomobject]@{
        Gpus         = $gpuNames
        Nvidia       = $nvidia
        TrtCapable   = [bool]($nvidia -and ($nvidia -match 'RTX' -or $nvidia -match 'GTX\s*16\d\d'))
        Sdk          = [bool](& { try { dotnet --list-sdks 2>$null | Where-Object { $_ -match '^(8|9|1\d)\.' } } catch { } })
        Mpv          = Test-Path (Join-Path $MpvDir "mpv.exe")
        VapourSynth  = (Test-Path (Join-Path $MpvDir "VSScript.dll")) -and (Test-Path (Join-Path $MpvDir "VSPipe.exe"))
        RifeVulkan   = Test-Path (Join-Path $Plugins "librife.dll")
        RifeModels   = @($RifeModels | Where-Object { Test-Path (Join-Path $Runtime "models\$_\flownet.param") })
        MvTools      = [bool](Get-ChildItem $Plugins -Filter "libmvtools*.dll" -ErrorAction SilentlyContinue)
        YtDlp        = Test-Path (Join-Path $MpvDir "yt-dlp.exe")
        TensorRt     = $trtInstalled
        TrtModels    = @($TrtModels | Where-Object { Test-Path (Join-Path $onnxDir $_) })
        App          = Test-Path (Join-Path $Dist "NovaMotion.exe")
        Tuning       = $tuning
    }
}

function Tuning-Problem($s) {
    $t = $s.Tuning
    if (-not $t) { return "not tuned yet" }
    if ([int]("0" + $t.version) -lt $TuningVersion) { return "tuned with an older test that overstated speed" }
    if ($s.TensorRt -and -not $t.sizes) { return "tuned before TensorRT was installed" }
    if (-not $s.TensorRt -and -not $t.vulkan) { return "tuned for TensorRT, which isn't installed now" }
    $tunedFor = "$($t.gpu)"
    $now = if ($s.TensorRt) { "$($s.Nvidia)" } else { ($s.Gpus -join ", ") }
    if ($tunedFor -and $now -and $tunedFor -ne $now) { return "tuned on different graphics ($tunedFor)" }
    return $null
}

function Show-Summary($s) {
    Step "NOVA Motion on this PC"
    Note ("Graphics:     " + ($s.Gpus -join ", "))
    $line = {
        param($ok, $name, $detail)
        $mark = if ($ok) { "[ok]" } else { "[--]" }
        $color = if ($ok) { "Green" } else { "Yellow" }
        Write-Host ("    {0} {1,-22} {2}" -f $mark, $name, $detail) -ForegroundColor $color
    }
    & $line ($s.Mpv -and $s.VapourSynth) "Player runtime" $(if ($s.Mpv -and $s.VapourSynth) { "mpv + VapourSynth" } else { "missing: run build.cmd" })
    & $line ($s.RifeVulkan -and $s.RifeModels.Count -gt 0) "RIFE on Vulkan" ("{0} of {1} models" -f $s.RifeModels.Count, $RifeModels.Count)
    & $line $s.MvTools "MVTools" $(if ($s.MvTools) { "fallback for any PC" } else { "missing" })
    & $line $s.YtDlp "yt-dlp" $(if ($s.YtDlp) { "YouTube and 1000+ sites" } else { "missing: online links won't play" })
    if ($s.TrtCapable -or $s.TensorRt) {
        & $line ($s.TensorRt -and $s.TrtModels.Count -gt 0) "RIFE on TensorRT" $(if ($s.TensorRt) { "{0} of {1} models" -f $s.TrtModels.Count, $TrtModels.Count } else { "not installed: setup-tensorrt.cmd (about 3 GB)" })
    } else {
        Note "    [  ] RIFE on TensorRT       not for this graphics card (needs NVIDIA RTX or GTX 16)"
    }
    $tp = Tuning-Problem $s
    & $line (-not $tp) "Tuning" $(if ($tp) { "${tp}: run tune.cmd" } else { "tuned $($s.Tuning.measured -replace 'T', ' ')" })
    & $line $s.App "App" $(if ($s.App) { "dist\NovaMotion.exe" } else { "not built yet" })
    if ($s.Tuning -and -not $tp) {
        if ($s.Tuning.sizes) {
            $key = @($s.Tuning.sizes.PSObject.Properties)[0]
            $best = $key.Value.PSObject.Properties | Sort-Object Value -Descending | Select-Object -First 1
            if ($best) { Note ("    Fastest AI option: {0} at {1}, {2:N0} frames a second (24 -> 60 needs 48)" -f $best.Name, $key.Name, [double]$best.Value) }
        } elseif ($s.Tuning.vulkan) {
            $best = $s.Tuning.vulkan.options.PSObject.Properties | Sort-Object Value -Descending | Select-Object -First 1
            if ($best) { Note ("    Fastest AI option: {0}, {1:N0} frames a second; MVTools {2:N0}" -f $best.Name, [double]$best.Value, [double]$s.Tuning.mvtools) }
        }
    }
}

$state = Get-State
if ($CheckOnly) { Show-Summary $state; exit 0 }

# ---------------------------------------------------------------- 1. .NET SDK
Step ".NET 8 SDK"
if ($state.Sdk) { Ok "installed" }
else {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget -and (Ask "The .NET 8 SDK is needed to build the app. Install it now with winget?")) {
        & winget install --id Microsoft.DotNet.SDK.8 --exact --silent --accept-source-agreements --accept-package-agreements
        # Pick up the new PATH without a new window
        $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")
        $state = Get-State
    }
    if (-not $state.Sdk) {
        Warn "Install the .NET 8 SDK (winget install Microsoft.DotNet.SDK.8, or https://dotnet.microsoft.com/download/dotnet/8.0, 'SDK x64'), then run build.cmd again."
        exit 1
    }
    Ok "installed"
}

# ---------------------------------------------------------------- 2. runtime
Step "Player runtime (mpv, VapourSynth, RIFE, MVTools, yt-dlp)"
$missing = @()
if (-not $state.Mpv) { $missing += "mpv" }
if (-not $state.VapourSynth) { $missing += "VapourSynth" }
if (-not $state.RifeVulkan) { $missing += "RIFE (Vulkan)" }
if ($state.RifeModels.Count -lt $RifeModels.Count) { $missing += "RIFE models" }
if (-not $state.MvTools) { $missing += "MVTools" }
if (-not $state.YtDlp) { $missing += "yt-dlp" }
if ($missing) {
    Note "missing: $($missing -join ', '); downloading (about 150 MB the first time)"
    if ((Run-Script "Get-Runtime.ps1") -ne 0) { Warn "The runtime download failed; see runtime\setup.log. Run build.cmd again to retry."; exit 1 }
    $state = Get-State
} else { Ok "all there" }
# Keep the runtime's engine files in step with the repo (edit engine\, rebuild, play)
Copy-Item (Join-Path $Root "engine\novamotion.vpy") $Runtime -Force
Copy-Item (Join-Path $Root "engine\novamotion_trt.py") $Runtime -Force
$portable = Join-Path $MpvDir "portable_config"
New-Item -ItemType Directory -Force $portable | Out-Null
Copy-Item (Join-Path $Root "engine\mpv\*") $portable -Recurse -Force
$old = Join-Path $portable "scripts\flowframe.lua"
if (Test-Path $old) { Remove-Item $old -Force }
Ok "engine files up to date"

# ---------------------------------------------------------------- 3. TensorRT
Step "NVIDIA TensorRT (the fastest AI engine)"
if (-not $state.TrtCapable) {
    Note "Not for this graphics card ($(($state.Gpus -join ', '))): RIFE runs on Vulkan, with MVTools as the fallback."
} elseif ($NoTensorRT) {
    Note "Skipped (-NoTensorRT). Run setup-tensorrt.cmd any time."
} elseif (-not $state.TensorRt) {
    if (Ask "$($state.Nvidia) supports TensorRT, which makes the AI much faster. Download it now (about 3 GB, one time)?") {
        if ((Run-Script "Get-TensorRT.ps1" @("-NoTune")) -ne 0) { Warn "TensorRT setup didn't finish; see runtime\setup-tensorrt.log. Vulkan RIFE still works." }
        $state = Get-State
    } else { Note "Skipped. Run setup-tensorrt.cmd any time." }
} elseif ($state.TrtModels.Count -lt $TrtModels.Count) {
    Note "adding the missing RIFE models (about 60 MB)"
    if ((Run-Script "Get-TensorRT.ps1" @("-ModelsOnly", "-NoTune")) -ne 0) { Warn "Some models couldn't be added; see runtime\setup-tensorrt.log." }
    $state = Get-State
} else { Ok "installed, $($state.TrtModels.Count) models" }

# ---------------------------------------------------------------- 4. tests and build
Step "Tests"
& dotnet run --project (Join-Path $Root "app\NovaMotion.SmokeTest") -c Release
if ($LASTEXITCODE -ne 0) { Warn "Tests failed; the messages above say which."; exit 1 }
Ok "passed"
Step "Building NOVA Motion into dist\"
Note "The first build on a PC downloads .NET packages and can take a few minutes; details go to build.log."
& dotnet publish (Join-Path $Root "app\NovaMotion.App") -c Release -r win-x64 -o $Dist *> (Join-Path $Root "build.log")
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $Root "build.log") -Tail 30; Warn "Build failed; full log in build.log."; exit 1 }
Ok "dist\NovaMotion.exe"
$state = Get-State

# ---------------------------------------------------------------- 5. tuning
Step "Tuning for this PC"
$problem = Tuning-Problem $state
if (-not $problem) { Ok "up to date ($($state.Tuning.measured -replace 'T', ' '))" }
elseif ($NoTune) { Note "$problem. Skipped (-NoTune): run tune.cmd any time." }
else {
    $how = if ($state.TensorRt) { "TensorRT models" } else { "RIFE on Vulkan and MVTools" }
    if (Ask "$problem. Tune now? It measures $how on this PC (10 to 20 minutes) so playback uses what it really keeps up with.") {
        if ((Run-Script "Tune.ps1") -ne 0) { Warn "Tuning didn't finish; run tune.cmd later. Playback still works with safe defaults." }
        $state = Get-State
    } else { Note "Skipped. Run tune.cmd any time; until then NOVA Motion uses safe defaults." }
}

# ---------------------------------------------------------------- 6. shortcuts
if (-not $NoShortcuts -and $state.App) {
    Step "Shortcuts"
    try {
        $shell = New-Object -ComObject WScript.Shell
        $targets = @(
            (Join-Path ([Environment]::GetFolderPath("Programs")) "NOVA Motion.lnk"),
            (Join-Path ([Environment]::GetFolderPath("Desktop")) "NOVA Motion.lnk"))
        foreach ($lnk in $targets) {
            $sc = $shell.CreateShortcut($lnk)
            $sc.TargetPath = Join-Path $Dist "NovaMotion.exe"
            $sc.WorkingDirectory = $Dist
            $sc.Description = "NOVA Motion: real-time AI motion smoothing"
            $sc.Save()
        }
        Ok "Start menu and desktop"
    } catch { Warn "Couldn't create shortcuts ($_). Start dist\NovaMotion.exe directly." }
}

# ---------------------------------------------------------------- 7. summary
Show-Summary $state
Write-Host ""
if ($state.App -and -not $Yes -and (Ask "Start NOVA Motion now?")) { Start-Process (Join-Path $Dist "NovaMotion.exe") }
exit 0
