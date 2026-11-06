# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
<#
  Shared by the setup scripts: progress the app can show, and downloads that resume.

  The app's AI Models page runs a setup script hidden and reads these lines from its output:
    @@step <from> <to> <text>     a step started, covering from..to of the whole job (0..1; -1 -1 = unknown)
    @@progress <fraction> <text>  how far through the current step (0..1; -1 = working, no measure)
    @@error <text>                why it stopped, in plain words
  In a console window the same lines read fine as they are.

  Dot-source it after the script's own Step function:   . (Join-Path $PSScriptRoot "Progress.ps1")
#>

$script:NovaWeights = @()
$script:NovaIndex = 0
$script:NovaInvariant = [Globalization.CultureInfo]::InvariantCulture

function Set-NovaSteps([double[]]$Weights) {
    <# The script's steps in order, each weighed by how long it takes (a big download weighs most). #>
    $script:NovaWeights = $Weights
    $script:NovaIndex = 0
}

# A script run by another (Get-EnhanceModels runs Get-StudioParts -PackagesOnly) reports nothing of its own:
# its parent's step covers it
$script:NovaNested = $env:NOVA_NESTED -eq "1"

function Invoke-NovaNested([scriptblock]$Block) {
    $env:NOVA_NESTED = "1"
    try { & $Block } finally { Remove-Item Env:NOVA_NESTED -ErrorAction SilentlyContinue }
}

function Send-NovaStep([string]$Text) {
    if ($script:NovaNested) { return }
    $w = $script:NovaWeights
    if ($w.Count -gt 0 -and $script:NovaIndex -lt $w.Count) {
        $total = ($w | Measure-Object -Sum).Sum
        $before = 0.0
        for ($i = 0; $i -lt $script:NovaIndex; $i++) { $before += $w[$i] }
        $from = $before / $total
        $to = ($before + $w[$script:NovaIndex]) / $total
        $script:NovaIndex++
        Write-Host ([string]::Format($script:NovaInvariant, "@@step {0:0.0000} {1:0.0000} {2}", $from, $to, $Text))
    } else {
        Write-Host "@@step -1 -1 $Text"
    }
}

function Send-NovaProgress([double]$Fraction, [string]$Text) {
    if ($script:NovaNested) { return }
    Write-Host ([string]::Format($script:NovaInvariant, "@@progress {0:0.0000} {1}", $Fraction, $Text))
}

function Send-NovaError([string]$Text) { Write-Host "@@error $Text" }

function Format-NovaMB([double]$Bytes) {
    if ($Bytes -ge 1GB) { return [string]::Format($script:NovaInvariant, "{0:0.00} GB", $Bytes / 1GB) }
    return [string]::Format($script:NovaInvariant, "{0:0} MB", $Bytes / 1MB)
}

function Save-NovaDownload {
    <#
      Downloads Url to OutFile with progress lines, resuming a broken download from OutFile.part
      (servers that support ranges: GitHub releases, Hugging Face). OutFile appears only once complete.
    #>
    # Offset/Share: this file is one of several in the step (its part of the step's bar)
    param([Parameter(Mandatory)] [string]$Url, [Parameter(Mandatory)] [string]$OutFile, [string]$Label = "",
          [hashtable]$Headers = @{}, [double]$Offset = 0, [double]$Share = 1)
    Add-Type -AssemblyName System.Net.Http
    if (-not $Label) { $Label = Split-Path $OutFile -Leaf }
    $part = "$OutFile.part"
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $true
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromHours(6)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd("NovaStudio-Setup")
    foreach ($k in $Headers.Keys) { if ($k -ne "User-Agent") { [void]$client.DefaultRequestHeaders.TryAddWithoutValidation($k, $Headers[$k]) } }
    $have = if (Test-Path $part) { (Get-Item $part).Length } else { 0 }
    try {
        for ($attempt = 1; ; $attempt++) {
            try {
                $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $Url)
                if ($have -gt 0) { $req.Headers.Range = New-Object System.Net.Http.Headers.RangeHeaderValue($have, $null) }
                $resp = $client.SendAsync($req, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
                if ($have -gt 0 -and [int]$resp.StatusCode -eq 416) { break }          # already complete
                if (-not $resp.IsSuccessStatusCode) { throw "the server answered $([int]$resp.StatusCode) $($resp.ReasonPhrase)" }
                $resumed = [int]$resp.StatusCode -eq 206
                if (-not $resumed) { $have = 0 }
                $len = $resp.Content.Headers.ContentLength
                $total = if ($len) { $have + [long]$len } else { 0 }
                $in = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                $mode = if ($resumed) { [IO.FileMode]::Append } else { [IO.FileMode]::Create }
                $out = New-Object IO.FileStream($part, $mode, [IO.FileAccess]::Write, [IO.FileShare]::Read)
                try {
                    $buf = New-Object byte[] (1MB)
                    $clock = [Diagnostics.Stopwatch]::StartNew()
                    $lastAt = 0; $lastBytes = $have; $speed = 0.0
                    while (($n = $in.Read($buf, 0, $buf.Length)) -gt 0) {
                        $out.Write($buf, 0, $n)
                        $have += $n
                        $ms = $clock.ElapsedMilliseconds
                        if ($ms - $lastAt -ge 500) {
                            $now = ($have - $lastBytes) / (($ms - $lastAt) / 1000.0)
                            $speed = if ($speed -gt 0) { 0.7 * $speed + 0.3 * $now } else { $now }
                            $lastAt = $ms; $lastBytes = $have
                            $rate = [string]::Format($script:NovaInvariant, "{0:0.0} MB/s", $speed / 1MB)
                            if ($total -gt 0) {
                                Send-NovaProgress ($Offset + $Share * $have / $total) "$Label`: $(Format-NovaMB $have) of $(Format-NovaMB $total), $rate"
                            } else {
                                Send-NovaProgress -1 "$Label`: $(Format-NovaMB $have), $rate"
                            }
                        }
                    }
                } finally { $out.Dispose(); $in.Dispose() }
                if ($total -gt 0 -and $have -lt $total) { throw "the connection closed early" }
                if ($total -gt 0) { Send-NovaProgress ($Offset + $Share) "$Label`: $(Format-NovaMB $have) of $(Format-NovaMB $total), done" }
                break
            } catch {
                if ($attempt -ge 5) { throw "$Label couldn't be downloaded: $_" }
                Send-NovaProgress -1 "$Label`: connection lost, trying again ($attempt of 4)"
                Start-Sleep -Seconds (3 * $attempt)
                $have = if (Test-Path $part) { (Get-Item $part).Length } else { 0 }
            }
        }
    } finally { $client.Dispose() }
    Move-Item $part $OutFile -Force
}
