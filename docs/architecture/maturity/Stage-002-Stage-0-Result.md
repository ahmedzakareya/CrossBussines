# Stage 002 — Stage 0 complete (Batch A + Batch B)

**Measured:** 2026-08-03, at the completion of Batch B.
**Previous record:** [Stage-001-Batch-A-Result.md](Stage-001-Batch-A-Result.md) — 47.50.
**Model:** [Platform-Maturity-Model.md](../Platform-Maturity-Model.md).

> **Immutable (R8).** Not edited after publication.

---

## Score

| # | Dimension | Weight | Level | % | Score | Δ vs 001 | Δ vs baseline |
|---|---|---|---|---|---|---|---|
1 | ERP Business Coverage | 20 | 3 | 60 | 12.00 | — | — |
2 | Security and Company Isolation | 15 | 2+ | 50 | 7.50 | — | +1.50 |
3 | Platform Kernel and Audit | 10 | 4 | **80** | **8.00** | **+1.00** | +2.00 |
4 | Business Object Coverage | 10 | 1 | 20 | 2.00 | — | — |
5 | Task and Work Management | 10 | 3 | 60 | 6.00 | — | — |
6 | Workflow and Approvals | 10 | 2 | 40 | 4.00 | — | — |
7 | Communication and Collaboration | 10 | 2+ | 50 | 5.00 | — | +1.00 |
8 | Search, AI and Intelligence | 7 | 1 | 20 | 1.40 | — | — |
9 | Testing and Reliability | 5 | 2+ | **50** | **2.50** | **+0.50** | +0.50 |
10 | Deployment and Operations | 3 | 3 | **60** | **1.80** | **+1.20** | +1.20 |
| | **Total** | **100** | | | **50.20** | **+2.70** | **+6.20** |

Tests: **164 → 230**, all passing. Verified stable across 8 consecutive full runs with `CROSSBUY_TEST_SQL` set.

---

## What moved, and why

### 3 · Platform Kernel and Audit: 70 → 80 (+1.00) — a full level

Level 4 requires "operator observability of the queue **and** a guarded retry path". Both are now delivered, which is
why this is a whole level rather than a half-step.

**Observability.** The Business Event Monitor (`/BusinessEventMonitor`) is the first screen that reads the event
stream across modules: server-paged, company-isolated, thirteen filters, six summary cards computed over the same
predicate as the grid, one row per **(event, consumer)** because that is the unit an operator acts on. Restricted and
System payloads are **masked**, not merely un-linked — they are absent from the response unless the caller holds the
elevated right. Documented for operators in
[docs/operations/Business-Event-Monitor.md](../../operations/Business-Event-Monitor.md).

**Guarded retry.** `IEventDispatchStore.RetryAsync` is the only sanctioned way to re-queue work from outside the
dispatcher. `Done` is refused (replaying duplicates side effects), `Pending` is refused (a no-op that would look like
an action), a **fresh** `Claimed` is refused (a worker holds it), a **stale** `Claimed` is allowed, a reason is
mandatory, `Attempts` is never reset, and an elevated override moves the ceiling by exactly one instead of rewriting
history.

**A real race was found and closed while building this.** The conditional update originally matched on `Status` alone.
When a worker **reclaims a stale `Claimed` row**, the status stays `Claimed` and only `Attempts`/`UpdatedAt` change —
so a status-only guard would still match, flip a row a worker had just taken back to `Pending`, and let a second
worker claim work already in flight. The guard now carries `Status`, `Attempts` **and** `UpdatedAt` in one statement.
`SqlServer/EventMonitorRetryConcurrencyTests` proves it by **injecting the reclaim between the read and the write**
through an EF command interceptor, so the interleave is deterministic rather than hoped for.

