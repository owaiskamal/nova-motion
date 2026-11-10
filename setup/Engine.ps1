# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
<#
  Shared by the setup scripts: puts NOVA Studio's own engine scripts into the runtime.

  Every script used to copy the files it knew of, by name. A new file had to be added to each list, and
  one that was missed stayed at its old version in the runtime (Enhance showed "Needs preparing" for good
  after such an update). Now all of them are copied, every time, from one place. The app does the same
  when it starts (RuntimeLayout.SyncEngine).

  Dot-source it:   . (Join-Path $PSScriptRoot "Engine.ps1")
#>
function Copy-NovaEngine([string]$Root, [string]$Runtime) {
    $engine = Join-Path $Root "engine"
    New-Item -ItemType Directory -Force -Path $Runtime | Out-Null
    Get-ChildItem $engine -File | Where-Object { $_.Extension -in ".py", ".vpy" } |
        ForEach-Object { Copy-Item $_.FullName $Runtime -Force }
    $mpv = Join-Path $Runtime "mpv"
    if ((Test-Path $mpv) -and (Test-Path (Join-Path $engine "mpv"))) {
        $portable = Join-Path $mpv "portable_config"
        New-Item -ItemType Directory -Force -Path $portable | Out-Null
        Copy-Item (Join-Path $engine "mpv\*") $portable -Recurse -Force
    }
}
