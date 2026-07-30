# CrossBuy — Accounting/Finance Module · Handoff Report

> Paste this into a new Claude Code session as context. It describes the double-entry
> accounting module built into CrossBuy (web + Flutter mobile), how to build/run it,
> the conventions to follow, and what remains.

---

## 1. Project & Stack

- **CrossBuy** — ASP.NET Core MVC (**.NET 8**) HR/ERP system, Metronic 8 theme, bilingual **AR/EN** (RTL), Cairo font.
- **DB**: SQL Server, database **`CrossBuyDB2`**, conn string in `appsettings.json` → `Server=.;Database=CrossBuyDB2;Trusted_Connection=True;TrustServerCertificate=True`.
- **ORM**: EF Core 9 (`CrossDbContext`). **EF migrations are broken/not used** — schema is applied via **manual SQL scripts** run with `sqlcmd` (see §3).
- **Auth (web)**: session-based. `[SessionValidation]` attribute reads `Session["Employee"]`; empty → redirect to `/Account/Login`. Test login: **`Admin` / `Admin@123`**.
- **Auth (mobile/API)**: JWT bearer. `POST /api/auth/login {userName,password}` → `{success, token, employee}`.
- **Charts**: ApexCharts (bundled). **jQuery** bundled but **no DataTables** — tables use a small vanilla pager + per-page search inputs.
- **Mobile**: Flutter app in `crossbuy_mobile/` (Dio + provider; manual AR/EN l10n in `lib/l10n/app_localizations.dart`).

### Standing UI rules (do not violate)
- Every list screen has a **real working search**; never static/hardcoded data — all from DB.
- Hierarchical/tree screens need **collapse/expand**, and the collapse caret sits on the **first column**.
- Pure Metronic styling, **no custom CSS**.

---

## 2. Build / Run — IMPORTANT gotchas

```bash
# from the project dir: c:/Users/Lenovo/Desktop/CrossBuy/CrossBuy/CrossBuy
# 1) kill any running instance (it locks the DLLs)
netstat -ano | grep ':5000' | grep LISTEN | awk '{print $5}' | head -1 | xargs -r taskkill //F //PID
# 2) build to the run folder
dotnet build -c Debug -o /c/temp/cb_run
# 3) run from the OUTPUT folder
cd /c/temp/cb_run
ASPNETCORE_URLS="http://localhost:5000" ASPNETCORE_ENVIRONMENT=Development nohup dotnet CrossBuy.dll > run.log 2>&1 &
```

- **Views are precompiled into the DLL** in this `-o` published output. Editing a `.cshtml` does **NOT** hot-reload — you must **rebuild + restart** for Razor changes to show.
- **Razor pitfall**: a method parameter named `from`/`to` clashes with the LINQ query keyword inside `from x in …` query syntax (CS1525/CS0742). Rename params to `fromDate`/`toDate` (or alias to locals) — this has bitten us repeatedly.
- **Razor pitfall**: don't emit markup from `@functions`/local functions with `@{ Foo(); }` — caused CS4033. Inline the loops instead. `T(...)` translation helper defined in the page's `@{ }` block is NOT in scope inside `@functions` methods — pass `isAr` and inline ternaries there.
- **sqlcmd** path (Windows path, not the git-bash `/c/...` form for `-i`):
  `"/c/Program Files/Microsoft SQL Server/Client SDK/ODBC/170/Tools/Binn/sqlcmd" -S . -d CrossBuyDB2 -E -C -i "C:\temp\acc_phaseN.sql"`
- Bracket reserved identifiers in raw SQL (`[LineNo]`). Filtered unique indexes need `SET QUOTED_IDENTIFIER ON`.

### Seed endpoints (Development only, key=`seed123`)
- `GET /api/dev/seed-users` — creates Admin/sara.ali/khaled.hassan.
- `GET /api/dev/seed-accounting?key=seed123` — Phase 0/2/5: account types, EGP currency, FY 2026 (+13 periods), full COA, cost centers, payroll posting rules.
- `GET /api/dev/seed-acc-demo?key=seed123` — fills all screens (customers, vendors, invoices, receipts/payments, banks, transfers, reconciliation, 6 months payroll). Idempotent.
- `GET /api/dev/seed-fa-demo?key=seed123` — Phase 6: 2 asset categories, 3 assets, depreciation Jan→Jun.
- `GET /api/dev/seed-tax-demo?key=seed123` — Phase 7: VAT + WHT tax codes, one filed VAT return.

