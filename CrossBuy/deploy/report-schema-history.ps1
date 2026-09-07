<#
=============================================================================================
Stage 0 Batch B — schema-history reporting command.

Compares deploy/sql/manifest.json (what the repository contains) against
dbo.PlatformSchemaHistory (what this database says has been applied) and reports, per script:

    applied  — the latest apply succeeded AND the file still hashes to what was applied.
    changed  — the latest apply succeeded but the file has been EDITED since. The database is
               running older DDL than the repository describes. This is the finding that a
               name-only deployment log can never produce.
    failed   — the latest recorded attempt failed. A half-applied script is the worst state a
               deploy can be in, so this is reported first and separately from "pending".
    pending  — no successful apply recorded.
    missing  — the log names a script that no longer exists in the repository (deleted or renamed).

WHAT THIS COMMAND DOES NOT DO: it never executes a deployment script. Applying SQL is a
deliberate manual act under the runbook (docs/deployment/SQL-Deployment-Runbook.md); this only
reads, plus -Record / -Baseline which write a single history row each.

USAGE

    # report
    powershell -NoProfile -File CrossBuy/deploy/report-schema-history.ps1 `
        -ConnectionString "Server=.;Database=CrossBuyDB2;Trusted_Connection=True;TrustServerCertificate=True"

    # record a script you have just applied by hand
    ... -Record "CrossBuy/deploy/sql/platform_business_events_slice_002.sql"

    # record a failed attempt, with the reason
    ... -Record "CrossBuy/deploy/sql/comm_outbox_slice_003.sql" -Failed -ErrorText "Msg 4922 ..."

    # assert that a script was already applied before this table existed (see the baseline
    # procedure in deploy/sql/platform_schema_history.sql)
    ... -Baseline "CrossBuy/deploy/sql/platform_business_events.sql"

EXIT CODES: 0 = everything applied (or report-only with nothing outstanding); 1 = something is
pending/changed/missing; 2 = a failed apply is recorded; 3 = the command itself could not run.
The distinction matters for CI: a failed apply is not the same as work not yet done.
=============================================================================================
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConnectionString,
    [string]$ManifestPath,
    [string]$Record,
    [string]$Baseline,
    [switch]$Failed,
    [string]$ErrorText,
    [string]$ApplicationVersion,
    [switch]$IncludeExcluded,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $ManifestPath) { $ManifestPath = Join-Path $PSScriptRoot 'sql\manifest.json' }

if (-not (Test-Path $ManifestPath)) {
    Write-Host "manifest not found: $ManifestPath" -ForegroundColor Red
    Write-Host "Generate it first: powershell -File CrossBuy/deploy/scan-sql-manifest.ps1"
    exit 3
}

# ---------------------------------------------------------------------------------------------
# Guard rail: this command writes to whatever database it is pointed at. It refuses a connection
# string with no Initial Catalog rather than defaulting to master, because "-Baseline against
# master" would silently create the table in the wrong place.
# ---------------------------------------------------------------------------------------------
try { $csb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString }
catch { Write-Host "not a valid connection string: $_" -ForegroundColor Red; exit 3 }
if ([string]::IsNullOrWhiteSpace($csb.InitialCatalog)) {
    Write-Host "the connection string must name a database (Initial Catalog / Database=)." -ForegroundColor Red
    exit 3
}

$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json

function Get-Sha256([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($sha.ComputeHash([System.IO.File]::ReadAllBytes($path))) -replace '-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Invoke-Sql([string]$sql, [hashtable]$parameters = @{}) {
    $rows = @()
    $connection = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $sql
        $command.CommandTimeout = 120
        foreach ($k in $parameters.Keys) {
            $p = $command.CreateParameter()
            $p.ParameterName = "@$k"
            if ($null -eq $parameters[$k]) { $p.Value = [DBNull]::Value } else { $p.Value = $parameters[$k] }
            [void]$command.Parameters.Add($p)
        }
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $row = [ordered]@{}
                for ($i = 0; $i -lt $reader.FieldCount; $i++) {
                    $row[$reader.GetName($i)] = if ($reader.IsDBNull($i)) { $null } else { $reader.GetValue($i) }
                }
                $rows += [pscustomobject]$row
            }
        } finally { $reader.Close() }
    } finally { $connection.Close() }
    return $rows
}

function Invoke-NonQuery([string]$sql, [hashtable]$parameters = @{}) {
    $connection = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $sql
        $command.CommandTimeout = 120
        foreach ($k in $parameters.Keys) {
            $p = $command.CreateParameter()
            $p.ParameterName = "@$k"
            if ($null -eq $parameters[$k]) { $p.Value = [DBNull]::Value } else { $p.Value = $parameters[$k] }
            [void]$command.Parameters.Add($p)
        }
        return $command.ExecuteNonQuery()
    } finally { $connection.Close() }
}

# ---------------------------------------------------------------------------------------------
# The table must exist. It is created by deploy/sql/platform_schema_history.sql, applied like any
# other script — this command does not create it, so that "SQL before code" stays true even here.
# ---------------------------------------------------------------------------------------------
$tableExists = (Invoke-Sql "SELECT CASE WHEN OBJECT_ID('dbo.PlatformSchemaHistory','U') IS NULL THEN 0 ELSE 1 END AS Present;")[0].Present
if (-not $tableExists) {
    Write-Host "dbo.PlatformSchemaHistory does not exist in [$($csb.InitialCatalog)]." -ForegroundColor Red
    Write-Host "Apply CrossBuy/deploy/sql/platform_schema_history.sql first, then re-run this command."
    exit 3
}

# ---------------------------------------------------------------------------------------------
# -Record / -Baseline: write exactly one history row, then fall through to the report.
# ---------------------------------------------------------------------------------------------
$writeTarget = if ($Record) { $Record } elseif ($Baseline) { $Baseline } else { $null }
if ($writeTarget) {
    $relative = $writeTarget.Replace('\', '/').TrimStart('./')
    $entry = $manifest.scripts | Where-Object { $_.path -eq $relative }
    if (-not $entry) {
        Write-Host "'$relative' is not in the manifest. Paths are repo-relative, e.g." -ForegroundColor Red
        Write-Host "  CrossBuy/deploy/sql/platform_business_events.sql"
        exit 3
    }
    $full = Join-Path $repo $relative.Replace('/', '\')
    if (-not (Test-Path $full)) { Write-Host "file not found on disk: $full" -ForegroundColor Red; exit 3 }

    # Hash the file as it is NOW. For -Record that is the version just applied; for -Baseline it is an
    # assertion that this version is what the database already carries — which is why the row is
    # stamped 'baseline' and stays distinguishable forever.
    $hash = Get-Sha256 $full
    if ($hash -ne $entry.sha256) {
        Write-Host "WARNING: the file has changed since the manifest was generated." -ForegroundColor Yellow
        Write-Host "  manifest: $($entry.sha256)"
        Write-Host "  on disk : $hash"
        Write-Host "  Re-run scan-sql-manifest.ps1 so the manifest and the recorded hash agree."
    }

    if ($Failed -and [string]::IsNullOrWhiteSpace($ErrorText)) {
        Write-Host "-Failed requires -ErrorText: a failure with no reason recorded is not a usable record." -ForegroundColor Red
        exit 3
    }
    if ($Baseline -and $Failed) {
        Write-Host "-Baseline and -Failed are contradictory: a baseline asserts the script is already applied." -ForegroundColor Red
        exit 3
    }

    $appliedBy = if ($Baseline) { 'baseline' } else { "$env:USERDOMAIN\$env:USERNAME" }
    $errorValue = if ($Failed) { $ErrorText.Substring(0, [Math]::Min(2000, $ErrorText.Length)) } else { $null }

    [void](Invoke-NonQuery @"
INSERT INTO dbo.PlatformSchemaHistory (ScriptName, ScriptHash, AppliedAt, AppliedBy, ApplicationVersion, Success, Error)
VALUES (@name, @hash, SYSUTCDATETIME(), @by, @version, @success, @error);
"@ @{
        name = $relative; hash = $hash; by = $appliedBy
        version = if ($ApplicationVersion) { $ApplicationVersion } else { $null }
        success = if ($Failed) { 0 } else { 1 }
        error = $errorValue
    })

    $verb = if ($Baseline) { 'baselined' } elseif ($Failed) { 'recorded FAILED' } else { 'recorded applied' }
    Write-Host ("{0}: {1}" -f $verb, $relative) -ForegroundColor Green
    Write-Host ""
}

# ---------------------------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------------------------
$current = Invoke-Sql "SELECT ScriptName, ScriptHash, AppliedAt, AppliedBy, Success, Error, ApplyCount FROM dbo.vw_PlatformSchemaCurrent;"
$byName = @{}
foreach ($row in $current) { $byName[$row.ScriptName] = $row }

# Deployable set = everything the manifest does not exclude. 'not-deployable' and excludedFromDeploy
# scripts are reported separately so they never sit in the pending list as permanent noise.
$deployable = $manifest.scripts | Where-Object {
    $IncludeExcluded -or (-not $_.excludedFromDeploy -and $_.idempotency -ne 'not-deployable')
}

$applied = @(); $changed = @(); $failedRows = @(); $pending = @()
foreach ($s in $deployable) {
    $row = $byName[$s.path]
    if (-not $row) { $pending += $s; continue }
    if (-not $row.Success) { $failedRows += [pscustomobject]@{ script = $s; row = $row }; continue }
    if ($row.ScriptHash -eq $s.sha256) { $applied += [pscustomobject]@{ script = $s; row = $row } }
    else { $changed += [pscustomobject]@{ script = $s; row = $row } }
}

$manifestPaths = @{}
foreach ($s in $manifest.scripts) { $manifestPaths[$s.path] = $true }
$missing = @($current | Where-Object { -not $manifestPaths.ContainsKey($_.ScriptName) })

if (-not $Quiet) {
    Write-Host "Schema history — [$($csb.InitialCatalog)] on $($csb.DataSource)"
    Write-Host ("manifest: {0} scripts ({1} deployable)" -f $manifest.totalScripts, @($deployable).Count)
    Write-Host ""

    if ($failedRows.Count) {
        Write-Host "FAILED — a recorded apply did not complete. Resolve these before anything else:" -ForegroundColor Red
        foreach ($f in $failedRows) {
            Write-Host ("  {0}" -f $f.script.path)
            Write-Host ("      at {0} by {1}" -f $f.row.AppliedAt, $f.row.AppliedBy)
            Write-Host ("      {0}" -f $f.row.Error)
        }
        Write-Host ""
    }
    if ($changed.Count) {
        Write-Host "CHANGED — applied here, but the file has been edited since. The database is running" -ForegroundColor Yellow
        Write-Host "older DDL than the repository describes. Re-apply (the scripts are idempotent) and re-record:" -ForegroundColor Yellow
        foreach ($c in $changed) {
            Write-Host ("  {0}" -f $c.script.path)
            Write-Host ("      applied {0} with {1}" -f $c.row.AppliedAt, $c.row.ScriptHash.Substring(0, 12))
            Write-Host ("      repo now {0}" -f $c.script.sha256.Substring(0, 12))
        }
        Write-Host ""
    }
    if ($missing.Count) {
        Write-Host "MISSING — recorded as applied, but no such file in the repository (deleted or renamed):" -ForegroundColor Yellow
        foreach ($m in $missing) { Write-Host ("  {0}   (applied {1})" -f $m.ScriptName, $m.AppliedAt) }
        Write-Host ""
    }
    if ($pending.Count) {
        Write-Host ("PENDING — no successful apply recorded ({0}), in apply order:" -f $pending.Count)
        foreach ($p in ($pending | Sort-Object rank, path)) {
            Write-Host ("  [rank {0,2}] {1}" -f $p.rank, $p.path)
        }
        Write-Host ""
    }
    Write-Host ("APPLIED: {0}   PENDING: {1}   CHANGED: {2}   FAILED: {3}   MISSING: {4}" -f `
        $applied.Count, $pending.Count, $changed.Count, $failedRows.Count, $missing.Count)

    $excludedList = @($manifest.scripts | Where-Object { $_.excludedFromDeploy -or $_.idempotency -eq 'not-deployable' })
    if ($excludedList.Count -and -not $IncludeExcluded) {
        Write-Host ""
        Write-Host ("Not deployable, excluded from the counts above ({0}) — pass -IncludeExcluded to list them in full:" -f $excludedList.Count)
        foreach ($e in $excludedList) { Write-Host ("  {0}   {1}" -f $e.path, $e.excludedReason) }
    }

    $unread = @($manifest.scripts | Where-Object { $_.idempotency -eq 'review' })
    if ($unread.Count) {
        Write-Host ""
        Write-Host ("WARNING: {0} script(s) in the manifest have an unguarded mutating batch nobody has read." -f $unread.Count) -ForegroundColor Yellow
        Write-Host "Do not deploy them. See the review ledger in scan-sql-manifest.ps1." -ForegroundColor Yellow
    }
}

if ($failedRows.Count) { exit 2 }
if ($pending.Count -or $changed.Count -or $missing.Count) { exit 1 }
exit 0
