<#
=============================================================================================
PLATFORM HEALTH - one answer per platform, from the tree and the database.

WHY THIS EXISTS: for weeks the honest answer to "is Reporting working?" required reading source,
grepping Program.cs, querying sys.tables and opening a page. Three of the four were skipped most
times, which is how a platform came to be "complete" with 221 passing tests, 12 tables that existed
nowhere, and no user able to reach it.

It reports FOUR facts per platform, because any one of them alone is misleading:

    CODE      the services exist in the tree
    WIRED     they are registered in Program.cs - code that is not registered never runs
    SCHEMA    its tables exist in the target database
    REACHABLE it has a screen a user can open

A platform is HEALTHY only when all four hold. "Tests pass" is not on the list on purpose: tests
prove the code does what it says, not that anybody can use it.

READ-ONLY. It executes no DDL, applies no slice and writes nothing. The database is queried through
sys.tables only, so it is safe to point at any environment - including production, which is why the
catalogue is NOT refused here as it is in the deployment pipeline.

EXIT CODES
    0  every platform healthy
    1  at least one platform degraded (something is missing)
    3  the command could not run

USAGE
    powershell -File governance/tools/platform-health.ps1
    powershell -File governance/tools/platform-health.ps1 -ConnectionString "Server=.;Database=X;Trusted_Connection=True;TrustServerCertificate=True"
