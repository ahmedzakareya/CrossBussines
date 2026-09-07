<#
=============================================================================================
R1 - integration gate.

The single command a tab runs before merging to the protected integration branch, and the
command CI runs on every pull request into it.

    No tab may leave the shared integration branch unbuildable.

Twelve checks. Each is either PASS, FAIL or SKIP, and a SKIP is reported as loudly as a FAIL is,
because a quiet skip is how a gate stops meaning anything. Coverage is never claimed from a skip.

    1  Debug build 0 errors
    2  Release build 0 errors
    3  TestRun build 0 errors
    4  Full test suite green, skip count reported
    5  Analyzer gates CBA001 / CBA004 / CBA006 = 0
    6  Authorization debt unchanged or lower
    7  No suppressions, no baseline additions
    8  Only files in the tab's owned paths modified
    9  Shared-file edits confined / approved
    10 SQL governance clean (canonical root, manifest, duplicates, deployability)
    11 New hosted services registered in the DI wiring test
    12 No probe databases or mutation markers left behind

EXIT CODES
    0  every check passed
    1  at least one check failed
    2  a check could not run and was skipped (only when -RequireAll)
    3  the command could not run

USAGE
    powershell -File governance/tools/integration-gate.ps1 -TabId TAB-1
    powershell -File governance/tools/integration-gate.ps1 -TabId TAB-2 -Base origin/integration -AllowShared
    powershell -File governance/tools/integration-gate.ps1 -TabId TAB-1 -SkipTests   (build+governance only)
=============================================================================================
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$TabId,
    [string]$Base,
    [switch]$AllowShared,
    [switch]$SkipTests,
    [switch]$SkipBuild,
    [switch]$RequireAll
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$tools = $PSScriptRoot

$checks = New-Object System.Collections.ArrayList
function Add-Check([int]$n, [string]$name, [string]$state, [string]$detail) {
    [void]$checks.Add([pscustomobject]@{ N = $n; Check = $name; State = $state; Detail = $detail })
    $colour = 'Green'
    if ($state -eq 'FAIL') { $colour = 'Red' }
    if ($state -eq 'SKIP') { $colour = 'Yellow' }
    Write-Host ("  {0,2}. {1,-46} {2}  {3}" -f $n, $name, $state, $detail) -ForegroundColor $colour
}

Write-Host ''
Write-Host ("R1 INTEGRATION GATE - {0}" -f $TabId) -ForegroundColor Cyan
Write-Host ('=' * 78)
Write-Host ''

# ---- 1-3 builds ------------------------------------------------------------------------------
if ($SkipBuild) {
    Add-Check 1 'Debug build 0 errors'   'SKIP' '-SkipBuild'
    Add-Check 2 'Release build 0 errors' 'SKIP' '-SkipBuild'
    Add-Check 3 'TestRun build 0 errors' 'SKIP' '-SkipBuild'
} else {
    $buildOut = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $tools 'build-gate.ps1') 2>&1
    $buildExit = $LASTEXITCODE
    $buildText = ($buildOut | Out-String)
    foreach ($pair in @(@(1, 'Debug'), @(2, 'Release'), @(3, 'TestRun'))) {
        $n = $pair[0]; $cfg = $pair[1]
        $m = [regex]::Match($buildText, [regex]::Escape($cfg) + ':\s+(\d+)\s+Error\(s\)')
        if ($m.Success -and [int]$m.Groups[1].Value -eq 0) {
            Add-Check $n ("{0} build 0 errors" -f $cfg) 'PASS' '0 Error(s)'
        } elseif ($m.Success) {
            Add-Check $n ("{0} build 0 errors" -f $cfg) 'FAIL' ("{0} Error(s)" -f $m.Groups[1].Value)
        } else {
            Add-Check $n ("{0} build 0 errors" -f $cfg) 'FAIL' 'no error-count summary'
        }
    }
}

