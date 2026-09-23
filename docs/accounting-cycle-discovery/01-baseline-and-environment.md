# 01 — Baseline and environment

What was examined, in what state, and how the isolated environment was built and proved.

---

## 1. Baseline

| | |
|---|---|
| Discovery date | **21 September 2026** |
| Repository | `C:\CrossBuy\CrossBuy` |
| Branch | `reporting/studio-gap-closure` |
| Commit | `32985d91822da8cf4f0ffb05caade44629748ab7` (2026-09-20 14:35:32 +0300) |
| Sync with `origin` | behind 0, ahead 0 |
| Application | ASP.NET Core 8 MVC, `net8.0` |
| Application version | **none declared** — no `<Version>`, `<Company>`, `<Authors>` or `<Copyright>` in the csproj, and no About screen |

### 1.1 Working tree — preserved, not touched

Six untracked files were present at the start and are **unchanged**:

```
CrossBuy/wwwroot/Backend-assets/brand/
CrossBuy/wwwroot/Backend-assets/fonts/cairo/Cairo-{Bold,ExtraBold,Regular,SemiBold}.woff2
CrossBuy/wwwroot/Backend-assets/fonts/cairo/OFL.txt
```

They belong to other work. Nothing in this discovery modified, staged or committed them, and
nothing was committed or deployed.

### 1.2 Reconciliation against the existing discoveries

| Package | Status |
|---|---|
| `docs/CrossBuy_User_Manual_Discovery/` | Reused: screen inventory, field/action catalogue, role matrix, glossary and brand/owner assets are still valid at this commit |
| `docs/CrossBusiness_Business_Brand_Discovery/` | Reused: brand tokens, Cairo fonts, logo assets |
| `docs/user-manual-discovery/` | A byte-identical duplicate of the first, left alone |

**What this package changes about them.** The user-manual discovery stated plainly that it
executed nothing and ran no report. Both gaps are now partly closed, and the earlier documents
should be read with these corrections:

- Its trial-balance figure (113,027,021.50, captured 2026-09-20) is superseded here by
  113,067,021.50 — the difference is exactly this discovery's eight postings.
- Its note that "no report was run" no longer holds: nine were run, in two languages.
- Its caution that a screenshot may catch a page mid-render is **extended**: animated counters
  produce wrong *numbers*, not just blank panels (§5).

---

## 2. The isolated environment

### 2.1 Database

```sql
-- read-only on the source: COPY_ONLY leaves the dev backup chain untouched
BACKUP DATABASE [CrossBuyDev]
  TO DISK = N'C:\temp\cb_acct_iso\CrossBuyDev_for_acct_test.bak'
  WITH INIT, COPY_ONLY, COMPRESSION;

RESTORE DATABASE [CrossBuyAcctTest]
  FROM DISK = N'C:\temp\cb_acct_iso\CrossBuyDev_for_acct_test.bak'
  WITH MOVE N'CrossBuyDev'     TO N'C:\temp\cb_acct_iso\CrossBuyAcctTest.mdf',
       MOVE N'CrossBuyDev_log' TO N'C:\temp\cb_acct_iso\CrossBuyAcctTest_log.ldf',
       RECOVERY;
```

23,818 pages restored. Server `AhmedZakareya` (default instance `MSSQLSERVER`), Windows
authentication — **no credential appears in this package or in any script it ships.**

### 2.2 Application instance

The developer's instance holds `bin\Debug\net8.0\CrossBuy.exe`, so an ordinary build fails with
`MSB3027`. That blocked the previous discovery. It is solved by publishing to a different tree:

```bash
dotnet publish CrossBuy/CrossBuy.csproj -c Debug \
  -o "C:/temp/cb_acct_iso/app" \
  -p:BaseOutputPath="C:/temp/cb_acct_iso/obj_out/" \
  -p:UseAppHost=false
```

and running it with the environment overriding every outbound integration:

```
ASPNETCORE_ENVIRONMENT = Development
ASPNETCORE_URLS        = http://localhost:5299
ConnectionStrings__DefaultConnection = Server=localhost;Database=CrossBuyAcctTest;Trusted_Connection=True;…
Smtp__Enabled          = false          ← no mail leaves the machine
Smtp__Host / User / (secret) = cleared
AiService__BaseUrl     = http://127.0.0.1:1   ← the AI proxy has nowhere to call
Runtime__InstanceName  = acct-discovery-isolated
```

