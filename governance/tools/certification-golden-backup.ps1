<#
    CrossBusiness - CERTIFICATION GOLDEN BACKUP.

    Creates the canonical CrossBuyCert artifact that every certification run is restored from.

    WHY A GOLDEN ARTIFACT AT ALL. Screenshot baselines are only falsifiable if the dataset behind
    them is pinned. The 2026-08-15 capture was taken while six background writers were live and the
    announcement fixtures carried an expiring window; nothing could reproduce it afterwards. A run
    that starts from a restored golden database starts from the same data every time, so a screenshot
    difference means the UI changed - which is the only claim a baseline is allowed to make.

    WHY NOT A SNAPSHOT. SQL Server Express (EngineEdition 4) does not support DATABASE SNAPSHOT, so
    the exact-revert route is unavailable on this host. BACKUP / RESTORE is supported on Express and
    is what the owner authorised.

    THE ARTIFACT IS LOCAL AND UNTRACKED. It is a binary of a few hundred MB: it is not reviewable,
    not diffable, and must never enter Git. It is written to the SQL Server instance's own backup
    directory, which is outside the repository and already writable by the service account
    (NT Service\MSSQLSERVER on a default instance) - granting a new directory to a virtual account
    would be one more
    thing to get wrong.

    SAFETY. Fails closed on every axis: Development only, UI_CONFORMANCE=1 only, source must be
    exactly CrossBuyCert, and CrossBuyDev / CrossBuyDB / CrossBuyDB2 / CrossBuy / alprimedb_prod are
    refused by name. Report-only unless -Apply. An existing golden artifact is NEVER silently
    overwritten - that needs -Replace as well.

        powershell -File governance/tools/certification-golden-backup.ps1 -ConnectionString "..."                    # report only
        powershell -File governance/tools/certification-golden-backup.ps1 -ConnectionString "..." -Apply             # create
        powershell -File governance/tools/certification-golden-backup.ps1 -ConnectionString "..." -Apply -Replace    # overwrite
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConnectionString,
    [switch]$Apply,
    [switch]$Replace,
    [string]$ArtifactDirectory,
    [string]$Environment = $env:ASPNETCORE_ENVIRONMENT,
    [string]$ConformanceFlag = $env:UI_CONFORMANCE
)

$ErrorActionPreference = 'Stop'
$toolVersion = 'certification-golden-backup/1.0'

# Mirrors CertificationDataContract; pinned by CertificationDataContractTests.
$certificationDatabase = 'CrossBuyCert'
$forbiddenCatalogs     = @('CrossBuyDev', 'CrossBuyDB', 'CrossBuyDB2', 'CrossBuy', 'alprimedb_prod')
$repoRoot              = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Die([string]$m) { Write-Host ("REFUSED: {0}" -f $m) -ForegroundColor Red; exit 2 }

Write-Host ''
Write-Host '=== CERTIFICATION GOLDEN BACKUP ===' -ForegroundColor Cyan

if ($Environment -ne 'Development') { Die "environment is '$Environment'; this tool runs only on Development." }
if ($ConformanceFlag -cne '1')      { Die "UI_CONFORMANCE is '$ConformanceFlag'; it must be exactly '1'." }

try { $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString }
catch { Die "connection string is not parseable: $($_.Exception.Message)" }

$catalog = $builder['Initial Catalog']
$server  = $builder['Data Source']
if ([string]::IsNullOrWhiteSpace($catalog)) { Die 'the connection string names no catalogue; this tool refuses to guess one.' }
foreach ($f in $forbiddenCatalogs) {
    if ($catalog -ieq $f) { Die "'$catalog' is not the certification database. A golden artifact may only ever be taken FROM $certificationDatabase - a backup of the wrong database silently becomes the canonical dataset." }
}
if ($catalog -ine $certificationDatabase) { Die "'$catalog' is not '$certificationDatabase'." }

