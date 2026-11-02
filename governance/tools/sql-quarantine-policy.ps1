# Shared by authoring validation and deployment preflight. Never executes SQL.
function Get-ValidatedSqlQuarantine([string]$Repo, $Manifest) {
    $result = @{}
    $policyPath = Join-Path $Repo 'governance\registry\sql-quarantine.json'
    if (-not (Test-Path -LiteralPath $policyPath -PathType Leaf)) { return $result }
    $policy = Get-Content -LiteralPath $policyPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($policy.schema -ne 1) { throw 'Unsupported SQL quarantine schema' }
    foreach ($item in @($policy.entries)) {
        # This admission is intentionally limited to the historical root scratch file.
        # No deployable feature slice can acquire an allowance by adding a registry row.
        if ($item.path -cne 'script.sql' -or $item.action -cne 'NEVER_DEPLOY' -or
            $item.lfSha256 -notmatch '^[a-f0-9]{64}$' -or
            [string]::IsNullOrWhiteSpace($item.reason) -or
            $item.decision -cne 'docs/deployment/R1-SQL-Quarantine.md' -or
            $result.ContainsKey($item.path)) { throw 'Invalid or duplicate SQL quarantine entry' }
        if (-not (Test-Path -LiteralPath (Join-Path $Repo $item.decision) -PathType Leaf)) {
            throw 'SQL quarantine decision evidence is missing'
        }
        $matches = @($Manifest.scripts | Where-Object { $_.path -ceq $item.path })
        if ($matches.Count -ne 1 -or -not $matches[0].excludedFromDeploy -or
            $matches[0].idempotency -cne 'not-deployable') {
            throw 'Quarantine requires one explicitly excluded, non-deployable manifest identity'
        }
        $raw = [IO.File]::ReadAllBytes((Join-Path $Repo $item.path))
        $lf = New-Object 'System.Collections.Generic.List[byte]'
        for ($i = 0; $i -lt $raw.Length; $i++) {
            if ($raw[$i] -eq 13 -and $i + 1 -lt $raw.Length -and $raw[$i + 1] -eq 10) { continue }
            $lf.Add($raw[$i])
        }
        $hash = [Security.Cryptography.SHA256]::Create()
        try { $actual = ([BitConverter]::ToString($hash.ComputeHash($lf.ToArray())) -replace '-', '').ToLowerInvariant() }
        finally { $hash.Dispose() }
        if ($actual -cne $item.lfSha256) { throw 'Quarantined SQL content changed; new review required' }
        $result[$item.path] = $true
    }
    return $result
}
