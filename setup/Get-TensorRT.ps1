# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
<#
.SYNOPSIS
  Optional: adds the RIFE TensorRT engine (NVIDIA RTX) to the NOVA Motion runtime.

.DESCRIPTION
  Downloads vs-mlrt (MIT/GPL; bundles NVIDIA TensorRT + CUDA runtime, ~2-3 GB),
  the RIFE ONNX models and the akarin plugin, then pre-builds TensorRT engines for
  1080p so the first play does not freeze while TensorRT optimises for your GPU.

  Run after setup\Get-Runtime.ps1:
    powershell -ExecutionPolicy Bypass -File setup\Get-TensorRT.ps1
#>
[CmdletBinding()]
param(
    [string[]]$Models = @("v4.6", "v4.22_lite", "v4.26"),
    [string[]]$Prebuild = @("v4.6", "v4.22_lite"),
    [switch]$KeepDownloads,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root     = Split-Path -Parent $PSScriptRoot
$Runtime  = Join-Path $Root "runtime"
$MpvDir   = Join-Path $Runtime "mpv"
$Plugins  = Join-Path $MpvDir "vs-plugins"
$OnnxDir  = Join-Path $Plugins "models\rife"
$Cache    = Join-Path $Runtime ".cache\trt"
$SevenZip = Join-Path $MpvDir "7z.exe"     # full 7-Zip (ships with VapourSynth portable); handles split archives
$LogFile  = Join-Path $Runtime "setup-tensorrt.log"
$Headers  = @{ "User-Agent" = "NovaMotion-Setup" }

New-Item -ItemType Directory -Force -Path $Cache, $OnnxDir | Out-Null
Start-Transcript -Path $LogFile -Force | Out-Null
trap {
    Write-Host ""
    Write-Host "TENSORRT SETUP FAILED: $_" -ForegroundColor Red
    Write-Host $_.InvocationInfo.PositionMessage
    Write-Host "Full log: $LogFile  (NOVA Motion keeps working with the Vulkan RIFE engine meanwhile)"
    Stop-Transcript | Out-Null
    exit 1
}

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Info($msg) { Write-Host "    $msg" }

if (-not (Test-Path (Join-Path $MpvDir "vapoursynth.dll"))) { throw "Run setup\Get-Runtime.ps1 first" }
if (-not (Test-Path $SevenZip)) { throw "7z.exe not found in $MpvDir" }

function Get-File([string]$Url, [string]$Name, [long]$Size = 0) {
    $dest = Join-Path $Cache $Name
    $have = if (Test-Path $dest) { (Get-Item $dest).Length } else { -1 }
    if ($have -ge 0 -and -not $Force -and ($Size -le 0 -or $have -eq $Size)) { Info "cached  $Name"; return $dest }
    if ($have -ge 0) { Info "incomplete $Name ($have of $Size bytes), downloading again"; Remove-Item $dest -Force }
    $mb = if ($Size -gt 0) { " ({0:N0} MB)" -f ($Size / 1MB) } else { "" }
    Info "download $Name$mb"
    # BITS handles multi-GB files and survives hiccups; poll it so the window shows progress
    try {
        $job = Start-BitsTransfer -Source $Url -Destination $dest -Asynchronous -ErrorAction Stop
        while ($job.JobState -in "Connecting", "Transferring", "Queued", "TransientError") {
            if ($job.BytesTotal -gt 0 -and $job.BytesTotal -ne [uint64]::MaxValue) {
                $pct = [math]::Floor(100 * $job.BytesTransferred / $job.BytesTotal)
                Write-Host ("`r    {0,3}%  {1:N0} / {2:N0} MB   " -f $pct, ($job.BytesTransferred / 1MB), ($job.BytesTotal / 1MB)) -NoNewline
            }
            Start-Sleep -Seconds 2
        }
        Write-Host ""
        if ($job.JobState -ne "Transferred") { $state = $job.JobState; Remove-BitsTransfer $job; throw "BITS ended in state $state" }
        Complete-BitsTransfer $job
    }
    catch {
        Info "background download unavailable ($_); using direct download"
        Invoke-WebRequest -Uri $Url -OutFile $dest -UseBasicParsing -Headers $Headers
    }
    if ($Size -gt 0 -and (Get-Item $dest).Length -ne $Size) { throw "$Name is incomplete ($((Get-Item $dest).Length) of $Size bytes); run setup again to resume" }
    return $dest
}

function Extract([string]$Archive, [string]$Dest, [string[]]$Include = @()) {
    $a7 = @("x", $Archive, "-o$Dest", "-y")
    foreach ($i in $Include) { $a7 += "-ir!$i" }
    & $SevenZip @a7 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7z failed on $Archive" }
}

# ---------------------------------------------------------------------------
Step "NVIDIA GPU check"
$gpus = (Get-CimInstance Win32_VideoController).Name
Info ($gpus -join ", ")
if (-not ($gpus -match "NVIDIA")) { throw "No NVIDIA GPU found; TensorRT needs one. NOVA Motion's Vulkan RIFE works on any GPU." }

Step "vs-mlrt release"
$rel = Invoke-RestMethod -Uri "https://api.github.com/repos/AmusementClub/vs-mlrt/releases/latest" -Headers $Headers
Info "vs-mlrt $($rel.tag_name): $(($rel.assets | ForEach-Object { $_.name }) -join ', ')"

# TensorRT-only package if this release has one, else the full CUDA package (may be split in parts)
# Packages over 2 GB are split into .7z.001, .7z.002, ... and every part is needed
$pkg = $rel.assets | Where-Object { $_.name -match '^vsmlrt-windows-x64-tensorrt\.v[\d.]+\.7z(\.\d{3})?$' }
if (-not $pkg) { $pkg = $rel.assets | Where-Object { $_.name -match '^vsmlrt-windows-x64-cuda\.v[\d.]+\.7z(\.\d{3})?$' } }
if (-not $pkg) { throw "No Windows TensorRT package in vs-mlrt $($rel.tag_name)" }
$pkg = $pkg | Sort-Object name
$sizeGB = [math]::Round((($pkg | Measure-Object size -Sum).Sum) / 1GB, 1)

Step "TensorRT runtime ($sizeGB GB, one-time download)"
$parts = foreach ($p in $pkg) { Get-File $p.browser_download_url $p.name $p.size }
$pkgX = Join-Path $Cache "pkg-x"
if ($Force -or -not (Get-ChildItem $pkgX -Recurse -Filter "vstrt.dll" -ErrorAction SilentlyContinue)) {
    Remove-Item $pkgX -Recurse -Force -ErrorAction SilentlyContinue
    Info "extracting (several minutes)..."
    Extract ($parts | Select-Object -First 1) $pkgX
}
$vstrt = Get-ChildItem $pkgX -Recurse -Filter "vstrt.dll" | Select-Object -First 1
if (-not $vstrt) { throw "vstrt.dll not found in the package" }
Copy-Item $vstrt.FullName $Plugins -Force
$cudaDir = Join-Path $vstrt.DirectoryName "vsmlrt-cuda"
if (-not (Test-Path $cudaDir)) { throw "vsmlrt-cuda runtime folder not found next to vstrt.dll" }
Info "installing CUDA/TensorRT runtime into vs-plugins\vsmlrt-cuda"
robocopy $cudaDir (Join-Path $Plugins "vsmlrt-cuda") /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copying vsmlrt-cuda failed (robocopy $LASTEXITCODE)" }

Step "vsmlrt.py"
$scripts = $rel.assets | Where-Object { $_.name -match '^scripts\.v[\d.]+\.7z$' } | Select-Object -First 1
if ($scripts) {
    $sx = Join-Path $Cache "scripts-x"
    Extract (Get-File $scripts.browser_download_url $scripts.name $scripts.size) $sx
    $py = Get-ChildItem $sx -Recurse -Filter "vsmlrt.py" | Select-Object -First 1
} else {
    $py = Get-ChildItem $pkgX -Recurse -Filter "vsmlrt.py" | Select-Object -First 1
}
if (-not $py) { throw "vsmlrt.py not found" }
Copy-Item $py.FullName $MpvDir -Force

Step "RIFE ONNX models: $($Models -join ', ')"
$want = $Models | ForEach-Object { "rife_$_.onnx" }
$missing = $want | Where-Object { -not (Test-Path (Join-Path $OnnxDir $_)) }
if ($missing) {
    # Prefer a RIFE-specific archive; otherwise pull just these files out of the big models archive
    $src = @($rel.assets | Where-Object { $_.name -match 'rife.*\.7z$' }) +
           @($rel.assets | Where-Object { $_.name -match '^(ext-)?models\.v[\d.]+\.7z$' })
    foreach ($a in $src) {
        if (-not $missing) { break }
        Info "looking in $($a.name)"
        $mx = Join-Path $Cache "models-x"
        Extract (Get-File $a.browser_download_url $a.name $a.size) $mx $missing
        foreach ($f in $missing) {
            $hit = Get-ChildItem $mx -Recurse -Filter $f | Where-Object { $_.FullName -notmatch 'rife_v2' } | Select-Object -First 1
            if ($hit) { Copy-Item $hit.FullName $OnnxDir -Force; Info "installed $f" }
        }
        $missing = $want | Where-Object { -not (Test-Path (Join-Path $OnnxDir $_)) }
    }
}
if ($missing) { Write-Warning "Not found in this vs-mlrt release: $($missing -join ', ')" }
if (-not (Get-ChildItem $OnnxDir -Filter "rife_*.onnx")) { throw "No RIFE ONNX models could be installed" }

Step "akarin plugin (exact 25->60 style frame rates without extra work)"
try {
    # vs-mlrt needs akarin v0.96g or newer; those builds are not always the release marked "latest"
    $aks = Invoke-RestMethod -Uri "https://api.github.com/repos/AkarinVS/vapoursynth-plugin/releases?per_page=40" -Headers $Headers
    $asset = $null
    foreach ($r in $aks) {
        foreach ($a in $r.assets) {
            if ($a.name -match '(win|amd64|x64).*\.(7z|zip)$' -and ($a.name + $r.tag_name) -match 'v?0\.(\d+)([a-z]?)') {
                $minor = [int]$Matches[1]; $sub = $Matches[2]
                if ($minor -gt 96 -or ($minor -eq 96 -and $sub -ge 'g')) { $asset = $a; break }
            }
        }
        if ($asset) { Info "akarin $($r.tag_name) -> $($asset.name)"; break }
    }
    if (-not $asset) { throw "no akarin build v0.96g or newer found" }
    Get-ChildItem $Plugins -Filter "*akarin*.dll" | Remove-Item -Force   # drop the older one
    if ($asset) {
        $ax = Join-Path $Cache "akarin-x"
        Extract (Get-File $asset.browser_download_url $asset.name $asset.size) $ax
        $dll = Get-ChildItem $ax -Recurse -Filter "*akarin*.dll" | Select-Object -First 1
        if ($dll) { Copy-Item $dll.FullName $Plugins -Force; Info "installed $($dll.Name)" }
    }
} catch { Write-Warning "akarin not installed ($_). TensorRT still works; non-whole rate steps cost a little more GPU." }

Step "Tuning for this PC: building and timing each model at 1080p (about 10 minutes)"
& (Join-Path $PSScriptRoot "Tune.ps1")
if ($LASTEXITCODE -ne 0) { Write-Warning "Tuning did not finish; run tune.cmd later. TensorRT still works with the default model." }

if (-not $KeepDownloads) {
    Info "removing downloaded archives (use -KeepDownloads to keep them)"
    Remove-Item $Cache -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "TensorRT ready. In NOVA Motion choose Engine: 'RIFE TensorRT (NVIDIA, fastest)'." -ForegroundColor Green
Write-Host "Other sizes build their engine on first play, or ahead of time: prepare-engines.cmd 720p 1440p 4K"
Stop-Transcript | Out-Null
