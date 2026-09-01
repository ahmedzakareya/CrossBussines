# CrossBusiness Platform — Roadmap v2 — 01 Verified Baseline

**Snapshot date: 2026-08-06.** Every number here is reconciled against the B6 final
delivery report and re-measured from the tree during this reassessment.

---

## 1. Stage 2A / B6

| Metric | Value |
|---|---|
| B6 status | **VERIFIED AND CLOSED** |
| Debug build | 0 errors |
| Release build | 0 errors |
| TestRun build | 0 errors |
| Full application suite | **1502 passed · 0 failed · 0 skipped** |
| Analyzer tests | 93 / 93 |
| Analyzer gates | CBA001 0 / CBA004 0 / CBA006 0 |
| Authorization debt | 143 |
| Detected mutating actions | 391 |
| Attribute-secured | 157 |
| In-body secured | 91 |
| Probe databases | 0 |
| Mutation markers | 0 |
| CrossBuyDB2 | present and untouched |
| Preservation | 84 files, 0 restore mismatches |

### The endpoint arithmetic

**157 + 91 + 143 = 391.**
The accepted Stage 1 measurement was 388 / 157 / 88 / 143. The `+3` on both total and
in-body is Batch A's `PlatformGrantsApiController.Create` / `.Revoke` / `.UpdateValidity`,
all authorized in-body through `IPlatformGrantWriter`. **Debt did not move**, and the gap set
matches the baseline id for id. B6 itself changed only services and added no endpoint.

### B6 scope, stated exactly

| Site | Status |
|---|---|
| `AccountingAccessService.cs:60` | CONVERTED |
| `InventoryAccessService.cs:48` | CONVERTED |
| `InventoryAccessService.cs:91` | CONVERTED (narrows behaviour) |
| `CrmAccessService.cs:59` → `return true` | **UNCHANGED — intentional** |
| `CrmAccessService.cs:98` → `return null` | **UNCHANGED — intentional** |

B6 replaced the **permission source only**. It changed no query filter, no row-level rule and
no returned data.

## 2. Other platform baselines

| Platform | Tests | Schema | Activation | UI |
|---|---|---|---|---|
| Reporting | 221 / 221 | 12 tables, from-empty and second-apply idempotency proved | Registered via `AddCrossBusinessReporting` | **None** |
| Communication | 274 / 274 | 14 tables, from-empty and idempotency proved | **Not activated in Program.cs** | **None** |
| Construction C1 | 38 / 38 | 8 tables in DDL, **applied nowhere** | Services registered in Program.cs | **None** |

Reporting: Dataset Layer, catalog, templates, history, archive, HTML/CSV/Excel implemented.
PDF runtime, email delivery and hosted scheduler deferred. No production module data source.

Communication: `CommEntityRef` and `ICommEntitySurface` are the common model; permission
boundary mechanically proved. Task and Support unregistered; DocComments migration not executed.

Construction: CR-01/CR-02/CR-03 mutation-proven closed. No DDL applied, no backfill executed.

## 3. Measured from the tree during this reassessment

| Measure | Value |
|---|---|
| MVC controllers | 33 |
| API controllers | 11 |
| Razor views (`.cshtml`, incl. partials and shared) | 327 |
| Top-level BL services | 121 |
| `DbSet<>` declarations in `CrossDbContext` | 245 |
| Registered hosted services | 8 |
| SQL slices — `deploy/sql` | 57 |
| SQL slices — `CrossBuy/deploy/sql` | 60 |
| Slices present in **both** trees | 4 — **and all 4 differ** |
| Test files in `CrossBuy.Tests` | 83 |
| ADRs written | 29 (ADR-001…037, with gaps) |
| Flutter mobile screens | 10 |

## 4. Corrections this reassessment makes to prior assumptions

| Prior assumption | Verified position |
|---|---|
| "Communication has no hosted worker" | True **for the Communication Platform**. But the *legacy* `BL/Comm` module does register `CommMessageDispatcherHostedService`, and `CommController` plus 3 views exist. Two different things share a prefix. |
| "Task Management is planned" | **Wrong.** Tasks has a controller, 5 views, 7 SQL slices (TM-1…TM-9), two registered hosted services and an access service. It is `LegacyExisting`, materially incomplete, and **unowned**. |
| "Calendar is planned" | **Wrong.** `CalendarService`, `CalendarController`, `calendar.sql` and 1 view exist. `Partial`, and unowned. |
| "One `deploy/sql`" | **Wrong.** Two trees, nearly disjoint, 4 overlapping files all different. |
| "183 skipped tests" | Those were SQL evidence tests skipping without `CROSSBUY_TEST_SQL`. With it configured the suite runs **1502 / 0 / 0**. |
