$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../tools/test-suite-summary.ps1')
$main = 'Passed! - Failed: 0, Passed: 5706, Skipped: 268, Total: 5974'
$analyzer = 'Passed! - Failed: 0, Passed: 111, Skipped: 0, Total: 111'
$green = Get-IntegrationTestSummary "$main`n$analyzer" 0
if ($green.State -ne 'PASS' -or $green.Passed -ne 5817 -or $green.Skipped -ne 268 -or $green.Projects -ne 2) { throw 'Both projects must be counted.' }
$cases = @(
    @{ Text = $main; Exit = 0 },
    @{ Text = ''; Exit = 0 },
    @{ Text = "$main`n$analyzer"; Exit = 1 },
    @{ Text = "$main`nFailed! - Failed: 1, Passed: 110, Skipped: 0"; Exit = 1 },
    @{ Text = "$main`nFailed! - Failed: 1, Passed: 110, Skipped: 0"; Exit = 0 },
    @{ Text = "$main`n$analyzer`n$analyzer"; Exit = 0 }
)
foreach ($case in $cases) {
    if ((Get-IntegrationTestSummary $case.Text $case.Exit).State -ne 'FAIL') { throw 'An incomplete or failed run was accepted.' }
}
Write-Host '7 test-summary controls passed; missing, failed and unexpected project summaries refused.'
exit 0
