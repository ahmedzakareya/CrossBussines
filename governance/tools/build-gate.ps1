<#
=============================================================================================
R1 - build gate.

Builds every configuration and CAPTURES THE ERROR COUNT EXPLICITLY, then reports the produced
assembly timestamps.

WHY THE ERROR COUNT IS CAPTURED RATHER THAN INFERRED FROM THE EXIT CODE: during Stage 2A a
mutation test reported a PASS that was worthless - the build had failed, `--no-build` then ran
the previous assembly, and the output was truncated so the error count was never seen. A test
result from an unverified build is not evidence; it is the previous result wearing a new
timestamp. This script exists so that "the build was green" is a recorded fact.

It also deletes build outputs first when -Clean is passed, because a stale bin/Debug is the other
half of the same failure.

EXIT CODES
    0  every configuration built with 0 errors
    1  at least one configuration reported errors
    3  the command could not run (dotnet missing, solution not found)

USAGE
    powershell -File governance/tools/build-gate.ps1
    powershell -File governance/tools/build-gate.ps1 -Clean
    powershell -File governance/tools/build-gate.ps1 -Configurations Debug,TestRun
=============================================================================================
#>
[CmdletBinding()]
param(
    [string[]]$Configurations = @('Debug', 'Release', 'TestRun'),
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$solution = Join-Path $repo 'CrossBuy.sln'

if (-not (Test-Path $solution)) { Write-Host "solution not found: $solution" -ForegroundColor Red; exit 3 }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Write-Host 'dotnet not on PATH' -ForegroundColor Red; exit 3 }

Write-Host ''
Write-Host 'R1 BUILD GATE' -ForegroundColor Cyan
Write-Host '============='

if ($Clean) {
    Write-Host ''
    Write-Host 'removing build outputs (a stale binary is how a false green happens)' -ForegroundColor Yellow
    foreach ($d in @('bin', 'obj')) {
        Get-ChildItem $repo -Directory -Recurse -Filter $d -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\\.git\\' -and $_.FullName -notmatch 'node_modules' } |
            ForEach-Object {
                try { Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop } catch { }
            }
    }
    Write-Host '  outputs removed'
}

$results = @()
$anyFailed = $false

foreach ($cfg in $Configurations) {
    Write-Host ''
    Write-Host ("building {0} ..." -f $cfg)
    $output = & dotnet build $solution -c $cfg -v q --nologo 2>&1
    $text = ($output | Out-String)

    # Parse the summary rather than trusting $LASTEXITCODE alone. Both are recorded.
    $errCount = $null
    $warnCount = $null
    $m = [regex]::Match($text, '(\d+)\s+Error\(s\)')
    if ($m.Success) { $errCount = [int]$m.Groups[1].Value }
    $w = [regex]::Match($text, '(\d+)\s+Warning\(s\)')
    if ($w.Success) { $warnCount = [int]$w.Groups[1].Value }

    $exit = $LASTEXITCODE
    $ok = ($errCount -eq 0 -and $exit -eq 0)

    if ($null -eq $errCount) {
        # No summary line found. Do NOT assume success - that is precisely the inference this
        # script exists to remove.
        Write-Host ("  {0}: NO ERROR-COUNT SUMMARY FOUND (exit {1}) - treating as FAILED" -f $cfg, $exit) -ForegroundColor Red
        $text.Split("`n") | Select-Object -Last 15 | ForEach-Object { Write-Host ("    {0}" -f $_.TrimEnd()) -ForegroundColor DarkGray }
        $ok = $false
        $errCount = -1
    }

    if ($ok) { Write-Host ("  {0}: {1} Error(s), {2} Warning(s)" -f $cfg, $errCount, $warnCount) -ForegroundColor Green }
    else {
        Write-Host ("  {0}: {1} Error(s), exit {2}" -f $cfg, $errCount, $exit) -ForegroundColor Red
        ($text -split "`n" | Where-Object { $_ -match 'error [A-Z]{2}\d+' } | Select-Object -First 10) |
            ForEach-Object { Write-Host ("    {0}" -f $_.Trim()) -ForegroundColor Red }
        $anyFailed = $true
    }

    $results += [pscustomobject]@{ Configuration = $cfg; Errors = $errCount; Warnings = $warnCount; ExitCode = $exit; Ok = $ok }
}

# Assembly timestamps - so a reader can tell the binary under test is the one just built.
Write-Host ''
Write-Host 'PRODUCED ASSEMBLIES'
foreach ($cfg in $Configurations) {
    $dll = Get-ChildItem (Join-Path $repo 'CrossBuy.Tests\bin') -Filter 'CrossBuy.Tests.dll' -Recurse -ErrorAction SilentlyContinue |
           Where-Object { $_.FullName -match [regex]::Escape("\bin\$cfg\") } | Select-Object -First 1
    if ($dll) { Write-Host ("  {0,-8} {1:yyyy-MM-dd HH:mm:ss}  {2}" -f $cfg, $dll.LastWriteTime, $dll.Name) }
    else { Write-Host ("  {0,-8} (no test assembly)" -f $cfg) -ForegroundColor DarkGray }
}

Write-Host ''
Write-Host 'SUMMARY' -ForegroundColor Cyan
$results | Format-Table Configuration, Errors, Warnings, ExitCode, Ok -AutoSize | Out-String | Write-Host

if ($anyFailed) { Write-Host 'FAIL: build gate.' -ForegroundColor Red; exit 1 }
Write-Host 'PASS: every configuration built with 0 errors.' -ForegroundColor Green
exit 0