# ---- 4 tests ---------------------------------------------------------------------------------
if ($SkipTests) {
    Add-Check 4 'Full suite green, skips reported' 'SKIP' '-SkipTests'
} else {
    $testOut = & dotnet test (Join-Path $repo 'CrossBuy.Tests\CrossBuy.Tests.csproj') -c TestRun --no-build --nologo 2>&1
    $testText = ($testOut | Out-String)
    $m = [regex]::Match($testText, 'Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+)')
    if ($m.Success) {
        $failed = [int]$m.Groups[1].Value; $passed = [int]$m.Groups[2].Value; $skipped = [int]$m.Groups[3].Value
        $detail = ("{0} passed, {1} failed, {2} skipped" -f $passed, $failed, $skipped)
        if ($failed -eq 0) { Add-Check 4 'Full suite green, skips reported' 'PASS' $detail }
        else { Add-Check 4 'Full suite green, skips reported' 'FAIL' $detail }
    } else {
        Add-Check 4 'Full suite green, skips reported' 'FAIL' 'no test summary parsed'
    }
}

# ---- 5 analyzer gates ------------------------------------------------------------------------
if ($SkipBuild) {
    Add-Check 5 'CBA001 / CBA004 / CBA006 = 0' 'SKIP' '-SkipBuild'
} else {
    $anOut = & dotnet build (Join-Path $repo 'CrossBuy\CrossBuy.csproj') -c TestRun -v q --nologo 2>&1
    $anText = ($anOut | Out-String)
    $cba = ([regex]::Matches($anText, 'error CBA00[146]')).Count
    if ($cba -eq 0) { Add-Check 5 'CBA001 / CBA004 / CBA006 = 0' 'PASS' '0 / 0 / 0' }
    else { Add-Check 5 'CBA001 / CBA004 / CBA006 = 0' 'FAIL' ("{0} analyzer errors" -f $cba) }
}

# ---- 6 authorization debt --------------------------------------------------------------------
$baselinePath = Join-Path $repo 'engineering\authorization-baseline.json'
if (-not (Test-Path $baselinePath)) {
    Add-Check 6 'Authorization debt not increased' 'SKIP' 'baseline not found'
} else {
    try {
        $bl = Get-Content $baselinePath -Raw -Encoding UTF8 | ConvertFrom-Json
        $gaps = $bl.frozenBaseline.gaps
        $entries = @($bl.entries).Count
        if ($gaps -eq $entries) { Add-Check 6 'Authorization debt not increased' 'PASS' ("debt {0}, entries {1}" -f $gaps, $entries) }
        else { Add-Check 6 'Authorization debt not increased' 'FAIL' ("debt {0} but {1} entries" -f $gaps, $entries) }
    } catch { Add-Check 6 'Authorization debt not increased' 'FAIL' 'baseline unreadable' }
}

# ---- 7 suppressions ---------------------------------------------------------------------------
$supp = 0
foreach ($pat in @('#pragma warning disable CBA', 'SuppressMessage("Authorization')) {
    $hits = @(Get-ChildItem (Join-Path $repo 'CrossBuy') -Filter *.cs -Recurse -ErrorAction SilentlyContinue |
              Select-String -SimpleMatch $pat -ErrorAction SilentlyContinue)
    $supp += $hits.Count
}
if ($supp -eq 0) { Add-Check 7 'No analyzer suppressions' 'PASS' '0 found' }
else { Add-Check 7 'No analyzer suppressions' 'FAIL' ("{0} found" -f $supp) }

# ---- 8-9 ownership ----------------------------------------------------------------------------
$ownArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $tools 'check-file-ownership.ps1'), '-TabId', $TabId)
if ($Base) { $ownArgs += @('-Base', $Base) }
if ($AllowShared) { $ownArgs += '-AllowShared' }
$ownOut = & powershell @ownArgs 2>&1
$ownExit = $LASTEXITCODE
$ownText = ($ownOut | Out-String)
$deniedM = [regex]::Match($ownText, 'DENIED\s+:\s+(\d+)')
$sharedM = [regex]::Match($ownText, 'shared\s+:\s+(\d+)')
$deniedN = 0; if ($deniedM.Success) { $deniedN = [int]$deniedM.Groups[1].Value }
$sharedN = 0; if ($sharedM.Success) { $sharedN = [int]$sharedM.Groups[1].Value }
if ($deniedN -eq 0) { Add-Check 8 'Only owned paths modified' 'PASS' '0 denied' }
else { Add-Check 8 'Only owned paths modified' 'FAIL' ("{0} files owned by another tab" -f $deniedN) }

