# ADR-024 — Pilot company isolation: read filters, write guard, raw-SQL boundary

**Status:** Accepted (Stage 1 Batch B / B2, B4, B5)
**Date:** 2026-08-03
**Depends on:** ADR-023 (controlled bypass — a hard prerequisite), ADR-022 (BusinessContext resolution),
`Stage-001-B1-Entity-Isolation-Classification.md` (the twelve and the exclusions).

---

## 1. Decision

Install EF Core global query filters on the **twelve pilot entities**, enforce the same twelve on the **write** path
with a `SaveChanges` interceptor, and treat **raw SQL** as outside both — inventoried and reviewed rather than
claimed as covered.

| Entity | Module | Note |
|---|---|---|
| `JournalEntry`, `SalesInvoice`, `PurchaseInvoice`, `Customer` | Accounting | |
| `Item`, `Warehouse`, `Quotation` | Inventory | `Item` is also read by the anonymous storefront |
| `Lead`, `Opportunity`, `CrmAccount` | CRM | |
| `BusinessEvent` | Platform | read across companies by the dispatcher, under `PlatformDispatch` |
| `Notification` | Platform | the only nullable `CompanyID` — see the NULL policy |

**Not filtered, each for cause** (asserted by `Stage1QueryFilterTests.No_entity_outside_the_pilot_is_filtered`):
`StockBalance`, `Employee`, `AccountingUserRole`, `InventoryUserRole`, `CrmUserRole`, `BranchUserRole`, `Companies`,
`Branch`, `BusinessEventDispatch`, `FiscalPeriod`.

## 2. The predicate, and the two formulations that were wrong

```csharp
e => db.CompanyScope.AllowsCrossCompany || e.CompanyID == db.CompanyScope.FilterCompanyId
```

Three outcomes: bypass in force → unfiltered; company resolved → that company only; **nothing resolved → nothing**.

Two earlier formulations failed, and both failures were found by tests rather than by reading:

**(a) Passing the holder as an argument — a silent cross-tenant leak.** EF caches the model per context type, so
`OnModelCreating` runs ONCE per process. A filter closing over a holder object captured the FIRST context's holder
and kept using it, so every request in the process would have been filtered to the first request's company. The fix
is to read the scope **through the executing context** (`db.CompanyScope`), which EF re-evaluates per query.
Caught by `Two_contexts_sharing_the_cached_model_are_filtered_by_their_own_scope`, which failed on the first version.

**(b) `scope.CompanyId != null && e.CompanyID == scope.CompanyId.Value` — a 500 on every unresolved request.** EF
does not short-circuit that: parameter extraction evaluates `.Value` before any SQL runs and threw
`InvalidOperationException: Nullable object must have a value`. `FilterCompanyId` returns `CompanyId ?? 0`, and
`Set` refuses `companyId <= 0`, so 0 is an id no row can hold — failing closed then costs one parameter and no
exception. It is also correct for `Notification`, whose nullable `CompanyID` never equals 0.

### 2.1 Why unresolved reads NOTHING

The two available answers are "show nothing" and "show everything"; only one is a control. It is the same decision as
Batch A's removal of the company-1 fallback. **The cost is stated:** a request that fails to resolve a company shows
an empty grid where it used to show company 1's data.

`CompanyScopeMiddleware` makes that case rare — it resolves the scope for every request before any controller runs,
and logs a **Warning** naming the request when an authenticated request cannot resolve one, so an empty grid has a
log line beside it. It never throws, never invents a company, and never authorizes.

## 3. Two paths that have no middleware — and would have failed silently

**Background workers.** Their holder was unresolved, so with the filters on they would have read **nothing**, on a
schedule:

* `CrmReminderHostedService` → `ICrmService` → reminders stop firing;
* `IntegrityCheckHostedService` → `Customers`, `Items`, `JournalEntries`, `PurchaseInvoices`, `SalesInvoices` —
  **the check would have passed by examining zero rows**, which is worse than failing;
* `TaskGeneratorService` → `Items`, `SalesInvoices`; `TaskScheduleMatcher` → `Customers`, `PurchaseInvoices`,
  `SalesInvoices`.