=============================================================================================
#>
[CmdletBinding()]
param(
    [string]$ConnectionString
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$app  = Join-Path $repo 'CrossBuy'

# ---------------------------------------------------------------------------------------------
# One row per platform. Each probe is a FACT that can be checked, never an opinion.
#   CodePath     : a file whose presence means the implementation exists
#   WiredToken   : the exact registration Program.cs must contain
#   Tables       : tables that must exist for the platform to function
#   Screen       : a view whose presence means a user can open it
# ---------------------------------------------------------------------------------------------
$platforms = @(
    [pscustomobject]@{
        Name = 'Reporting'
        CodePath = 'BL\Reporting\ReportService.cs'
        WiredToken = 'AddCrossBusinessReporting'
        Tables = @('ReportTemplates', 'ReportRuns', 'ReportFavorites')
        Screen = 'Views\Reports\Index.cshtml'
        Owner = 'TAB-2'
    },
    [pscustomobject]@{
        Name = 'Workspace'
        CodePath = 'BL\Workspace\WorkspaceService.cs'
        WiredToken = 'AddCrossBusinessWorkspace'
        Tables = @()                       # owns no tables by design - it orchestrates contracts
        Screen = 'Views\Workspace\Index.cshtml'
        Owner = 'TAB-1'
    },
    [pscustomobject]@{
        Name = 'Business Events'
        CodePath = 'BL\Platform\BusinessEventService.cs'
        WiredToken = 'BusinessEventDispatchWorker'
        Tables = @('BusinessEvents', 'BusinessEventDispatch')
        Screen = 'Views\BusinessEventMonitor\Index.cshtml'
        Owner = 'TAB-0'
    },
    [pscustomobject]@{
        Name = 'Tasks'
        CodePath = 'BL\TaskService.cs'
        WiredToken = 'TaskGeneratorHostedService'
        # TaskItems, verified against sys.tables - NOT 'Tasks'. The first version guessed the name from
        # the module and reported a live platform as DEGRADED. A probe that names the wrong table is
        # worse than no probe: it manufactures a fault and trains the reader to ignore the report.
        Tables = @('TaskItems')
        Screen = 'Views\Tasks\Index.cshtml'
        Owner = 'TAB-5 (UNASSIGNED)'
    },
    [pscustomobject]@{
        Name = 'Calendar'
        CodePath = 'BL\CalendarService.cs'
        WiredToken = 'ICalendarService'
        Tables = @('CalendarEvents')
        Screen = 'Views\Calendar\Index.cshtml'
        Owner = 'TAB-5 (UNASSIGNED)'
    }
)

Write-Host ''
Write-Host 'PLATFORM HEALTH' -ForegroundColor Cyan
Write-Host '==============='

# ---- Program.cs, read once -------------------------------------------------------------------
$programPath = Join-Path $app 'Program.cs'
if (-not (Test-Path $programPath)) { Write-Host "Program.cs not found: $programPath" -ForegroundColor Red; exit 3 }
$program = Get-Content $programPath -Raw

# ---- schema, queried once --------------------------------------------------------------------
$dbTables = $null
$catalog = '(not queried)'
if ($ConnectionString) {
    try {
        $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString
        $catalog = $builder['Initial Catalog']
        $conn = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
        $conn.Open()
        try {
            $cmd = $conn.CreateCommand()
            $cmd.CommandText = 'SELECT name FROM sys.tables'   # READ ONLY
            $reader = $cmd.ExecuteReader()
            $dbTables = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
            while ($reader.Read()) { [void]$dbTables.Add($reader.GetString(0)) }
            $reader.Close()
        } finally { $conn.Close() }
    } catch {
        Write-Host ("  schema probe unavailable: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
        $dbTables = $null
    }
}

Write-Host ("  database : {0}" -f $catalog)
Write-Host ''
Write-Host ('  {0,-17} {1,-6} {2,-6} {3,-9} {4,-10} {5,-12} {6}' -f 'PLATFORM','CODE','WIRED','SCHEMA','REACHABLE','VERDICT','OWNER')
Write-Host ('  ' + ('-' * 92))

$degraded = 0
$rows = @()

foreach ($p in $platforms) {
    $code      = Test-Path (Join-Path $app $p.CodePath)
    $wired     = $program.Contains($p.WiredToken)
    $reachable = Test-Path (Join-Path $app $p.Screen)

    if ($p.Tables.Count -eq 0)      { $schema = 'n/a' }
    elseif ($null -eq $dbTables)    { $schema = 'unknown' }
    else {
        $missing = @($p.Tables | Where-Object { -not $dbTables.Contains($_) })
        $schema  = if ($missing.Count -eq 0) { 'yes' } else { "MISSING" }
    }

    # A platform is healthy only when every applicable fact holds. 'unknown' is never healthy - not
    # knowing is a different thing from being fine, and collapsing the two is how a missing schema
    # went unnoticed for weeks.
    $healthy = $code -and $wired -and $reachable -and ($schema -in @('yes', 'n/a'))
    $verdict = if ($healthy) { 'HEALTHY' } elseif ($schema -eq 'unknown') { 'UNKNOWN' } else { 'DEGRADED' }
    if (-not $healthy) { $degraded++ }

    $colour = if ($healthy) { 'Green' } elseif ($verdict -eq 'UNKNOWN') { 'Yellow' } else { 'Red' }
    Write-Host ('  {0,-17} {1,-6} {2,-6} {3,-9} {4,-10} {5,-12} {6}' -f `
        $p.Name, $(if($code){'yes'}else{'NO'}), $(if($wired){'yes'}else{'NO'}), `
        $schema, $(if($reachable){'yes'}else{'NO'}), $verdict, $p.Owner) -ForegroundColor $colour

    $rows += [pscustomobject]@{ Platform=$p.Name; Code=$code; Wired=$wired; Schema=$schema; Reachable=$reachable; Verdict=$verdict; Owner=$p.Owner }
}

# ---- detail for anything not healthy ----------------------------------------------------------
$bad = @($rows | Where-Object { $_.Verdict -ne 'HEALTHY' })
if ($bad.Count) {
    Write-Host ''
    Write-Host '  WHY' -ForegroundColor Yellow
    foreach ($r in $bad) {
        $p = $platforms | Where-Object { $_.Name -eq $r.Platform }
        if (-not $r.Code)      { Write-Host ("    {0}: implementation not found at {1}" -f $r.Platform, $p.CodePath) }
        if (-not $r.Wired)     { Write-Host ("    {0}: '{1}' does not appear in Program.cs - the code is never reached" -f $r.Platform, $p.WiredToken) }
        if ($r.Schema -eq 'MISSING') {
            $missing = @($p.Tables | Where-Object { -not $dbTables.Contains($_) })
            Write-Host ("    {0}: tables absent from '{1}': {2}" -f $r.Platform, $catalog, ($missing -join ', '))
        }
        if ($r.Schema -eq 'unknown') { Write-Host ("    {0}: schema not probed - pass -ConnectionString to establish it" -f $r.Platform) }
        if (-not $r.Reachable) { Write-Host ("    {0}: no screen at {1}" -f $r.Platform, $p.Screen) }
    }
}

Write-Host ''
Write-Host ('  healthy {0} / {1}' -f ($rows.Count - $degraded), $rows.Count)
Write-Host ''

if ($degraded -gt 0) { exit 1 }
exit 0