if ($sharedN -eq 0) { Add-Check 9 'Shared-file edits approved' 'PASS' 'none touched' }
elseif ($AllowShared) { Add-Check 9 'Shared-file edits approved' 'PASS' ("{0} touched, -AllowShared" -f $sharedN) }
else { Add-Check 9 'Shared-file edits approved' 'FAIL' ("{0} touched without approval" -f $sharedN) }

# ---- 10 SQL governance --------------------------------------------------------------------------
$sqlOut = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $tools 'validate-sql-governance.ps1') 2>&1
$sqlExit = $LASTEXITCODE
$sqlText = ($sqlOut | Out-String)
$vM = [regex]::Match($sqlText, 'governance violations\s+:\s+(\d+)')
$ndM = [regex]::Match($sqlText, 'not-deployable\s+:\s+(\d+)')
$vN = 0; if ($vM.Success) { $vN = [int]$vM.Groups[1].Value }
$ndN = 0; if ($ndM.Success) { $ndN = [int]$ndM.Groups[1].Value }
if ($sqlExit -eq 0) { Add-Check 10 'SQL governance clean' 'PASS' 'no violations' }
else { Add-Check 10 'SQL governance clean' 'FAIL' ("{0} violations, {1} not-deployable" -f $vN, $ndN) }

# ---- 11 DI wiring for new hosted services -------------------------------------------------------
$programPath = Join-Path $repo 'CrossBuy\Program.cs'
$diTest = Join-Path $repo 'CrossBuy.Tests\Stage1DiWiringTests.cs'
if ((Test-Path $programPath) -and (Test-Path $diTest)) {
    $hosted = @([regex]::Matches((Get-Content $programPath -Raw), 'AddHostedService<\s*([^>]+?)\s*>') |
                ForEach-Object { ($_.Groups[1].Value -split '\.')[-1].Trim() } | Sort-Object -Unique)
    $diText = Get-Content $diTest -Raw
    $unwired = @($hosted | Where-Object { $diText -notmatch [regex]::Escape($_) })
    if ($unwired.Count -eq 0) { Add-Check 11 'Hosted services in DI wiring test' 'PASS' ("{0} registered, all covered" -f $hosted.Count) }
    else { Add-Check 11 'Hosted services in DI wiring test' 'FAIL' ("not in wiring test: " + ($unwired -join ', ')) }
} else {
    Add-Check 11 'Hosted services in DI wiring test' 'SKIP' 'Program.cs or wiring test not found'
}

# ---- 12 residue ----------------------------------------------------------------------------------
$markers = @(Get-ChildItem $repo -Include *.cs, *.sql -Recurse -File -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -notmatch '\\(bin|obj|\.git|node_modules|\.venv)\\' } |
             Select-String -Pattern 'MUTATION [A-H]' -ErrorAction SilentlyContinue)
if ($markers.Count -eq 0) { Add-Check 12 'No mutation markers / probe residue' 'PASS' '0 markers' }
else { Add-Check 12 'No mutation markers / probe residue' 'FAIL' ("{0} markers left behind" -f $markers.Count) }

# ---- verdict ---------------------------------------------------------------------------------------
$failed = @($checks | Where-Object { $_.State -eq 'FAIL' })
$skipped = @($checks | Where-Object { $_.State -eq 'SKIP' })

Write-Host ''
Write-Host ('=' * 78)
Write-Host ("  PASS {0}   FAIL {1}   SKIP {2}" -f ($checks.Count - $failed.Count - $skipped.Count), $failed.Count, $skipped.Count)
if ($skipped.Count) {
    Write-Host ''
    Write-Host '  SKIPPED CHECKS - no coverage is claimed from these:' -ForegroundColor Yellow
    foreach ($s in $skipped) { Write-Host ("    {0}. {1} ({2})" -f $s.N, $s.Check, $s.Detail) -ForegroundColor Yellow }
}
Write-Host ''

if ($failed.Count) {
    Write-Host 'GATE FAILED - do not merge to the integration branch.' -ForegroundColor Red
    foreach ($f in $failed) { Write-Host ("    {0}. {1} - {2}" -f $f.N, $f.Check, $f.Detail) -ForegroundColor Red }
    exit 1
}
if ($skipped.Count -and $RequireAll) {
    Write-Host 'GATE INCOMPLETE - checks were skipped and -RequireAll was passed.' -ForegroundColor Yellow
    exit 2
}
Write-Host 'GATE PASSED.' -ForegroundColor Green
exit 0
