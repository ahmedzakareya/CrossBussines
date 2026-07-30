# دليل نشر CrossBuy — Windows Server 2025 + IIS + SQL Server

> نظام: ASP.NET Core 8 (MVC) + SQL Server (CrossBuyDB2) + SignalR + جلسات في SQL + موبايل Flutter (JWT).
> هذا الدليل **خطوة بخطوة بالأوامر الدقيقة**. أماكن القيم الحساسة مكتوبة بـ `__PLACEHOLDER__` — ضعها أنت على السيرفر.
> حِزمة النشر المجهّزة محليًا: `CrossBuy/publish/` + مجلد `deploy/` (راجع `deploy/README.md`).

---

## 0) ملخّص سريع (Checklist)
- [ ] **(ح)** أول دخول RDP على Windows Server 2025 — تفادي قفل الحساب (اقرأ القسم ح أولًا).
- [ ] **(أ)** .NET 8 Hosting Bundle + IIS + WebSocket Protocol + SQL Server + SSMS.
- [ ] **(ب)** إنشاء/استرجاع `CrossBuyDB2` + تفريغ بيانات الاختبار.
- [ ] **(ج)** نقل `publish/` + إنشاء Website/App Pool (No Managed Code, Production).
- [ ] **(د)** ضبط `appsettings.Production.json` / متغيّرات البيئة (سلسلة الاتصال + JWT).
- [ ] **(و)** صلاحية كتابة على `C:\ProgramData\CrossBuy\keys` لهوية الـ App Pool (حل re-login).
- [ ] **(هـ)** شهادة HTTPS عبر win-acme + إجبار HTTPS.
- [ ] **(ز)** نسخ احتياطي يومي تلقائي.
- [ ] **(ط)** ضبط رابط الإنتاج في تطبيق Flutter.
- [ ] تأكيد أن `/api/dev/*` يرجّع **404**.

---

## ح) ملاحظة Windows Server 2025 — قفل الحساب عند أول دخول RDP
أحيانًا يظهر **"Your account has been locked / User Account Has Been Locked"** عند أول جلسة RDP، بسبب سياسة الأمان الافتراضية (محاولات دخول فاشلة أو سياسة قفل صارمة).
**التفادي/الحل:**
1. ادخل من **VNC/Console** الخاص بلوحة Contabo (ليس RDP) كـ Administrator.
2. شغّل `secpol.msc` → *Account Policies > Account Lockout Policy*:
   - `Account lockout threshold` = **0** (تعطيل القفل) أو رقم أعلى مع مدة قصيرة.
3. أو من PowerShell (كـ Administrator):
   ```powershell
   net accounts /lockoutthreshold:0
   ```
4. إن كان الحساب مقفولًا الآن: `net user Administrator /active:yes` ثم أعد التشغيل، أو افتح القفل من *Computer Management > Local Users and Groups > Users > Administrator > Account is locked out (إلغاء التحديد)*.
5. بعد الاستقرار، أعد ضبط threshold لقيمة آمنة (مثل 10) لمنع هجمات brute-force، خصوصًا قبل فتح RDP للإنترنت.

> توصية: لا تفتح منفذ RDP (3389) للعالم بلا حماية — قيّده على IP عملك أو استخدم Firewall.

---

## أ) متطلّبات السيرفر

### 1. IIS + WebSocket (ضروري لـ SignalR)
من PowerShell (كـ Administrator):
```powershell
Install-WindowsFeature -Name Web-Server -IncludeManagementTools
# WebSocket Protocol — لازم لـ SignalR (التنبيهات الفورية)
Install-WindowsFeature -Name Web-WebSockets
# (اختياري) URL Rewrite لإعادة توجيه HTTP→HTTPS — يُثبَّت لاحقًا أو من Web Platform Installer
```
> تأكيد: *Server Manager > IIS* يعمل، و`http://localhost` يفتح صفحة IIS الافتراضية.

