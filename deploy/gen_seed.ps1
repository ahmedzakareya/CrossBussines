# Generates 03_seed_config.sql — minimal config/reference data for a clean production DB.
# Pure-config tables are seeded in full; org/auth tables are filtered to the primary company + Admin only.
# No transactional or demo data (no customers/items/vendors/invoices/test users/test employees).
$ErrorActionPreference = 'Stop'
$cs = 'Server=.;Database=CrossBuyDB2;Trusted_Connection=True;TrustServerCertificate=True;'
$out = 'C:\Users\Lenovo\Desktop\CrossBuy\CrossBuy\deploy\sql\fresh\03_seed_config.sql'

# (table, optional WHERE). Order = FK-safe (parents before children).
$plan = @(
  @{t='CountriesLookup'},
  @{t='CompanyTypes'},
  @{t='Currencies'},
  @{t='ExchangeRates'},
  @{t='AccountTypes'},
  @{t='Accounts'},
  @{t='CostCenters'},
  @{t='PostingRules'},
  @{t='FiscalYears'},
  @{t='FiscalPeriods'},
  @{t='NumberSequences'},
  @{t='UnitsOfMeasure'},
  @{t='UoMConversions'},
  @{t='TaxCodes'},
  @{t='PayrollTaxBrackets'},
  @{t='LeaveTypes'},
  @{t='JobTitles'},
  @{t='AssetCategories'},
  @{t='ItemCategories'},
  @{t='HierarchicalTypes'},
  @{t='SystemForms'},
  @{t='Policies'},
  @{t='CrmPipelines'},
  @{t='CrmPipelineStages'},
  @{t='AccountingSettings'},
  @{t='InventorySettings'},
  @{t='PayrollSettings'},
  @{t='EtaSettings'},
  @{t='Companies'; w='CompanyID = 1'},
  @{t='Branches';  w='CompanyID = 1'},
  @{t='AspNetUsers'; w="UserName = 'Admin'"},
  # the Admin's own Employee row (login REQUIRES a linked employee); drop the test org-tree link.
  @{t='Employee'; w='ID = 5'; o=@{DepartmentID='NULL'}}
)

$cn = New-Object System.Data.SqlClient.SqlConnection $cs
$cn.Open()

function Get-Cols($table) {
  # returns ordered insertable columns (excludes computed), plus identity flag
  $cmd = $cn.CreateCommand()
  $cmd.CommandText = @"
SELECT c.name, c.is_computed, c.is_identity
FROM sys.columns c WHERE c.object_id = OBJECT_ID(@t) ORDER BY c.column_id
"@
  [void]$cmd.Parameters.AddWithValue('@t',$table)
  $r=$cmd.ExecuteReader(); $cols=@(); $hasId=$false
  while($r.Read()){ if(-not $r['is_computed']){ $cols+=$r['name'] }; if($r['is_identity']){ $hasId=$true } }
  $r.Close()
  return ,@($cols,$hasId)
}
function Fmt($v) {
  if ($v -is [System.DBNull]) { return 'NULL' }
  if ($v -is [bool])     { if($v){return '1'}else{return '0'} }
  if ($v -is [byte[]])   { return '0x' + (($v | ForEach-Object { $_.ToString('x2') }) -join '') }
  if ($v -is [System.DateTimeOffset]) { return "N'" + $v.ToString('yyyy-MM-dd HH:mm:ss.fffffff zzz') + "'" }
  if ($v -is [System.TimeSpan])       { return "N'" + $v.ToString('hh\:mm\:ss\.fffffff') + "'" }
  if ($v -is [datetime]) { return "N'" + $v.ToString('yyyy-MM-dd HH:mm:ss.fff') + "'" }   # 3 frac digits = valid for datetime AND datetime2
  if ($v -is [guid])     { return "N'$v'" }
  if ($v -is [decimal] -or $v -is [double] -or $v -is [single]) { return [string]::Format([System.Globalization.CultureInfo]::InvariantCulture,'{0}',$v) }
  if ($v -is [int32] -or $v -is [int64] -or $v -is [int16] -or $v -is [byte]) { return "$v" }
  return "N'" + ($v.ToString() -replace "'","''") + "'"
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('SET QUOTED_IDENTIFIER ON;'); [void]$sb.AppendLine('SET ANSI_NULLS ON;'); [void]$sb.AppendLine('SET NOCOUNT ON;'); [void]$sb.AppendLine('GO')
foreach ($p in $plan) {
  $t=$p.t; $where = if($p.w){" WHERE $($p.w)"}else{""}
  $info = Get-Cols $t; $cols=$info[0]; $hasId=$info[1]
  $collist = ($cols | ForEach-Object { "[$_]" }) -join ', '
  $cmd=$cn.CreateCommand(); $cmd.CommandText = "SELECT $collist FROM [$t]$where"
  $r=$cmd.ExecuteReader(); $rows=@()
  while($r.Read()){ $vals=@(); foreach($c in $cols){ if($p.o -and $p.o.ContainsKey($c)){ $vals+= $p.o[$c] } else { $vals+= (Fmt $r[$c]) } }; $rows += ('(' + ($vals -join ', ') + ')') }
  $r.Close()
  [void]$sb.AppendLine("-- $t ($($rows.Count) rows)")
  if ($rows.Count -eq 0) { [void]$sb.AppendLine("-- (no config rows; left empty intentionally)"); [void]$sb.AppendLine('GO'); continue }
  [void]$sb.AppendLine("IF NOT EXISTS (SELECT 1 FROM [$t]) BEGIN")
  if ($hasId) { [void]$sb.AppendLine("  SET IDENTITY_INSERT [$t] ON;") }
  # batch inserts in groups of 200 (SQL row-value-list limit is 1000)
  for ($i=0; $i -lt $rows.Count; $i+=200) {
    $chunk = $rows[$i..([Math]::Min($i+199,$rows.Count-1))]
    [void]$sb.AppendLine("  INSERT INTO [$t] ($collist) VALUES")
    [void]$sb.AppendLine('  ' + ($chunk -join ",`r`n  ") + ';')
  }
  if ($hasId) { [void]$sb.AppendLine("  SET IDENTITY_INSERT [$t] OFF;") }
  [void]$sb.AppendLine("END"); [void]$sb.AppendLine('GO')
}
$cn.Close()
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UnicodeEncoding($false,$true)))
Write-Host "seed -> $out"
