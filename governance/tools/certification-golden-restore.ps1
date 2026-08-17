<#
    CrossBusiness - CERTIFICATION GOLDEN RESTORE.

    Restores CrossBuyCert from the golden artifact so every certification capture starts from the
    same data. This is the operation that makes a screenshot baseline mean something: if the dataset
    is identical, a pixel difference can only have come from the UI.

    ORDER OF PROOF. The artifact is verified BEFORE anything destructive happens - SHA-256 against
    the recorded metadata, then the backup header, then FILELISTONLY. A restore that discovers a
    problem halfway through has already destroyed the database it was meant to reset.

    SINGLE_USER IS A WINDOW, NOT A STATE. Taking the database SINGLE_USER is required to evict the
    application's connections, but a failed restore that leaves it there makes every later run fail
    with a misleading error. The transition is therefore wrapped in try/finally and MULTI_USER is
    restored on every path, including failure.

    SAFETY. Development only, UI_CONFORMANCE=1 only, target exactly CrossBuyCert, and
    CrossBuyDev / CrossBuyDB / CrossBuyDB2 / CrossBuy / alprimedb_prod refused by name. Report-only
    unless -Apply.

        powershell -File governance/tools/certification-golden-restore.ps1 -ConnectionString "..."           # report only
        powershell -File governance/tools/certification-golden-restore.ps1 -ConnectionString "..." -Apply    # restore
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConnectionString,
    [switch]$Apply,
    [string]$ArtifactDirectory,
    [string]$Environment = $env:ASPNETCORE_ENVIRONMENT,
    [string]$ConformanceFlag = $env:UI_CONFORMANCE
)

$ErrorActionPreference = 'Stop'

$certificationDatabase = 'CrossBuyCert'
$forbiddenCatalogs     = @('CrossBuyDev', 'CrossBuyDB', 'CrossBuyDB2', 'CrossBuy', 'alprimedb_prod')
$repoRoot              = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Die([string]$m) { Write-Host ("REFUSED: {0}" -f $m) -ForegroundColor Red; exit 2 }

Write-Host ''
Write-Host '=== CERTIFICATION GOLDEN RESTORE ===' -ForegroundColor Cyan

if ($Environment -ne 'Development') { Die "environment is '$Environment'; this tool runs only on Development." }
if ($ConformanceFlag -cne '1')      { Die "UI_CONFORMANCE is '$ConformanceFlag'; it must be exactly '1'." }

try { $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString }
catch { Die "connection string is not parseable: $($_.Exception.Message)" }

$catalog = $builder['Initial Catalog']
$server  = $builder['Data Source']
if ([string]::IsNullOrWhiteSpace($catalog)) { Die 'the connection string names no catalogue; this tool refuses to guess one.' }
foreach ($f in $forbiddenCatalogs) {
    if ($catalog -ieq $f) { Die "'$catalog' is not the certification database. A restore OVERWRITES its target - this tool never points at development, reference or production data." }
}
if ($catalog -ine $certificationDatabase) { Die "'$catalog' is not '$certificationDatabase'." }

function Invoke-Sql([string]$sql, [int]$timeout = 600) {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("cert-gr-{0}.sql" -f ([guid]::NewGuid().ToString('N')))
    Set-Content -Path $tmp -Value $sql -Encoding utf8
    try {
        $out = & sqlcmd -S $server -d master -i $tmp -W -h -1 -t $timeout 2>&1
        $bad = @($out | Where-Object { $_ -match '^Msg \d+, Level' })
        if ($LASTEXITCODE -ne 0 -or $bad.Count -gt 0) { throw (($out | Select-Object -First 6) -join ' / ') }
        return $out
    } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
}