function Invoke-Sql([string]$sql, [int]$timeout = 600) {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("cert-gb-{0}.sql" -f ([guid]::NewGuid().ToString('N')))
    Set-Content -Path $tmp -Value $sql -Encoding utf8
    try {
        $out = & sqlcmd -S $server -d master -i $tmp -W -h -1 -t $timeout 2>&1
        $bad = @($out | Where-Object { $_ -match '^Msg \d+, Level' })
        if ($LASTEXITCODE -ne 0 -or $bad.Count -gt 0) { throw (($out | Select-Object -First 6) -join ' / ') }
        return $out
    } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
}

# ---- artifact location ------------------------------------------------------------------------
# WHERE THE ARTIFACT LIVES. Not in the repository (a few hundred MB of binary is not reviewable and
# would be swept up by the first `git add -A`), and not in the instance's own Backup folder either -
# that sits under Program Files, where an ordinary developer account cannot create a directory or
# write the metadata file beside the artifact. So: a machine-local directory that BOTH the operator
# and the SQL Server service account can use. Overridable per host via CROSSBUY_CERT_ARTIFACTS or
# -ArtifactDirectory; nothing here is a credential.
#
# The directory must already grant write access to the SQL Server service account, e.g.
#   icacls C:\CrossBuyCertification /grant "NT Service\MSSQLSERVER:(OI)(CI)M"
# (a named instance uses NT Service\MSSQL$<INSTANCE> instead)
# The backup itself is written by SQL Server, not by this script, so a missing grant surfaces as a
# loud BACKUP failure rather than a silent one.
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = if ($env:CROSSBUY_CERT_ARTIFACTS) { $env:CROSSBUY_CERT_ARTIFACTS } else { 'C:\CrossBuyCertification' }
}
# A golden artifact inside the repository would be committed by the first `git add -A`.
$full = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if ($full.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
    Die "the artifact directory '$full' is inside the repository ('$repoRoot'). The golden backup is a local artifact and must never be committed."
}
$backupFile   = Join-Path $ArtifactDirectory 'CrossBuyCert.golden.bak'
$metadataFile = Join-Path $ArtifactDirectory 'CrossBuyCert.golden.metadata.json'

Write-Host ("  source     : {0} on {1}" -f $catalog, $server) -ForegroundColor DarkGray
Write-Host ("  artifact   : {0}" -f $backupFile) -ForegroundColor DarkGray
Write-Host ("  repo root  : {0}  (artifact is outside it)" -f $repoRoot) -ForegroundColor DarkGray

if ((Test-Path $backupFile) -and -not $Replace) {
    Write-Host ''
    Write-Host 'A golden artifact already exists. Re-run with -Apply -Replace to overwrite it deliberately.' -ForegroundColor Yellow
    if (-not $Apply) { exit 0 }
    Die 'refusing to overwrite an existing golden artifact without -Replace.'
}

# ---- canonical state: fixture reset, then pre-flight -------------------------------------------
Write-Host ''
Write-Host '  establishing canonical state...' -ForegroundColor Yellow
$resetTool    = Join-Path $PSScriptRoot 'certification-fixture-reset.ps1'
$preflightTool = Join-Path $PSScriptRoot 'certification-preflight.ps1'

if ($Apply) {
    & powershell -NoProfile -File $resetTool -ConnectionString $ConnectionString -Environment $Environment -ConformanceFlag $ConformanceFlag -Apply | Out-Null
    if ($LASTEXITCODE -ne 0) { Die "the canonical fixture reset failed (exit $LASTEXITCODE); the dataset is not canonical, so no golden artifact may be taken from it." }
    Write-Host '    fixture reset: applied' -ForegroundColor DarkGray
}

& powershell -NoProfile -File $preflightTool -ConnectionString $ConnectionString -Environment $Environment -ConformanceFlag $ConformanceFlag | Out-Null
if ($LASTEXITCODE -ne 0) { Die "certification pre-flight failed (exit $LASTEXITCODE). A golden artifact is only ever taken from a dataset that already passes pre-flight." }
Write-Host '    pre-flight: CLEARED' -ForegroundColor DarkGray

# ---- canonical evidence (identity, not a timestamp) -------------------------------------------
$evidenceSql = @"
SET NOCOUNT ON;
SELECT 'FIXTURE|' + Title + '|' + CAST(IsActive AS varchar(1)) + '|'
     + ISNULL(CONVERT(varchar(30), StartsAt, 126),'NULL') + '|' + ISNULL(CONVERT(varchar(30), ExpiresAt, 126),'NULL')
