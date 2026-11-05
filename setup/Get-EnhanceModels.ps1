# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
<#
.SYNOPSIS
  Adds what Enhance's AI clean-up and upscaling need (about 150 MB).

.DESCRIPTION
  Into runtime\mpv\vs-plugins\models\ (enhance, cugan, RealESRGANv2, dpir), from this project's
  "enhance-models-v1" release, each file checked against the release's SHA256SUMS:
    - Real-ESRGAN general, three strengths (BSD-3-Clause): film and live action
    - Real-CUGAN (MIT) and Real-ESRGAN AnimeVideo v3 (BSD-3-Clause): anime and art
    - DPIR deblocking (MIT): compressed downloads
  On PCs without NVIDIA TensorRT it also adds vs-mlrt's Vulkan runtime (vsncnn, GPL-3.0) and
  vsmlrt.py, so the models run on any modern graphics card.

    powershell -ExecutionPolicy Bypass -File setup\Get-EnhanceModels.ps1 [-Force]
#>
[CmdletBinding()]
param([switch]$Force, [string]$Tag = "enhance-models-v2", [switch]$NoPrepare)

$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root     = Split-Path -Parent $PSScriptRoot
$Runtime  = Join-Path $Root "runtime"
$MpvDir   = Join-Path $Runtime "mpv"
$Plugins  = Join-Path $MpvDir "vs-plugins"
$Models   = Join-Path $Plugins "models"
$Cache    = Join-Path $Runtime ".cache\enhance"
$Headers  = @{ "User-Agent" = "NovaMotion-Setup" }
$Repo     = "owaiskamal/nova-motion"

# Where each model goes, by name: the folders vs-mlrt looks in
function Folder-For([string]$name) {
    if ($name -like "realesr-general*" -or $name -like "RealESRGAN_x4plus*") { return "enhance" }
    if ($name -like "up2x-*" -or $name -like "pro-*") { return "cugan" }
    if ($name -like "realesr-animevideo*") { return "RealESRGANv2" }
    if ($name -like "drunet*") { return "dpir" }
    return $null
}

New-Item -ItemType Directory -Force $Cache, $Models | Out-Null
$LogFile = Join-Path $Runtime "setup-enhance.log"
Start-Transcript -Path $LogFile -Force | Out-Null
trap {
    Write-Host ""
    Write-Host "ENHANCE SETUP FAILED: $_" -ForegroundColor Red
    Write-Host "Full log: $LogFile  (smoothing and exports without AI enhance keep working)"
    Stop-Transcript | Out-Null
    exit 1
}

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Info($msg) { Write-Host "    $msg" }

if (-not (Test-Path (Join-Path $MpvDir "vapoursynth.dll"))) { throw "The player runtime isn't set up: run build.cmd first" }
$SevenZip = @((Join-Path $MpvDir "7z.exe"), (Join-Path $Runtime ".cache\7zr.exe")) | Where-Object { Test-Path $_ } | Select-Object -First 1