---

## 3. Architecture & conventions

- **Single company** for now: `AccountingController.DefaultCompanyId = 1` (multi-company later). `CompanyID` (legal entity) is separate from cost center.
- **EGP-only** now, but currency columns are kept on entities.
- **Layout**: accounting screens use `~/Views/Shared/_LayoutAccounting.cshtml` (clone of backend layout; accounting-only sidebar with accordions: النظام المحاسبي, الأصول الثابتة, الضرائب, القوائم المالية). A centralized vanilla-JS pager near `</body>` auto-enhances flat tables (skips trees via `data-parent`), 10/page, wired to `.card-header input[type=text]`.
- **Double-entry posting engine** = `JournalEntryService` (`BL/JournalEntryService.cs`):
  - `CreateDraftAsync`, `PostAsync`, `CreateAndPostAsync`, `ReverseAsync` (reversal, never delete).
  - Validates: ≥2 lines, each line debit XOR credit, **balanced** (ΣDr==ΣCr), account exists/active.
  - **Control accounts** (`IsPostable=0`) reject manual postings; **system entries** (those with a `SourceType`) may post to them. Set `SourceType`+`SourceId` on all auto entries.
  - Cost-center requirement: accounts with `RequireCostCenter=1` (e.g. expense `5xxxxx`) **must** have a `CostCenterId` on every line — enforced regardless of system/manual.
  - Period guard: resolves the `FiscalPeriod` for `EntryDate`; **Closed** period rejects posting. `EntryNo` reserved at post time via `NumberSequences` (`JV-YYYY-NNNNNN`); column nullable + filtered unique index.
- **GL queries** include `Status IN ('Posted','Reversed')` (reversed lines are real history).

---

## 4. Phases delivered (0 → 10) — all verified with a balanced ledger

| Phase | Scope | Key files |
|------|-------|-----------|
| 0–1 | Foundation, COA tree (**CRUD**, caret on first col) | `BL/ChartOfAccountsService.cs`, `Views/Accounting/ChartOfAccounts.cshtml` |
| 2 | Cost centers (tree from org hierarchy) | `BL/CostCenterService.cs`, `CostCenters.cshtml` |
| 3 | Customers/Vendors, sales/purchase invoices, receipts/payments, AR/AP aging (FIFO) | `BL/ReceivableService.cs`, `BL/PayableService.cs` |
| 4 | Banks, cash boxes, transfers, bank reconciliation | `BL/BankService.cs` |
| 5 | Payroll → GL bridge (posting rules) | `BL/AccountingPostingService.cs`, `BL/PostingRule.cs` |
| 6 | **Fixed assets & depreciation** (acquisition / monthly straight-line / disposal with gain-loss) | `BL/FixedAssetService.cs`, `Models/Context/Accounting/FixedAssets.cs` |
| 7 | **Taxes**: VAT codes, VAT return (compute+settle), WHT on payments; **ETA isolated behind `IEtaInvoiceService` stub, deferred** | `BL/TaxService.cs`, `Models/Context/Accounting/Taxes.cs` |
| 8 | **Financial statements**: Income Statement, Balance Sheet, Cash Flow (by `CashFlowCategory` of contra accounts) | `BL/FinancialStatementService.cs` |
| 9 | **Year-end close** → zeroes P&L per (account×cost-center) into Retained Earnings (3201), locks periods; **reopen** reverses it | `BL/ClosingService.cs`, `Models/Context/Accounting/Closing.cs` |
| 10 | **Mobile finance tab** — `GET /api/acc/summary` (one-call KPIs) + Flutter `FinanceScreen` | `Controllers/Api/AccountingApiController.cs`, `crossbuy_mobile/lib/screens/finance_screen.dart` |

