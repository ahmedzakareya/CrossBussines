# Stage 1 Batch B — delivery report

**Delivered in the instructed order: B1 → B3 → B2 → B4 → B5 → B6 → B7.**
No query filter was enabled until the controlled bypass existed and its tests passed.

**Verification:** `412 passed / 0 failed / 0 skipped` on real SQL Server (fresh build, 0 warnings);
`370 passed / 42 skipped` on SQLite alone (the 42 are the SQL-Server-gated suite, skipped not silently passed).
**129 tests added** (283 → 412). Scratch databases remaining: **0**.

**No production database was modified. No SQL was executed against CrossBuyDB2 or any other database** — the only
database access outside the test harness was two **read-only** `SELECT COUNT(*)` queries, cited where used.

---

## 1. What was delivered

| Sub-batch | Deliverable | Status |
|---|---|---|
| **B1** | Authoritative entity-isolation classification | Complete (approved earlier) |
| **B3** | Controlled, scoped, authorized, audited bypass — 4 kinds | Complete · ADR-023 |
| **B2** | Pilot global query filters on the 12 approved entities | Complete · ADR-024 |
| **B4** | Write-side company enforcement | Complete · ADR-024 §4 |
| **B5** | Raw-SQL and direct-DbContext safety | Complete · ADR-024 §5 |
| **B6** | Backlog regenerated and reclassified | Complete · CORRECTION-004 + B6 doc |
| **B7** | Company-isolation test matrix (2 companies, 2 employees, 18 cases) | Complete |

## 2. The three defects found by building this, not by reading it

These are the reason B2 took as long as it did, and each was caught by a test that failed first.

### 2.1 A silent cross-tenant leak in the filter itself — Critical

EF caches the model per context type, so `OnModelCreating` runs **once per process**. The first implementation
passed the scope holder in as an argument, so the compiled filter captured the **first** context's holder and reused
it forever: **every request in the process would have been filtered to the first request's company.** A filter that
appeared to work, on data belonging to whoever happened to hit the app first.

Fixed by reading the scope **through the executing context** (`db.CompanyScope`), which EF re-evaluates per query.
`Two_contexts_sharing_the_cached_model_are_filtered_by_their_own_scope` failed against the original and passes now.

### 2.2 Background workers would have read nothing, on a schedule — High

`ICompanyScopeHolder` was only ever populated as a *side effect* of a platform service resolving a context. Workers
have no request and no middleware, so their holder was unresolved — and the filters fail closed:

* `IntegrityCheckHostedService` reads `Customers`, `Items`, `JournalEntries`, `PurchaseInvoices`, `SalesInvoices` —
  **it would have reported zero problems because it examined zero rows.** A reconciliation that passes by looking at
  nothing is worse than one that fails.
* `CrmReminderHostedService` — reminders stop firing. `TaskGeneratorService`, `TaskScheduleMatcher` — same shape.

Fixed with `WorkerScope.ForCompany(scopes, companyId)` at the four per-company worker bodies; enforced by a test so
a fifth worker cannot omit it. **`SignalR` hubs and `JournalEntryService`'s isolated JV-numbering scope were checked
and are clear** — no hub reads a pilot entity, and numbering reads `NumberSequences`, which is not one.

### 2.3 An unresolved request would have thrown, not failed closed — Medium

`scope.CompanyId != null && e.CompanyID == scope.CompanyId.Value` does **not** short-circuit inside EF: parameter
extraction evaluates `.Value` before any SQL runs and threw `Nullable object must have a value` — a 500 naming
nothing, on any request that could not resolve a company. Replaced with a non-nullable sentinel
(`FilterCompanyId = CompanyId ?? 0`; `Set` refuses `<= 0`, so 0 matches no row).

## 3. B6: the accepted backlog figure was wrong — 193, not 189

`scan-architecture.ps1` counted **`PosLaneActivityGuard`** as a module permission. It is not: it compares a branch's
activity preset against a lane, checks **no role**, and calls `next()` when the session is absent — so it does not
even require authentication. That credited 44 mutating POS actions as protected.

Removing it would have been its own false finding, because **40 of the 44 authorize themselves in-body** via
`IPosAccessService`, which an attribute scanner cannot see. So the scanner now measures that too and reports three
categories:

```
mutating actions                                : 384
  with a permission ATTRIBUTE at any level      : 151
  authorized IN-BODY (access service)           :  40
  no authorization any scan can see             : 193   <-- the backlog
```

