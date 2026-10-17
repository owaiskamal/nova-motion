# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
<#
.SYNOPSIS
  Downloads and assembles the NOVA Motion playback runtime (all open-source parts).

.DESCRIPTION
  Builds  <repo>\runtime\mpv\  containing:
    - mpv (shinchiro Windows build)                 GPL/LGPL
    - Python 3.12 embeddable                         PSF
    - VapourSynth portable (pinned, default R70)     LGPL
    - RIFE ncnn-Vulkan VapourSynth plugin + models   MIT
    - MVTools for VapourSynth                        GPL
    - yt-dlp                                         Unlicense
  Then copies NOVA Motion's own mpv config + engine script into place.

  Run from any folder:
    powershell -ExecutionPolicy Bypass -File setup\Get-Runtime.ps1
#>
[CmdletBinding()]
param(
    [string]$PythonVersion   = "3.12.9",
    [string]$VapourSynthTag  = "R70",
    [string]$RifeRepo        = "styler00dollar/VapourSynth-RIFE-ncnn-Vulkan",
    [string]$MvToolsRepo     = "dubhater/vapoursynth-mvtools",
    [string]$MpvRepo         = "shinchiro/mpv-winbuild-cmake",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"   # makes Invoke-WebRequest ~10x faster on PS 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root      = Split-Path -Parent $PSScriptRoot
$Runtime   = Join-Path $Root "runtime"
$MpvDir    = Join-Path $Runtime "mpv"
$Plugins   = Join-Path $MpvDir "vs-plugins"
$Models    = Join-Path $Runtime "models"
$Cache     = Join-Path $Runtime ".cache"
$SevenZip  = Join-Path $Cache "7zr.exe"

New-Item -ItemType Directory -Force -Path $Runtime | Out-Null
$LogFile = Join-Path $Runtime "setup.log"
Start-Transcript -Path $LogFile -Force | Out-Null
trap {
    Write-Host ""
    Write-Host "SETUP FAILED: $_" -ForegroundColor Red
    Write-Host $_.InvocationInfo.PositionMessage
    Write-Host "Full log: $LogFile"
    Stop-Transcript | Out-Null
    exit 1
}

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Info($msg) { Write-Host "    $msg" }

# GitHub API calls: anonymous use is limited to 60 an hour per IP, which shared CI machines hit.
# A token (CI sets GITHUB_TOKEN) lifts that; it is only ever sent to api.github.com.
$ApiHeaders = @{ "User-Agent" = "NovaMotion-Setup" }
if ($env:GITHUB_TOKEN) { $ApiHeaders["Authorization"] = "Bearer $env:GITHUB_TOKEN" }

function Get-File([string]$Url, [string]$Name) {
    $dest = Join-Path $Cache $Name
    if ((Test-Path $dest) -and -not $Force) { Info "cached  $Name"; return $dest }
    Info "download $Url"
    Invoke-WebRequest -Uri $Url -OutFile $dest -UseBasicParsing -Headers @{ "User-Agent" = "NovaMotion-Setup" }
    return $dest
}

function Get-Release([string]$Repo, [string]$Tag = "latest") {
    $api = if ($Tag -eq "latest") { "https://api.github.com/repos/$Repo/releases/latest" }
           else { "https://api.github.com/repos/$Repo/releases/tags/$Tag" }
    return Invoke-RestMethod -Uri $api -Headers $ApiHeaders
}

function Get-NewestAssetAcrossReleases([string]$Repo, [string]$Pattern) {
    # Some projects stop attaching Windows builds to newer releases; take the newest one that has it
    $rels = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases?per_page=30" -Headers $ApiHeaders
    foreach ($rel in $rels) {
        $asset = $rel.assets | Where-Object { $_.name -match $Pattern } | Select-Object -First 1
        if ($asset) {
            Info "$Repo $($rel.tag_name) -> $($asset.name)"
            return Get-File $asset.browser_download_url $asset.name
        }
    }
    throw "No release of $Repo has an asset matching '$Pattern'"
}

function Get-ReleaseAsset([string]$Repo, [string]$Pattern, [string]$Tag = "latest") {
    $rel = Get-Release $Repo $Tag
    $asset = $rel.assets | Where-Object { $_.name -match $Pattern } | Select-Object -First 1
    if (-not $asset) {
        $names = ($rel.assets | ForEach-Object { $_.name }) -join ", "
        throw "No asset matching '$Pattern' in $Repo ($($rel.tag_name)). Available: $names"
    }
    Info "$Repo $($rel.tag_name) -> $($asset.name)"
    return Get-File $asset.browser_download_url $asset.name
}

function Expand-Any([string]$Archive, [string]$Dest) {
    New-Item -ItemType Directory -Force -Path $Dest | Out-Null
    if ($Archive -match '\.zip$') {
        Expand-Archive -Path $Archive -DestinationPath $Dest -Force
    } elseif ($Archive -match '\.7z$') {
        & $SevenZip x $Archive "-o$Dest" -y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7zr failed on $Archive" }
    } else {
        Copy-Item $Archive $Dest -Force
    }
}

# ---------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $Runtime, $MpvDir, $Plugins, $Models, $Cache | Out-Null

Step "7-Zip extractor"
$null = Get-File "https://www.7-zip.org/a/7zr.exe" "7zr.exe"

Step "mpv"
$mpvArchive = Get-ReleaseAsset $MpvRepo '^mpv-x86_64-\d{8}-git-[0-9a-f]+\.7z$'
$tmp = Join-Path $Cache "mpv-x"
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Expand-Any $mpvArchive $tmp
Copy-Item (Join-Path $tmp "*") $MpvDir -Recurse -Force

Step "Python $PythonVersion (embeddable)"
$py = Get-File "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip" "python-$PythonVersion-embed-amd64.zip"
Expand-Any $py $MpvDir

Step "VapourSynth $VapourSynthTag (portable)"
$vs = Get-ReleaseAsset "vapoursynth/vapoursynth" 'VapourSynth64-Portable-.*\.(zip|7z)$' $VapourSynthTag
Expand-Any $vs $MpvDir
# R70+ ships the Python module + core DLL as a wheel; a wheel is a zip, so unpack it beside python.exe
$pyTag = "cp" + ($PythonVersion.Split('.')[0..1] -join '')
$wheel = Get-ChildItem (Join-Path $MpvDir "wheel") -Filter "*-$pyTag-*.whl" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($wheel) {
    $wheelZip = Join-Path $Cache ($wheel.BaseName + ".zip")
    Copy-Item $wheel.FullName $wheelZip -Force
    Expand-Archive -Path $wheelZip -DestinationPath $MpvDir -Force
    # The core vapoursynth.dll sits in the wheel's data folder; it must live next to the .pyd
    Get-ChildItem $MpvDir -Recurse -Filter "vapoursynth.dll" | Where-Object { $_.DirectoryName -ne $MpvDir } |
        ForEach-Object { Copy-Item $_.FullName $MpvDir -Force }
    Info "installed $($wheel.Name)"
} elseif (-not (Get-ChildItem $MpvDir -Filter "vapoursynth*.pyd")) {
    throw "VapourSynth Python module for $pyTag not found in the portable package"
}
# Marker file that makes VapourSynth run in portable mode (autoloads .\vs-plugins)
New-Item -ItemType File -Force -Path (Join-Path $MpvDir "portable.vs") | Out-Null

Step "RIFE ncnn-Vulkan plugin + models"
$rel = Get-Release $RifeRepo
$rifeX = Join-Path $Cache "rife-x"
Remove-Item $rifeX -Recurse -Force -ErrorAction SilentlyContinue
$wanted = $rel.assets | Where-Object { $_.name -match '(win|windows).*\.(dll|zip|7z)$' -or $_.name -match '^models.*\.(zip|7z)$' }
if (-not $wanted) { throw "No Windows assets found in $RifeRepo $($rel.tag_name)" }
foreach ($a in $wanted) {
    Info "$RifeRepo $($rel.tag_name) -> $($a.name)"
    Expand-Any (Get-File $a.browser_download_url $a.name) $rifeX
}
$rifeDll = Get-ChildItem $rifeX -Recurse -Filter "*rife*.dll" | Select-Object -First 1
if (-not $rifeDll) { throw "RIFE plugin DLL not found in downloaded assets" }
Copy-Item $rifeDll.FullName (Join-Path $Plugins "librife.dll") -Force
# Every folder that holds a flownet.param is a model
Get-ChildItem $rifeX -Recurse -Filter "flownet.param" | ForEach-Object {
    $dir = $_.Directory
    Copy-Item $dir.FullName (Join-Path $Models $dir.Name) -Recurse -Force
}

# Model files live in the repository source, not in the release downloads: fetch just the ones we use
$wantModels = @("rife-v4.6_ensembleFalse", "rife-v4.26_ensembleFalse", "rife-v4.25-lite_ensembleFalse", "rife-v4.22_lite_ensembleFalse")
$tree = Invoke-RestMethod -Uri "https://api.github.com/repos/$RifeRepo/git/trees/$($rel.tag_name)?recursive=1" -Headers $ApiHeaders
foreach ($m in $wantModels) {
    if (Test-Path (Join-Path $Models "$m\flownet.bin")) { Info "cached  model $m"; continue }
    $files = $tree.tree | Where-Object { $_.type -eq "blob" -and $_.path -match "(^|/)models/$([regex]::Escape($m))/[^/]+$" }
    if (-not $files) { Info "model $m not in this release, skipping"; continue }
    $dest = Join-Path $Models $m
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    foreach ($f in $files) {
        $name = Split-Path $f.path -Leaf
        $url = "https://raw.githubusercontent.com/$RifeRepo/$($rel.tag_name)/$($f.path)"
        Info "model $m/$name"
        Invoke-WebRequest -Uri $url -OutFile (Join-Path $dest $name) -UseBasicParsing -Headers @{ "User-Agent" = "NovaMotion-Setup" }
    }
}
$modelCount = (Get-ChildItem $Models -Directory | Where-Object { Test-Path (Join-Path $_.FullName "flownet.param") }).Count
if ($modelCount -eq 0) { Write-Warning "No RIFE models installed; NOVA Motion will fall back to MVTools" }
Info "$modelCount RIFE model(s) installed in runtime\models"

Step "MVTools"
$mv = Get-NewestAssetAcrossReleases $MvToolsRepo 'win64\.(7z|zip)$'
$mvX = Join-Path $Cache "mv-x"
Remove-Item $mvX -Recurse -Force -ErrorAction SilentlyContinue
Expand-Any $mv $mvX
$mvDll = Get-ChildItem $mvX -Recurse -Filter "libmvtools*.dll" | Select-Object -First 1
if (-not $mvDll) { throw "libmvtools.dll not found" }
Copy-Item $mvDll.FullName $Plugins -Force

Step "yt-dlp"
$ytdlp = Get-ReleaseAsset "yt-dlp/yt-dlp" '^yt-dlp\.exe$'
Copy-Item $ytdlp (Join-Path $MpvDir "yt-dlp.exe") -Force

Step "NOVA Motion config + engine"
$portable = Join-Path $MpvDir "portable_config"
New-Item -ItemType Directory -Force -Path $portable | Out-Null
Copy-Item (Join-Path $Root "engine\mpv\*") $portable -Recurse -Force
Copy-Item (Join-Path $Root "engine\novamotion.vpy") $Runtime -Force
Copy-Item (Join-Path $Root "engine\novamotion_trt.py") $Runtime -Force

Step "Self-test"
$vspipe = Join-Path $MpvDir "VSPipe.exe"
if (Test-Path $vspipe) {
    $test = Join-Path $Cache "selftest.vpy"
    @"
import vapoursynth as vs
core = vs.core
print('VapourSynth', core.version_number())
print('rife    :', hasattr(core, 'rife'))
print('mvtools :', hasattr(core, 'mv'))
core.std.BlankClip().set_output()
"@ | Set-Content -Encoding ASCII $test
    # PS 5.1 turns native stderr into terminating errors under "Stop"; the self-test is informational
    $ErrorActionPreference = "Continue"
    try { & $vspipe --info $test - 2>&1 | ForEach-Object { Info "$_" } }
    catch { Info "self-test could not run: $_" }
    $ErrorActionPreference = "Stop"
} else {
    Info "VSPipe.exe not found; skipping self-test"
}

Write-Host ""
Write-Host "Runtime ready: $MpvDir" -ForegroundColor Green
Stop-Transcript | Out-Null
Write-Host "Try:  runtime\mpv\mpv.exe --vf=vapoursynth=`"$Runtime\novamotion.vpy`" <video file or YouTube URL>"
