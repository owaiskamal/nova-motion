# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
<#
.SYNOPSIS
  Adds what Generate's "Animate a picture" needs: the depth model and three Python packages.

.DESCRIPTION
  Into <repo>\runtime\studio\ (about 150 MB):
    - Depth Anything V2 Small, ONNX (Apache-2.0), from Hugging Face (onnx-community)
    - numpy (BSD), Pillow (MIT-CMU) and onnxruntime-directml (MIT), from PyPI, checked against
      the SHA-256 pinned below
  They run on the runtime's own Python; DirectML works on any DirectX 12 graphics card.

    powershell -ExecutionPolicy Bypass -File setup\Get-StudioParts.ps1 [-Force]
#>
[CmdletBinding()]
param([switch]$Force, [switch]$PackagesOnly)

$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root     = Split-Path -Parent $PSScriptRoot
$Runtime  = Join-Path $Root "runtime"
$MpvDir   = Join-Path $Runtime "mpv"
$Studio   = Join-Path $Runtime "studio"
$Packages = Join-Path $Studio "packages"
$Cache    = Join-Path $Runtime ".cache"
$Python   = Join-Path $MpvDir "python.exe"
$ModelFile = Join-Path $Studio "depth-anything-v2-small.onnx"
$ModelUrl  = "https://huggingface.co/onnx-community/depth-anything-v2-small/resolve/main/onnx/model.onnx"

# Python 3.12, 64-bit Windows wheels
$Wheels = @(
    @{ Name = "numpy";                Version = "2.5.4";  Sha = "a7b1b6353e36a7e50de2973a38d705c88ee93adcf120673cee7f45a4a3fa223a" },
    @{ Name = "pillow";               Version = "13.0.0"; Sha = "ff3fe9eb621fbb9db4c4be33ab946f6bac5f98ab50ca8d3260503653656c961b" },
    @{ Name = "onnxruntime-directml"; Version = "1.24.4"; Sha = "f2ecb68b7b7b259d2ef3112ae760149f9b5a1e7c0fbb73d539da6250a648a614" }
)

New-Item -ItemType Directory -Force $Studio, $Packages, $Cache | Out-Null
$LogFile = Join-Path $Runtime "setup-studio.log"
Start-Transcript -Path $LogFile -Force | Out-Null
trap {
    Write-Host ""
    Send-NovaError "$_"
    Write-Host "SETUP FAILED: $_" -ForegroundColor Red
    Write-Host "Full log: $LogFile"
    Stop-Transcript | Out-Null
    exit 1
}

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Info($msg) { Write-Host "    $msg" }
. (Join-Path $PSScriptRoot "Progress.ps1")

if (-not (Test-Path $Python)) { throw "The player runtime isn't set up: run build.cmd first" }

Step "Python packages (numpy, Pillow, onnxruntime with DirectML)"
# -PackagesOnly (Enhance a photo): just numpy and Pillow, no depth model or DirectML runtime
$Wanted = if ($PackagesOnly) { @($Wheels | Where-Object { $_.Name -in "numpy", "pillow" }) } else { $Wheels }
# For the app's progress bar: numpy ~13 MB, Pillow ~7, onnxruntime-directml ~25, the depth model ~100, the check
Set-NovaSteps $(if ($PackagesOnly) { @(13, 7, 2) } else { @(13, 7, 25, 100, 3) })
foreach ($w in $Wanted) {
    $marker = Join-Path $Packages ("{0}-{1}.installed" -f $w.Name, $w.Version)
    Send-NovaStep "Adding $($w.Name) $($w.Version)"
    if ((Test-Path $marker) -and -not $Force) { Info "have    $($w.Name) $($w.Version)"; continue }
    $meta = Invoke-RestMethod -Uri "https://pypi.org/pypi/$($w.Name)/$($w.Version)/json" -UseBasicParsing
    $file = $meta.urls | Where-Object { $_.filename -match 'cp312-cp312-win_amd64\.whl$' } | Select-Object -First 1
    if (-not $file) { throw "$($w.Name) $($w.Version) has no Windows wheel for Python 3.12" }
    $dest = Join-Path $Cache $file.filename
    if (-not (Test-Path $dest) -or (Get-FileHash $dest -Algorithm SHA256).Hash -ne $w.Sha.ToUpper()) {
        Info ("download {0} ({1:N0} MB)" -f $file.filename, ($file.size / 1MB))
        Save-NovaDownload -Url $file.url -OutFile $dest -Label $w.Name
    }
    $hash = (Get-FileHash $dest -Algorithm SHA256).Hash
    if ($hash -ne $w.Sha.ToUpper()) { Remove-Item $dest -Force; throw "$($file.filename) doesn't match its pinned checksum" }
    # A wheel is a zip: unpacked into the folder, it is installed
    Get-ChildItem $Packages -Filter "$($w.Name -replace '-', '_')*" -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
    $zip = Join-Path $Cache ($file.filename + ".zip")
    Copy-Item $dest $zip -Force
    Expand-Archive -Path $zip -DestinationPath $Packages -Force
    Remove-Item $zip -Force
    Get-ChildItem $Packages -Filter ("{0}-*.installed" -f $w.Name) | Remove-Item -Force
    Set-Content $marker $w.Version
    Info "added   $($w.Name) $($w.Version)"
}

if ($PackagesOnly) {
    Send-NovaStep "Checking"
    & $Python -c "import sys; sys.path.insert(0, r'$Packages'); import numpy, PIL; print('numpy', numpy.__version__, 'Pillow', PIL.__version__)"
    if ($LASTEXITCODE -ne 0) { throw "numpy or Pillow doesn't load" }
    Stop-Transcript | Out-Null
    exit 0
}

Step "Depth Anything V2 Small (depth model, Apache-2.0)"
Send-NovaStep "Downloading the depth model (Depth Anything V2 Small)"
if ((Test-Path $ModelFile) -and (Get-Item $ModelFile).Length -gt 50MB -and -not $Force) {
    Info "have    $(Split-Path $ModelFile -Leaf)"
} else {
    $part = "$ModelFile.part"
    Info "download model.onnx (about 100 MB) from huggingface.co/onnx-community/depth-anything-v2-small"
    Save-NovaDownload -Url $ModelUrl -OutFile $part -Label "Depth model"
    if ((Get-Item $part).Length -lt 50MB) { Remove-Item $part -Force; throw "The depth model download is incomplete; try again" }
    Move-Item $part $ModelFile -Force
    Info "added   $(Split-Path $ModelFile -Leaf)"
}

Step "Check"
Send-NovaStep "Checking"
$check = "import sys; sys.path.insert(0, r'$Packages'); import numpy, PIL, onnxruntime as o; " +
         "s = o.InferenceSession(r'$ModelFile', providers=['CPUExecutionProvider']); " +
         "print('onnxruntime', o.__version__, ', '.join(o.get_available_providers()))"
& $Python -c $check
if ($LASTEXITCODE -ne 0) { throw "The packages or the model don't load" }
Copy-Item (Join-Path $Root "engine\parallax.py") $Runtime -Force

$versions = [ordered]@{ model = "depth-anything-v2-small" }
foreach ($w in $Wheels) { $versions[$w.Name] = $w.Version }
[IO.File]::WriteAllText((Join-Path $Studio "versions.json"), ($versions | ConvertTo-Json))

Write-Host ""
Write-Host "Animate a picture is ready: NOVA Studio, Generate." -ForegroundColor Green
Stop-Transcript | Out-Null
exit 0