**The four actions the correction adds**, named because "four POS actions" is not actionable:
`PosAppController.AddCustomer` (**creates a `Customer`** — a pilot entity — with a session check but no role check),
`PosAppController.Start`, `HyperPosController.Start`, `HyperPosController.PriceCheck`.

**Severity frame, so 193 is not misread:** `SessionValidationMiddleware` is registered globally, so **192 of the 193
are authenticated-but-unauthorized** — any signed-in employee may invoke them. Exactly **one** is anonymous by
design (the JWT login endpoint). This is a privilege problem, not an exposure problem, and the B6 document says so
in those words.

## 4. One real cross-company read, fixed (B5)

`IntegrityCheckService`'s `barcode_cross_table_dup` read `Items` and `ItemBarcodes` with **no company predicate at
all**. Raw SQL is not covered by query filters, so on a multi-company install it counted another company's barcodes.
A `CompanyID` predicate was added; the check remains *counted* (`Ok = true`), so the number changes and no verdict
does — `failedCount` stays 0.

Nine production raw-SQL sites are now inventoried with a per-site verdict, re-derived from source by a test, so a new
raw query cannot land unreviewed. Also enforced: `IgnoreQueryFilters()` appears **nowhere** in production code, and
production never constructs a `CrossDbContext` by hand.

## 5. Prohibitions — compliance, item by item

| Instruction | Compliance |
|---|---|
| Do not add permission attributes to all actions automatically | **No attribute was added to any action.** B6 classified; Batch D remediates. |
| Do not modify the 189-action backlog in bulk | Not modified. It was re-measured and reclassified; the figure corrected to 193 with disclosure. |
| Do not apply global query filters blindly | 12 named entities, one at a time, no reflection sweep. 10 exclusions each with a reason, asserted by a test. |
| Do not activate filters before the bypass exists and is tested | B3 completed and green (42 tests) before B2 began. |
| Do not use the administrative bypass for the anonymous storefront | `PublicCompanyRead` is a separate kind; `AllowsCrossCompany` is **false**; unreachable through `Begin()`. |
| Do not collapse public catalogue access into a cross-company permission | Four distinct kinds; the public kind pins rather than unrestricts. |
| Introduce no new Tenant abstraction | None. `BusinessContext.TenantId` remains unused and null. |
| Do not change company semantics silently | Every behavioural change is in ADR-023/024 with its cost stated (empty grids on an unresolved scope; the insert stamp). |
| Do not break legitimate cross-company flows | All four B1 flows work under the filters, each with a named right. Verified. |
| No TODOs, mocks, stubs or pseudocode in production logic | None. |
| `DefaultCompanyId = 1` / silent company-1 fallback | Absent. Four tests specifically assert company 1 is never implicit. |
| Do not execute SQL against CrossBuyDB2 | **Nothing executed.** Two read-only `SELECT COUNT(*)` queries only, both cited. |
| Do not change accounting/stock/payroll/POS/manufacturing calculations | None changed. The only business-logic edit is a company predicate on a *counted* integrity check. |
| Do not refactor unrelated code | The only non-isolation edits are the four worker scope bindings and the `TestRun` declaration — both required for B2 to be correct. |
| Do not describe planned platforms as implemented | §7 states the limits; the maturity record scores the pilot as a pilot. |
| Do not mark complete with missing or skipped tests | 0 failed, **0 skipped** on SQL Server. |
| No maturity points for planned features | +1.50 total. Testing scored **0** despite 129 new tests — see §6. |

## 6. Maturity: +1.50 (52.70 → 54.20)

Only **Security and Company Isolation** moves, 60 → 70. **Testing and Reliability deliberately does not move**,
despite 129 new tests, because Stage 003 defined level 3 as *"the financial writers and business flows tested"* and
all 129 exercise isolation, permission or the bypass — neither sanctioned writer gained an accounting or costing
test. Scoring it up would reward volume against a criterion about subject matter.

## 7. What Batch B does NOT claim

* **Not system-wide isolation.** 12 of 261 entities — **4.6%**. 128 further `CompanyScopedDirect` entities are
  neither filtered nor guarded.
* **Raw SQL is not covered**, only inventoried. `StockBalance` remains excluded by instruction and by evidence.
* **Indirect children are not filtered** — they are isolated *by* their filtered parent, which is tested and stated.
* **Hotfix A.1 is not done.** B4 incidentally blocks the cross-company *write* on the `JournalEntry` path of
  `AccountingApiController.PayrollPost`; the **authorization** hole — a GL-posting endpoint with no module
  permission — is untouched. Reported as a side effect, not as the hotfix.

