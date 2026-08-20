<#
=============================================================================================
R1 - tab ownership and shared-file enforcement.

Answers one question about a set of changed files: is this tab allowed to change them?

WHY THIS EXISTS: five cross-tab build breaks came from three different tabs during Stage 2A. In
every case the change itself was legitimate work by its author - what was missing was any
mechanism that noticed the file belonged to somebody else before the tree went red.

Ownership comes from governance/registry/tab-ownership.json and shared-files.json, both generated
from the roadmap canonical dataset. This script does not hold its own copy of the rules.

RULES

  1. A file matching one of the tab's ownedPaths globs is ALLOWED.
  2. A file listed in shared-files.json is SHARED: allowed only with -AllowShared, which a human
     passes when the Integration Owner has approved the edit. It is never automatic.
  3. A file owned by ANOTHER tab is DENIED.
  4. A file matched by nobody is UNCLAIMED: reported, and denied under -Strict.

Unclaimed is deliberately not fatal by default. The repository has 327 views and 121 services
that predate the registry; failing on all of them would make the gate useless on day one, and a
gate people switch off protects nothing. -Strict is for the end of R1 once claims are complete.

EXIT CODES
    0  allowed
    1  a denied file was touched
    2  unclaimed files present and -Strict was passed
    3  the command could not run

USAGE
    # what this tab changed, against the integration branch
    powershell -File governance/tools/check-file-ownership.ps1 -TabId TAB-1
    powershell -File governance/tools/check-file-ownership.ps1 -TabId TAB-1 -Base origin/integration
    powershell -File governance/tools/check-file-ownership.ps1 -TabId TAB-2 -AllowShared -Strict
    # explicit list instead of git
    powershell -File governance/tools/check-file-ownership.ps1 -TabId TAB-1 -Files a.cs,b.cs
=============================================================================================
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$TabId,
    [string]$Base,
    [string[]]$Files,
    [switch]$AllowShared,
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$tabsPath = Join-Path $repo 'governance\registry\tab-ownership.json'
$sharedPath = Join-Path $repo 'governance\registry\shared-files.json'

foreach ($p in @($tabsPath, $sharedPath)) {
    if (-not (Test-Path $p)) { Write-Host "registry not found: $p" -ForegroundColor Red; exit 3 }
}

try {
    $tabs = (Get-Content $tabsPath -Raw -Encoding UTF8 | ConvertFrom-Json).tabs
    $shared = (Get-Content $sharedPath -Raw -Encoding UTF8 | ConvertFrom-Json).sharedFiles
} catch { Write-Host "registry is not valid JSON: $_" -ForegroundColor Red; exit 3 }

$me = $tabs | Where-Object { $_.tabId -eq $TabId }
if (-not $me) {
    Write-Host "unknown TabId '$TabId'. Known: $(($tabs.tabId) -join ', ')" -ForegroundColor Red
    exit 3
}