- **Controller**: `Controllers/AccountingController.cs` (one controller, all web actions + detail views: `JournalEntry`, `SalesInvoiceDetail`, `PurchaseInvoiceDetail`, `CustomerStatement`, `VendorStatement`, `FixedAssetDetail`, `DepreciationRunDetail`, `VatReturnDetail`).
- **Web API**: `Controllers/Api/AccountingApiController.cs` at route `api/acc/*` (JWT) — accounts, journals (CRUD/post/reverse), trial-balance, ledger, payroll, AR/AP, and `summary` for mobile.
- **DI**: all services registered in `Program.cs` (`IChartOfAccountsService, IJournalEntryService, IGeneralLedgerService, ICostCenterService, IFiscalPeriodService, IAccountingPostingService, IReceivableService, IPayableService, IBankService, IAccountingDashboardService, IFixedAssetService, ITaxService, IEtaInvoiceService, IFinancialStatementService, IClosingService`).
- **DbSets** in `Models/Context/CrossDbContext.cs`: AccountTypes, Accounts, Currencies, ExchangeRates, FiscalYears, FiscalPeriods, JournalEntries(+Lines), NumberSequences, CostCenters, Projects, PostingRules, Customers, Vendors, SalesInvoices(+Lines), PurchaseInvoices(+Lines), Receipts, Payments, BankAccounts, CashBoxes, BankReconciliations(+Lines), **AssetCategories, FixedAssets, DepreciationRuns, DepreciationLines, TaxCodes, VatReturns, EtaSettings, YearEndClosings**.
- **SQL DDL scripts**: `C:\temp\acc_phase0.sql … acc_phase9.sql`, `acc_idx.sql`, `acc_entryno.sql`, `app_cache.sql`.

### Chart-of-accounts anchors (codes the code relies on)
`1101*` cash & banks · `1102` AR control · `1201` asset cost · `1202` accum. dep · `110401` VAT input · `2101` AP control · `210201` VAT output · `210202` WHT payable · `210205` VAT payable to authority (auto-created) · `3101` capital · `3201` retained earnings · `4101` revenue · `520103` depreciation expense · `4901`/`5901` gain/loss on disposal (auto-created).

---

## 5. Dashboard (home) — `Views/Accounting/Index.cshtml`

Built to mirror the Metronic **social.html** template layout:
- **Row 1**: big Revenue-vs-Expense area chart (8) + a blue "Financial Position" campaign-style card (4) with two dashed stat boxes (Total Assets / Liab+Equity), detail rows, a **balanced** badge, and a white mini area chart (net trend) filling the base.
- **Row 2**: 6 brand-style stat cards (Cash, Receivables, Payables, Net Income, Fixed-Assets NBV, VAT).
- **Row 3**: recent journal entries table (campaign-table style) + Top-Expenses "Notable Channels" progress list with a sparkline.
- **Row 4**: Fixed-assets panel, VAT panel, expanded Quick-Access grid (all modules).
- Data from `AccountingDashboardService.BuildAsync` (balances, 6-month trend, balance-sheet snapshot, fixed-asset summary, VAT, fiscal-year status). All computed from the GL.

---

## 6. KNOWN ISSUE (unresolved) — re-login on every rebuild

The user is forced to log in again after each rebuild. A fix is **in place but did NOT resolve it in their environment**:
- `Program.cs`: DataProtection keys persisted to `<LocalAppData>/CrossBuy/keys` with `SetApplicationName("CrossBuy")`; session moved from in-memory to **SQL Server** (`AddDistributedSqlServerCache` → `dbo.AppCache`, DDL at `C:\temp\app_cache.sql`).
- Verified via curl that a cookie survives a server restart (200 after restart). But the user still hits the re-login.
- **Not fully diagnosed** — deferred at user request. Next angles: do they run via **Visual Studio / IIS Express** (different session + DataProtection config than `dotnet CrossBuy.dll`)? Stale pre-fix cookie in the browser? Different host/port? Confirm how they launch the app before changing more.

---

## 7. Verified invariants (sanity checks)

- Trial balance / ledger always balances: `Σ Debit == Σ Credit` over `Status IN ('Posted','Reversed')`.
- Balance sheet identity holds: `Assets == Liabilities + Equity + NetResult`.
- VAT return ties to GL movement on `210201` (output) and `110401` (input).
- Year close zeroes P&L (REV/EXP net = 0 up to year end) and moves the result to `3201`; reopen restores it. All steps re-checked with `sqlcmd` aggregates.

---

## 8. Suggested next steps

- Diagnose the re-login issue against the user's actual run method (§6).
- Multi-company support (drop the hardcoded `DefaultCompanyId=1`).
- ETA e-invoice real implementation behind the existing `IEtaInvoiceService`.
- Budgeting (currently out of scope), inventory (out of scope).
- More mobile finance screens (statements, drill-down) on top of `api/acc/*`.