FROM [$certificationDatabase].dbo.Announcements
WHERE CompanyID = 1 AND Title LIKE 'ZZ-UI-CONFORMANCE%' ORDER BY Title;
SELECT 'ROWS|' + CAST(SUM(p.rows) AS varchar(20)) + '|' + CAST(COUNT(DISTINCT t.name) AS varchar(10))
FROM [$certificationDatabase].sys.tables t
JOIN [$certificationDatabase].sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0,1);
"@
$evidence = Invoke-Sql $evidenceSql
$fixtures = @($evidence | Where-Object { $_ -like 'FIXTURE|*' })
$rowsLine = (@($evidence | Where-Object { $_ -like 'ROWS|*' }) | Select-Object -First 1)
$parts    = $rowsLine -split '\|'
$totalRows = $parts[1]; $totalTables = $parts[2]

# The fixture-state hash is identity, not a timestamp: two artifacts with the same hash carry the
# same fixture state whatever their file dates say.
$fixtureHash = [System.BitConverter]::ToString(
    [System.Security.Cryptography.SHA256]::Create().ComputeHash(
        [System.Text.Encoding]::UTF8.GetBytes(($fixtures -join "`n")))).Replace('-','').ToLowerInvariant()

Write-Host ''
Write-Host '  canonical evidence:' -ForegroundColor Yellow
$fixtures | ForEach-Object { Write-Host ("    {0}" -f $_) -ForegroundColor DarkGray }
Write-Host ("    tables={0}  rows={1}" -f $totalTables, $totalRows) -ForegroundColor DarkGray
Write-Host ("    fixture-state sha256={0}" -f $fixtureHash) -ForegroundColor DarkGray

if (-not $Apply) {
    Write-Host ''
    Write-Host 'REPORT ONLY - no artifact was created. Re-run with -Apply.' -ForegroundColor Cyan
    exit 0
}

# ---- backup -----------------------------------------------------------------------------------
if (-not (Test-Path $ArtifactDirectory)) { New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null }

Write-Host ''
Write-Host '  creating backup (COPY_ONLY, CHECKSUM)...' -ForegroundColor Yellow
$escaped = $backupFile.Replace("'", "''")
Invoke-Sql "BACKUP DATABASE [$certificationDatabase] TO DISK = N'$escaped' WITH COPY_ONLY, INIT, FORMAT, CHECKSUM, STATS = 25, NAME = N'CrossBuyCert golden';" | Out-Null
Write-Host '    backup written' -ForegroundColor DarkGray

Write-Host '  verifying backup (RESTORE VERIFYONLY)...' -ForegroundColor Yellow
Invoke-Sql "RESTORE VERIFYONLY FROM DISK = N'$escaped' WITH CHECKSUM;" | Out-Null
Write-Host '    verified' -ForegroundColor DarkGray

$sha = (Get-FileHash -Path $backupFile -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $backupFile).Length

$metadata = [ordered]@{
    createdUtc        = (Get-Date).ToUniversalTime().ToString('o')
    tool              = $toolVersion
    sqlInstance       = $server
    sourceDatabase    = $certificationDatabase
    backupFile        = $backupFile
    backupSha256      = $sha
    backupSizeBytes   = $size
    fixtureState      = $fixtures
    fixtureStateSha256 = $fixtureHash
    canonicalTables   = [int]$totalTables
    canonicalRows     = [long]$totalRows
}
$metadata | ConvertTo-Json -Depth 5 | Set-Content -Path $metadataFile -Encoding utf8

Write-Host ''
Write-Host ("  sha256 : {0}" -f $sha) -ForegroundColor Green
Write-Host ("  size   : {0:N0} bytes" -f $size) -ForegroundColor Green
Write-Host ("  metadata: {0}" -f $metadataFile) -ForegroundColor Green
Write-Host ''
Write-Host 'GOLDEN BACKUP CREATED.' -ForegroundColor Green
exit 0