**Deployment-state tracking** (level 4's third element, and part of level 5): `PlatformSchemaHistory` +
`vw_PlatformSchemaCurrent` + `report-schema-history.ps1`, recording script name, **SHA-256**, applied-at, applied-by,
application version, success and error — append-only, so a bad deploy stays readable.

**Against level 5:** no retention or archival policy for `BusinessEvents`; the registry still covers 9 of 58 objects,
so "every correction primitive" is only true for the ones that exist.

### 9 · Testing and Reliability: 40 → 50 (+0.50)

Level 2 remains met; two of level 3's characteristics are now partially present, and the half-step names them:

**The deployment scripts themselves are now tested**, not just the code that runs against them.
`SqlServer/PlatformSchemaDeploymentTests` (9 tests) applies slice 1 **three times** and slices 2 and 3 **twice** on a
disposable database, then interrogates `sys.*` to assert every table, PK, FK, CHECK, unique index and **filtered**
index the code depends on — including that `ISJSON` actually rejects non-JSON, that `PayloadVersion >= 1` and
`Attempts >= 0` actually reject, that the dedup index is unique **per company** and ignores NULL keys, that slice 2
performs **no backfill**, and that the platform scripts create no journal/stock/invoice table (so the DDL does not
breach the two-writers rule).

**Concurrency is tested by construction, not by luck.** The interceptor-injected interleave described above, plus a
25-round contention loop that asserts the safety invariant on every round and refuses to assert on timing.

**A pre-existing flaky test was found and fixed.** `CommOutboxConcurrencyTests.MaxAttempts_stops_retry_…` failed
roughly half the time. Cause: with `RetryBackoffSeconds = 0`, eligibility compares `UpdatedAt` — stamped from the
**application** clock by `MarkFailedAsync` — against `SYSUTCDATETIME()` on the **server**. With a zero window, a
server clock even fractionally behind makes the row look like it lives in the future and the claim skips it.
Production uses a 120-second backoff so this was only ever a test artefact; two other tests carrying the same latent
pattern were fixed the same way, by using a real backoff and ageing the row explicitly. **Suite stability was then
verified across 8 consecutive full runs.**

**Against level 3:** unchanged and still the honest limit — **Accounting, Inventory, POS, HR, CRM and Projects have
zero tests.** 230 tests that all exercise the platform layer do not make the financial writers tested. No CI.

### 10 · Deployment and Operations: 20 → 60 (+1.20) — two full levels

Level 3 requires a manifest, applied-state tracking, a runbook with smoke tests, **and** single-process worker
control. All four are delivered.

- **`deploy/sql/manifest.json`** — 110 scripts with dependency `rank`, SHA-256, detected encoding, guards found,
  mutations found, per-`GO`-batch unguarded list, and an idempotency verdict. Generated by
  `CrossBuy/deploy/scan-sql-manifest.ps1`, which **refuses to call an unread script safe**: it flags every mutating
  batch with no recognised guard, and a human records a verdict with evidence in a review ledger. Result: 88
  `guarded`, 7 `guarded-by-predicate` (read and judged), 14 `no-mutation`, 1 `not-deployable`, **0 unread**.
  It also reports the **4 file names that exist in both `deploy/sql` trees with different content** — so
  "apply `pos_setup.sql`" is now known to be an ambiguous instruction rather than a silent hazard.
- **Applied-state tracking** — `platform_schema_history.sql` and `report-schema-history.ps1`, which classifies every
  script as `applied` / `changed` / `failed` / `pending` / `missing` and exits 0/1/2/3 accordingly. `changed` is the
  finding a name-only log can never produce: *applied here, but the file has been edited since, so the database is
  running older DDL than the repository describes.*
- **[SQL-Deployment-Runbook.md](../../deployment/SQL-Deployment-Runbook.md)** — pre-flight with stop conditions, apply
  order, an explicit baseline procedure for CrossBuyDB2, seven smoke tests (including the reversal path, because that
  is the one coupled to the kernel schema), and a rollback section that states plainly that the only true rollback is
  the verified backup.
- **Single-worker-process control** — `Runtime:RequireSingleWorkerProcess` (default **true**), enforced by a SQL
  Server **session** application lock held for the process lifetime. All **6 of 6** workers now wait on the gate.
  Startup logging carries instance name, instance id, machine, PID, process start time, version, environment and the
  worker-safety mode; `GET /BusinessEventMonitor/Runtime` returns the same as JSON; the monitor renders it as a
  banner. Documented in [Single-Worker-Process.md](../../deployment/Single-Worker-Process.md).

**Against level 4:** no automated apply, no health endpoint beyond the diagnostics JSON, no pre-flight automation in
CI.

---

## What did not move, and why not

| Dimension | Why unchanged |
|---|---|
**1 · ERP Business Coverage** | Batch B added no business function. |
**2 · Security and Company Isolation** | Batch B added a screen with cross-module audit data and gated it correctly (`[PlatformOps]`, company isolation, payload masking, probe-resistant messages, an override that is **refused** rather than silently downgraded for a non-admin). Gating a **new** surface properly is not a system-wide improvement: the 306 unguarded mutating actions, the absent EF query filters, `DefaultCompanyId = 1`, the four access services and the unguarded static file URLs are all exactly as they were. Per R5, the dimension scores the weakest link. |
**4 · Business Object Coverage** | Still 9 of 58 registered, timeline on 4. |
**5 · Task and Work Management** | Untouched. |
**6 · Workflow and Approvals** | Untouched. Four silos remain. |
**7 · Communication and Collaboration** | The monitor makes a failed notification visible and retryable — that is scored under **Kernel observability** and is deliberately not double-counted here. No channel became entity-addressable, and `CommMessage` still has no `EntityType`. |
**8 · Search, AI and Intelligence** | Untouched. |

---

## Regressions (R6)

**None.** No dimension decreased.

Two changes altered existing behaviour and are recorded here because "no regression" has to mean something:

1. **All six background workers now wait on the worker gate.** On a single-process deployment (the normal case) this
   changes nothing. On a **multi-process** deployment the non-primary process now runs **no** background work where
   previously it ran all of it. That is the intended fix — the four non-atomic workers were duplicating their output —
   but it is a behaviour change, and if anyone was relying on two processes sharing the load, they were relying on
   duplication.
2. **`RetryAsync`'s conditional update is stricter than the code it replaced never had.** There was no operator retry
   before, so nothing regressed; noted because the strictness (`RaceLost` instead of a silent overwrite) will
   occasionally refuse an action a naive implementation would have accepted.

## Measurement corrections (R7)

**One, and it is significant.** [CORRECTION-002](../CORRECTION-002-Evidence-Scanner-Defects.md):

| Metric | Previously published | Corrected | Effect on the score |
|---|---|---|---|
Mutating actions | 362 | **381** | none — both are used as denominators only |
Mutating actions with **no** module permission | **267** | **306** | none |
Controller actions | 1012 | **1054** | none |
SQL scripts unsafe to re-apply | 9 | **0** | none |

**Why none of them changed a score.** The baseline was deliberately computed on the **corrected** figures (see
[Stage-000-Baseline.md](Stage-000-Baseline.md)), so the whole series is on one basis. Had the baseline used 267, this
stage would have had to report a fake "regression" from a number that was never true.

**The system did not get worse; what we knew got better.** The Stage 1 security backlog is 306, not 267. Of the 39
additional endpoints, 18 are `AdminController` actions that a four-file `partial` class hid from the scan, 20 are API
endpoints that a UI-focused scan never listed, and 1 is Batch B's own `BusinessEventMonitorController.Retry` (which is
class-gated, and is why the corrected table publishes **both** the action-level count of 306 and the any-level count
of 305 rather than quietly picking one).

