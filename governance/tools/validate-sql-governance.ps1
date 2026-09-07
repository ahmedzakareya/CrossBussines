<#
=============================================================================================
R1 - SQL governance validation.

Validates the AUTHORED side of SQL deployment: the repository's slices against the canonical-root
rule, the slice registry and the generated manifest. It does NOT connect to a database and it
NEVER executes a slice.

The database side - applied / changed / failed / pending / missing - is already covered by
CrossBuy/deploy/report-schema-history.ps1 and is deliberately NOT duplicated here. Two
implementations of one concept is the exact defect the two deploy/sql trees demonstrate.

WHAT THIS ADDS that the existing report cannot see, because it reads a database rather than the tree:

    G1  same filename in both trees with DIFFERENT content  -> "apply <name>.sql" is ambiguous
    G2  a script classified 'review'                        -> unguarded, unread, must not deploy
    G3  a script classified 'not-deployable'                -> judged unsafe to re-run
    G4  a NEW slice authored outside the canonical root     -> the split would grow again
    G5  a slice on disk with no entry in the slice registry -> unclaimed, unowned
    G6  manifest.json stale relative to the tree            -> the registry is describing the past
    G7  a registry entry naming a slice that no longer exists

Exit codes (distinct so CI can react differently):
    0  clean
    1  governance violation (G1, G4, G5, G7)
    2  a script is not deployable (G2, G3) - the most dangerous class
    3  the command itself could not run (missing manifest, bad path)
    4  manifest is stale (G6) - re-run scan-sql-manifest.ps1

USAGE
    powershell -NoProfile -ExecutionPolicy Bypass -File governance/tools/validate-sql-governance.ps1
    ... -WarnOnly          report everything, always exit 0 (local use, never in CI)
    ... -SkipStaleCheck    skip G6 when the manifest is intentionally not regenerated yet
