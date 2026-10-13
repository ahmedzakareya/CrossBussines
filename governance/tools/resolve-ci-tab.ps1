[CmdletBinding()]
param([string]$Branch, [string]$RequestedTab, [string]$EventName)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$known = @((Get-Content (Join-Path $repo 'governance/registry/tab-ownership.json') -Raw | ConvertFrom-Json).tabs.tabId)
$fromBranch = $null
if ($Branch -match '^tab[/-](tab-\d+)(?:[-/].*)?$') { $fromBranch = $Matches[1].ToUpperInvariant() }
if ($EventName -eq 'pull_request') {
    if (-not $fromBranch) { throw 'Pull requests require an attributed tab/tab-N-* branch.' }
    $tab = $fromBranch
} elseif ($EventName -eq 'workflow_dispatch') {
    $tab = $RequestedTab.ToUpperInvariant()
    if ($fromBranch -and $fromBranch -ne $tab) { throw 'Requested tab conflicts with branch attribution.' }
} else { throw 'Unsupported event: aggregate integration pushes have no single tab owner.' }
if ($tab -notin $known) { throw "Unknown tab '$tab'; attribution must match the ownership registry." }
Write-Output $tab
