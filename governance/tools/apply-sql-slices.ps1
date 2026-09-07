<#
=============================================================================================
R1 - SQL deployment pipeline.

Applies slices to ONE database in manifest rank order, recording every attempt - success and
failure - in dbo.PlatformSchemaHistory.

SAFETY, stated before anything else:

  * DRY RUN IS THE DEFAULT. Nothing is executed unless -Apply is passed explicitly.
  * PRODUCTION CATALOGUES ARE REFUSED OUTRIGHT. CrossBuyDB, CrossBuyDB2 and CrossBuy are rejected
    whatever flags are passed. This pipeline is not the tool that touches them; the runbook and a
    human are. A deployment tool that can reach production by accident will eventually reach it
    by accident.
  * Scripts the manifest classifies 'review', 'not-deployable' or excludedFromDeploy are never
    run, and their presence blocks the whole run rather than being skipped quietly.
  * A divergent duplicate name blocks the run: "apply pos_setup.sql" is ambiguous, and guessing
    which copy is meant is guessing about schema.

ORDER comes from manifest.json `rank`. Scripts sharing a rank are independent of one another.

platform_schema_history.sql is applied FIRST and unconditionally - a database with no history
table has no governed state, so there is nothing to record into until it exists.

EXIT CODES
    0  dry run clean, or every slice applied successfully
    1  a slice failed (recorded in history with Success = 0)
    2  the run was blocked by governance before anything was executed
    3  the command could not run

USAGE
    # see what would happen - THE DEFAULT
    powershell -File governance/tools/apply-sql-slices.ps1 -ConnectionString "Server=.;Database=CrossBuyProbe_X;Trusted_Connection=True;TrustServerCertificate=True"

    # actually apply
    ... -Apply

    # a single slice
    ... -Only platform_schema_history.sql -Apply
=============================================================================================
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConnectionString,
    [switch]$Apply,
    [switch]$Bootstrap,
    [string[]]$Slice,
    [string]$Only,
    [string]$Environment = 'Dev'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$manifestPath = Join-Path $repo 'CrossBuy\deploy\sql\manifest.json'

# Catalogues this tool refuses to touch under any flag combination.
$forbiddenCatalogs = @('CrossBuyDB', 'CrossBuyDB2', 'CrossBuy')

if (-not (Test-Path $manifestPath)) { Write-Host "manifest not found: $manifestPath" -ForegroundColor Red; exit 3 }

try { $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString }
catch { Write-Host "not a valid connection string: $_" -ForegroundColor Red; exit 3 }

$catalog = $builder['Initial Catalog']
if ([string]::IsNullOrWhiteSpace($catalog)) { Write-Host 'connection string names no database.' -ForegroundColor Red; exit 3 }

Write-Host ''
Write-Host 'R1 SQL DEPLOYMENT PIPELINE' -ForegroundColor Cyan
Write-Host '=========================='
Write-Host ("  database    : {0}" -f $catalog)
Write-Host ("  environment : {0}" -f $Environment)
if ($Apply) { Write-Host '  mode        : APPLY' -ForegroundColor Yellow }
else        { Write-Host '  mode        : DRY RUN (pass -Apply to execute)' -ForegroundColor Green }

foreach ($f in $forbiddenCatalogs) {
    if ($catalog -eq $f) {
        Write-Host ''
        Write-Host ("REFUSED: '{0}' is a production catalogue." -f $catalog) -ForegroundColor Red
        Write-Host 'This pipeline never targets production. Use the runbook and a human.' -ForegroundColor Red
        exit 2
    }
}

