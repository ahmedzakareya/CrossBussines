# شغّله على السيرفر (PowerShell as Administrator) لمعرفة المجلد الفعلي + الـ App Pool لكل موقع/تطبيق
Import-Module WebAdministration -ErrorAction SilentlyContinue
Write-Host "=== المواقع (Sites) ===" -ForegroundColor Cyan
Get-Website | Select-Object Name, State, PhysicalPath, @{n='AppPool';e={$_.applicationPool}} | Format-Table -AutoSize
Write-Host "=== التطبيقات الفرعية (Applications) ===" -ForegroundColor Cyan
Get-WebApplication | Select-Object @{n='Site';e={$_.GetParentElement().Attributes['name'].Value}}, Path, PhysicalPath, applicationPool | Format-Table -AutoSize
Write-Host "=== تأكيد البناء الحالي على السيرفر (تاريخ CrossBuy.dll) ===" -ForegroundColor Cyan
Get-Website | ForEach-Object {
  $p = Join-Path $_.PhysicalPath 'CrossBuy.dll'
  if (Test-Path $p) { "{0,-20} {1}" -f $_.Name, (Get-Item $p).LastWriteTime }
}
