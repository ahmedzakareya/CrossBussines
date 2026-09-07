# Stage 001 — Stage 0 Batch A (retrospective)

**Measured:** 2026-08-03, retrospectively, against the repository state **at the end of Batch A** (before Batch B).
**Previous record:** [Stage-000-Baseline.md](Stage-000-Baseline.md) — 44.00.
**Model:** [Platform-Maturity-Model.md](../Platform-Maturity-Model.md).

> **Immutable (R8).** Not edited after publication.

---

## Score

| # | Dimension | Weight | Level | % | Score | Δ |
|---|---|---|---|---|---|---|
1 | ERP Business Coverage | 20 | 3 | 60 | 12.00 | — |
2 | Security and Company Isolation | 15 | 2+ | **50** | **7.50** | **+1.50** |
3 | Platform Kernel and Audit | 10 | 3+ | **70** | **7.00** | **+1.00** |
4 | Business Object Coverage | 10 | 1 | 20 | 2.00 | — |
5 | Task and Work Management | 10 | 3 | 60 | 6.00 | — |
6 | Workflow and Approvals | 10 | 2 | 40 | 4.00 | — |
7 | Communication and Collaboration | 10 | 2+ | **50** | **5.00** | **+1.00** |
8 | Search, AI and Intelligence | 7 | 1 | 20 | 1.40 | — |
9 | Testing and Reliability | 5 | 2 | 40 | 2.00 | — |
10 | Deployment and Operations | 3 | 1 | 20 | 0.60 | — |
| | **Total** | **100** | | | **47.50** | **+3.50** |

Tests: **91 → 164**, all passing (146 passed + 18 skipped without `CROSSBUY_TEST_SQL`).

---

## What moved, and why

### 2 · Security and Company Isolation: 40 → 50 (+1.50)

Level 2 remains fully met; **two** of level 3's criteria are now partially met, which is what the half-step records.

**Background work is company-correct.** Four hosted services each carried `private const int CompanyId = 1`:
`IntegrityCheckHostedService`, `CrmReminderHostedService`, `TaskGeneratorHostedService`,
`TaskScheduleMatchHostedService`. All four now iterate real companies through `IWorkerCompanyScope` +
`WorkerCompanyRunner.ForEachCompanyAsync`, **with per-company failure isolation and no fallback to company 1**. Gap
A7 recorded "two workers"; it was four. This is the first genuine tenancy enforcement anywhere in the system.

**Security regression tests exist.** `Slice3ManufacturingSecurityTests.cs` (13 tests) asserts permission attributes
off **compiled metadata**, so no source-formatting change can fool them — and **any new work-order POST added later
without a permission attribute fails the suite**. That is level 4 behaviour (enforced, not merely available) applied
to one module.

**Not moved by this:** request-path tenancy (`DefaultCompanyId = 1` in 8 controllers, `CompanyId = 1` in 4 access
services) is unchanged; the 306 unguarded mutating actions are unchanged; the unguarded static file URLs (A4) are
unchanged. Defense-in-depth `[InvPerm("read")]` was added to five work-order read screens, and it changes no
behaviour today because `InventoryAccessService.CanAsync("read")` returns `true` for any authenticated user by that
module's own policy. It is scored as making the requirement explicit, not as new protection.

### 3 · Platform Kernel and Audit: 60 → 70 (+1.00)

**The only accounting-correction primitive now leaves a durable fact.** `JournalEntryService.ReverseAsync` records
`JournalEntry.Reversed` **inside its ambient `ScopedTx`, before commit, with no swallowing catch** — so the event and
the reversal commit or roll back together. `Visibility = Confidential`, dedup-keyed
`JournalEntry.Reversed:<id>`, `CompanyIdOverride` from the entry. `JournalEntryEventPayload` carries the original and
reversing entry ids and numbers, the source document, the reason, the amount and the timestamp. 8 tests.

This matters out of proportion to its size: **every** correction path in the system routes through `ReverseAsync` —
edit sales/purchase invoice, edit returns, cancel a paid POS order, reopen a fiscal year, FX-revaluation reverse,
manual reversal. Before Batch A, none of them was recorded as a business fact.

**Vocabulary closure.** `Quotation` registered in `IEntityRegistry`, and `DocCommentService.AddAsync` validates
`EntityType` through the registry **on write only** — historical rows keep loading. No data migration was needed: the
stored literal already equalled the canonical code, which was **verified, not assumed**.