Reproduction script: `scripts/run-isolated.cmd` (also copied to `C:\temp\cb_acct_iso\`).

### 2.3 Outbound integrations — surveyed, then disabled

The outbound surface was **enumerated from source** before anything was switched off, so the
list is complete rather than assumed:

| Integration | Finding | Action |
|---|---|---|
| **SMTP** | Real. `Smtp:Enabled = true` in `appsettings.json` | **Disabled** by environment |
| **AI service** | `AiService:BaseUrl = http://localhost:8000`, `DeploymentMode = LocalLoopback`. The service is not running | Pointed at a dead port |
| **OpenAI** | `OpenAiOptions.FromConfiguration` at `Program.cs:831`; the egress policy refuses external destinations by default | No key configured; destination class `Internal` |
| **E-invoicing (ETA)** | **Makes no outbound call.** `EtaInvoiceService.SubmitSalesInvoiceAsync` returns `new EtaSubmitResult { Submitted = false, Status = "NotConfigured", Message = "The tax authority integration is not enabled (deferred)" }` (`BL/TaxService.cs:26-27`) | None needed |
| **Payment processing** | No payment-gateway client found in `BL/` | None needed |

*Repository evidence for every row.*

### 2.4 Background workers

Left at their defaults. `Runtime:RequireSingleWorkerProcess = true`, and the lease is a SQL
Server application lock **scoped to the database** — so the isolated instance becomes worker
primary for `CrossBuyAcctTest` while the developer's instance keeps the lease on `CrossBuyDev`.
The two do not interfere. Event dispatch therefore ran normally, which is the real product
behaviour and was left unchanged deliberately.

---

## 3. Isolation proof

Taken from `sys.dm_exec_sessions` **after** the instance was up and **before** any write:

| Process | Database | Sessions |
|---|---|---|
| **39228 — this discovery** | **CrossBuyAcctTest** | 4 |
| 32692 — developer's IIS Express (:44368) | CrossBuyDev | 3 |
| 24512 — developer's other process | CrossBuyDev | 1 |

Zero sessions from PID 39228 on `CrossBuyDev`. Both instances answered `/Account/Login` with
HTTP 200 throughout, so the developer's session was never disturbed.

**Production was never contacted.** No command in this package targets any remote server;
every connection is to `localhost`.

### 3.1 Baseline row counts in the isolated database

| Table | Rows | | Table | Rows |
|---|---:|---|---|---:|
| JournalEntries | 11,783 | | SalesInvoices | 5,501 |
| JournalEntryLines | 31,945 | | PurchaseInvoices | 116 |
| Accounts | 63 | | Receipts | 4,424 |
| Customers | 42 | | Payments | 4 |
| Vendors | 10 | | FixedAssets | 7 |
| Companies | 14 | | Items | 208 |
| FiscalYears | 2 | | TaxCodes | 6 |
| FiscalPeriods | 26 | | Currencies | 5 |
| BankAccounts | 1 | | CostCenters | 2 |
| CashBoxes | 1 | | | |

### 3.2 Period state — why 2026-09-21 was chosen

Both fiscal years are **Open**, each with 13 periods (12 months plus an adjustment period —
FY2025 period 13 spans 2025-12-31 to 2025-12-31 only).

```
Period ID 9 | FY 1 (2026) | PeriodNo 9 | 2026-09-01 → 2026-09-30 | Open
```

Every executed transaction is dated **2026-09-21**, inside that open period. The application
resolved `FiscalPeriodId = 9` from the date without being told.

---

## 4. Synthetic data convention

Records created by this discovery are prefixed **`ZZ-DISCOVERY`** in their description or name,
so they are trivially identifiable and removable.

**Pass 1** created journal entries only. **Pass 2 also created master data**, which the pass-1
text of this section denied — corrected here:

| Created in pass 2 | Id | Name |
|---|---|---|
| Supplier | 3015 | `ZZ-DISCOVERY Supplier Co` / ZZ-DISCOVERY مورّد الاكتشاف |
| Customer | 9047 | `ZZ-DISCOVERY Customer Co` / ZZ-DISCOVERY عميل الاكتشاف |
| Fixed asset | 2026 | `ZZ-DISCOVERY Test Machine`, cost 24,000.00, life 48 months |
| Identity users | — | `dev.superadmin`, `dev.auditor`, `dev.clerk`, `dev.otherco` |
| Documents | — | 2 purchase invoices, 2 payments, 1 purchase return, 1 sales invoice, 2 receipts, 1 sales return |
| Journal entries | — | `JV-2026-008517` … `JV-2026-008532` |

No existing customer, supplier or item was modified. **`ApprovalThreshold` was changed to
1,000.00 for the segregation-of-duties test and restored to 0.0000**; that is the only setting
this discovery altered, and it is back at its baseline.

**Cleanup** (on the isolated database only — never on `CrossBuyDev`):

```sql
-- inspect first
SELECT ID, EntryNo, Status, Description FROM JournalEntries
WHERE Description LIKE 'ZZ-DISCOVERY%' OR EntryNo BETWEEN 'JV-2026-008517' AND 'JV-2026-008532';

SELECT ID, Name FROM Vendors   WHERE Name LIKE 'ZZ-DISCOVERY%';
SELECT ID, Name FROM Customers WHERE Name LIKE 'ZZ-DISCOVERY%';
```

The simplest cleanup is to drop the whole database — it is disposable by design:

```sql
ALTER DATABASE [CrossBuyAcctTest] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [CrossBuyAcctTest];
```

then delete `C:\temp\cb_acct_iso\`. **This package leaves both in place** so the evidence can be
re-checked; removing them is the reader's call.

---

## 5. A capture-method correction, recorded because it matters

The first trial-balance capture showed **113,047,014.46** in the summary tiles and
**113,047,021.50** in the table footer — a 7.04 difference on one screen.

Both are rendered from the same `Model.TotalDebit` (`TrialBalance.cshtml:49` and `:153`), so they
cannot genuinely differ. The tiles carry `data-kt-countup`, Metronic's animated counter, and the
screenshot was taken while it was still counting up.

**The number was never true.** The readiness gate now waits until every `[data-kt-countup]`
element's text has been stable across three polls, and the affected reports were re-captured.
The figures now agree exactly and reconcile to the ledger.

This is logged as an evidence-quality control, not as a product defect:
`readiness_checks.countersSettled` appears on every row of `screenshot-manifest.csv`.

---

## 6. The environment after execution pass 2

**Left running and available for review, as instructed.** Nothing here has been cleaned up.

### 6.1 What is standing

| | |
|---|---|
| Isolated database | **`CrossBuyAcctTest`** on the local SQL Server instance, restored to checkpoint **CP2** |
| Isolated instance | **`http://localhost:5299`**, started by `C:\temp\cb_acct_iso\run-isolated.cmd` |
| Process | the pid changes across restarts — it was 39228, then **5088** after the CP2 restore. Resolve it with `netstat -ano \| findstr :5299` rather than trusting a recorded number |
| Published application | `C:\temp\cb_acct_iso\app\` |
| Log | `C:\temp\cb_acct_iso\app.log` |
| Developer's instance | **`:44368` on `CrossBuyDev`, untouched**, re-proved after every restart |

### 6.2 Checkpoints

Both are `COPY_ONLY` backups in `C:\temp\cb_acct_iso\`.

| File | Taken | Contents |
|---|---|---|
| `CP1_pre_execution.bak` | before pass 2 | the state after pass 1 — the journal loop only |
| `CP2_post_execution_pre_closing.bak` | after the transactions, before the closing scenarios | **the state the database is in now** |

The closing scenarios are destructive — a closed year refuses further posting — so they were run
**after CP2**, and **CP2 was then restored**. FY2026 is `Open` again, the transactions are
intact, and the year-end entry is gone from the live database. Its evidence survives in
[10](10-execution-pass-2.md) §7 and in the screenshots.

To roll back to either checkpoint:

```sql
ALTER DATABASE [CrossBuyAcctTest] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE [CrossBuyAcctTest]
  FROM DISK = 'C:\temp\cb_acct_iso\CP2_post_execution_pre_closing.bak'
  WITH REPLACE;
ALTER DATABASE [CrossBuyAcctTest] SET MULTI_USER;
```

**Stop the instance on :5299 first** — the restore needs exclusive access, and the application
holds sessions open.

### 6.3 Re-proving isolation after any restart

The instance must be re-proved, not assumed, because a restart gets a new pid:

```sql
SELECT p.program_name, s.dbid, DB_NAME(s.dbid) AS db, COUNT(*) AS sessions
FROM sys.sysprocesses s
CROSS APPLY (SELECT CAST(s.program_name AS varchar(128)) AS program_name) p
WHERE s.hostprocess = '<pid on :5299>'
GROUP BY p.program_name, s.dbid;
```

The expected result is sessions on **`CrossBuyAcctTest` only** and **zero** on `CrossBuyDev`.

### 6.4 Credentials

The dev seeder's password was supplied per invocation and is held in
**`C:\temp\cb_acct_iso\.seedpw`** — outside this package and outside the repository. It appears
in no document, script, screenshot or inventory here, and the package's secret scan confirms it.