# ---- locate + validate the artifact BEFORE anything destructive -------------------------------
# WHERE THE ARTIFACT LIVES. Not in the repository (a few hundred MB of binary is not reviewable and
# would be swept up by the first `git add -A`), and not in the instance's own Backup folder either -
# that sits under Program Files, where an ordinary developer account cannot create a directory or
# write the metadata file beside the artifact. So: a machine-local directory that BOTH the operator
# and the SQL Server service account can use. Overridable per host via CROSSBUY_CERT_ARTIFACTS or
# -ArtifactDirectory; nothing here is a credential.
#
# The directory must already grant write access to the SQL Server service account, e.g.
#   icacls C:\CrossBuyCertification /grant "NT Service\MSSQL$SQLEXPRESS:(OI)(CI)M"
# The backup itself is written by SQL Server, not by this script, so a missing grant surfaces as a
# loud BACKUP failure rather than a silent one.
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = if ($env:CROSSBUY_CERT_ARTIFACTS) { $env:CROSSBUY_CERT_ARTIFACTS } else { 'C:\CrossBuyCertification' }
}
$full = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if ($full.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) { Die "the artifact directory '$full' is inside the repository." }

$backupFile   = Join-Path $ArtifactDirectory 'CrossBuyCert.golden.bak'
$metadataFile = Join-Path $ArtifactDirectory 'CrossBuyCert.golden.metadata.json'
if (-not (Test-Path $backupFile))   { Die "no golden artifact at '$backupFile'. Create one with certification-golden-backup.ps1." }
if (-not (Test-Path $metadataFile)) { Die "no metadata at '$metadataFile'. An artifact without recorded identity cannot be validated, and a restore is not something to do on trust." }

$meta = Get-Content $metadataFile -Raw | ConvertFrom-Json
Write-Host ("  artifact : {0}" -f $backupFile) -ForegroundColor DarkGray
Write-Host ("  recorded : created={0}  source={1}" -f $meta.createdUtc, $meta.sourceDatabase) -ForegroundColor DarkGray

if ($meta.sourceDatabase -ine $certificationDatabase) {
    Die "the artifact records sourceDatabase='$($meta.sourceDatabase)', not '$certificationDatabase'. Restoring another database's backup INTO the certification database would silently replace the canonical dataset."
}

Write-Host '  validating sha256...' -ForegroundColor Yellow
$actual = (Get-FileHash -Path $backupFile -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne ($meta.backupSha256).ToLowerInvariant()) {
    Die "artifact SHA-256 mismatch. recorded=$($meta.backupSha256) actual=$actual. The artifact has changed since it was recorded; refusing to restore it."
}
Write-Host ("    sha256 matches metadata ({0}...)" -f $actual.Substring(0,16)) -ForegroundColor DarkGray

Write-Host '  reading backup header + file list...' -ForegroundColor Yellow
$escaped = $backupFile.Replace("'", "''")
$hdr = Invoke-Sql "SET NOCOUNT ON; DECLARE @h TABLE(DatabaseName nvarchar(256), BackupName nvarchar(256)); SELECT 'HDRCHECK';"
# FILELISTONLY / HEADERONLY return wide result sets; ask SQL for just what matters.
$header = & sqlcmd -S $server -d master -W -h -1 -Q "SET NOCOUNT ON; RESTORE HEADERONLY FROM DISK = N'$escaped';" 2>&1
$fileList = & sqlcmd -S $server -d master -W -h -1 -Q "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK = N'$escaped';" 2>&1
if ($LASTEXITCODE -ne 0) { Die "could not read the backup header/file list: $(($fileList | Select-Object -First 4) -join ' / ')" }
$headerNamesSource = ($header | Where-Object { $_ -match $certificationDatabase } | Select-Object -First 1)
if (-not $headerNamesSource) { Die "the backup header does not name '$certificationDatabase'. Refusing to restore an artifact whose source identity cannot be proven." }
Write-Host '    header names CrossBuyCert; file list read' -ForegroundColor DarkGray

Write-Host '  verifying artifact integrity (RESTORE VERIFYONLY)...' -ForegroundColor Yellow
Invoke-Sql "RESTORE VERIFYONLY FROM DISK = N'$escaped' WITH CHECKSUM;" | Out-Null
Write-Host '    verified' -ForegroundColor DarkGray

if (-not $Apply) {
    Write-Host ''
    Write-Host 'REPORT ONLY - nothing was restored. Re-run with -Apply.' -ForegroundColor Cyan
    exit 0
}