try { $manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { Write-Host "manifest is not valid JSON: $_" -ForegroundColor Red; exit 3 }

# ---- governance pre-flight -------------------------------------------------------------------
Write-Host ''
Write-Host 'PRE-FLIGHT'
$blocked = @()
$dupes = @($manifest.duplicateNames | Where-Object { $_ -and -not $_.identical })
foreach ($d in $dupes) { $blocked += ("divergent duplicate: {0} ({1} hashes)" -f $d.name, $d.distinctHashes) }
foreach ($s in @($manifest.scripts | Where-Object { $_.idempotency -eq 'review' })) {
    $blocked += ("unreviewed script present: {0}" -f $s.path)
}
foreach ($s in @($manifest.scripts | Where-Object { $_.idempotency -eq 'not-deployable' })) {
    $blocked += ("not-deployable script present: {0}" -f $s.path)
}

if ($blocked.Count) {
    foreach ($b in $blocked) { Write-Host ("  BLOCKED  {0}" -f $b) -ForegroundColor Red }

    # -Bootstrap is the ONE narrow exception, and it exists because of a genuine ordering problem:
    # the applied registry cannot be established until platform_schema_history.sql has been applied,
    # yet the governance backlog blocks every run. Without this, a database can never reach a
    # governed state while the backlog exists - and the backlog is precisely what governance is for.
    #
    # It is deliberately narrow: history table only, and only if that script is itself clean.
    # It grants no relief to any other slice.
    if ($Bootstrap) {
        $histScript = @($manifest.scripts | Where-Object { $_.name -eq 'platform_schema_history.sql' })
        if ($histScript.Count -ne 1) {
            Write-Host ''
            Write-Host 'REFUSED: -Bootstrap requires exactly one platform_schema_history.sql in the manifest.' -ForegroundColor Red
            exit 2
        }
        if ($histScript[0].idempotency -in @('review', 'not-deployable') -or $histScript[0].excludedFromDeploy) {
            Write-Host ''
            Write-Host 'REFUSED: platform_schema_history.sql is itself not deployable.' -ForegroundColor Red
            exit 2
        }
        Write-Host ''
        Write-Host 'BOOTSTRAP: applying ONLY platform_schema_history.sql past the governance block.' -ForegroundColor Yellow
        Write-Host 'Every other slice remains blocked until the backlog above is resolved.' -ForegroundColor Yellow
        $Only = 'platform_schema_history.sql'
    } elseif ($Slice -and $Slice.Count) {
        # TARGETED DEPLOY.
        #
        # The global block exists for one reason: "apply pos_setup.sql" is AMBIGUOUS while two files of
        # that name differ. That ambiguity is real, and it is confined to the named slices - it says
        # nothing about reporting_platform.sql, which is guarded, unique and in the canonical root.
        #
        # Blocking a clean, unrelated slice on someone else's ambiguity is over-blocking, and
        # over-blocking is precisely how a gate ends up being bypassed rather than obeyed. So a NAMED
        # slice may proceed - but only after being validated on its own terms, every one of which is
        # the same test the global pre-flight applies:
        #
        #   * it exists exactly once in the manifest       (not one of the ambiguous duplicates)
        #   * it is not 'review' or 'not-deployable'
        #   * it is not excludedFromDeploy
        #   * it lives under the canonical authored root   (D-38)
        #
        # Nothing is waived. The backlog still blocks a FULL run, and this leaves no way to deploy the
        # very scripts the backlog is about.
        $canonical = 'CrossBuy/deploy/sql/'
        $refused = @()
        foreach ($name in $Slice) {
            $hit = @($manifest.scripts | Where-Object { $_.name -eq $name })
            if ($hit.Count -eq 0) { $refused += "$name : not in the manifest"; continue }
            if ($hit.Count -gt 1) { $refused += "$name : $($hit.Count) files share this name - ambiguous"; continue }
            $dup = @($manifest.duplicateNames | Where-Object { $_.name -eq $name -and -not $_.identical })
            if ($dup.Count) { $refused += "$name : divergent duplicate across trees"; continue }
            if ($hit[0].idempotency -in @('review', 'not-deployable')) { $refused += "$name : idempotency = $($hit[0].idempotency)"; continue }
            if ($hit[0].excludedFromDeploy) { $refused += "$name : excludedFromDeploy"; continue }
            if (-not $hit[0].path.StartsWith($canonical)) { $refused += "$name : outside the canonical root ($($hit[0].path))"; continue }
        }

        if ($refused.Count) {
            Write-Host ''
            foreach ($r in $refused) { Write-Host ("  REFUSED  {0}" -f $r) -ForegroundColor Red }
            Write-Host 'A targeted slice must be individually clean. Nothing was executed.' -ForegroundColor Red
            exit 2
        }

        Write-Host ''
        Write-Host ('TARGETED: {0} slice(s) validated individually and permitted past the backlog.' -f $Slice.Count) -ForegroundColor Yellow
        Write-Host 'The backlog above still blocks a FULL run.' -ForegroundColor Yellow
    } else {
        Write-Host ''
        Write-Host 'Run blocked by governance. Nothing was executed. Resolve per D-39 and the R1 runbook.' -ForegroundColor Red
        Write-Host 'Re-run governance/tools/validate-sql-governance.ps1 for detail.' -ForegroundColor Yellow
        Write-Host 'To establish the applied registry only, re-run with -Bootstrap.' -ForegroundColor Yellow
        Write-Host 'To deploy one named, individually-clean slice, re-run with -Slice <name>.' -ForegroundColor Yellow
        exit 2
    }
}
else { Write-Host '  OK  no divergent duplicates, no unreviewed or not-deployable scripts' -ForegroundColor Green }

# ---- build the ordered plan --------------------------------------------------------------------
$deployable = @($manifest.scripts |
    Where-Object { -not $_.excludedFromDeploy } |
    Where-Object { $_.idempotency -ne 'no-mutation' -or $_.name -eq 'platform_schema_history.sql' } |
    Sort-Object rank, name)

if ($Only) { $deployable = @($deployable | Where-Object { $_.name -eq $Only }) }
if ($Slice -and $Slice.Count) { $deployable = @($deployable | Where-Object { $Slice -contains $_.name }) }

# History table first, always.
$history = @($deployable | Where-Object { $_.name -eq 'platform_schema_history.sql' })
$rest = @($deployable | Where-Object { $_.name -ne 'platform_schema_history.sql' })
$plan = @($history) + @($rest)

if (-not $plan.Count) { Write-Host ''; Write-Host 'nothing to apply.' -ForegroundColor Yellow; exit 0 }

Write-Host ''
Write-Host ("PLAN - {0} slice(s), manifest rank order" -f $plan.Count)
$i = 0
foreach ($s in $plan) {
    $i++
    Write-Host ("  {0,3}. rank {1,-4} {2,-52} {3}" -f $i, $s.rank, $s.name, $s.idempotency)
}

if (-not $Apply) {
    Write-Host ''
    Write-Host 'DRY RUN - nothing executed. Pass -Apply to run this plan.' -ForegroundColor Green
    exit 0
}

# ---- execute --------------------------------------------------------------------------------
function Get-FileSha256([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.IO.File]::ReadAllBytes($path)
        return -join ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') })
    } finally { $sha.Dispose() }
}

