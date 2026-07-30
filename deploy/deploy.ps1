# ============================================================================
# CrossBuy — سكربت رفع آمن لـ IIS (يحلّ مشكلة "التحديثات لا تظهر")
# يضع app_offline → يوقف الـ App Pool → ينسخ الملفات الجديدة (مع الحفاظ على
# web.config و appsettings الخاصة بالسيرفر) → يزيل app_offline → يعيد التشغيل.
#
# الاستخدام (PowerShell as Administrator على السيرفر):
#   .\deploy.ps1 -Source "C:\path\to\cb_publish" -SitePath "C:\inetpub\CrossBuy" -AppPool "CrossBuyPool"
#
# جِد القيم الصحيحة بتشغيل find_site.ps1 أولًا.
# ============================================================================
param(
  [Parameter(Mandatory=$true)][string]$Source,    # مجلد النشر الجديد (cb_publish بعد فكّ الضغط)
  [Parameter(Mandatory=$true)][string]$SitePath,  # المجلد الفعلي للموقع على IIS
  [Parameter(Mandatory=$true)][string]$AppPool    # اسم الـ Application Pool
)
$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

if (-not (Test-Path (Join-Path $Source 'CrossBuy.dll'))) { throw "Source غير صحيح: لا يوجد CrossBuy.dll في $Source" }
if (-not (Test-Path $SitePath)) { throw "SitePath غير موجود: $SitePath" }

Write-Host "1) وضع app_offline.htm لتحرير قفل الملفات..." -ForegroundColor Cyan
Set-Content -Path (Join-Path $SitePath 'app_offline.htm') -Value '<h1>التحديث جارٍ… Updating</h1>' -Encoding UTF8

Write-Host "2) إيقاف الـ App Pool: $AppPool" -ForegroundColor Cyan
if ((Get-WebAppPoolState -Name $AppPool).Value -ne 'Stopped') { Stop-WebAppPool -Name $AppPool; Start-Sleep -Seconds 3 }

Write-Host "3) نسخ الملفات الجديدة (مع استثناء إعدادات السيرفر)..." -ForegroundColor Cyan
# robocopy: ينسخ كل شيء ولا يحذف، ويستثني إعدادات السيرفر و app_offline من الاستبدال
$rc = "/E", "/NFL", "/NDL", "/NJH", "/NP", "/R:3", "/W:2",
      "/XF", "web.config", "appsettings.json", "appsettings.Production.json", "appsettings.Development.json", "app_offline.htm"
robocopy $Source $SitePath @rc | Out-Null
if ($LASTEXITCODE -ge 8) { throw "فشل النسخ (robocopy code $LASTEXITCODE)" }
Write-Host "   تم النسخ (robocopy code $LASTEXITCODE)" -ForegroundColor DarkGray

Write-Host "4) إزالة app_offline.htm" -ForegroundColor Cyan
Remove-Item (Join-Path $SitePath 'app_offline.htm') -Force -ErrorAction SilentlyContinue

Write-Host "5) تشغيل الـ App Pool وإعادة تدويره" -ForegroundColor Cyan
Start-WebAppPool -Name $AppPool
Restart-WebAppPool -Name $AppPool

$dll = Join-Path $SitePath 'CrossBuy.dll'
Write-Host "`n=== تم ===" -ForegroundColor Green
Write-Host ("CrossBuy.dll على السيرفر الآن: " + (Get-Item $dll).LastWriteTime) -ForegroundColor Green
Write-Host "تأكّد أن التاريخ أعلاه = تاريخ البناء الجديد، ثم افتح الموقع بـ Ctrl+F5." -ForegroundColor Yellow
Write-Host "لا تنسَ تشغيل سكربت قاعدة البيانات: deploy\sql\manuf_schema_4x.sql" -ForegroundColor Yellow
