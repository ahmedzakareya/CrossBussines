# Platform Maturity — current baseline

**This file names the baseline that the next stage measures against. It is the only file in the maturity set that
changes.** The stage records under `maturity/` are immutable (model rule R8); this pointer is not.

---

## Current baseline

| | |
|---|---|
**Baseline stage** | 003 — Stage 1 Batch A (BusinessContext and session-free permissions) |
**Baseline record** | [maturity/Stage-003-Stage-1-Batch-A-Result.md](maturity/Stage-003-Stage-1-Batch-A-Result.md) |
**Baseline total** | **52.70 / 100** |
**Measured** | 2026-08-03 |
**Test suite at baseline** | 283 tests, all passing (241 passed + 42 skipped without `CROSSBUY_TEST_SQL`) |

### Per-dimension baseline

| # | Dimension | Weight | Level | % | Score |
|---|---|---|---|---|---|
1 | ERP Business Coverage | 20 | 3 | 60 | 12.00 |
2 | Security and Company Isolation | 15 | 3 | 60 | 9.00 |
3 | Platform Kernel and Audit | 10 | 4+ | 90 | 9.00 |
4 | Business Object Coverage | 10 | 1 | 20 | 2.00 |
5 | Task and Work Management | 10 | 3 | 60 | 6.00 |
6 | Workflow and Approvals | 10 | 2 | 40 | 4.00 |
7 | Communication and Collaboration | 10 | 2+ | 50 | 5.00 |
8 | Search, AI and Intelligence | 7 | 1 | 20 | 1.40 |
9 | Testing and Reliability | 5 | 2+ | 50 | 2.50 |
10 | Deployment and Operations | 3 | 3 | 60 | 1.80 |
| | **Total** | **100** | | | **52.70** |

---

## Series

| Stage | Record | Total | Δ | Note |
|---|---|---|---|---|
000 | [Baseline (as-built)](maturity/Stage-000-Baseline.md) | 44.00 | — | measured retrospectively on CORRECTION-001/002 corrected evidence |
001 | [Stage 0 Batch A](maturity/Stage-001-Batch-A-Result.md) | 47.50 | +3.50 | measured retrospectively |
002 | [Stage 0 complete](maturity/Stage-002-Stage-0-Result.md) | 50.20 | +2.70 | |
**003** | [**Stage 1 Batch A**](maturity/Stage-003-Stage-1-Batch-A-Result.md) | **52.70** | +2.50 | **current baseline.** Includes CORRECTION-003: the security backlog is **189**, not 306 |

Machine-readable: [`evidence/Platform-Maturity-Metrics.csv`](evidence/Platform-Maturity-Metrics.csv).

---

## Where the headroom is

Ordered by **weighted points available**, which is the only ordering that answers "what should the next stage do?".

| Dimension | Now | Max | **Headroom** | What the first step actually is |
|---|---|---|---|---|
**Business Object Coverage** | 2.00 | 10 | **8.00** | 9 of 58 objects registered. Registering objects is cheap; wiring timeline + comments + entity-addressed notifications per object is the work. Highest single opportunity in the model. |
**Security and Company Isolation** | 9.00 | 15 | **6.00** | Stage 1 Batch A removed the company-1 fallback and made permission evaluation identity-correct. What remains: **EF global query filters (Stage 1 Batch B)**, the 767 `DefaultCompanyId` call sites, the **189**-action backlog (Batch D, after the three missing access services in Batch C), and the unguarded upload URLs (A4). |
**Workflow and Approvals** | 4.00 | 10 | **6.00** | One engine replacing four silos. The discount silo — no table, no record, no approver — is the worst of them. |
**Search, AI and Intelligence** | 1.40 | 7 | **5.60** | Blocked on B3 (one access contract taking `BusinessContext`): per-object AI context cannot be assembled safely without permission filtering. |
**Communication and Collaboration** | 5.00 | 10 | **5.00** | Make the standalone channels entity-addressable. `CommMessage` has no `EntityType`/`EntityId`; object-attached files do not exist. |
**Task and Work Management** | 6.00 | 10 | **4.00** | One unified work inbox across modules; make tasks registry citizens. |
**ERP Business Coverage** | 12.00 | 20 | **4.00** | Gated on real multi-tenancy (same B2 as Security), then consolidated reporting, budgeting, procure-to-pay. |
**Platform Kernel and Audit** | 9.00 | 10 | **1.00** | Retention/archival policy for `BusinessEvents`; coverage grows with dimension 4. |
**Testing and Reliability** | 2.50 | 5 | **2.50** | Regression tests for `JournalEntryService` and `StockService` — the two writers — then CI. |
**Deployment and Operations** | 1.80 | 3 | **1.20** | Automated apply with pre-flight; a health endpoint. |

**One change unlocks the most:** **B2 — removing the hard-coded company** raises Security *and* ERP Business Coverage.
**B3 — one access contract taking `BusinessContext`** is now DELIVERED for the four existing modules
(`IModuleAccessService`, Stage 1 Batch A), which is the prerequisite Search/AI was waiting on — but it scores nothing
until something uses it (model rule R3), and it does not yet exist for HR, Projects or Tasks (Stage 1 Batch C).

---

## Rules that bind the next stage

From [Platform-Maturity-Model.md](Platform-Maturity-Model.md) §2, repeated here because they are the ones most easily
skipped under delivery pressure:

- **Weights do not move** (§1). Re-weighting requires re-scoring every historical stage and publishing both series.
- **The calculation ships with the stage** (R10): the full ten-row table, the total, the delta per dimension, a prose
  line for every dimension that moved, and an explicit list of regressions and measurement corrections.
- **Regenerate the evidence first** — `scan-sql-manifest.ps1` then `scan-architecture.ps1` — and score from what they
  print, not from what the last document said.
- **Record what did not move** (R6/R5). A stage report that shows movement everywhere is not measuring.
- **Do not edit an earlier stage record** (R8). Publish a new one that references it.