They already created one DI scope per company; the missing half was telling that scope which company it is.
`WorkerScope.ForCompany(scopes, companyId)` does it via `IBusinessContextFactory.ForWorker`, and
`Stage1RawSqlSafetyTests` asserts all four workers use it and create no second bare scope.

**SignalR hubs and the JV numbering scope — checked and clear.** No hub reads a pilot entity (`ChatHub`'s `.Items`
is `HubCallerContext.Items`, not a DbSet). `JournalEntryService`'s isolated numbering scope reads `NumberSequences`,
not a pilot entity, so JV allocation is unaffected. Both verified rather than assumed.

## 4. The write guard (B4)

`CompanyWriteGuardInterceptor`, attached to the DbContext in `Program.cs`. A filter is a **read** control: it makes
another company's rows invisible, and invisible is not immutable.

| Case | Behaviour |
|---|---|
| INSERT naming another company | refused — a company id from a request/view model/route may not redirect a write |
| UPDATE/DELETE of another company's row | refused, on the **ORIGINAL** value, so a caller cannot "become" the owner by assigning its own company id first |
| Moving your own row to another company | refused |
| INSERT with no company | **stamped** from the scope and logged as a Warning — see below |
| Unresolved scope | refused, with a message that says why |
| `PublicCompanyRead` / `PlatformMonitoring` in force | refused — these are read-only kinds. **This closes the item ADR-023 recorded as open.** |
| `PlatformDispatch` / `CrossCompanyAdministration` in force | allowed — the projection legitimately writes a notification for a recipient in the event's company |

**The stamp is a deliberate middle path**, not a silent fix: refusing would turn a pre-existing omission into a
runtime failure on paths this batch has not enumerated, while the row would otherwise be written with
`CompanyID = 0` — an orphan no company can read and no filter can place. It is stamped from the **scope** (never a
constant) and the Warning names the entity so the omission gets fixed at its source.

## 5. Raw SQL is outside both (B5)

* `Database.SqlQueryRaw<T>` / `ExecuteSqlRaw` — no entity, therefore **no filter at all**.
* `DbSet.FromSql*` — the filter composes as an **outer** predicate. Correct, but the raw text runs as a subquery,
  which is exactly why `StockBalance` is excluded: its `WITH (UPDLOCK, HOLDLOCK)` lock is taken by the inner query.

Both demonstrated as tests rather than asserted in prose. **Nine production raw-SQL call sites** are inventoried with
a per-site verdict, re-derived from source by `Stage1RawSqlSafetyTests`, so a new raw query cannot land unreviewed.

**One real finding, fixed:** `IntegrityCheckService`'s `barcode_cross_table_dup` read `Items` and `ItemBarcodes`
across **every company** — a wrong count and a cross-company read. A `CompanyID` predicate was added; the check stays
*counted*, so the number changes and no verdict does.

**Also enforced:** `IgnoreQueryFilters()` appears nowhere in production code (it undoes isolation with no
authorization and no audit line), and production never constructs a `CrossDbContext` by hand.

## 6. What this does NOT claim

* **Not system-wide isolation.** 12 of 261 entities — **4.6%**. 128 further `CompanyScopedDirect` entities are
  unfiltered and unguarded. No maturity score moves on the strength of a pilot.
* **Raw SQL is not covered** — only inventoried (§5).
* **Indirect children are not filtered.** They are isolated *by* their filtered parent; reaching one requires knowing
  its id directly. Tested, and stated as the limitation it is.
* **`DevSeedController` is not covered.** Its `[DevOnly]` child scopes create their own DI scope without binding a
  company, so they read nothing under the filters. Declared as an open item, not fixed — the file was being edited
  concurrently by the parallel team throughout this batch and a blind edit risked a lost update.

## 7. Tests

`Stage1QueryFilterTests` (24) · `Stage1WriteGuardTests` (24) · `Stage1RawSqlSafetyTests` (10) ·
`Stage1IsolationMatrixTests` (21, the B7 matrix) · `Stage1PermissionBacklogTests` (4, B6) ·
`Stage1DiWiringTests` (4 — DI constructor selection, which nothing else covers) · plus `Stage1BypassTests` (42)
from B3 — **129 tests added by Batch B**.
**Full suite: 412 passed, 0 failed, 0 skipped on SQL Server** (`CROSSBUY_TEST_SQL`, scratch database, dropped).