### 2. .NET 8 Hosting Bundle
حمّل **ASP.NET Core 8 Runtime — Hosting Bundle** من dotnet.microsoft.com (يثبّت runtime + ASP.NET Core Module V2 لـ IIS).
بعد التثبيت أعد تشغيل IIS:
```powershell
net stop was /y
net start w3svc
```
تأكيد التثبيت:
```powershell
dotnet --list-runtimes
# يجب أن ترى: Microsoft.AspNetCore.App 8.x.x  و  Microsoft.NETCore.App 8.x.x
```

### 3. SQL Server + SSMS
- ثبّت **SQL Server 2022 Express** (مجاني حتى 10GB) أو **Developer/Standard**.
  - أثناء التثبيت: Mixed Mode Authentication (SQL + Windows)، واضبط كلمة سر `sa` قوية.
- ثبّت **SQL Server Management Studio (SSMS)**.
- فعّل **TCP/IP** من *SQL Server Configuration Manager* إن احتجت اتصالًا عن بُعد (للسيرفر الواحد غير مطلوب).

---

## ب) قاعدة البيانات (الطريقة الموثوقة = Restore، لأن migrations معطّلة)

> **لا تستخدم `dotnet ef database update`** — الـ migrations مكسورة. مصدر السكيمة الموثّق هو نسخة `CrossBuyDB2`
> المسترجَعة من التطوير. التفاصيل والسبب في `deploy/README.md`.

