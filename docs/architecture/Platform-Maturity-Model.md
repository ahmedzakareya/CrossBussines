# Platform Maturity Model

**Status:** Mandatory. Introduced Stage 0 (Slice-003), Batch B.
**Applies to:** every future stage. The calculation is part of the stage template, not an optional appendix.

This model exists to answer one question with a number that can be argued with: **how much of an enterprise
platform does CrossBuy actually have today?** Not how much is designed, planned, or half-wired — how much is
demonstrably in use.

---

## 1. Dimensions and weights

The ten dimensions and their weights are **fixed**. They total 100.

| # | Dimension | Weight |
|---|---|---|
1 | ERP Business Coverage | **20** |
2 | Security and Company Isolation | **15** |
3 | Platform Kernel and Audit | **10** |
4 | Business Object Coverage | **10** |
5 | Task and Work Management | **10** |
6 | Workflow and Approvals | **10** |
7 | Communication and Collaboration | **10** |
8 | Search, AI and Intelligence | **7** |
9 | Testing and Reliability | **5** |
10 | Deployment and Operations | **3** |
| | **Total** | **100** |

### The weights do not move

**Weights are never adjusted between stages.** A model whose weights change is not a measurement, it is a
presentation: any stage could be made to look successful by re-weighting toward whatever it happened to touch. If a
weight is ever genuinely wrong, changing it requires re-scoring **every** historical stage on the new weights and
publishing both series side by side. Nothing less is acceptable.

---

## 2. Scoring rules

These are binding. Each one exists because it is a specific way this kind of score gets inflated.

**R1 — Evidence or zero.** Every score cites a file, a table, a CSV row, a test, or a screen. A dimension with no
citable evidence scores 0, not "probably fine".

**R2 — Shared infrastructure does not count until it is wired.** The existence of a component, a service, a base
class or a partial view is **not** coverage. It counts when a **real screen or a real business flow uses it**.
`_DocEventTimeline` exists; it is wired to 4 objects out of 58, so Business Object Coverage scores against 4, not
against "the timeline exists".

**R3 — No points for planned, designed, or in-progress work.** A design document, an ADR, a TODO, a scaffolded
interface and a merged-but-undeployed schema all score 0 for the capability they describe. What is deployed and
usable scores; what is intended does not.

**R4 — Code merged but its schema not deployed scores as partial, and the gap is named.** This is the honest middle
of R3. `NotificationProjectionConsumer` resolves click-through through `Notifications.EntityType`; the code is
complete and tested, and the column is **not** in CrossBuyDB2. That is worth something (the work is done and
verified) and it is not worth full marks (nobody can use it). Such cases are scored down and the missing deployment
step is named in the stage record.

**R5 — Score the weakest link, not the best example.** One well-instrumented module does not lift a dimension. If
manufacturing writes are fully permission-gated and 306 mutating actions elsewhere are not, the security score
reflects the 306.

**R6 — Regressions are recorded explicitly, with their cause.** A dimension that goes down says so, in its own row,
with the reason. A stage that improves eight dimensions and breaks one has broken one.

**R7 — A measurement correction is not a regression, but it must be disclosed as loudly as one.** When corrected
evidence makes a number worse — as CORRECTION-002 did, moving the unguarded mutating-action count from 267 to 306 —
the stage record states the old figure, the new figure, and why. The system did not get worse; what we knew got
better. Hiding that is the same dishonesty as hiding a regression.

**R8 — History is immutable.** Stage records under `maturity/` are never edited after publication. A later
correction is a **new** record that references the old one. The series must remain readable as what was believed at
each point.

**R9 — Partial credit is quantised to 10-point steps.** A dimension scores one of **0, 10, 20 … 100** percent of its
weight. The **even multiples of 20** are the six levels in §3. The **odd steps (10, 30, 50, 70, 90) are half-levels**
and mean exactly one thing: *every criterion of the level below is met, and some — named explicitly — of the level
above.* A half-level with no named partial is not allowed.

