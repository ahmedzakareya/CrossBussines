<#
    CrossBusiness — UI CERTIFICATION PRE-FLIGHT.

    VERIFICATION ONLY. This tool never writes, never repairs and never reseeds. Its single job is
    to answer "may a certification capture start right now?" and to ABORT loudly when the answer
    is no.

    WHY IT EXISTS, measured rather than assumed. A governed matrix was captured on 2026-08-15
    against CrossBuyCert while two announcement fixtures were visible, then verified later against
    CrossBuyDev where those same rows were switched off. Every route came back ~167px shorter and
    all 138 screenshots failed while the application was byte-identical. Nothing in the harness
    noticed, because nothing in the harness had ever been asked to check WHICH DATABASE it was
    looking at. That check is this file.

    A tool that silently fixes an unexpected database is worse than no tool: it converts "these two
    runs disagree" into "these two runs agree for a reason nobody recorded". So every failure here
    is a refusal, and reset is a separate, explicit, destructive operation.

        powershell -File governance/tools/certification-preflight.ps1 `
            -ConnectionString "Server=localhost\SQLEXPRESS;Database=CrossBuyCert;Trusted_Connection=True;TrustServerCertificate=True"

    Exit code 0 = cleared for capture. Any other value = ABORT.

    The catalogue rules mirror BL/Platform/CertificationDataContract.cs. That duplication is
    deliberate and is pinned by CertificationDataContractTests: the guard has to hold in the shell
    tool as well as in the application, and a shared file neither of them can load at the moment
    they need it would be a worse answer than two implementations that tests keep honest.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConnectionString,
    [string]$Environment = $env:ASPNETCORE_ENVIRONMENT,
    [string]$ConformanceFlag = $env:UI_CONFORMANCE
)

$ErrorActionPreference = 'Stop'

# Mirrors CertificationDataContract.
$certificationDatabase = 'CrossBuyCert'
$forbiddenCatalogs     = @('CrossBuyDev', 'CrossBuyDB', 'CrossBuyDB2', 'CrossBuy', 'alprimedb_prod')
$criticalBannerTitle   = 'ZZ-UI-CONFORMANCE Critical banner'
$highBannerTitle       = 'ZZ-UI-CONFORMANCE High priority banner'
$certificationCompany  = 1

$failures = New-Object System.Collections.Generic.List[string]
function Fail([string]$m) { $script:failures.Add($m); Write-Host ("  FAIL  {0}" -f $m) -ForegroundColor Red }
function Pass([string]$m) { Write-Host ("  ok    {0}" -f $m) -ForegroundColor DarkGray }

Write-Host ''
Write-Host '=== UI CERTIFICATION PRE-FLIGHT ===' -ForegroundColor Cyan

# ---- 1. host must have declared itself a certification host --------------------------------
if ($Environment -ne 'Development') { Fail "environment is '$Environment'; certification runs only on Development." }
else { Pass "environment = Development" }

if ($ConformanceFlag -cne '1') {
    Fail "UI_CONFORMANCE is '$ConformanceFlag'; it must be exactly '1'. Without it the certification clock middleware is not even in the pipeline, so relative-time labels move between captures."
} else { Pass "UI_CONFORMANCE = 1 (certification clock middleware active)" }

# ---- 2. target catalogue --------------------------------------------------------------------
$builder = $null
try { $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString }
catch { Fail "connection string is not parseable: $($_.Exception.Message)" }

$catalog = if ($builder) { $builder['Initial Catalog'] } else { $null }

if ([string]::IsNullOrWhiteSpace($catalog)) {
    Fail 'the connection string names no catalogue; pre-flight refuses to guess one.'
} else {
    $forbidden = $forbiddenCatalogs | Where-Object { $_ -ieq $catalog }
    if ($forbidden) {
        Fail "'$catalog' is a forbidden target. Certification never reads development, reference or production data — this is exactly the mistake that produced 138 false findings."
    } elseif ($catalog -ine $certificationDatabase) {
        Fail "'$catalog' is not '$certificationDatabase'."
    } else {
        Pass "target catalogue = $certificationDatabase"
    }
}

# ---- 3. dataset checks (read-only) -----------------------------------------------------------
# Only run if the target already passed, so a bad target is never queried at all.
if ($failures.Count -eq 0) {

# The closing delimiter of a here-string must sit at column 0, so this block is deliberately
# not indented with the `if` above it.
$query = @"
SET NOCOUNT ON;
SELECT 'BANNER|' + Title + '|' + CAST(IsActive AS varchar(1)) + '|'
     + CONVERT(varchar(30), StartsAt, 126) + '|' + ISNULL(CONVERT(varchar(30), ExpiresAt, 126), 'NULL')
FROM Announcements
WHERE Title IN ('$criticalBannerTitle', '$highBannerTitle') AND CompanyID = $certificationCompany;
SELECT 'VISIBLE|' + CAST(COUNT(*) AS varchar(10))
FROM Announcements
WHERE Title IN ('$criticalBannerTitle', '$highBannerTitle') AND CompanyID = $certificationCompany
  AND IsActive = 1
  AND (StartsAt  IS NULL OR StartsAt  <= SYSUTCDATETIME())
  AND (ExpiresAt IS NULL OR ExpiresAt >= SYSUTCDATETIME());
SELECT 'COMPANY|' + CAST(COUNT(*) AS varchar(10)) FROM Companies WHERE CompanyID = $certificationCompany;
SELECT 'EMPLOYEES|' + CAST(COUNT(*) AS varchar(10)) FROM Employee WHERE EmpCompanyID = $certificationCompany AND IsActive = 1;
"@

    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("cert-preflight-{0}.sql" -f ([guid]::NewGuid().ToString('N')))
    Set-Content -Path $tmp -Value $query -Encoding utf8
    try {
        $server = $builder['Data Source']
        $raw = & sqlcmd -S $server -d $catalog -i $tmp -W -h -1 2>&1
        # A batch that fails to COMPILE (a wrong column name, say) emits only "Msg NNN" and produces
        # no rows at all. Without this the missing rows would be reported as "fixtures not found",
        # which is a different and much more alarming claim than "the query was wrong".
        $sqlErrors = @($raw | Where-Object { $_ -match '^Msg \d+, Level' })
        if ($LASTEXITCODE -ne 0 -or $sqlErrors.Count -gt 0) {
            Fail ("could not query '{0}': {1}" -f $catalog, (($raw | Select-Object -First 4) -join ' / '))
        }
        else {
            # Every scalar is read defensively: an absent marker line means the check did not run,
            # and a check that did not run is a FAILURE, never a pass.
            function Scalar([string]$prefix) {
                $line = @($raw | Where-Object { $_ -like "$prefix|*" }) | Select-Object -First 1
                if ($null -eq $line) { return -1 }
                $v = ($line -replace ("^" + [regex]::Escape($prefix) + "\|"), '').Trim()
                $n = 0
                if ([int]::TryParse($v, [ref]$n)) { return $n }
                return -1
            }
            $banners = @($raw | Where-Object { $_ -like 'BANNER|*' })
            if ($banners.Count -ne 2) { Fail "expected 2 certification banner fixtures, found $($banners.Count)." }
            else { Pass "both certification banner fixtures present" ; $banners | ForEach-Object { Write-Host ("        {0}" -f $_) -ForegroundColor DarkGray } }

            $visible = Scalar 'VISIBLE'
            if ($visible -ne 2) {
                Fail "only $visible of 2 certification banners are visible at the CURRENT UTC instant. Announcement visibility is a DATA query (AnnouncementService filters on DateTime.UtcNow) and the conformance clock deliberately does not reach it, so a fixture whose window has expired silently changes every page height. Reset before capturing."
            } else { Pass "both banners visible at the current UTC instant" }

            $company = Scalar 'COMPANY'
            if ($company -ne 1) { Fail "certification company $certificationCompany not found." } else { Pass "certification company $certificationCompany present" }

            $emps = Scalar 'EMPLOYEES'
            if ($emps -lt 1) { Fail "no active employees in company $certificationCompany; the governed routes cannot render personas." } else { Pass "$emps active employee persona(s) present" }
        }
    } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
} else {
    Write-Host '  (dataset checks skipped — an earlier check already failed, so nothing was queried)' -ForegroundColor DarkYellow
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host ("PRE-FLIGHT ABORT — {0} failure(s). Capture must NOT start." -f $failures.Count) -ForegroundColor Red
    Write-Host 'Nothing was modified. Reset is a separate, explicit operation.' -ForegroundColor Red
    exit 2
}
Write-Host 'PRE-FLIGHT CLEARED — the dataset matches the certification contract.' -ForegroundColor Green
exit 0