Step "Enhance models ($Tag)"
try { $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/tags/$Tag" -Headers $Headers }
catch {
    # v2 adds the Max detail models for photos; until it's published, v1 has everything else
    if ($Tag -ne "enhance-models-v1") {
        Info "$Tag isn't published yet; using enhance-models-v1 (everything but Max detail)"
        $Tag = "enhance-models-v1"
        try { $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/tags/$Tag" -Headers $Headers }
        catch { throw "The Enhance models aren't published yet. Smoothing works meanwhile." }
    } else { throw "The Enhance models ($Tag) aren't published yet. Smoothing works meanwhile." }
}
$sumsAsset = $rel.assets | Where-Object { $_.name -eq "SHA256SUMS" } | Select-Object -First 1
if (-not $sumsAsset) { throw "$Tag has no SHA256SUMS" }
$sums = @{}
# Saved to a file and read as text: Windows PowerShell hands a download without a text type back as
# raw bytes, which split into numbers instead of lines
$sumsFile = Join-Path $Cache "SHA256SUMS"
Invoke-WebRequest -Uri $sumsAsset.browser_download_url -OutFile $sumsFile -UseBasicParsing -Headers $Headers
foreach ($line in (Get-Content $sumsFile)) {
    if ($line.Trim() -match '^([0-9a-fA-F]{64})\s+\*?(\S+)$') { $sums[$Matches[2]] = $Matches[1].ToUpper() }
}
if ($sums.Count -eq 0) { throw "SHA256SUMS of $Tag couldn't be read" }
foreach ($a in $rel.assets | Where-Object { $_.name -like "*.onnx" }) {
    $folder = Folder-For $a.name
    if (-not $folder) { Info "skip    $($a.name)"; continue }
    if (-not $sums.ContainsKey($a.name)) { throw "$($a.name) isn't listed in SHA256SUMS" }
    $dest = Join-Path (Join-Path $Models $folder) $a.name
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    if ((Test-Path $dest) -and -not $Force -and (Get-FileHash $dest -Algorithm SHA256).Hash -eq $sums[$a.name]) { Info "have    $folder\$($a.name)"; continue }
    Info ("download {0} ({1:N1} MB)" -f $a.name, ($a.size / 1MB))
    $part = "$dest.part"
    Invoke-WebRequest -Uri $a.browser_download_url -OutFile $part -UseBasicParsing -Headers $Headers
    if ((Get-FileHash $part -Algorithm SHA256).Hash -ne $sums[$a.name]) { Remove-Item $part -Force; throw "$($a.name) doesn't match its checksum" }
    Move-Item $part $dest -Force
    # A changed model needs its TensorRT engine built again
    Get-ChildItem (Split-Path $dest) -Filter "$($a.name)*.engine" -ErrorAction SilentlyContinue | Remove-Item -Force
}

$hasTrt = Test-Path (Join-Path $Plugins "vstrt.dll")
$hasNcnn = [bool](Get-ChildItem $Plugins -Filter "vsncnn*.dll" -ErrorAction SilentlyContinue)
$hasScript = Test-Path (Join-Path $MpvDir "vsmlrt.py")
if (-not $hasScript -or (-not $hasTrt -and -not $hasNcnn)) {
    Step "vs-mlrt on Vulkan (runs the models on any modern graphics card)"
    if (-not $SevenZip) { throw "7-Zip not found in the runtime: run build.cmd again" }
    $releases = Invoke-RestMethod -Uri "https://api.github.com/repos/AmusementClub/vs-mlrt/releases?per_page=15" -Headers $Headers
    # A stable release with both the Vulkan plugin and the scripts; test builds only if there's none
    $usable = $releases | Where-Object { -not $_.draft -and ($_.assets | Where-Object { $_.name -match '^VSNCNN-Windows-x64.*\.7z$' }) -and
                                         ($_.assets | Where-Object { $_.name -match '^scripts\..+\.7z$' }) }
    $rel = @($usable | Where-Object { -not $_.prerelease }) + @($usable) | Select-Object -First 1
    if (-not $rel) { throw "No Vulkan (vsncnn) build found in vs-mlrt's recent releases" }
    Info "vs-mlrt $($rel.tag_name)"
    if (-not $hasTrt -and -not $hasNcnn) {
        $a = $rel.assets | Where-Object { $_.name -match '^VSNCNN-Windows-x64.*\.7z$' } | Select-Object -First 1
        $file = Join-Path $Cache $a.name
        Invoke-WebRequest -Uri $a.browser_download_url -OutFile $file -UseBasicParsing -Headers $Headers
        $x = Join-Path $Cache "ncnn-x"
        & $SevenZip x $file "-o$x" -y | Out-Null
        $dll = Get-ChildItem $x -Recurse -Filter "vsncnn*.dll" | Select-Object -First 1
        if (-not $dll) { throw "vsncnn.dll not found in $($a.name)" }
        Copy-Item $dll.FullName $Plugins -Force
        Info "installed $($dll.Name)"
    }
    if (-not $hasScript) {
        $a = $rel.assets | Where-Object { $_.name -match '^scripts\..+\.7z$' } | Select-Object -First 1
        if (-not $a) { throw "vsmlrt.py not found in vs-mlrt $($rel.tag_name)" }
        $file = Join-Path $Cache $a.name
        Invoke-WebRequest -Uri $a.browser_download_url -OutFile $file -UseBasicParsing -Headers $Headers
        $x = Join-Path $Cache "scripts-x"
        & $SevenZip x $file "-o$x" -y | Out-Null
        $py = Get-ChildItem $x -Recurse -Filter "vsmlrt.py" | Select-Object -First 1
        if (-not $py) { throw "vsmlrt.py not found in $($a.name)" }
        Copy-Item $py.FullName $MpvDir -Force
        Info "installed vsmlrt.py"
    }
} else {
    Info "runs on $(if ($hasTrt) { 'TensorRT' } else { 'Vulkan (vsncnn)' })"
}
Copy-Item (Join-Path $Root "engine\enhance.py") $Runtime -Force
Copy-Item (Join-Path $Root "engine\enhance_build.py") $Runtime -Force
Copy-Item (Join-Path $Root "engine\jobs.py") $Runtime -Force
Copy-Item (Join-Path $Root "engine\photo.py") $Runtime -Force

# Enhance a photo reads and writes pictures with numpy and Pillow (shared with Animate a picture)
Step "Picture support for Enhance a photo (numpy, Pillow)"
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "Get-StudioParts.ps1") -PackagesOnly
if ($LASTEXITCODE -ne 0) { Write-Warning "numpy and Pillow couldn't be added; Enhance a photo needs them (videos don't)." }

# TensorRT prepares each model for this graphics card once. Done here, so the first export doesn't wait
if ((Test-Path (Join-Path $Plugins "vstrt.dll")) -and -not $NoPrepare) {
    Step "Preparing every model for this graphics card, so no setting has to wait later (on GTX 16 cards about 3-6 minutes a model, 10-20 for the anime ones, once)"
    & (Join-Path $MpvDir "python.exe") (Join-Path $Runtime "enhance_build.py")
    if ($LASTEXITCODE -ne 0) { Write-Warning "Not all models were prepared; the first export with them prepares them instead." }
}

Remove-Item $Cache -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "AI enhance is ready: NOVA Studio, Enhance. The first export with each model prepares it for this graphics card (a few minutes, once)." -ForegroundColor Green
Stop-Transcript | Out-Null
exit 0