# ---- restore ----------------------------------------------------------------------------------
Write-Host ''
Write-Host '  restoring (SINGLE_USER window)...' -ForegroundColor Yellow
$restored = $false
try {
    Invoke-Sql "ALTER DATABASE [$certificationDatabase] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;" 120
    Invoke-Sql "RESTORE DATABASE [$certificationDatabase] FROM DISK = N'$escaped' WITH REPLACE, RECOVERY, STATS = 25;" 1800
    $restored = $true
}
finally {
    # MULTI_USER on EVERY path. A failed restore that leaves the database SINGLE_USER turns one
    # clear failure into a confusing one for every run that follows.
    try { Invoke-Sql "IF DATABASEPROPERTYEX('$certificationDatabase','Status') = 'ONLINE' ALTER DATABASE [$certificationDatabase] SET MULTI_USER;" 120 | Out-Null }
    catch { Write-Host ("  WARNING: could not return {0} to MULTI_USER: {1}" -f $certificationDatabase, $_.Exception.Message) -ForegroundColor Red }
}
if (-not $restored) { Die 'the restore did not complete.' }
Write-Host '    restored; database returned to MULTI_USER' -ForegroundColor DarkGray

# ---- post-restore verification ----------------------------------------------------------------
Write-Host ''
Write-Host '  post-restore verification...' -ForegroundColor Yellow
$state = Invoke-Sql "SET NOCOUNT ON; SELECT 'STATE|' + state_desc + '|' + user_access_desc FROM sys.databases WHERE name = '$certificationDatabase';"
Write-Host ("    {0}" -f (@($state | Where-Object { $_ -like 'STATE|*' }) | Select-Object -First 1)) -ForegroundColor DarkGray

$evidenceSql = @"
SET NOCOUNT ON;
SELECT 'FIXTURE|' + Title + '|' + CAST(IsActive AS varchar(1)) + '|'
     + ISNULL(CONVERT(varchar(30), StartsAt, 126),'NULL') + '|' + ISNULL(CONVERT(varchar(30), ExpiresAt, 126),'NULL')
FROM [$certificationDatabase].dbo.Announcements
WHERE CompanyID = 1 AND Title LIKE 'ZZ-UI-CONFORMANCE%' ORDER BY Title;
SELECT 'ROWS|' + CAST(SUM(p.rows) AS varchar(20))
FROM [$certificationDatabase].sys.tables t
JOIN [$certificationDatabase].sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0,1);
"@
$ev = Invoke-Sql $evidenceSql
$fixtures = @($ev | Where-Object { $_ -like 'FIXTURE|*' })
$rows = ((@($ev | Where-Object { $_ -like 'ROWS|*' }) | Select-Object -First 1) -split '\|')[1]
$fixtureHash = [System.BitConverter]::ToString(
    [System.Security.Cryptography.SHA256]::Create().ComputeHash(
        [System.Text.Encoding]::UTF8.GetBytes(($fixtures -join "`n")))).Replace('-','').ToLowerInvariant()

$fixtures | ForEach-Object { Write-Host ("    {0}" -f $_) -ForegroundColor DarkGray }
Write-Host ("    rows={0}  (recorded {1})" -f $rows, $meta.canonicalRows) -ForegroundColor DarkGray
Write-Host ("    fixture-state sha256={0}" -f $fixtureHash) -ForegroundColor DarkGray

if ($fixtureHash -ne ($meta.fixtureStateSha256).ToLowerInvariant()) {
    Write-Host '  FIXTURE STATE MISMATCH after restore.' -ForegroundColor Red
    exit 3
}
Write-Host '    fixture state matches the golden metadata' -ForegroundColor DarkGray

Write-Host '  running certification pre-flight...' -ForegroundColor Yellow
& powershell -NoProfile -File (Join-Path $PSScriptRoot 'certification-preflight.ps1') -ConnectionString $ConnectionString -Environment $Environment -ConformanceFlag $ConformanceFlag | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host '  PRE-FLIGHT FAILED after restore.' -ForegroundColor Red; exit 4 }
Write-Host '    pre-flight: CLEARED' -ForegroundColor DarkGray

Write-Host ''
Write-Host 'GOLDEN RESTORE COMPLETE.' -ForegroundColor Green
exit 0