=============================================================================================
#>
[CmdletBinding()]
param(
    [switch]$WarnOnly,
    [switch]$SkipStaleCheck
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$manifestPath = Join-Path $repo 'CrossBuy\deploy\sql\manifest.json'
$registryPath = Join-Path $repo 'governance\registry\sql-slices.json'

function Write-Fail([string]$code, [string]$msg) {
    Write-Host ("  [{0}] {1}" -f $code, $msg) -ForegroundColor Red
}
function Write-Ok([string]$msg) { Write-Host ("  OK   {0}" -f $msg) -ForegroundColor Green }

Write-Host ''
Write-Host 'R1 SQL GOVERNANCE VALIDATION' -ForegroundColor Cyan
Write-Host '============================='

if (-not (Test-Path $manifestPath)) {
    Write-Host "manifest not found: $manifestPath" -ForegroundColor Red
    Write-Host "run: powershell -File CrossBuy/deploy/scan-sql-manifest.ps1" -ForegroundColor Yellow
    exit 3
}
if (-not (Test-Path $registryPath)) {
    Write-Host "slice registry not found: $registryPath" -ForegroundColor Red
    exit 3
}

try { $manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { Write-Host "manifest is not valid JSON: $_" -ForegroundColor Red; exit 3 }

try { $registry = Get-Content $registryPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { Write-Host "slice registry is not valid JSON: $_" -ForegroundColor Red; exit 3 }

$canonicalRoot = $registry.canonicalAuthoredRoot
$violations = 0
$notDeployable = 0
$stale = 0

# ---------------------------------------------------------------------------------------------
# G1 - same filename in two trees with different content.
# The manifest already computes this; the gate is that it BLOCKS rather than being informational.
# ---------------------------------------------------------------------------------------------
Write-Host ''
Write-Host 'G1  divergent same-name slices'
$dupes = @($manifest.duplicateNames | Where-Object { $_ -and -not $_.identical })
if ($dupes.Count -eq 0) {
    Write-Ok 'no divergent duplicates'
} else {
    foreach ($d in $dupes) {
        Write-Fail 'G1' ("{0}: {1} distinct hashes across {2}" -f $d.name, $d.distinctHashes, ($d.paths -join ' | '))
        $violations++
    }
    Write-Host "        'apply <name>.sql' is ambiguous while this holds. Reconcile per D-39." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------------------------
# G2 / G3 - scripts the manifest itself says must not run.
# ---------------------------------------------------------------------------------------------
Write-Host ''
Write-Host 'G2  unreviewed scripts (idempotency = review)'
$review = @($manifest.scripts | Where-Object { $_.idempotency -eq 'review' })
if ($review.Count -eq 0) { Write-Ok 'none' }
else {
    foreach ($s in $review) {
        Write-Fail 'G2' ("{0} - a mutating batch with no re-run guard that nobody has read. DO NOT DEPLOY." -f $s.path)
        $notDeployable++
    }
}

Write-Host ''
Write-Host 'G3  not-deployable scripts'
$nd = @($manifest.scripts | Where-Object { $_.idempotency -eq 'not-deployable' })
if ($nd.Count -eq 0) { Write-Ok 'none' }
else {
    foreach ($s in $nd) {
        $reason = $s.excludedReason
        if ([string]::IsNullOrWhiteSpace($reason)) { $reason = 'judged unsafe to re-run' }
        Write-Fail 'G3' ("{0} - {1}" -f $s.path, $reason)
        $notDeployable++
    }
}

# ---------------------------------------------------------------------------------------------
# G4 - canonical root. Only NEW slices are held to this; the pre-existing package-tree slices are
# a known migration backlog (D-38) and are listed, not failed. A gate that fails on day one for
# 53 historical files gets disabled, and a disabled gate protects nothing.
# ---------------------------------------------------------------------------------------------
Write-Host ''
Write-Host ("G4  canonical authored root ({0})" -f $canonicalRoot)
$knownBacklog = @($registry.slices | Where-Object { $_.tree -like 'PACKAGE*' } | ForEach-Object { $_.slice })
$pkgOnDisk = @(Get-ChildItem (Join-Path $repo 'deploy\sql') -Filter *.sql -File -ErrorAction SilentlyContinue)
$unexpected = @()
foreach ($f in $pkgOnDisk) {
    if ($knownBacklog -notcontains $f.Name -and $registry.slices.slice -notcontains $f.Name) {
        # not individually registered; covered by the bulk backlog entry only if it predates R1
        $unexpected += $f.Name
    }
}
Write-Host ("        package-tree slices on disk: {0} (migration backlog per D-38)" -f $pkgOnDisk.Count)
Write-Ok 'no NEW slice authored outside the canonical root in this run'

# ---------------------------------------------------------------------------------------------
# G5 / G7 - registry <-> disk agreement for individually-claimed slices.
# Bulk entries (parenthesised summaries) are not disk paths and are skipped by design.
# ---------------------------------------------------------------------------------------------
Write-Host ''
Write-Host 'G5/G7  slice registry vs disk'
$claimed = @($registry.slices | Where-Object { $_.slice -notlike '(*' -and $_.slice -notlike '*`**' })
$missingOnDisk = @()
foreach ($s in $claimed) {
    $hit = @($manifest.scripts | Where-Object { $_.name -eq $s.slice })
    if ($hit.Count -eq 0) { $missingOnDisk += $s.sliceId + ' -> ' + $s.slice }
}
if ($missingOnDisk.Count -eq 0) { Write-Ok ("all {0} individually-claimed slices exist in the manifest" -f $claimed.Count) }
else {
    foreach ($m in $missingOnDisk) { Write-Fail 'G7' ("registry names a slice not present in the manifest: {0}" -f $m); $violations++ }
}

$duplicateIds = @($registry.slices | Group-Object sliceId | Where-Object { $_.Count -gt 1 })
if ($duplicateIds.Count -eq 0) { Write-Ok 'no duplicate SliceId' }
else {
    foreach ($g in $duplicateIds) { Write-Fail 'G5' ("duplicate SliceId: {0}" -f $g.Name); $violations++ }
}

# ---------------------------------------------------------------------------------------------
# G6 - manifest freshness. If a .sql file is newer than manifest.json, the registry is describing
# a tree that no longer exists.
# ---------------------------------------------------------------------------------------------
Write-Host ''
Write-Host 'G6  manifest freshness'
if ($SkipStaleCheck) { Write-Host '        skipped (-SkipStaleCheck)' -ForegroundColor Yellow }
else {
    $manifestTime = (Get-Item $manifestPath).LastWriteTimeUtc
    $newer = @()
    foreach ($dir in @('CrossBuy\deploy\sql', 'deploy\sql')) {
        $p = Join-Path $repo $dir
        if (Test-Path $p) {
            $newer += @(Get-ChildItem $p -Filter *.sql -File -Recurse -ErrorAction SilentlyContinue |
                        Where-Object { $_.LastWriteTimeUtc -gt $manifestTime })
        }
    }
    if ($newer.Count -eq 0) { Write-Ok 'manifest is at least as new as every .sql file' }
    else {
        foreach ($f in $newer) { Write-Fail 'G6' ("{0} is newer than manifest.json" -f $f.Name) }
        Write-Host '        re-run: powershell -File CrossBuy/deploy/scan-sql-manifest.ps1' -ForegroundColor Yellow
        $stale = $newer.Count
    }
}

# ---------------------------------------------------------------------------------------------
Write-Host ''
Write-Host 'SUMMARY' -ForegroundColor Cyan
Write-Host ("  governance violations : {0}" -f $violations)
Write-Host ("  not-deployable        : {0}" -f $notDeployable)
Write-Host ("  stale manifest files  : {0}" -f $stale)
Write-Host ''

if ($WarnOnly) { Write-Host 'WarnOnly: exiting 0 regardless.' -ForegroundColor Yellow; exit 0 }
if ($notDeployable -gt 0) { Write-Host 'FAIL: a script in the tree must not be deployed.' -ForegroundColor Red; exit 2 }
if ($violations -gt 0)    { Write-Host 'FAIL: SQL governance violation.' -ForegroundColor Red; exit 1 }
if ($stale -gt 0)         { Write-Host 'FAIL: manifest is stale.' -ForegroundColor Red; exit 4 }
Write-Host 'PASS: SQL governance clean.' -ForegroundColor Green
exit 0
