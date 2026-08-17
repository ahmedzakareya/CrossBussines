<#
    CrossBusiness — UI CONFORMANCE, ONE COMMAND, ONE VERDICT.

    Runs both halves of the gate and prints exactly one word: PASS or FAIL.

        1. STATIC   (CrossBuy.Tests/UiConformance) — source-decidable rules. No browser, ~300ms.
        2. RENDERED (ui-conformance.mjs)           — visual / responsive / RTL / accessibility.

    VERIFICATION ONLY. Nothing here modifies an application file.

    A rendered check that could not run is reported NOT VERIFIED and fails the run. It is never
    counted as a pass.

        .\run.ps1                     both halves
        .\run.ps1 -StaticOnly         source gates only (CI without a browser or a server)
        .\run.ps1 -Bless              record screenshot baselines (deliberate, dedicated commit)
#>
[CmdletBinding()]
param(
    [switch]$StaticOnly,
    [switch]$Bless,
    [string]$BaseUrl = $(if ($env:UI_BASE_URL) { $env:UI_BASE_URL } else { 'https://localhost:44368' }),
    [string]$Configuration = 'TestRun'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$staticOk = $false
$renderedOk = $false

Write-Host ''
Write-Host '=== 1/2  STATIC GATES ===' -ForegroundColor Cyan

# TestRun exists because VS/IIS Express hold a lock on bin\Debug. It is DECLARED and defines DEBUG,
# so it compiles the same program Debug does - an undeclared TestRun once did not, and every
# acceptance run against it measured the wrong binary.
$env:CROSSBUY_REPO_ROOT = $repoRoot
& dotnet test (Join-Path $repoRoot 'CrossBuy.Tests\CrossBuy.Tests.csproj') `
    -c $Configuration --nologo --filter 'FullyQualifiedName~UiConformance'
$staticOk = ($LASTEXITCODE -eq 0)

if ($StaticOnly) {
    Write-Host ''
    if ($staticOk) { Write-Host 'PASS  (static gates only - rendered checks NOT VERIFIED)' -ForegroundColor Yellow; exit 0 }
    Write-Host 'FAIL' -ForegroundColor Red; exit 1
}

Write-Host ''
Write-Host '=== 2/2  RENDERED GATES ===' -ForegroundColor Cyan

if (-not $env:UI_USER -or -not $env:UI_PASS) {
    Write-Host 'NOT VERIFIED - UI_USER / UI_PASS are not set.' -ForegroundColor Yellow
    Write-Host 'Every governed route is [SessionValidation]; without a session this would verify the' -ForegroundColor Yellow
    Write-Host 'login page and report a pass. Refusing to do that.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'FAIL' -ForegroundColor Red
    exit 1
}

Push-Location $PSScriptRoot
try {
    if (-not (Test-Path (Join-Path $PSScriptRoot 'node_modules'))) {
        Write-Host 'Installing verification dependencies (first run only)...'
        & npm install --silent
        & npx playwright install chromium
    }

    $env:UI_BASE_URL = $BaseUrl
    if ($Bless) { & node ui-conformance.mjs --bless } else { & node ui-conformance.mjs }
    $renderedOk = ($LASTEXITCODE -eq 0)
}
finally { Pop-Location }

Write-Host ''
if ($staticOk -and $renderedOk) {
    Write-Host 'PASS' -ForegroundColor Green
    Write-Host ''
    Write-Host 'Cleanup may now be RECOMMENDED to the Workspace owner. This tool performs no cleanup.'
    exit 0
}

Write-Host 'FAIL' -ForegroundColor Red
Write-Host "  static gates:   $(if ($staticOk) { 'pass' } else { 'FAIL' })"
Write-Host "  rendered gates: $(if ($renderedOk) { 'pass' } else { 'FAIL / NOT VERIFIED' })"
exit 1
