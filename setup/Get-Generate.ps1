# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
<#
.SYNOPSIS
  Adds generative detail for photos: SeedVR2 (ByteDance, Apache-2.0) and what runs it. About 8 GB.

.DESCRIPTION
  Into <repo>\runtime\generate\:
    - uv (Astral, MIT/Apache-2.0), pinned, which brings a Python 3.12 of its own
    - PyTorch for CUDA 12.8 and the packages in setup\generate-requirements.txt, pinned
    - SeedVR2's runner (numz/ComfyUI-SeedVR2_VideoUpscaler, Apache-2.0) at a pinned commit
    - the SeedVR2 3B model (fp8) and its VAE, checked against the SHA-256 the runner lists
  Needs an NVIDIA graphics card with 4 GB or more. Cards under 8 GB get the 4-bit model (about 2 GB):
  measured on a GTX 1650 (4 GB), about 90 seconds a megapixel; the RTX 4070's 12 GB runs the 8-bit one.

    powershell -ExecutionPolicy Bypass -File setup\Get-Generate.ps1 [-Force]
#>
[CmdletBinding()]
param([switch]$Force, [ValidateSet("", "fp8", "q4")] [string]$Model = "", [string]$Root = "")

$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
$Runtime  = Join-Path $Root "runtime"
$Gen      = Join-Path $Runtime "generate"
$Cache    = Join-Path $Runtime ".cache\generate"
$Venv     = Join-Path $Gen ".venv"
$Python   = Join-Path $Venv "Scripts\python.exe"
$Runner   = Join-Path $Gen "runner"
$Models   = Join-Path $Gen "models"
$Ready    = Join-Path $Gen "ready.txt"
$Headers  = @{ "User-Agent" = "NovaMotion-Setup" }

$UvVersion    = "0.9.9"
$RunnerCommit = "4490bd1f482e026674543386bb2a4d176da245b9"
$TorchIndex   = "https://download.pytorch.org/whl/cu128"
$Torch        = @("torch==2.8.0", "torchvision==0.23.0")
# From the runner's src\utils\model_registry.py at that commit
# -Model q4: the 4-bit GGUF version (about 2 GB) for cards with little memory, from the runner's other
# model repository
$Dit = if ($Model -eq "q4") {
    @{ Name = "seedvr2_ema_3b-Q4_K_M.gguf"; Sha = "e665e3909de1a8c88a69c609bca9d43ff5a134647face2ce4497640cc3597f0e"; Repo = "AInVFX/SeedVR2_comfyUI" }
} else {
    @{ Name = "seedvr2_ema_3b_fp8_e4m3fn.safetensors"; Sha = "3bf1e43ebedd570e7e7a0b1b60d6a02e105978f505c8128a241cde99a8240cff"; Repo = "numz/SeedVR2_comfyUI" }
}
$ModelFiles = @(
    $Dit,
    @{ Name = "ema_vae_fp16.safetensors"; Sha = "20678548f420d98d26f11442d3528f8b8c94e57ee046ef93dbb7633da8612ca1"; Repo = "numz/SeedVR2_comfyUI" }
)

New-Item -ItemType Directory -Force $Gen, $Cache, $Models | Out-Null
$LogFile = Join-Path $Runtime "setup-generate.log"
Start-Transcript -Path $LogFile -Force | Out-Null
trap {
    Write-Host ""
    Send-NovaError "$_"
    Write-Host "GENERATE SETUP FAILED: $_" -ForegroundColor Red
    Write-Host "Full log: $LogFile  (everything else keeps working)"
    Stop-Transcript | Out-Null
    exit 1
}

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Info($msg) { Write-Host "    $msg" }
. (Join-Path $PSScriptRoot "Progress.ps1")

if (-not (Test-Path (Join-Path $Runtime "photo.py"))) { throw "Enhance isn't set up: run setup-enhance.cmd first" }

# For the app's progress bar, weighed by size in MB: card check, uv, PyTorch, packages, runner, the model and VAE
Set-NovaSteps @(1, 30, 3000, 300, 5, 3000)
Step "Graphics card"
Send-NovaStep "Checking the graphics card"
$smi = Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
if (-not $smi) { throw "Generative detail needs an NVIDIA graphics card (nvidia-smi wasn't found)" }
$cards = & $smi.Source --query-gpu=name,memory.total --format=csv,noheader,nounits
$best = 0
foreach ($c in $cards) {
    $name, $mb = $c -split ",\s*"
    Info "$name, $([math]::Round([double]$mb / 1024, 1)) GB"
    $best = [math]::Max($best, [double]$mb)
}
if ($best -lt 3.5 * 1024 -and -not $Force) {
    throw "Generative detail needs an NVIDIA card with 4 GB or more (this one has $([math]::Round($best / 1024, 1)) GB). -Force installs it anyway."
}
if (-not $Model) { $Model = if ($best -lt 7.5 * 1024) { "q4" } else { "fp8" } }
Info "model: $(if ($Model -eq 'q4') { '4-bit, for this card''s memory' } else { '8-bit' })"

