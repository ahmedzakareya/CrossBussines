<#
    CrossBusiness - CERTIFICATION FIXTURE RESET (announcement banners).

    WHAT THIS IS. The deterministic half of the certification reset: it materialises the two
    announcement fixtures into the exact state BL/Platform/CertificationDataContract.cs declares.
    It is idempotent, guarded, CrossBuyCert-only, and it reports before it writes.

    WHAT THIS IS NOT. It is NOT the golden database restore. It does not create, drop, restore or
    reseed a database, and it cannot undo runtime mutation of any other table. See the increment
    report: a full golden reset needs a mechanism this repository does not yet have.

    WHY THE FIXTURES NEEDED FIXING AT ALL. Both banners were materialised with
    ExpiresAt = 2026-08-20T19:49:47. AnnouncementService selects active announcements with
    `IsActive && StartsAt <= DateTime.UtcNow && ExpiresAt >= DateTime.UtcNow`, and ConformanceClock
    deliberately does not reach data queries - it fixes the reference instant for relative-time
    LABELS and says so. So on 2026-08-21 both banners would simply stop rendering, every governed
    page would lose the same ~167px of shared chrome, and all 138 screenshot baselines would fail
    for a reason no code change could explain. A certification dataset with a fuse in it is not a
    certification dataset.

    The fix is entirely in the FIXTURE DATA. No production time semantics change, no title is
    special-cased in business logic, and no bypass is introduced: these two rows are simply given a
    window wide enough that no instant a certification host can report falls outside it.

    SAFETY. Fails closed on every axis - Development only, UI_CONFORMANCE=1 only, CrossBuyCert only,
    and CrossBuyDev / CrossBuyDB / CrossBuyDB2 / CrossBuy / alprimedb_prod are refused by name. It
    reports the intended mutation and exits unless -Apply is passed. Rows are matched by exact
    title AND company, and the UPDATE touches only StartsAt / ExpiresAt / IsActive.

        powershell -File governance/tools/certification-fixture-reset.ps1 -ConnectionString "..."          # report only
        powershell -File governance/tools/certification-fixture-reset.ps1 -ConnectionString "..." -Apply   # write
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConnectionString,
    [switch]$Apply,
    [string]$Environment = $env:ASPNETCORE_ENVIRONMENT,
    [string]$ConformanceFlag = $env:UI_CONFORMANCE
)

$ErrorActionPreference = 'Stop'

# Mirrors CertificationDataContract; pinned by CertificationDataContractTests.
$certificationDatabase = 'CrossBuyCert'
$forbiddenCatalogs     = @('CrossBuyDev', 'CrossBuyDB', 'CrossBuyDB2', 'CrossBuy', 'alprimedb_prod')
$criticalBannerTitle   = 'ZZ-UI-CONFORMANCE Critical banner'
$highBannerTitle       = 'ZZ-UI-CONFORMANCE High priority banner'
$certificationCompany  = 1
$windowStart           = '2000-01-01T00:00:00'
$windowEnd             = '9999-12-31T23:59:59'

function Die([string]$m) { Write-Host ("REFUSED: {0}" -f $m) -ForegroundColor Red; exit 2 }

Write-Host ''
Write-Host '=== CERTIFICATION FIXTURE RESET ===' -ForegroundColor Cyan

if ($Environment -ne 'Development') { Die "environment is '$Environment'; this tool runs only on Development." }
if ($ConformanceFlag -cne '1')      { Die "UI_CONFORMANCE is '$ConformanceFlag'; it must be exactly '1'." }

try { $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString }
catch { Die "connection string is not parseable: $($_.Exception.Message)" }

$catalog = $builder['Initial Catalog']
$server  = $builder['Data Source']
if ([string]::IsNullOrWhiteSpace($catalog)) { Die 'the connection string names no catalogue; this tool refuses to guess one.' }
foreach ($f in $forbiddenCatalogs) {
    if ($catalog -ieq $f) { Die "'$catalog' is not a certification database. This tool never writes to development, reference or production data." }
}
if ($catalog -ine $certificationDatabase) { Die "'$catalog' is not '$certificationDatabase'." }
Write-Host ("  target: {0} on {1}" -f $catalog, $server) -ForegroundColor DarkGray

