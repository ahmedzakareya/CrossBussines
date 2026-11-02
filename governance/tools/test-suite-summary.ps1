function Get-IntegrationTestSummary {
    param([string]$Text, [int]$ExitCode, [int]$ExpectedProjects = 2)
    $summaries = [regex]::Matches($Text, 'Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+)')
    $failed = 0; $passed = 0; $skipped = 0
    foreach ($summary in $summaries) {
        $failed += [int]$summary.Groups[1].Value
        $passed += [int]$summary.Groups[2].Value
        $skipped += [int]$summary.Groups[3].Value
    }
    $state = 'FAIL'
    if ($ExitCode -eq 0 -and $failed -eq 0 -and $summaries.Count -eq $ExpectedProjects) { $state = 'PASS' }
    [pscustomobject]@{
        State = $state; Failed = $failed; Passed = $passed; Skipped = $skipped
        Projects = $summaries.Count
        Detail = "$passed passed, $failed failed, $skipped skipped; $($summaries.Count)/$ExpectedProjects test projects; exit $ExitCode"
    }
}
