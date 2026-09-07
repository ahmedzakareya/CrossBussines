# Stage 005 — Hotfix A.1 result

**Measured:** 2026-08-04 · **Scope:** Stage 1 Security Hotfix A.1 (AccountingApiController)
**Basis:** `Permission-Coverage.csv` regenerated · 492 tests passing on SQL Server · ADR-025.
**Immutable:** Stages 000-004 are unchanged. This record adds one row; it rewrites none.

---

## 1. Scores — maturity before and after

| # | Dimension | Weight | Level | % | Score | Δ vs 004 |
|---|---|---|---|---|---|---|
1 | ERP Business Coverage | 20 | 3 | 60 | 12.00 | — |
2 | Security and Company Isolation | 15 | 3+ | 70 | 10.50 | **—** |
3 | Platform Kernel and Audit | 10 | 4+ | 90 | 9.00 | — |
4 | Business Object Coverage | 10 | 1 | 20 | 2.00 | — |
5 | Task and Work Management | 10 | 3 | 60 | 6.00 | — |
6 | Workflow and Approvals | 10 | 2 | 40 | 4.00 | — |
7 | Communication and Collaboration | 10 | 2+ | 50 | 5.00 | — |
8 | Search, AI and Intelligence | 7 | 1 | 20 | 1.40 | — |
9 | Testing and Reliability | 5 | 2+ | 50 | 2.50 | — |
10 | Deployment and Operations | 3 | 3 | 60 | 1.80 | — |
| | **Total** | **100** | | | **54.20** | **0.00** |

| | |
|---|---|
| Maturity **before** | **54.20%** |
| Maturity **after** | **54.20%** |
| Net percentage-point gain | **0.00 pp** |
| Relative improvement | **0.00%** |
| Affected dimensions | **none** |
| Unchanged dimensions | **all ten** |

## 2. Why a real security fix scores zero — and why that is the correct answer

This is the most severe subset in the Stage 1 backlog: ten endpoints that let any holder of a valid mobile token post
to the general ledger, post a payroll run, and direct invoices, receipts, payments, customers and vendors at
**another company**. They are now authorized, company-validated and covered by 79 endpoint tests (80 added in total). Nothing about the fix is
small.

It still moves no score, because the model's own threshold is not met. Dimension 2's definition, quoted verbatim:

> - 60: most mutating actions gated by a single mechanism; `CompanyID` enforced centrally (e.g. EF query filters).
> - **80: as above plus no hard-coded company anywhere, one access contract, and security regression tests.**

Measured against level 80, after the hotfix:

| Level-80 requirement | Status |
|---|---|
| Security regression tests | **Met.** 209 isolation / permission / API-security tests (Batch B 129 + Hotfix A.1 80), green on SQL Server. |
| One access contract | **Partly.** `IModuleAccessService` is the contract for Accounting, Inventory, CRM and POS — but HR, Projects and Tasks/Communication have **no access service at all** (Batch C). |
| **No hard-coded company anywhere** | **NOT met.** `AccountingController` (MVC) still uses `DefaultCompanyId`; `PosCompanyPolicy.CatalogCompanyId = 1` remains a declared exception. |

The model advances in 10-point steps, so the only positions available are 70 and 80. 80 is not reachable while a
hard-coded company remains, and inventing a 75 to reward this work would break the model's own step rule — which is
exactly the flattery the maturity model exists to prevent. **Dimension 2 stays at 70.**

Dimension 9 stays at 50 for the reason Stage 003 fixed: level 3 requires *"the financial writers and business flows
tested."* The 79 new tests prove the **controller's** authorization and company handling with a recording service
double; `JournalEntryService` and `StockService` still have no accounting or costing test of their own.

## 3. What WOULD move dimension 2 to 80

Stated so the next batch is not guesswork:

1. Remove `DefaultCompanyId` from `AccountingController` and the remaining MVC controllers (the same treatment this
   hotfix gave the API), and resolve or retire `PosCompanyPolicy.CatalogCompanyId`.
2. Create the missing HR, Projects and Tasks/Communication access services (**Batch C**), so "one access contract"
   is true of every module rather than four of seven.
3. Reduce the 183-action backlog materially (**Batch D**).

None of the three is in this hotfix's scope.

## 4. Correction versus implementation impact

**Zero correction impact; all implementation.** No published figure was found to be wrong by this hotfix, and no
earlier number is withdrawn.

The permission backlog moved **193 → 183** purely because ten endpoints became authorized — not because a
measurement changed. The reconciliation is arithmetic, and it is pinned as a test:

```
384 mutating = 151 attribute-protected + 50 authorized in-body + 183 backlog
                                          ↑ was 40; the ten accounting API actions joined it
```

The mutating **total is unchanged at 384**, which is the check that the hotfix secured existing actions rather than
adding or removing any.

**One measurement mechanism was extended, disclosed here (R7):** `scan-architecture.ps1`'s in-body detection now
recognises `_guard.AuthorizeAsync(` alongside the access-service call shapes. Without it the scanner would have kept
reporting these ten as unprotected — a false *negative* after the fix, as surely as crediting the POS lane guard was
a false positive before it (CORRECTION-004). The pattern is narrow: a guard-shaped authorization call, not any method
whose name contains "Auth".

**One unrelated count moved and is not attributed to this hotfix:** total actions went 1055 → 1056. It is a
**non-mutating** action in another controller — `AccountingApiController` is unchanged at 22 actions, and the mutating
total held at 384 — consistent with the parallel team's concurrent edits to `DevSeedController` observed twice during
Batch B. Recorded rather than absorbed into this hotfix's numbers.

## 5. Explicitly awarded no points

Per instruction, and stated so nobody looks for the credit later:

* the three **UI identity documents** (A6.1-A6.3) — documentation;
* **Stage 9** Architecture Validation Framework (A8) — planned, not implemented;
* **Stage 10** Enterprise Financial Intelligence Platform (A9) — planned, not implemented;
* the **CLAUDE.md** engineering rules (A7) — documentation.

## 6. Evidence

| Claim | Where |
|---|---|
| All 10 mutating actions authorized; all 12 reads too | `HotfixA1AccountingApiTests` (79) · `Stage1PermissionBacklogTests.Every_accounting_api_mutating_action_is_authorized` |
| Permission matrix matches the real access service | `The_permission_matrix_matches_the_accounting_access_service` (16 cases, real role rows) |
| Company tampering rejected per endpoint, nothing written | `Company_tampering_is_refused_per_endpoint_and_nothing_is_written` (8 cases) |
| Journal ownership by id; foreign = not-found | `Acting_on_another_companys_journal_by_id_is_not_found_and_never_posted` |
| Backlog reconciliation 384 = 151 + 50 + 183 | `Stage1PermissionBacklogTests` (5) |
| Suite | **492 passed / 0 failed / 0 skipped** on SQL Server; 450 passed / 42 skipped on SQLite alone |