## 8. Open items handed forward

1. **`DevSeedController` child scopes (blocks the next headless acceptance).** Its `[DevOnly]` `CreateScope()` calls
   do not bind a company, so under the filters those scopes read nothing and their writes are refused. Remedy is
   mechanical — `WorkerScope.ForCompany(_scopes, company)` at ~8 sites. **Not done deliberately:** the file was being
   edited by the parallel team throughout this batch (its line numbers moved twice mid-session) and a blind edit
   risked a lost update. Production is unaffected.
2. **B3-BACKLOG-1 — legacy NULL-company notifications.** Close the write path first, then resolve existing rows from
   `Employee.EmpCompanyID`. Currently affects **0 rows** (measured: 1271 rows, 0 NULL).
3. **Filter performance.** The predicate is `@bypass = 1 OR CompanyID = @company`; the `OR` may cost an index seek on
   the larger tables. Live volumes are small (`JournalEntries` 1938 rows), so this is a measurement task, not a
   known regression. Neither claimed as fine nor as a problem.
4. **128 unfiltered `CompanyScopedDirect` entities** — the pilot's natural continuation, after the pilot is observed.

## 9. Two shared-tree events worth the owner's attention

Recorded per the standing build-freshness rule, not as complaints:

1. **`StockService` — one of our two architectural-invariant writers — gained a second
   `FromSqlInterpolated` UPDLOCK read of `StockBalances` during this batch**, uncommitted, from the parallel team.
   The B5 raw-SQL inventory caught it within minutes. Its shape is identical to the existing one (explicit
   `CompanyID`), so the verdict is unchanged — but per the standing rule a change to either writer should be
   pre-coordinated with the owner.
2. **The shared tree broke twice mid-session.** Once transiently (a mid-save `DevSeedController`), and once
   structurally: `DevSeedController` references `StockService._testBypassLandedLockRead`, which is declared inside
   `#if DEBUG`, so it compiled under Debug and **failed under `TestRun`**. That exposed a defect of ours —
   `TestRun` was never declared in any csproj, so MSBuild never defined `DEBUG` and every prior `-c TestRun`
   acceptance compiled a different program than Debug. Both csproj files now declare it. **Every number in this
   report comes from a fresh build after that fix**; nothing was accepted on an older binary.

## 10. Files

**New production code:** `Models/Platform/CompanyBypass.cs` · `BL/Platform/CompanyIsolationBypass.cs` ·
`CompanyQueryFilters.cs` · `NotificationCompanyPolicy.cs` · `CompanyScopeMiddleware.cs` ·
`CompanyWriteGuardInterceptor.cs`

**Modified:** `CompanyScopeHolder.cs` (bypass state, `FilterCompanyId`) · `CrossDbContext.cs` (scope-aware ctor,
filters) · `Program.cs` (DI, interceptor, middleware) · `appsettings.json` (`Store`) ·
`BusinessEventDispatchWorker.cs` · `NotificationProjectionConsumer.cs` · `BusinessEventMonitorService.cs` ·
`NotificationService.cs` · `StoreController.cs` · `HomeController.cs` · `IntegrityCheckService.cs` (company
predicate) · `WorkerCompanyScope.cs` (`WorkerScope`) · 4 hosted services · both `.csproj` (`TestRun`) ·
`deploy/scan-architecture.ps1` (CORRECTION-004)

**New tests (129):** `Stage1BypassTests` 42 · `Stage1QueryFilterTests` 24 · `Stage1WriteGuardTests` 24 ·
`Stage1IsolationMatrixTests` 21 · `Stage1RawSqlSafetyTests` 10 · `Stage1PermissionBacklogTests` 4 ·
`Stage1DiWiringTests` 4

**Documents:** ADR-023 · ADR-024 · CORRECTION-004 · `Stage-001-B3-Notification-Company-Policy.md` ·
`Stage-001-B6-Permission-Backlog-Reclassification.md` · `maturity/Stage-004-Stage-1-Batch-B-Result.md` ·
evidence CSVs regenerated (`Permission-Coverage.csv` with the new column, `Permission-Backlog-B6.csv`,
`Platform-Maturity-Metrics.csv`)

---

**Batch C and Batch D have not been started**, per instruction. Hotfix A.1 remains a separate delivery.