function Invoke-Sql([string]$sql) {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("cert-fixture-{0}.sql" -f ([guid]::NewGuid().ToString('N')))
    Set-Content -Path $tmp -Value $sql -Encoding utf8
    try {
        $out = & sqlcmd -S $server -d $catalog -i $tmp -W -h -1 2>&1
        if ($LASTEXITCODE -ne 0) { throw ($out -join ' / ') }
        $bad = @($out | Where-Object { $_ -match '^Msg \d+, Level' })
        if ($bad.Count -gt 0) { throw ($bad -join ' / ') }
        return $out
    } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
}

# ---- BEFORE ---------------------------------------------------------------------------------
$selectState = @"
SET NOCOUNT ON;
SELECT 'ROW|' + CAST(Id AS varchar(10)) + '|' + Title + '|' + CAST(IsActive AS varchar(1))
     + '|' + ISNULL(CONVERT(varchar(30), StartsAt, 126), 'NULL')
     + '|' + ISNULL(CONVERT(varchar(30), ExpiresAt, 126), 'NULL')
FROM Announcements
WHERE CompanyID = $certificationCompany AND Title IN ('$criticalBannerTitle', '$highBannerTitle')
ORDER BY Id;
"@

Write-Host ''
Write-Host '  BEFORE:' -ForegroundColor Yellow
$before = @((Invoke-Sql $selectState) | Where-Object { $_ -like 'ROW|*' })
if ($before.Count -eq 0) { Die "neither certification fixture exists in '$catalog'. This tool updates fixtures; it does not invent them." }
$before | ForEach-Object { Write-Host ("    {0}" -f $_) -ForegroundColor DarkGray }

$needsChange = @($before | Where-Object { $_ -notlike "*|1|$windowStart*" -or $_ -notlike "*|$windowEnd*" })

Write-Host ''
Write-Host '  INTENDED MUTATION:' -ForegroundColor Yellow
Write-Host ("    UPDATE Announcements SET IsActive = 1, StartsAt = '{0}', ExpiresAt = '{1}'" -f $windowStart, $windowEnd)
Write-Host ("    WHERE CompanyID = {0} AND Title IN ('{1}', '{2}')" -f $certificationCompany, $criticalBannerTitle, $highBannerTitle)
Write-Host ("    rows matched: {0}   (no other table, column or row is touched)" -f $before.Count)

if (-not $Apply) {
    Write-Host ''
    Write-Host 'REPORT ONLY - nothing was written. Re-run with -Apply to perform the update.' -ForegroundColor Cyan
    exit 0
}

# ---- APPLY ----------------------------------------------------------------------------------
$update = @"
SET NOCOUNT ON;
UPDATE Announcements
   SET IsActive = 1, StartsAt = '$windowStart', ExpiresAt = '$windowEnd'
 WHERE CompanyID = $certificationCompany
   AND Title IN ('$criticalBannerTitle', '$highBannerTitle');
SELECT 'AFFECTED|' + CAST(@@ROWCOUNT AS varchar(10));
"@
$res = Invoke-Sql $update
$affected = (@($res | Where-Object { $_ -like 'AFFECTED|*' }) | Select-Object -First 1) -replace '^AFFECTED\|', ''
Write-Host ''
Write-Host ("  APPLIED - rows affected: {0}" -f $affected) -ForegroundColor Green

Write-Host '  AFTER:' -ForegroundColor Yellow
$after = @((Invoke-Sql $selectState) | Where-Object { $_ -like 'ROW|*' })
$after | ForEach-Object { Write-Host ("    {0}" -f $_) -ForegroundColor DarkGray }

# Idempotency is asserted, not assumed: a second pass must change nothing.
$second = Invoke-Sql $update
$again = (@($second | Where-Object { $_ -like 'AFFECTED|*' }) | Select-Object -First 1) -replace '^AFFECTED\|', ''
$afterAgain = @((Invoke-Sql $selectState) | Where-Object { $_ -like 'ROW|*' })
if (Compare-Object $after $afterAgain) {
    Write-Host '  IDEMPOTENCY FAILED: a second pass changed the state.' -ForegroundColor Red
    exit 3
}
Write-Host ("  idempotent: a second pass matched {0} row(s) and left the state identical" -f $again) -ForegroundColor DarkGray

Write-Host ''
Write-Host 'FIXTURE RESET COMPLETE.' -ForegroundColor Green
exit 0