**Against level 4:** still no operator observability of the dispatch queue and no guarded retry path. That is Batch B.

### 7 · Communication and Collaboration: 40 → 50 (+1.00)

**Email delivery became reliable, on the one channel that leaves the building.** Gap A3 was "failed emails are never
retried and are invisible". Batch A added `SqlCommMessageDispatchStore` (the same
`UPDATE … WITH (ROWLOCK, READPAST, UPDLOCK)` atomic-claim discipline as the kernel outbox),
`CommMessageDispatcherHostedService`, bounded retry with backoff, stale-claim reclaim, and permanent-failure logging
with the reason retained. `deploy/sql/comm_outbox_slice_003.sql` adds `ClaimedAt`, a filtered dispatch index and two
CHECK constraints. 22 tests (13 in-process + 9 on real SQL Server).

Two details worth recording because they were easy to get wrong:

- **`Attempts` is incremented exactly once, by the claim.** `TrySendAsync` (which owns status) was split from a new
  `TransmitAsync(int id, CancellationToken)` that performs the wire send and **touches no state**. Without that split
  the interactive path and the dispatcher would each count an attempt.
- **The dispatcher skips its entire pass when SMTP is not configured**, so a misconfiguration cannot burn `Attempts`
  against a wall.

**Against level 3:** the channels are still not entity-addressable. `CommMessage` still has no
`EntityType`/`EntityId`; chat, announcements, calendar and the file library are still standalone. This is reliability
(a level 4 criterion) arriving before addressability (a level 3 one) — the half-step is the honest way to record
that.

---

## What did not move, and why not

Recorded deliberately: a stage that reports movement everywhere is not measuring.

| Dimension | Why unchanged |
|---|---|
**1 · ERP Business Coverage** | Batch A added no business function. Nothing posts differently. |
**4 · Business Object Coverage** | `Quotation` makes it **9 of 58 (16%)** — still below level 2's 40% threshold, and Quotation gained comments, not a timeline. One registration is not coverage. |
**5 · Task and Work Management** | The task workers became company-correct (scored under Security), but no task capability changed. Still no unified work inbox, still not registry citizens. |
**6 · Workflow and Approvals** | Untouched. Four silos, read-only inbox, discount approval still leaves no record. |
**8 · Search, AI and Intelligence** | Untouched. |
**9 · Testing and Reliability** | 91 → 164 tests is real work, but the rubric's level 3 is "the financial writers and the main business flows have regression tests". All 73 new tests are platform/Stage-0 tests. **Accounting, Inventory, POS, HR, CRM and Projects still have zero.** No CI. Counting test *volume* as maturity is exactly the inflation R1/R5 exist to prevent. |
**10 · Deployment and Operations** | One new script (`comm_outbox_slice_003.sql`) written to the existing discipline. Still no manifest, no applied-scripts table, no runbook, no single-process worker control. Level 2 needs the manifest; it does not exist yet. |

---

## Regressions (R6)

**None.** No dimension decreased. No test was removed or weakened. No behaviour was tightened in a way that could
lock a user out of a screen they could previously open — specifically, work-order **read** actions were gated at
`read`, not raised to `doc`.

## Measurement corrections (R7)

**None in this stage.** The corrections that changed the evidence base
([CORRECTION-001](../CORRECTION-001-Manufacturing-Permission-Finding.md) during Batch A,
[CORRECTION-002](../CORRECTION-002-Evidence-Scanner-Defects.md) during Batch B) are already reflected in the
Stage 000 baseline, so this stage's deltas are not contaminated by them.

## Deployment reality at the end of Batch A (R4)

Two of Batch A's deliverables were **code-complete and verified but not deployed** to CrossBuyDB2, and were scored
down accordingly:

| Deliverable | State |
|---|---|
`comm_outbox_slice_003.sql` | not applied — `CommMessages.ClaimedAt` absent. The email dispatcher cannot run against production until it is. |
`platform_business_events_slice_002.sql` | not applied — `Notifications.EntityType` absent, so entity-addressed notifications do not work in production even though the consumer that uses them is live. |

Slice 1 **is** applied (applied externally; `BusinessEvents` and `BusinessEventDispatch` exist with 5 event rows),
which is what allows `JournalEntry.Reversed` — and therefore every reversal path — to work at all.