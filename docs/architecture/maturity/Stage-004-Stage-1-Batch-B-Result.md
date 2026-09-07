# Stage 004 — Stage 1 Batch B result

**Measured:** 2026-08-04 · **Scope:** Stage 1 Batch B (B1 → B3 → B2 → B4 → B5 → B6 → B7)
**Basis:** `Permission-Coverage.csv` + `Entity-Isolation-Classification.csv` regenerated ·
412 tests passing on SQL Server · ADR-023, ADR-024, CORRECTION-004.
**Immutable:** Stages 000–003 are unchanged. This record adds one row; it rewrites none.

---

## 1. Scores

| # | Dimension | Weight | Level | % | Score | Δ vs 003 |
|---|---|---|---|---|---|---|
1 | ERP Business Coverage | 20 | 3 | 60 | 12.00 | — |
2 | Security and Company Isolation | 15 | 3+ | **70** | **10.50** | **+1.50** |
3 | Platform Kernel and Audit | 10 | 4+ | 90 | 9.00 | — |
4 | Business Object Coverage | 10 | 1 | 20 | 2.00 | — |
5 | Task and Work Management | 10 | 3 | 60 | 6.00 | — |
6 | Workflow and Approvals | 10 | 2 | 40 | 4.00 | — |
7 | Communication and Collaboration | 10 | 2+ | 50 | 5.00 | — |
8 | Search, AI and Intelligence | 7 | 1 | 20 | 1.40 | — |
9 | Testing and Reliability | 5 | 2+ | 50 | 2.50 | — |
10 | Deployment and Operations | 3 | 3 | 60 | 1.80 | — |
| | **Total** | **100** | | | **54.20** | **+1.50** |

## 2. Why dimension 2 moves one step, and only one

**60 → 70 (Level 3 → 3+).** What was earned:

* an **enforced** read boundary on 12 entities, and a **write** boundary on the same 12 — previously isolation was
  ~1,100 hand-written predicates, correct wherever someone remembered;
* every request's company resolved **deterministically** by middleware rather than as a side effect;
* the four legitimate cross-company flows moved from *unstated properties of the code* to **named, authorized,
  audited rights** (ADR-023);
* a fail-closed default everywhere: unresolved scope reads nothing and writes nothing;
* the raw-SQL boundary **inventoried and test-enforced**, including one real cross-company read fixed.

**Why not 80.** 12 of 261 entities is **4.6%**. 128 further `CompanyScopedDirect` entities are unfiltered and
unguarded; raw SQL is outside the mechanism by construction; the permission backlog is **193** actions. A pilot
that works is worth one step, not two, and this is the dimension where over-scoring would be least honest.

## 3. Why dimension 9 does NOT move, despite 129 new tests

**50, unchanged.** 129 tests were added (283 → 412), the suite is green on **both** providers with **0 skipped** on
SQL Server, and three *enforcing* inventories now fail rather than merely document drift (the raw-SQL inventory, the
permission-backlog reconciliation, the filtered-entity set). The `TestRun` configuration was also declared, so the
tested binary is finally the same program developers run (§5.3).

None of that satisfies the criterion Stage 003 set for level 3, in its own words: *"level 3 needs the financial
writers and business flows tested."* Every one of the 129 exercises isolation, permission or the bypass. Neither
`JournalEntryService` nor `StockService` gained an accounting or costing test in this batch, and Accounting,
Inventory, POS, HR, CRM and Projects still have none.

Scoring this dimension up would be rewarding volume against a criterion about subject matter — the exact
flattery the model exists to prevent. It stays at 50 until a sanctioned writer is tested.

## 4. Dimensions deliberately unchanged

| Dimension | Why unchanged |
|---|---|
| 1 ERP Business Coverage | No business capability was added. |
| 3 Platform Kernel and Audit | The bypass audit is new but the kernel's own audit position is what Stage 003 scored; no kernel capability was added. |
| 4, 5, 6, 7, 8 | Untouched by this batch. Batch C (missing HR/Projects/Tasks access services) is where 4 and 5 would move. |
| 10 Deployment and Operations | No script, runbook or deployment mechanism changed. **No SQL was executed against any database** in this batch. |

## 5. Measurement corrections disclosed (R7)

1. **The permission backlog is 193, not the accepted 189.** `scan-architecture.ps1` counted
   `PosLaneActivityGuard` — a branch/lane compatibility check that verifies no role and lets an unauthenticated
   request through — as a module permission, crediting 44 POS actions. 40 of those 44 authorize themselves in-body
   via `IPosAccessService`; 4 do not. See CORRECTION-004. **No score changes**: the backlog was already a gap and
   193 crosses no 10-point step.
2. **An error made while producing that measurement, and corrected.** The first in-body pass reported 41 and
   concluded 189 survived. Its 25-line window ran past the end of `PosAppController.AddCustomer` into the next
   method's `_access.CanOrder`. The scanner's member-boundary window gives 40, and `AddCustomer` — which creates a
   `Customer` — is genuinely role-unchecked.
3. **Every previous `-c TestRun` acceptance ran a different program than Debug.** `TestRun` was never declared in
   any csproj, so MSBuild did not define `DEBUG` and all `#if DEBUG` code was compiled out. It is now declared in
   both projects. This does not invalidate any earlier result — the affected `#if DEBUG` blocks are test-only
   bypasses that default to off, so Debug and non-Debug behave identically unless a test sets them — but the
   discrepancy existed and is recorded rather than quietly fixed.

## 6. Evidence

| Claim | Where |
|---|---|
| 12 entities filtered, 0 outside the pilot | `Stage1QueryFilterTests` (24) |
| Write boundary on the same 12 | `Stage1WriteGuardTests` (24) |
| Bypass authorization / lifetime / leak-freedom | `Stage1BypassTests` (42) |
| Raw SQL inventory, `IgnoreQueryFilters`, worker scope binding | `Stage1RawSqlSafetyTests` (10) |
| 2 companies × 2 employees × 18 cases | `Stage1IsolationMatrixTests` (21) |
| 384 = 151 + 40 + 193 | `Stage1PermissionBacklogTests` (4) |
| DI selects the scope-aware constructor | `Stage1DiWiringTests` (4) |
| Suite | **412 passed / 0 failed / 0 skipped** on SQL Server; 370 passed / 42 skipped on SQLite alone |