Step "uv $UvVersion (installs Python and the packages)"
Send-NovaStep "Downloading the installer for Python (uv)"
$uv = Join-Path $Gen "uv\uv.exe"
if (-not (Test-Path $uv) -or $Force) {
    $base = "https://github.com/astral-sh/uv/releases/download/$UvVersion"
    $zip = Join-Path $Cache "uv-$UvVersion.zip"
    Save-NovaDownload -Url "$base/uv-x86_64-pc-windows-msvc.zip" -OutFile $zip -Label "uv" -Headers $Headers
    $sumFile = Join-Path $Cache "uv-$UvVersion.zip.sha256"
    Invoke-WebRequest -Uri "$base/uv-x86_64-pc-windows-msvc.zip.sha256" -OutFile $sumFile -UseBasicParsing -Headers $Headers
    $want = ((Get-Content $sumFile -Raw).Trim() -split "\s+")[0].ToUpper()
    if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $want) { throw "uv's download doesn't match its checksum" }
    Expand-Archive $zip (Join-Path $Gen "uv") -Force
}
$env:UV_CACHE_DIR = Join-Path $Cache "uv"
$env:UV_PYTHON_INSTALL_DIR = Join-Path $Gen "python"
$env:UV_LINK_MODE = "copy"

Step "Python 3.12 and PyTorch for CUDA (about 3 GB, once)"
Send-NovaStep "Installing Python and PyTorch for CUDA (about 3 GB)"
Send-NovaProgress -1 "Installing Python and PyTorch for CUDA (about 3 GB, a few minutes)"
if (-not (Test-Path $Python) -or $Force) {
    & $uv venv --python 3.12 --allow-existing $Venv
    if ($LASTEXITCODE -ne 0) { throw "uv couldn't make the Python environment" }
}
& $uv pip install --python $Python --index-url $TorchIndex @Torch
if ($LASTEXITCODE -ne 0) { throw "PyTorch couldn't be installed" }
Send-NovaStep "Installing the packages SeedVR2 needs"
Send-NovaProgress -1 "Installing the packages SeedVR2 needs"
& $uv pip install --python $Python -r (Join-Path $Root "setup\generate-requirements.txt")
if ($LASTEXITCODE -ne 0) { throw "The packages couldn't be installed" }
& $Python -c "import torch; assert torch.cuda.is_available(), 'CUDA is not available to PyTorch'; print('    PyTorch', torch.__version__, 'on', torch.cuda.get_device_name(0))"
if ($LASTEXITCODE -ne 0) { throw "PyTorch can't use the graphics card: update the NVIDIA driver and run this again" }

Step "SeedVR2's runner ($($RunnerCommit.Substring(0, 7)))"
Send-NovaStep "Downloading SeedVR2's runner"
$stamp = Join-Path $Runner ".commit"
if (-not (Test-Path $stamp) -or (Get-Content $stamp -Raw).Trim() -ne $RunnerCommit -or $Force) {
    $zip = Join-Path $Cache "runner-$RunnerCommit.zip"
    Save-NovaDownload -Url "https://github.com/numz/ComfyUI-SeedVR2_VideoUpscaler/archive/$RunnerCommit.zip" -OutFile $zip -Label "SeedVR2 runner" -Headers $Headers
    $x = Join-Path $Cache "runner-x"
    if (Test-Path $x) { Remove-Item $x -Recurse -Force }
    Expand-Archive $zip $x -Force
    if (Test-Path $Runner) { Remove-Item $Runner -Recurse -Force }
    Move-Item (Get-ChildItem $x -Directory | Select-Object -First 1).FullName $Runner
    Set-Content $stamp $RunnerCommit
}
Info "runner in $Runner"

Step "SeedVR2 3B model (2-4 GB, once)"
Send-NovaStep "Downloading the SeedVR2 model"
$share = @{ $ModelFiles[0].Name = 0.85; $ModelFiles[1].Name = 0.15 }
$offset = 0.0
foreach ($m in $ModelFiles) {
    $dest = Join-Path $Models $m.Name
    if ((Test-Path $dest) -and -not $Force -and (Get-FileHash $dest -Algorithm SHA256).Hash -eq $m.Sha.ToUpper()) { Info "have    $($m.Name)"; $offset += $share[$m.Name]; continue }
    Info "download $($m.Name)"
    $part = "$dest.part"
    # Resumes a broken download where it stopped (run setup again)
    Save-NovaDownload -Url "https://huggingface.co/$($m.Repo)/resolve/main/$($m.Name)" -OutFile $part -Label $m.Name -Offset $offset -Share $share[$m.Name]
    Send-NovaProgress ($offset + $share[$m.Name]) "Checking $($m.Name)"
    if ((Get-FileHash $part -Algorithm SHA256).Hash -ne $m.Sha.ToUpper()) { Remove-Item $part -Force; throw "$($m.Name) didn't download correctly (its checksum doesn't match); try again" }
    Move-Item $part $dest -Force
    $offset += $share[$m.Name]
}

Copy-Item (Join-Path $Root "engine\generate.py") $Runtime -Force
Copy-Item (Join-Path $Root "engine\photo.py") $Runtime -Force
Set-Content $Ready "runner $RunnerCommit`r`nmodel $($ModelFiles[0].Name)`r`nvram_mb $best"
Write-Host ""
Write-Host "Generative detail is ready: on the Enhance page, Photos, choose the model Generative." -ForegroundColor Green
Stop-Transcript | Out-Null
