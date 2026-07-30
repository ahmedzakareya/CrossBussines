# Generates a clean from-scratch build of CrossBuyDB2 by scripting the VERIFIED dev schema via SMO.
# Outputs (deploy/sql/fresh): 01_schema.sql (tables+indexes), 02_foreign_keys.sql, 03_seed_config.sql
# Schema comes from the real DB => correct columns/types/[LineNo]/filtered-index handling automatically.
$ErrorActionPreference = 'Stop'
[void][System.Reflection.Assembly]::LoadWithPartialName('Microsoft.SqlServer.Smo')
[void][System.Reflection.Assembly]::LoadWithPartialName('Microsoft.SqlServer.ConnectionInfo')

$srv = New-Object Microsoft.SqlServer.Management.Smo.Server('.')
$srv.ConnectionContext.LoginSecure = $true
$db  = $srv.Databases['CrossBuyDB2']
if (-not $db) { throw 'CrossBuyDB2 not found' }

$outDir = 'C:\Users\Lenovo\Desktop\CrossBuy\CrossBuy\deploy\sql\fresh'
New-Item -ItemType Directory -Force $outDir | Out-Null

# tables to skip entirely (EF noise + concurrency test artifact)
$skip = @('__EFMigrationsHistory','_ConcTest')

# ---- schema options (tables + indexes + PK/defaults/checks; NO inline FKs) ----
$o = New-Object Microsoft.SqlServer.Management.Smo.ScriptingOptions
$o.ScriptSchema=$true; $o.ScriptData=$false
$o.Indexes=$true; $o.ClusteredIndexes=$true; $o.NonClusteredIndexes=$true
$o.DriPrimaryKey=$true; $o.DriUniqueKeys=$true; $o.DriDefaults=$true; $o.DriChecks=$true
$o.DriForeignKeys=$false; $o.DriAllConstraints=$false
$o.IncludeIfNotExists=$true; $o.NoCollation=$true; $o.AnsiPadding=$true
$o.IncludeHeaders=$false; $o.Default=$true; $o.AllowSystemObjects=$false
$o.ExtendedProperties=$false

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('SET QUOTED_IDENTIFIER ON;'); [void]$sb.AppendLine('SET ANSI_NULLS ON;'); [void]$sb.AppendLine('GO')
$tableCount=0
foreach ($t in ($db.Tables | Sort-Object Name)) {
  if ($t.IsSystemObject -or $skip -contains $t.Name) { continue }
  foreach ($line in $t.Script($o)) { [void]$sb.AppendLine($line); [void]$sb.AppendLine('GO') }
  $tableCount++
}
[System.IO.File]::WriteAllText("$outDir\01_schema.sql", $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

# ---- foreign keys (separate => table create order irrelevant) ----
$ofk = New-Object Microsoft.SqlServer.Management.Smo.ScriptingOptions
$ofk.IncludeIfNotExists=$true; $ofk.IncludeHeaders=$false; $ofk.NoCollation=$true
$fb = New-Object System.Text.StringBuilder
[void]$fb.AppendLine('SET QUOTED_IDENTIFIER ON;'); [void]$fb.AppendLine('SET ANSI_NULLS ON;'); [void]$fb.AppendLine('GO')
$fkCount=0
foreach ($t in ($db.Tables | Sort-Object Name)) {
  if ($t.IsSystemObject -or $skip -contains $t.Name) { continue }
  foreach ($fk in $t.ForeignKeys) { foreach ($line in $fk.Script($ofk)) { [void]$fb.AppendLine($line); [void]$fb.AppendLine('GO') }; $fkCount++ }
}
[System.IO.File]::WriteAllText("$outDir\02_foreign_keys.sql", $fb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

Write-Host "schema: $tableCount tables -> 01_schema.sql"
Write-Host "fkeys : $fkCount FKs -> 02_foreign_keys.sql"
