# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
<#
.SYNOPSIS
  Plays a real video through the player in several settings, a few seconds each, and records how
  it went: frames made, frames dropped, what the graphics card was doing. Nothing is changed: each
  run uses its own copy of the settings. Results go to runtime\profile\<time>\summary.txt.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Video,
    [int]$Seconds = 25,
    [string]$Start = "",                 # where in the video (seconds); default: a third of the way in
    [string]$Only = ""                   # only these runs, e.g. "off,auto,auto-builtin"
)
$ErrorActionPreference = "Stop"
$Root    = Split-Path -Parent $PSScriptRoot
$Runtime = Join-Path $Root "runtime"
$Mpv     = Join-Path $Runtime "mpv\mpv.exe"
$Vpy     = Join-Path $Runtime "novamotion.vpy"
if (-not (Test-Path $Mpv)) { throw "The player isn't set up here: run build.cmd first." }
if (-not (Test-Path -LiteralPath $Video)) { throw "No such video: $Video" }
$Video = (Resolve-Path -LiteralPath $Video).Path
$Out = Join-Path $Runtime ("profile\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Summary = Join-Path $Out "summary.txt"
function Say($t) { Write-Host $t; Add-Content -Path $Summary -Value $t -Encoding UTF8 }

# --- the PC ---
Say "NOVA Studio playback profile, $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
Say "Video: $(Split-Path -Leaf $Video)"
try { Say ("Graphics: " + (((Get-CimInstance Win32_VideoController) | ForEach-Object { "$($_.Name) [$($_.DriverVersion)] $($_.CurrentHorizontalResolution)x$($_.CurrentVerticalResolution)@$($_.CurrentRefreshRate)" }) -join " | ")) } catch { }
try { $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1; Say "CPU: $($cpu.Name.Trim()), $($cpu.NumberOfCores) cores / $($cpu.NumberOfLogicalProcessors) threads" } catch { }
try {
    $bat = Get-CimInstance Win32_Battery | Select-Object -First 1
    if ($bat) { Say ("Power: " + $(if ($bat.BatteryStatus -eq 2) { "plugged in" } else { "ON BATTERY (status $($bat.BatteryStatus)): graphics cards run much slower on battery" }) + ", $($bat.EstimatedChargeRemaining)%") }
} catch { }
try { Say ("Power plan: " + ((powercfg /getactivescheme) -replace '^.*\((.*)\).*$', '$1')) } catch { }
$smi = @("$env:SystemRoot\System32\nvidia-smi.exe", "$env:ProgramFiles\NVIDIA Corporation\NVSMI\nvidia-smi.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($smi) { try { Say ("NVIDIA: " + (& $smi --query-gpu=name,driver_version,memory.total,memory.used,temperature.gpu,power.draw --format=csv,noheader)) } catch { } }
$userCfgPath = Join-Path $env:APPDATA "NovaMotion\engine.json"
$userCfg = @{}
if (Test-Path $userCfgPath) {
    Copy-Item $userCfgPath (Join-Path $Out "engine-as-set.json")
    try { (Get-Content $userCfgPath -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $userCfg[$_.Name] = $_.Value } } catch { }
}
foreach ($f in @("tuning.json")) { if (Test-Path (Join-Path $Runtime $f)) { Copy-Item (Join-Path $Runtime $f) $Out } }
Say ""

# --- the runs: each is the user's settings with a few changed ---
$cases = [ordered]@{
    "off"            = @{ enabled = $false }
    "as-set"         = @{ }
    "auto"           = @{ enabled = $true; engine = "rife_trt"; target = "display"; trt_model = "auto"; ai_scale = "auto"; quality_step = 0; rate_step = 0; drawn = $false; demo = $false }
    "auto-drawn"     = @{ enabled = $true; engine = "rife_trt"; target = "display"; trt_model = "auto"; ai_scale = "auto"; quality_step = 0; rate_step = 0; drawn = $true; demo = $false }
    "60"             = @{ enabled = $true; engine = "rife_trt"; target = "60"; trt_model = "auto"; ai_scale = "auto"; quality_step = 0; rate_step = 0; drawn = $false; demo = $false }
    "x2"             = @{ enabled = $true; engine = "rife_trt"; target = "x2"; trt_model = "auto"; ai_scale = "auto"; quality_step = 0; rate_step = 0; drawn = $false; demo = $false }
    "v4.6-half"      = @{ enabled = $true; engine = "rife_trt"; target = "display"; trt_model = "rife-v4.6"; ai_scale = 0.5; quality_step = 0; rate_step = 0; drawn = $false; demo = $false }
    "vulkan"         = @{ enabled = $true; engine = "rife"; target = "60"; quality_step = 0; rate_step = 0; drawn = $false; demo = $false }
}
# Laptops with two graphics chips: the same runs with the picture drawn by the built-in one, which
# leaves the NVIDIA card to the AI alone (decoding and drawing took about a quarter of a GTX 1650)
$builtIn = ""
try {
    $names = @((Get-CimInstance Win32_VideoController).Name)
    if ($names | Where-Object { $_ -match 'NVIDIA' }) { $builtIn = @($names | Where-Object { $_ -match 'Intel|AMD|Radeon' -and $_ -notmatch 'NVIDIA' })[0] }
} catch { }
if ($builtIn) {
    foreach ($base in @("off", "auto", "x2")) {
        $c = @{}; foreach ($k in $cases[$base].Keys) { $c[$k] = $cases[$base][$k] }
        $c["_mpv"] = @("--d3d11-adapter=$builtIn")
        $cases["$base-builtin"] = $c
    }
}
$Lua = Join-Path $PSScriptRoot "playback-profile.lua"
$env:NOVAMOTION_NO_FALLBACK = $null
Say ("{0,-12} {1,7} {2,7} {3,8} {4,7} {5,7} {6,7} {7,7} {8,6} {9,6}  {10}" -f "run", "src fps", "made", "screen", "dropped", "decoder", "mistime", "delayed", "gpu %", "temp", "engine")
foreach ($name in $cases.Keys) {
    if ($Only -and (($Only -split ",") -notcontains $name)) { continue }
    $cfg = @{}; foreach ($k in $userCfg.Keys) { $cfg[$k] = $userCfg[$k] }
    $extra = @()
    foreach ($k in $cases[$name].Keys) { if ($k -eq "_mpv") { $extra = @($cases[$name][$k]) } else { $cfg[$k] = $cases[$name][$k] } }
    $cfgFile = Join-Path $Out "$name.engine.json"; $state = Join-Path $Out "$name.state.json"; $csv = Join-Path $Out "$name.csv"
    [IO.File]::WriteAllText($cfgFile, ($cfg | ConvertTo-Json -Depth 6))
    $env:NOVAMOTION_ENGINE_CONFIG = $cfgFile; $env:NOVAMOTION_ENGINE_STATE = $state
    $gpuLog = Join-Path $Out "$name.gpu.csv"; $smiProc = $null
    if ($smi) { $smiProc = Start-Process -FilePath $smi -ArgumentList "--query-gpu=utilization.gpu,utilization.memory,memory.used,temperature.gpu,power.draw,clocks.gr", "--format=csv,noheader,nounits", "-lms", "1000" -RedirectStandardOutput $gpuLog -WindowStyle Hidden -PassThru }
    $mpvArgs = @("--fs", "--mute=yes", "--keep-open=no", "--idle=no", "--osd-level=1", "--save-position-on-quit=no",
              "--length=$Seconds", "--script=$Lua", "--log-file=$(Join-Path $Out "$name.mpv.log")",
              "--script-opts=novamotion-vpy=$Vpy,novamotion-config=$cfgFile,profile-out=$csv",
              "--osd-msg1=NOVA profile: $name")
    $mpvArgs += $(if ($Start) { "--start=$Start" } else { "--start=33%" })
    $mpvArgs += $extra
    $mpvArgs += @("--", $Video)
    $p = Start-Process -FilePath $Mpv -ArgumentList ($mpvArgs | ForEach-Object { '"' + $_ + '"' }) -PassThru
    if (-not $p.WaitForExit(($Seconds + 150) * 1000)) { try { $p.Kill() } catch { } }
    if ($smiProc) { try { $smiProc.Kill() } catch { } }

    # the middle of the run: the first seconds are the engine starting
    $rows = @(); if (Test-Path $csv) { $rows = @(Import-Csv $csv | Where-Object { $_.wall -notlike "#*" -and $_.'time-pos' }) }
    if ($rows.Count -lt 6) { Say ("{0,-12} no data ({1} samples): see $name.mpv.log" -f $name, $rows.Count); continue }
    $steady = @($rows | Select-Object -Skip ([Math]::Min(6, [int]($rows.Count / 3))))
    $first = $steady[0]; $last = $steady[-1]
    $span = [Math]::Max(1, [double]$last.wall - [double]$first.wall)
    function Rate($col) { try { [Math]::Round(([double]$last.$col - [double]$first.$col) / $span, 2) } catch { -1 } }
    function Mid($col) { $v = @($steady | ForEach-Object { try { [double]$_.$col } catch { } } | Sort-Object); if ($v.Count) { [Math]::Round($v[[int]($v.Count / 2)], 2) } else { -1 } }
    $gpuUse = ""; $temp = ""
    if (Test-Path $gpuLog) {
        $g = @(Get-Content $gpuLog | Where-Object { $_ -match '^\s*\d' } | Select-Object -Skip 6)
        if ($g.Count) {
            $u = @($g | ForEach-Object { [double]($_ -split ',')[0] } | Sort-Object); $gpuUse = $u[[int]($u.Count / 2)]
            $temp = (@($g | ForEach-Object { [double]($_ -split ',')[3] }) | Measure-Object -Maximum).Maximum
        }
    }
    $eng = ""; if (Test-Path $state) { try { $eng = ((Get-Content $state -Raw) -replace '\s+', ' ').Trim() } catch { } }
    Say ("{0,-12} {1,7} {2,7} {3,8} {4,7} {5,7} {6,7} {7,7} {8,6} {9,6}  {10}" -f $name, (Mid "container-fps"), (Mid "estimated-vf-fps"), (Mid "estimated-display-fps"),
        (Rate "frame-drop-count"), (Rate "decoder-frame-drop-count"), (Rate "mistimed-frame-count"), (Rate "vo-delayed-frame-count"), $gpuUse, $temp, $eng)
}
Say ""
Say "dropped / decoder / mistime / delayed are frames a second during the steady part of each run (0 is perfect)."
Say "Results: $Out"