Two reasons for this granularity rather than levels alone. Whole levels only (0/20/40/…) are too coarse to show a
stage of real work: Batch A improved four dimensions without moving any of them a full level, and a scale that
reports that as "no change" is not measuring. Free precision ("63%") is too fine: it invites arguing the decimal
instead of the evidence. Half-levels force the improvement to be *named* to be counted.

**R10 — The calculation ships with the stage.** Every future stage delivery report contains the full ten-row table,
the total, the delta from the previous stage, and a line per changed dimension explaining what moved it. A stage
report without it is incomplete.

---

## 3. Level rubric

Each dimension is scored on the same six levels. Percentages are of that dimension's weight.

| Level | % | General meaning |
|---|---|---|
**0 — Absent** | 0 | nothing exists, or only a design document exists |
**1 — Present** | 20 | something exists and works for one narrow case; not a platform capability |
**2 — Partial** | 40 | works for several real cases; inconsistent, per-module, or with a known structural gap |
**3 — Established** | 60 | works consistently across most of the system with a single mechanism |
**4 — Governed** | 80 | consistent, enforced (not merely available), and covered by tests |
**5 — Complete** | 100 | consistent, enforced, tested, observable, and documented for operators |

### Per-dimension criteria

**1. ERP Business Coverage (20)** — the breadth and depth of real business function that posts real GL and stock.
- 20: one module usable end to end.
- 40: several modules, but core financial closure (fiscal periods, multi-currency, fixed assets) incomplete.
- 60: the main ERP surfaces exist and post correctly through the two sanctioned writers; multi-company is nominal.
- 80: as above **plus** genuine multi-tenancy (no hard-coded company) and consolidated reporting.
- 100: as above plus budgeting, procurement-to-pay with approvals, and period-close controls.

**2. Security and Company Isolation (15)** — measured on **mutating** actions and on tenancy enforcement.
- 20: authentication only; authorization ad hoc.
- 40: a permission mechanism exists and covers a minority of mutating actions; tenancy is manual.
- 60: most mutating actions gated by a single mechanism; `CompanyID` enforced centrally (e.g. EF query filters).
- 80: as above plus no hard-coded company anywhere, one access contract, and security regression tests.
- 100: as above plus authorization at the data layer, audited access to sensitive reads, and no unguarded asset URLs.

**3. Platform Kernel and Audit (10)** — durable business-fact recording and its fan-out.
- 20: audit columns (`CreatedBy/At`) only.
- 40: an event log exists, written transactionally, with one consumer.
- 60: per-consumer dispatch with retry, visibility tiers, legacy reconstruction, more than one consumer.
- 80: as above plus operator observability of the queue and a guarded retry path.
- 100: as above plus deployment-state tracking, retention policy, and coverage of every correction primitive.

**4. Business Object Coverage (10)** — how many catalogued business objects are first-class platform citizens
(registry code + timeline + comments + notifications addressable by entity).
- 20: fewer than 20% registered; a handful with timeline.
- 40: ≥ 40% registered, timeline on the primary documents of each module.
- 60: ≥ 60% registered with timeline, comments and entity-addressed notifications.
- 80: ≥ 80%, plus object-scoped files and relations.
- 100: the catalogue is the registry; no significant object is outside it.

**5. Task and Work Management (10)**
- 20: a task table and a list screen.
- 40: assignment, due dates, links to documents, timesheets.
- 60: as above plus automation/scheduling and cost/billing roll-up.
- 80: as above plus one unified work inbox across every module, and tasks as registry citizens.
- 100: as above plus capacity/workload planning and SLA tracking.

**6. Workflow and Approvals (10)**
- 20: hard-coded approval on one document type.
- 40: several independent approval mechanisms (silos); a read-only unified inbox.
- 60: one engine, configurable steps, used by more than one module.
- 80: as above plus delegation, escalation, audit of every decision, and no silo left.
- 100: as above plus conditional routing, parallel branches, and SLA/escalation policy.

**7. Communication and Collaboration (10)**
- 20: email or notifications, standalone.
- 40: several channels (notifications, chat, email, announcements, calendar, files), mostly standalone.
- 60: the main channels are **entity-addressable** and reach business objects.
- 80: as above plus reliable delivery (retry, dead-letter visibility) and object-attached files.
- 100: as above plus mentions/followers as first-class, and a knowledge base.