The SQL correction moved in the **safe** direction: nine scripts believed unsafe to re-apply are in fact safe, so the
previous figure cost caution rather than creating exposure.

## Deployment reality (R4)

Unchanged from Batch A, and it caps three dimensions:

| Script | State | Consequence |
|---|---|---|
`platform_business_events.sql` (slice 1) | **applied** | reversal and the kernel work in production |
`platform_business_events_slice_002.sql` | **not applied** | entity-addressed notifications do not work in production although the consumer is live |
`comm_outbox_slice_003.sql` | **not applied** | the email dispatcher cannot run in production |
`platform_schema_history.sql` | **not applied** | the schema history is not yet recording anything in production |

Both of the first two are **approved for controlled manual deployment to CrossBuyDB2 after a full backup and script
review**, and **nothing in this work executed anything against CrossBuyDB2**. Its live state was read to establish
this table, and read only. Every SQL verification ran on disposable databases that were created and dropped;
**0 scratch databases remain**.

---

## Stage 0 verdict

**Complete.** Batch A and Batch B are both delivered and verified. Stage 0 was reported as *Partial* after Batch A as
instructed, and is reported as **Complete** now.

**Baseline → Stage 0 complete: 44.00 → 50.20 (+6.20).** Six of ten dimensions did not move, which is the honest shape
of a stabilization stage: Stage 0 was scoped to security, deployment and reliability, and it moved exactly those —
Kernel/Audit +2.00, Security +1.50, Deployment/Operations +1.20, Communication +1.00, Testing +0.50. It added no
business function, no workflow engine, no search and no AI, and the score says so.