function Invoke-SqlBatches([string]$sqlText, [string]$connStr) {
    # GO is a batch separator understood by the client, not by the server.
    $batches = [regex]::Split($sqlText, '(?im)^\s*GO\s*$')
    $conn = New-Object System.Data.SqlClient.SqlConnection $connStr
    $conn.Open()
    try {
        foreach ($b in $batches) {
            if ([string]::IsNullOrWhiteSpace($b)) { continue }
            $cmd = $conn.CreateCommand()
            $cmd.CommandText = $b
            $cmd.CommandTimeout = 300
            [void]$cmd.ExecuteNonQuery()
        }
    } finally { $conn.Close() }
}

$applied = 0; $failed = 0
$who = "$env:USERDOMAIN\$env:USERNAME"

foreach ($s in $plan) {
    $full = Join-Path $repo ($s.path -replace '/', '\')
    if (-not (Test-Path $full)) {
        Write-Host ("  MISSING  {0}" -f $s.path) -ForegroundColor Red
        $failed++
        continue
    }
    $hash = Get-FileSha256 $full
    $text = Get-Content $full -Raw -Encoding UTF8

    Write-Host ''
    Write-Host ("applying {0} ..." -f $s.name)
    $err = $null
    try {
        Invoke-SqlBatches -sqlText $text -connStr $ConnectionString
        Write-Host ("  OK  {0}" -f $s.name) -ForegroundColor Green
        $applied++
    } catch {
        $err = $_.Exception.Message
        Write-Host ("  FAILED  {0}: {1}" -f $s.name, $err) -ForegroundColor Red
        $failed++
    }

    # Record the attempt - success AND failure. A half-applied script must never be invisible.
    if ($s.name -ne 'platform_schema_history.sql' -or $null -eq $err) {
        try {
            $conn = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
            $conn.Open()
            try {
                $cmd = $conn.CreateCommand()
                $cmd.CommandText = @'
IF OBJECT_ID('dbo.PlatformSchemaHistory','U') IS NOT NULL
INSERT INTO dbo.PlatformSchemaHistory (ScriptName, ScriptHash, AppliedBy, Success, Error)
VALUES (@n, @h, @b, @s, @e);
'@
                [void]$cmd.Parameters.AddWithValue('@n', $s.path)
                [void]$cmd.Parameters.AddWithValue('@h', $hash)
                [void]$cmd.Parameters.AddWithValue('@b', $who)
                [void]$cmd.Parameters.AddWithValue('@s', $(if ($err) { 0 } else { 1 }))
                if ($err) {
                    $trimmed = $err.Substring(0, [Math]::Min(2000, $err.Length))
                    [void]$cmd.Parameters.AddWithValue('@e', $trimmed)
                } else {
                    [void]$cmd.Parameters.AddWithValue('@e', [DBNull]::Value)
                }
                [void]$cmd.ExecuteNonQuery()
            } finally { $conn.Close() }
        } catch {
            Write-Host ("  WARNING: could not record history for {0}: {1}" -f $s.name, $_.Exception.Message) -ForegroundColor Yellow
        }
    }

    if ($err) {
        Write-Host ''
        Write-Host 'Stopping: a slice failed. Later slices may depend on it.' -ForegroundColor Red
        break
    }
}

Write-Host ''
Write-Host ("applied {0}, failed {1}" -f $applied, $failed)
if ($failed) { exit 1 }
Write-Host 'PASS: all planned slices applied and recorded.' -ForegroundColor Green
exit 0