# ---- collect the changed files -------------------------------------------------------------
if ($Files -and $Files.Count) {
    # `powershell -File script.ps1 -Files a,b` delivers ONE string "a,b", not an array. Splitting
    # here is not cosmetic: without it the gate compares a single joined string against every
    # ownership glob, matches nothing, and reports PASS on files it must deny.
    $changed = @($Files | ForEach-Object { $_ -split ',' } |
                 Where-Object { $_ -and $_.Trim() } |
                 ForEach-Object { $_.Trim().Replace('\', '/') })
} else {
    Push-Location $repo
    try {
        if ($Base) { $raw = git diff --name-only $Base 2>$null }
        else       { $raw = git status --porcelain 2>$null | ForEach-Object { $_.Substring(3) } }
    } finally { Pop-Location }
    $changed = @($raw | Where-Object { $_ -and $_.Trim() } | ForEach-Object { $_.Trim().Replace('\', '/') })
}

if (-not $changed.Count) {
    Write-Host "no changed files detected - nothing to check." -ForegroundColor Green
    exit 0
}

# ---- matching -------------------------------------------------------------------------------
# ownedPaths entries are shell-ish globs (BL/Platform/Bootstrap*, docs/roadmap-v2/**).
# Translate to regex once, rather than per file.
function ConvertTo-PathRegex([string]$glob) {
    $g = $glob.Trim().Replace('\', '/')
    if ($g.EndsWith('/**')) { $g = $g.Substring(0, $g.Length - 3) + '/*' }
    $rx = [regex]::Escape($g)
    $rx = $rx.Replace('\*\*', '.*').Replace('\*', '[^/]*').Replace('/\.\*', '/.*')
    return '^' + $rx
}

$myRegex = @($me.ownedPaths | ForEach-Object { ConvertTo-PathRegex $_ })
$otherTabs = @($tabs | Where-Object { $_.tabId -ne $TabId })
$sharedRegex = @($shared | ForEach-Object {
    [pscustomobject]@{ FileId = $_.fileId; Rx = (ConvertTo-PathRegex $_.path); Owner = $_.owner; Rule = $_.changeRule }
})

$allowed = New-Object System.Collections.ArrayList
$sharedHit = New-Object System.Collections.ArrayList
$denied = New-Object System.Collections.ArrayList
$unclaimed = New-Object System.Collections.ArrayList

foreach ($f in $changed) {
    $isShared = $null
    foreach ($s in $sharedRegex) { if ($f -match $s.Rx) { $isShared = $s; break } }
    if ($isShared) { [void]$sharedHit.Add([pscustomobject]@{ File = $f; Shared = $isShared }); continue }

    $mine = $false
    foreach ($rx in $myRegex) { if ($f -match $rx) { $mine = $true; break } }
    if ($mine) { [void]$allowed.Add($f); continue }

    $owner = $null
    foreach ($t in $otherTabs) {
        foreach ($p in $t.ownedPaths) {
            if ($f -match (ConvertTo-PathRegex $p)) { $owner = $t; break }
        }
        if ($owner) { break }
    }
    if ($owner) { [void]$denied.Add([pscustomobject]@{ File = $f; Owner = $owner.tabId; Tab = $owner.tab }) }
    else { [void]$unclaimed.Add($f) }
}

# ---- report ---------------------------------------------------------------------------------
Write-Host ''
Write-Host ("FILE OWNERSHIP CHECK - {0} ({1})" -f $TabId, $me.tab) -ForegroundColor Cyan
Write-Host ('=' * 60)
Write-Host ("  changed files : {0}" -f $changed.Count)
Write-Host ("  owned         : {0}" -f $allowed.Count) -ForegroundColor Green
Write-Host ("  shared        : {0}" -f $sharedHit.Count) -ForegroundColor Yellow
Write-Host ("  unclaimed     : {0}" -f $unclaimed.Count) -ForegroundColor Yellow
Write-Host ("  DENIED        : {0}" -f $denied.Count) -ForegroundColor Red

if ($sharedHit.Count) {
    Write-Host ''
    Write-Host 'SHARED FILES TOUCHED' -ForegroundColor Yellow
    foreach ($s in $sharedHit) {
        Write-Host ("  {0}  [{1}, owner {2}]" -f $s.File, $s.Shared.FileId, $s.Shared.Owner)
        Write-Host ("      rule: {0}" -f $s.Shared.Rule) -ForegroundColor DarkGray
    }
    if (-not $AllowShared) {
        Write-Host ''
        Write-Host '  Shared edits require Integration Owner approval. Re-run with -AllowShared once approved.' -ForegroundColor Yellow
    }
}

if ($denied.Count) {
    Write-Host ''
    Write-Host 'DENIED - these files belong to another tab' -ForegroundColor Red
    foreach ($d in $denied) { Write-Host ("  {0}  -> {1} ({2})" -f $d.File, $d.Owner, $d.Tab) -ForegroundColor Red }
}

if ($unclaimed.Count -and ($Strict -or $unclaimed.Count -le 20)) {
    Write-Host ''
    Write-Host 'UNCLAIMED - no tab claims these paths' -ForegroundColor Yellow
    foreach ($u in ($unclaimed | Select-Object -First 40)) { Write-Host ("  {0}" -f $u) }
    if ($unclaimed.Count -gt 40) { Write-Host ("  ... and {0} more" -f ($unclaimed.Count - 40)) }
}

Write-Host ''
if ($denied.Count) { Write-Host 'FAIL: another tab owns files in this change.' -ForegroundColor Red; exit 1 }
if ($sharedHit.Count -and -not $AllowShared) {
    Write-Host 'FAIL: shared files touched without -AllowShared.' -ForegroundColor Red; exit 1
}
if ($unclaimed.Count -and $Strict) { Write-Host 'FAIL: unclaimed files under -Strict.' -ForegroundColor Red; exit 2 }
Write-Host 'PASS: ownership clean.' -ForegroundColor Green
exit 0