**8. Search, AI and Intelligence (7)**
- 20: screen-local search; no cross-entity search. AI absent or a fixed report.
- 40: cross-entity search over the registry, exposed in a real UI; AI over aggregates.
- 60: as above plus per-object AI context assembled under permission filtering.
- 80: as above plus semantic/vector retrieval and grounded answers with citations.
- 100: as above plus agentic actions under the same authorization contract.

**9. Testing and Reliability (5)**
- 20: a few tests around one subsystem.
- 40: the platform layer tested, including real-database concurrency; business modules untested.
- 60: the financial writers and the main business flows have regression tests.
- 80: as above plus CI running them on every change.
- 100: as above plus coverage gates, load/soak testing, and failure-injection.

**10. Deployment and Operations (3)**
- 20: scripts exist; ordering and applied-state are tribal knowledge.
- 40: a manifest with ordering and an idempotency verdict per script.
- 60: as above plus applied-state tracking, a runbook with smoke tests, and single-process worker control.
- 80: as above plus automated apply with pre-flight checks and health endpoints.
- 100: as above plus zero-downtime deploys and automated rollback verification.

---

## 4. How a stage record is produced

1. Regenerate the mechanical evidence:
   ```powershell
   powershell -NoProfile -File CrossBuy/deploy/scan-sql-manifest.ps1
   powershell -NoProfile -File CrossBuy/deploy/scan-architecture.ps1
   ```
2. Run the full test suite and record the count and the pass state.
3. Score each of the ten dimensions against §3, citing evidence (R1).
4. Compute weighted score = Σ (level % × weight).
5. Write `maturity/Stage-NNN-<name>.md` with: the ten-row table, the total, the delta per dimension, a prose line
   for every dimension that moved, and an explicit list of regressions (R6) and measurement corrections (R7).
6. Append the row to `evidence/Platform-Maturity-Metrics.csv`.
7. **Do not edit any earlier record** (R8).

---

## 5. Series to date

| Stage | Record | Total | Δ |
|---|---|---|---|
000 | [Baseline (as-built, post-CORRECTION-001/002)](maturity/Stage-000-Baseline.md) | **44.00** | — |
001 | [Stage 0 Batch A (retrospective)](maturity/Stage-001-Batch-A-Result.md) | **47.50** | +3.50 |
002 | [Stage 0 complete (Batch A + B)](maturity/Stage-002-Stage-0-Result.md) | **50.20** | +2.70 |
003 | [Stage 1 Batch A](maturity/Stage-003-Stage-1-Batch-A-Result.md) | **52.70** | +2.50 |
004 | [Stage 1 Batch B](maturity/Stage-004-Stage-1-Batch-B-Result.md) | **54.20** | +1.50 |
005 | [Stage 1 Hotfix A.1 (Accounting API security)](maturity/Stage-005-Hotfix-A1-Result.md) | **54.20** | 0.00 |
006 | [Stage 1 Batch C (missing module access services)](maturity/Stage-006-Stage-1-Batch-C-Result.md) | **54.20** | 0.00 |

Machine-readable: [`evidence/Platform-Maturity-Metrics.csv`](evidence/Platform-Maturity-Metrics.csv).

The current baseline for comparison is documented in
[Platform-Maturity-Baseline.md](Platform-Maturity-Baseline.md).

---

## 6. What this score is not

It is **not** a quality score, a code-health score, or a competitive comparison. A 50 does not mean the system is
50% good — CrossBuy posts real accounting for a real business, and the two-writer discipline in `JournalEntryService`
and `StockService` is stronger than a lot of what ships. It means **half of an enterprise *platform*** — the
cross-cutting layer that makes every business object observable, governed, searchable and workflow-capable — is in
place and in use.

The ERP is the strongest dimension and it is capped at 60% for one reason worth naming: multi-tenancy is nominal.
`DefaultCompanyId = 1` in eight controllers and `CompanyId = 1` in four access services mean a second company is not
really supported, which limits both the ERP and the Security dimensions until it is fixed.