### 1. على جهاز التطوير — أنشئ النسخة
```cmd
sqlcmd -S . -d master -E -i deploy\sql\00_backup_dev_db.sql
```
ينتج `C:\temp\CrossBuyDB2.bak`. انسخه إلى السيرفر (مثلًا `D:\backups\`).

### 2. على السيرفر — استرجِع القاعدة (SSMS أو sqlcmd)
```sql
RESTORE DATABASE CrossBuyDB2 FROM DISK = N'D:\backups\CrossBuyDB2.bak'
WITH MOVE 'CrossBuyDB2'     TO N'C:\SQLData\CrossBuyDB2.mdf',
     MOVE 'CrossBuyDB2_log' TO N'C:\SQLData\CrossBuyDB2_log.ldf',
     RECOVERY, REPLACE;
```
> أنشئ مجلد `C:\SQLData` مسبقًا. تحقق من الأسماء المنطقية إن لزم: `RESTORE FILELISTONLY FROM DISK=N'...bak';`
> الاسترجاع ينقل **كل الجداول** بما فيها جدول الجلسات `AppCache` — لازم لعمل الجلسات.

### بديل (مُختبَر): بناء من الصفر بدل الاسترجاع
لو تفضّل قاعدة **نظيفة تمامًا** بدل نسخة التطوير، استخدم حزمة `deploy\sql\fresh\` (مولَّدة من السكيمة
الفعلية ومُختبَرة — قاعدة جديدة منها تشغّل التطبيق وتسجّل دخول Admin). على السيرفر:
```cmd
cd deploy\sql\fresh
REM عدّل سطر SVR داخل الملف لو الـ instance مختلف (الافتراضي localhost\SQLEXPRESS)
run_all.cmd
```
تنشئ CrossBuyDB2 + كل الجداول/الفهارس/القيود + seed الإعداد الأساسي فقط (دليل حسابات، عملات، فترات،
posting rules، إعدادات، الشركة #1، مستخدم Admin) **بلا أي بيانات اختبار**. التفاصيل في `deploy\sql\fresh\README.md`.
عند هذا الخيار **تجاوز خطوتي الاسترجاع والتفريغ** أدناه.

### 3. (مسار الاسترجاع) على السيرفر — تفريغ بيانات الاختبار (قاعدة إنتاج نظيفة)
```cmd
sqlcmd -S . -d CrossBuyDB2 -E -i deploy\sql\10_purge_for_production.sql
```
> يمسح كل الحركات/القيود/المستندات + كيانات CRM/التسويق التجريبية، ويُبقي الأساسيات (دليل الحسابات، العملات،
> الفترات، المخازن، الأصناف، الإعدادات، المستخدمين، الصلاحيات، إعداد خطوط الأنابيب…). قسم حذف العملاء/الموردين/
> الأصناف **اختياري ومعطّل افتراضيًا** داخل السكربت — فعّله فقط لو كل تلك السجلات تجريبية.

### 4. مستخدم SQL للتطبيق (مُستحسَن بدل Trusted_Connection)
```sql
CREATE LOGIN crossbuy_app WITH PASSWORD = N'__STRONG_DB_PASSWORD__', CHECK_POLICY = ON;
USE CrossBuyDB2;
CREATE USER crossbuy_app FOR LOGIN crossbuy_app;
ALTER ROLE db_owner ADD MEMBER crossbuy_app;   -- يحتاج db_owner: التطبيق يكتب AppCache + يُنشئ بيانات
```

---

## ج) نقل النشر وإعداد IIS

### 1. انقل مجلد النشر
انسخ كامل محتوى `CrossBuy/publish/` إلى السيرفر، مثلًا `C:\inetpub\crossbuy`.
> الإنتاج يستخدم **Views مترجمة مسبقًا** (أسرع/أأمن) — لا حاجة لـ runtime compilation.

### 2. Application Pool
*IIS Manager > Application Pools > Add Application Pool*:
- Name: `CrossBuyPool`
- **.NET CLR version: No Managed Code**
- Managed pipeline: Integrated
- بعد الإنشاء: *Advanced Settings > Start Mode = AlwaysRunning* (اختياري، يقلّل بطء أول طلب).

### 3. Website
*IIS Manager > Sites > Add Website*:
- Site name: `CrossBuy`
- Physical path: `C:\inetpub\crossbuy`
- Application pool: `CrossBuyPool`
- Binding: `http` المنفذ 80 + الـ Host name (الدومين). HTTPS نضيفه في القسم (هـ).

### 4. متغيّر البيئة Production
البيئة تُضبط في `web.config` (القسم د) أو على مستوى النظام:
```powershell
setx ASPNETCORE_ENVIRONMENT Production /M   # /M = على مستوى الجهاز؛ يتطلب إعادة تشغيل IIS
```

---

## د) ضبط الإعدادات والأسرار على السيرفر

أمامك خياران (متغيّرات البيئة **تتجاوز** ملف appsettings):

### الخيار 1 (مستحسَن): متغيّرات بيئة داخل `web.config`
عدّل `C:\inetpub\crossbuy\web.config` ليصبح بلوك `aspNetCore`:
```xml
<aspNetCore processPath="dotnet" arguments=".\CrossBuy.dll" stdoutLogEnabled="false"
            stdoutLogFile=".\logs\stdout" hostingModel="inprocess">
  <environmentVariables>
    <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
    <environmentVariable name="ConnectionStrings__DefaultConnection"
       value="Server=.;Database=CrossBuyDB2;User Id=crossbuy_app;Password=__STRONG_DB_PASSWORD__;TrustServerCertificate=True;Connection Timeout=120;" />
    <environmentVariable name="Jwt__Key" value="__64+CHAR_RANDOM_SECRET__" />
    <environmentVariable name="Jwt__Issuer" value="CrossBuy" />
    <environmentVariable name="Jwt__Audience" value="CrossBuyMobile" />
    <environmentVariable name="AiService__Secret" value="__ROTATE_FROM_DEV__" />
  </environmentVariables>
</aspNetCore>
```

### الخيار 2: تحرير `appsettings.Production.json` على السيرفر
الملف مجهّز بـ placeholders — استبدل `__SET_ON_SERVER__` بالقيم الحقيقية.

### توليد مفتاح JWT عشوائي (نفّذه على السيرفر)
```powershell
[Convert]::ToBase64String((1..48 | % {Get-Random -Max 256}))
```
> **مهم:** غيّر `Jwt__Key` عن قيمة التطوير — الموبايل يعتمد عليه. أي تغيير لاحق يُبطل التوكنات الحالية (إعادة دخول للجميع).
> لا تضع منفذ AiService للعالم؛ خدمة الـ AI منفصلة (Python) وتُنشر لاحقًا.

---

## و) حل مشكلة إعادة الدخول (re-login) في الإنتاج — حرج

ثلاثة أعمدة، أولها يحتاج خطوة يدوية منك على السيرفر:

1. **تشغيل عبر IIS فقط** — لا تشغّل Kestrel CLI (`dotnet CrossBuy.dll`) أو IIS Express بالتوازي مع موقع IIS. مثيل واحد فقط يخدم الدومين.
2. **مفاتيح DataProtection على مسار ثابت دائم** — الكود يحفظها في `C:\ProgramData\CrossBuy\keys` باسم تطبيق ثابت `CrossBuy` وعمر 10 سنوات. لكن **هوية الـ App Pool لازم تقدر تكتب هناك**، وإلا يسقط لمسار المستخدم المؤقت → تُفقد المفاتيح عند كل recycle → إعادة دخول:
   ```powershell
   New-Item -ItemType Directory -Force "C:\ProgramData\CrossBuy\keys" | Out-Null
   icacls "C:\ProgramData\CrossBuy" /grant "IIS AppPool\CrossBuyPool:(OI)(CI)M" /T
   ```
   > لو غيّرت اسم الـ Pool عدّل السطر. تحقق بعد أول إقلاع أن مجلد `keys` فيه ملف `key-*.xml`.
3. **الجلسات في SQL** — مخزّنة في جدول `AppCache` (جاء مع الاسترجاع). تصمد عبر إعادة التشغيل/النشر. لو فُقد الجدول أنشئه:
   ```cmd
   dotnet sql-cache create "Server=.;Database=CrossBuyDB2;Trusted_Connection=True;TrustServerCertificate=True;" dbo AppCache
   ```
4. **الكوكي:** الإعداد الحالي `SecurePolicy=None` + `SameSite=Lax` + `IsEssential=true` — يعمل على HTTPS بلا مشاكل. (تقوية اختيارية بعد تثبيت HTTPS فقط: تغيير السياسة إلى `Always` يتطلب تعديل كود + إعادة بناء؛ غير مطلوب للتشغيل.)

---

## هـ) HTTPS عبر win-acme + إجبار HTTPS

1. وجِّه سجل **A** للدومين على IP السيرفر (من مزوّد الدومين)، وانتظر انتشار DNS.
2. تأكد أن منفذي **80 و443** مفتوحان في Windows Firewall:
   ```powershell
   New-NetFirewallRule -DisplayName "HTTP"  -Direction Inbound -Protocol TCP -LocalPort 80  -Action Allow
   New-NetFirewallRule -DisplayName "HTTPS" -Direction Inbound -Protocol TCP -LocalPort 443 -Action Allow
   ```
3. حمّل **win-acme** (`wacs.exe`) من win-acme.com، شغّله كـ Administrator:
   ```
   wacs.exe
   ```
   اختر: `N` (شهادة جديدة) → اختر موقع IIS `CrossBuy` → الدومين → موافقة Let's Encrypt.
   win-acme يصدر الشهادة، **يضيف Binding 443 تلقائيًا للموقع**، ويجدول التجديد كل 60 يومًا.
4. **إجبار HTTPS** — ثبّت **URL Rewrite Module** ثم أضف قاعدة في `web.config` (داخل `<system.webServer>`):
   ```xml
   <rewrite>
     <rules>
       <rule name="HTTP to HTTPS" stopProcessing="true">
         <match url="(.*)" />
         <conditions><add input="{HTTPS}" pattern="off" /></conditions>
         <action type="Redirect" url="https://{HTTP_HOST}/{R:1}" redirectType="Permanent" />
       </rule>
     </rules>
   </rewrite>
   ```
5. تحقق: `https://your-domain` يفتح بقفل صحيح، و`http://` يحوّل تلقائيًا لـ HTTPS.

---

## ز) نسخ احتياطي يومي تلقائي

### الخيار أ: SQL Server Agent (Standard/Developer — غير متاح في Express)
*SSMS > SQL Server Agent > Jobs > New Job* → Step (T-SQL):
```sql
DECLARE @f NVARCHAR(260) = N'D:\backups\CrossBuyDB2_' + FORMAT(GETDATE(),'yyyyMMdd_HHmm') + N'.bak';
BACKUP DATABASE CrossBuyDB2 TO DISK = @f WITH INIT, COMPRESSION, STATS = 10;
```
Schedule: يوميًا (مثلًا 2:00 ص).

### الخيار ب: Task Scheduler (يصلح لـ Express)
أنشئ `C:\scripts\backup_crossbuy.cmd`:
```cmd
@echo off
for /f "tokens=2 delims==" %%i in ('wmic os get localdatetime /value') do set dt=%%i
set STAMP=%dt:~0,8%_%dt:~8,4%
sqlcmd -S . -E -Q "BACKUP DATABASE CrossBuyDB2 TO DISK=N'D:\backups\CrossBuyDB2_%STAMP%.bak' WITH INIT, STATS=10"
```
ثم:
```powershell
$action  = New-ScheduledTaskAction -Execute "C:\scripts\backup_crossbuy.cmd"
$trigger = New-ScheduledTaskTrigger -Daily -At 2:00AM
Register-ScheduledTask -TaskName "CrossBuy DB Backup" -Action $action -Trigger $trigger -RunLevel Highest -User "SYSTEM"
```
> احفظ النسخ على **قرص/مسار منفصل** (D: أو تخزين خارجي)، واحذف ما يزيد عن N يومًا دوريًا.

---

## ط) تطبيق Flutter — رابط الإنتاج
الملف: `crossbuy_mobile/lib/app_config.dart` (مجهّز بمفتاح تبديل):
- ضع دومينك في `prodBaseUrl = 'https://YOUR-DOMAIN.com'` (بـ https، بلا `/` في الآخر).
- ابنِ نسخة إنتاج مع تفعيل العَلَم:
  ```bash
  flutter build apk --release --dart-define=PRODUCTION=true
  # أو للويب:
  flutter build web --release --dart-define=PRODUCTION=true
  ```
  (أو غيّر `useProduction` الافتراضي إلى true داخل الملف.)
- تأكد أن `Jwt__Issuer/Audience/Key` على السيرفر تطابق المتوقَّع، وأن HTTPS يعمل (SignalR + JWT عبر نفس الهوست).

---

## ي) تأكيدات ما بعد النشر
- [ ] `https://your-domain/Account/Login` يفتح (Admin / كلمة السر).
- [ ] **`https://your-domain/api/dev/inv-test-integrity?key=seed123` يرجّع 404** (endpoints التطوير معطّلة في الإنتاج عبر `[DevOnly]`).
- [ ] تسجيل الدخول ثم **إعادة نشر/تدوير الـ Pool** → تظل الجلسة (لا re-login) ← يثبت نجاح القسم (و).
- [ ] عملية واحدة (فاتورة/استلام) تُرحّل قيدها متوازنًا.
- [ ] التنبيهات الفورية (SignalR) تصل ← يثبت تفعيل WebSocket.
- [ ] الموبايل يسجّل دخول ويستقبل تنبيهات.

---

### ملاحظات أمنية
1. ✅ `/api/dev/*` يرجّع 404 في الإنتاج (`[DevOnly]` على `DevSeedController`).
2. غيّر **Jwt__Key** و**AiService__Secret** عن قيم التطوير.
3. لا تنشر بوضع Debug، ولا تترك `ASPNETCORE_ENVIRONMENT=Development`.
4. استخدم مستخدم SQL محدود (`crossbuy_app`) لا حساب `sa`.
5. قيّد RDP، وفعّل Windows Update + Defender.
6. **فحص السلامة** اليومي (HostedService) يُخطر عند أي انحراف محاسبي؛ وخدمة **تذكيرات CRM** تعمل كل 5 دقائق.
