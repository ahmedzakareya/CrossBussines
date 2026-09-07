# Slice 003 — Stage 0 Stabilization

> **Status: COMPLETE.** Batch A and Batch B are both implemented and verified.
> Stage 0 was reported as *Partial* after Batch A, as instructed, and is reported as **Complete** now.

**Delivery model:** two batches, separately reviewed.

**Batch A (sections 0–10 of this document):** email outbox dispatcher · `JournalEntry.Reversed` · Quotation
registration + DocComment validation · multi-company background workers · manufacturing read permissions and UI
gating · correction of the withdrawn manufacturing security finding.

**Batch B (section 11):** Business Event Monitor + guarded per-consumer retry · platform SQL verification on
disposable SQL Server databases · SQL deployment manifest · `PlatformSchemaHistory` and applied-script reporting ·
single-worker-process runtime control · checked-in evidence scanners and regenerated CSVs · the Platform Maturity
Model and its baseline · five ADRs · the SQL deployment runbook.

| | Batch A | Batch B | Stage 0 |
|---|---|---|---|
Tests | 91 → **164** | 164 → **230** | +139 |
Maturity | 44.00 → **47.50** | 47.50 → **50.20** | **+6.20** |
Gaps closed | A3, A5, A6, A7 (+ A1 withdrawn) | A8 withdrawn, B5, R4, R14 | 9 |
ADRs issued | 0 | **5** (008, 009, 012, 013, 016) | 5 |

---

## 0. The correction that reshaped this stage

Before implementing anything, Phase 1 inspection established that **the premise of the stage's first objective was
false**. All nine Manufacturing work-order write actions already carried `[InvPerm("doc")]` and
`[ValidateAntiForgeryToken]`. The prior discovery pass reported them as unguarded because its attribute scanner
could not read concatenated same-line attributes, and its `writes` heuristic had false positives and negatives in
both directions.

Full root cause, corrected measurements and the ranked Stage 1 backlog:
**[CORRECTION-001](../architecture/CORRECTION-001-Manufacturing-Permission-Finding.md)**.

Consequences for this slice:
- The manufacturing item became **defense in depth on the read path**, not an emergency write fix.
- `A1` and `R1` were withdrawn from the gap and risk registers.
- The security metric changed basis from a body heuristic to the **HTTP verb**, which is verifiable.
- The 267 genuinely unprotected mutating actions were recorded as the **Stage 1 security backlog** and
  deliberately **not** touched.

---

## 1. Email outbox dispatcher

**Problem.** `CommService.SendAsync` queued a `CommMessage` then transmitted it **synchronously inside the
request**. If SMTP failed the row became `Failed` and stayed `Failed` forever; if SMTP was unconfigured the row
stayed `Queued` forever. Nobody was told either way. The outbox columns (`Status`/`Attempts`/`Error`/`SentAt`)
already existed — only the drain did not. The Communication Hub design specified a retry dispatcher that was never
built.

**Design.** `CommMessage` keeps owning email-delivery state. Mail was **not** routed through
`BusinessEventDispatch`: that would put two unrelated lifecycles in one table and make "retry this email" mean
"replay this event". What *is* reused is the pattern proven by ADR-003/ADR-007 — claim by status, never a cursor,
atomic `UPDATE ... OUTPUT` with `UPDLOCK`/`READPAST`.

| Component | Role |
|---|---|
`ICommMessageDispatchStore` | the queue contract. No cursor, no `MAX(Id)`, no watermark |
`SqlCommMessageDispatchStore` | SQL Server claim + `MarkSent`/`MarkFailed`; portable conditional-update fallback for the SQLite test host (documented as **not** a production path) |
`CommMessageDispatcherHostedService` | polls, claims, transmits, records the outcome |
`CommMessageDispatchOptions` | `CommMessageDispatch` config section |
`ICommService.TransmitAsync` | **new** — performs the wire send only, touching no delivery state |

**The state-ownership split matters.** `TrySendAsync` (interactive path) still sends *and* owns its status
transition — unchanged behaviour. `TransmitAsync` is the wire send alone, so when the dispatcher uses it the store
is the single writer of `Status`/`Attempts`/`ClaimedAt`/`SentAt`/`Error`, and **`Attempts` is incremented exactly
once — by the claim.** Reusing `TrySendAsync` would have double-counted every attempt.

**Claim SQL (production path):**
```sql
UPDATE TOP (@take) m
   SET m.Status='Claimed', m.Attempts=m.Attempts+1, m.ClaimedAt=SYSUTCDATETIME(), m.UpdatedAt=SYSUTCDATETIME()
OUTPUT inserted.Id, inserted.CompanyID, inserted.Attempts
  FROM CommMessages AS m WITH (ROWLOCK, READPAST, UPDLOCK)
 WHERE m.DeletedAt IS NULL AND m.Attempts < @maxAttempts
   AND ( m.Status=@queued
      OR (m.Status=@failed  AND (m.UpdatedAt IS NULL OR m.UpdatedAt <= DATEADD(second,-@backoff,SYSUTCDATETIME())))
      OR (m.Status=@claimed AND (m.ClaimedAt IS NULL OR m.ClaimedAt <= DATEADD(minute,-@stale,SYSUTCDATETIME()))) );
```

**Behaviour.** Status vocabulary `Queued | Claimed | Sent | Failed` (`Claimed` is new).
`Sent` is terminal and never reclaimed. `Failed` retries after `RetryBackoffSeconds` until `MaxAttempts`, then
stays `Failed` **with its error** and is logged at **Error** level — never deleted, never silently dropped. A
`Claimed` row whose worker died is reclaimed after `StaleClaimMinutes`. All timestamps UTC. One DI scope per batch.
Cancellation leaves the row `Claimed` for stale reclaim.

**Two deliberate guards.** The dispatcher **skips the pass entirely when SMTP is unconfigured** — otherwise every
message would burn its `Attempts` budget against a server that cannot be reached and reach `MaxAttempts`
permanently. And startup never waits on SMTP (`InitialDelaySeconds`, then polling).

**Logging discipline.** Message id, company id, attempt counter and the SMTP error only. Never the body, the
recipient list, attachment bytes or the SMTP password.

**Attachments** are read at send time and never written, so a retry cannot duplicate them — verified on both
providers.

**Schema:** `deploy/sql/comm_outbox_slice_003.sql` — adds `ClaimedAt`, `UpdatedAt`, the filtered claim index
`IX_CommMessages_Dispatch` (`WHERE Status <> 'Sent'`, so sent mail leaves the index), and two CHECK constraints.
Additive, idempotent, and the status CHECK **skips itself with a printed warning** if pre-existing rows hold an
unexpected status rather than failing the deployment.

---

## 2. `JournalEntry.Reversed`

**Problem.** Reversal is the most audit-relevant operation in the system and left no durable trace beyond the two
entry rows.

`JournalEntry` and the event are now registered, and the producer sits inside `ReverseAsync`'s existing `ScopedTx`,
**before** `CommitAsync`, with **no** swallowing catch — so the event shares the fate of the mirror entry *and* the
original's status flip. Dedup-keyed (`JournalEntry.Reversed:{id}`), because the `Status == "Posted"` guard means a
posted entry can only be reversed once.

**Visibility = `Confidential`, not `Internal`.** The payload carries the entry **total** (Σ debits), a monetary
fact, so it sits behind the accounting `post` right via `AccountingPermissionAdapter`
(`ViewConfidential → post`). **This is the platform's first non-`Internal` event**, which means ADR-004's elevated
tiers are now exercised by real data rather than only by tests. The choice is enforceable — proven by a test that a
`View`-only caller cannot see a `Confidential` row while a `ViewConfidential` caller can.

**Payload** (`JournalEntryEventPayload`, v1): both entry ids and numbers, `OriginalSourceType`/`SourceId`,
`ReversalReason`, `OriginalAmount`, `ReversedAt`.
**Excluded by contract and asserted by test:** journal lines, account ids/codes, per-line debits/credits, cost
centres, projects, employees, credentials.

`SupportsTimeline` is **false** for `JournalEntry`: there is no journal-entry timeline screen, and enabling the flag
without one would make `ITimelineProjectionService` answer for a screen that does not exist. The presenter mapping
exists so the event renders wherever a journal timeline is later added.

**No notification mapping was added** — there is no existing business requirement for one, and inventing an
audience was explicitly out of scope.

---

## 3. Quotation registration + DocComment validation

`Quotation` registered: `/Inventory/QuotationDetails?id={id}`, `SupportsComments = true` (the widget is wired),
`SupportsSearch = true`, `SupportsTimeline = false` (no producer, no screen),
`PermissionScope = Inventory`, `ListedInRecordPicker = false` (never a TM-2 picker type — the picker's seven types
and order are test-pinned as unchanged).

**No compatibility mapping was needed, and that was verified rather than assumed.** `QuotationDetails.cshtml`
stored the literal `"Quotation"`, which is exactly the canonical PascalCase code. The other two wired screens store
`"SalesInvoice"` and `"PurchaseInvoice"` — also already canonical.

`DocCommentService.AddAsync` now validates through `IEntityRegistry`:
- unregistered code → `DocCommentEntityTypeException` → **HTTP 400** from `CommentsController` (a wiring error, not
  a server fault; the message is not echoed because it names internal registry state);
- registered but `SupportsComments = false` → rejected, because accepting it would create data no screen displays;
- non-positive entity id → rejected.

**Validation is WRITE-ONLY by design.** `ListAsync` still reads whatever is stored, so historical rows keep loading
even for a type later renamed or retired — verified by a test that seeds a deliberately unregistered legacy row and
asserts it still loads. **No DocComment data was migrated.**

---

## 4. Multi-company background workers

The brief named two workers. Inspection found **four**, all carrying `private const int CompanyId = 1`:
`IntegrityCheckHostedService`, `CrmReminderHostedService`, `TaskGeneratorHostedService`,
`TaskScheduleMatchHostedService`. All four were fixed.

`IWorkerCompanyScope` / `WorkerCompanyScope` is the single place a worker learns which companies to process.
`WorkerCompanyRunner.ForEachCompanyAsync` is the shared driver, so all four get identical semantics rather than four
near-copies:

- **failure isolation** — one company's exception is caught and logged with its id; the remaining companies still run;
- **no fallback to company 1** — an install with no companies processes nothing, and logs a warning;
- **cancellation** honoured between companies; shutdown propagates instead of being logged as a company failure;
- **one DI scope per company**, so one company's `DbContext` state cannot leak into the next;
- logs carry the company id only — never exception data that might contain business values.

**Honest limitation recorded:** `Companies` has **no `IsActive`/`IsDeleted`/`Status` column**, so "eligible" can only
mean "exists". `WorkerCompanyScope` is the one place to change if such a state is ever added. No eligibility rule
was invented.

**Unchanged:** every worker's schedule and startup delay.
**Still outstanding (Batch B / Stage 1):** these four workers still have **no distributed claiming**, so the
single-worker-process constraint remains. Multi-company is not multi-instance.

---

## 5. Manufacturing read permissions and UI gating

`[InvPerm("read")]` added to `WorkOrders`, `WorkOrdersData`, `WorkOrderItemPickData`, `NewWorkOrder`,
`WorkOrderDetails` — screens exposing work-order cost, WIP balance and component data that previously carried
class-level `[SessionValidation]` only.

**No behaviour change today**: `InventoryAccessService.CanAsync("read")` grants any authenticated user by the
module's own policy. The gate makes the requirement explicit and gives manufacturing one place to tighten when real
Manufacturing RBAC lands in Stage 1.

UI gating aligned with the server via `ViewBag.CanDoc` in `WorkOrderDetails.cshtml` (release/complete/cancel/produce
blocks and the draft edit form), `WorkOrders.cshtml` (the "New work order" button) and `NewWorkOrder.cshtml` (the
create form, replaced by a permission notice). **Hiding a button is never the control** — the server remains the
authority, and a direct POST by an unauthorized user is still rejected by `InvPermAttribute`.

**Write semantics were not touched.** `doc` remains `doc`; reads were **not** raised to `doc`, which would have
locked viewers out of screens they could previously open.

---

## 6. Files

**Created (11 production + 5 test):**
`BL/Comm/ICommMessageDispatchStore.cs` · `BL/Comm/SqlCommMessageDispatchStore.cs` ·
`BL/Comm/CommMessageDispatcherHostedService.cs` · `BL/Platform/WorkerCompanyScope.cs` ·
`Models/Platform/JournalEntryEventPayload.cs` · `deploy/sql/comm_outbox_slice_003.sql` ·
`docs/architecture/CORRECTION-001-Manufacturing-Permission-Finding.md` ·
`docs/platform/Slice-003-Stage-0-Stabilization.md`
Tests: `Slice3ManufacturingSecurityTests.cs` · `Slice3JournalReversalTests.cs` ·
`Slice3QuotationCommentTests.cs` · `Slice3WorkerCompanyTests.cs` · `Slice3CommOutboxTests.cs` ·
`SqlServer/CommOutboxConcurrencyTests.cs`

**Modified (17):** `Controllers/InventoryController.cs` · `Controllers/CommentsController.cs` ·
`BL/CommService.cs` · `BL/JournalEntryService.cs` · `BL/DocCommentService.cs` ·
`BL/Platform/EntityRegistry.cs` · `BL/Platform/BusinessEventTypes.cs` ·
`BL/Platform/TimelineEventPresenter.cs` · `BL/IntegrityCheckHostedService.cs` ·
`BL/CrmReminderHostedService.cs` · `BL/TaskGeneratorHostedService.cs` ·
`BL/TaskScheduleMatchHostedService.cs` · `Models/Context/Comm/CommMessage.cs` ·
`Models/Context/CrossDbContext.cs` · `Program.cs` · `appsettings.json` ·
`Views/Inventory/{WorkOrderDetails,WorkOrders,NewWorkOrder}.cshtml`
Docs: `13-Security-Authorization-Isolation.md` · `19-Current-Gaps.md` · `20-Architecture-Risks.md` ·
`21-Modernization-Roadmap.md` · `evidence/Endpoint-Inventory.csv` · `evidence/Permission-Coverage.csv`

---

## 7. Deployment order

```
1. Backup
2. deploy/sql/platform_business_events.sql            (slice 1 — STILL NOT APPLIED)
3. deploy/sql/platform_business_events_slice_002.sql   (slice 2 — STILL NOT APPLIED)
4. deploy/sql/comm_outbox_slice_003.sql                (this slice)
5. Application deployment
6. Smoke tests
```

**SQL before code**, as always: `RecordAsync` sits inside the business transaction with no swallowing catch, so a
missing table fails the operation that produced the event. All three scripts print `EXISTS`/`SKIPPED` on a second
run.

**`CrossBuyDB2` was NOT modified by this work.** No script was executed against it. The only database touched was
the integration suite's disposable `CrossBuyPlatformTest_<guid>`, created and dropped per run, and the fixture
refuses a connection string naming `CrossBuyDB`/`CrossBuyDB2`/`CrossBuy`.

---

## 8. Rollback

Independent per item:

| Item | Rollback |
|---|---|
Email dispatcher | remove the `AddHostedService<CommMessageDispatcherHostedService>()` line + the store registration. Columns are additive/nullable; `TrySendAsync` behaviour is unchanged, so the interactive path keeps working |
`JournalEntry.Reversed` | delete the `RecordAsync` block in `ReverseAsync`; optionally unregister `JournalEntry` |
Quotation validation | remove the registry validation block in `DocCommentService.AddAsync` (reads were never changed) and the `catch` in `CommentsController`; optionally unregister `Quotation` |
Workers | restore `private const int CompanyId = 1` and the single-company loop body in each of the four |
Manufacturing read gate | remove five `[InvPerm("read")]` attributes and the three `ViewBag.CanDoc` blocks |
Schema | additive only. If required: `DROP INDEX IX_CommMessages_Dispatch ON CommMessages;` then drop the two CHECK constraints and the two columns |

---

## 9. Known limitations

1. **Stage 0 is PARTIAL** — Batch B not started.
2. **Platform SQL slices 1 and 2 remain undeployed** to `CrossBuyDB2` (A2 / R3 still open). Verification on a
   disposable database is Batch B.
3. **The four legacy workers still have no distributed claiming.** Multi-company ≠ multi-instance; the
   single-worker-process constraint stands and is not yet documented in `DEPLOYMENT.md` (Batch B).
4. **267 mutating actions remain without a module permission** — the Stage 1 security backlog, deliberately
   untouched.
5. **`Companies` has no eligibility state**, so workers process every company that exists.
6. **`InvPerm("read")` grants any authenticated user today** — real tightening needs Manufacturing RBAC (Stage 1).
7. **`DocComment` validation is write-only**; historical rows are unvalidated and unmigrated by design.
8. **No DocComment or `CommMessage` backfill** was performed.
9. `JournalEntry.SupportsTimeline = false`, so the reversal event is recorded and renderable but not yet displayed
   anywhere.
10. The email dispatcher **skips the pass when SMTP is unconfigured**, so in that state messages accumulate as
    `Queued` — visible, retried once SMTP is configured, and by design rather than silently failing.

---

## 10. Related documents

`../architecture/CORRECTION-001-Manufacturing-Permission-Finding.md` ·
`PKS-001-Platform-Kernel-Specification.md` · `ADR-001` (in-transaction events, applied again here for reversal) ·
`ADR-002` (registry, extended with two codes) · `ADR-003`/`ADR-007` (the claim pattern reused by the mail outbox) ·
`ADR-004` (visibility — first `Confidential` producer) · `ADR-006` (notification delivery; email outbox is its
missing sibling) · `Slice-002-Kernel-Expansion.md`

---

# 11. Batch B

## 11.1 What was delivered

| # | Deliverable | Code |
|---|---|---|
1 | **Business Event Monitor** — server-paged, company-isolated, payload-safe operator screen over `BusinessEvents` × `BusinessEventDispatch` | `BL/Platform/BusinessEventMonitorService.cs`, `Models/Platform/BusinessEventMonitorModels.cs`, `Controllers/BusinessEventMonitorController.cs`, `Views/BusinessEventMonitor/{Index,_Rows,_Details}.cshtml`, `Models/PlatformOpsAttribute.cs` |
2 | **Guarded per-consumer retry** through `IEventDispatchStore` | `BL/Platform/IEventDispatchStore.cs`, `BL/Platform/SqlEventDispatchStore.cs` |
3 | **Single-worker-process control** — runtime identity, SQL Server session app-lock lease, startup logging, diagnostics | `BL/Platform/RuntimeInstance.cs`, wired into all 6 workers, `appsettings.json` `Runtime` section |
4 | **SQL deployment manifest** | `CrossBuy/deploy/scan-sql-manifest.ps1` → `deploy/sql/manifest.json` |
5 | **Applied-script tracking** | `deploy/sql/platform_schema_history.sql`, `CrossBuy/deploy/report-schema-history.ps1` |
6 | **Checked-in evidence scanner** | `CrossBuy/deploy/scan-architecture.ps1` → 5 regenerated CSVs |
7 | **Platform Maturity Model** | `../architecture/Platform-Maturity-Model.md`, `Platform-Maturity-Baseline.md`, `maturity/Stage-00{0,1,2}-*.md`, `evidence/Platform-Maturity-Metrics.csv` |
8 | **ADRs** | `ADR-008`, `ADR-009`, `ADR-012`, `ADR-013`, `ADR-016` |
9 | **Operator/deployment documentation** | `../deployment/SQL-Deployment-Runbook.md`, `../deployment/Single-Worker-Process.md`, `../operations/Business-Event-Monitor.md` |
10 | **Correction record** | `../architecture/CORRECTION-002-Evidence-Scanner-Defects.md` |

## 11.2 The two defects Batch B found in its own subject matter

Recorded here because both were real and both were found by building the thing, not by reviewing it.

**A read-then-write race in operator retry.** The conditional UPDATE originally matched on `Status` alone. When a
worker **reclaims a stale `Claimed` row** the status stays `Claimed` and only `Attempts`/`UpdatedAt` change, so a
status-only guard still matches — it would flip a row a worker had just taken back to `Pending` and let a second worker
claim work in flight. The guard now carries all three columns in one statement, and the race is proven by **injecting**
the reclaim between the read and the write through an EF command interceptor. ADR-008.

**A `DbType` inference bug that would have broken every retry.** ADO.NET infers plain `datetime` for an untyped
`DateTime` parameter, which rounds to ~3.33 ms. Compared against the `datetime2(7)` `UpdatedAt` column the equality
guard never matched, and **every** retry returned `RaceLost`. Caught by the SQL Server suite, not by the in-process
one — SQLite stores `DateTime` as text and round-trips exactly, so the in-process tests passed.

## 11.3 The flaky test

`SqlServer/CommOutboxConcurrencyTests.MaxAttempts_stops_retry_and_the_failure_reason_is_kept` — a **Batch A** test —
failed about half the time. Root cause: with `RetryBackoffSeconds = 0`, eligibility compares `UpdatedAt` (stamped from
the **application** clock by `MarkFailedAsync`) against `SYSUTCDATETIME()` on the **server**. A zero window makes the
outcome depend on sub-millisecond clock agreement between two machines.

Fixed by using a real backoff and ageing the row explicitly; two other tests with the same latent pattern were fixed
the same way. Suite stability then verified across **8 consecutive full runs**.

**The mixed-clock comparison itself is left in place and recorded as a Stage 1 item.** With the production 120-second
backoff a few seconds of skew is irrelevant; if the application and database servers ever drift by minutes, retry and
stale-claim timing drift with them.

## 11.4 Verification

| | |
|---|---|
Build | 0 errors (`-c TestRun`, because VS/IIS hold locks on `bin\Debug`) |
Tests | **230 / 230 passing**, stable across 8 consecutive runs with `CROSSBUY_TEST_SQL` |
SQL verification | disposable SQL Server databases only, created and dropped per run; the fixture **refuses** a connection string naming `CrossBuyDB`, `CrossBuyDB2` or `CrossBuy` |
Scratch databases remaining | **0** |
Executed against CrossBuyDB2 | **nothing.** Its state was read to establish what is and is not applied |

## 11.5 Known limitations (Batch B)

1. **`platform_business_events_slice_002.sql`, `comm_outbox_slice_003.sql` and `platform_schema_history.sql` are not
   deployed** to CrossBuyDB2. Slice 1 **is**. The first two are approved for controlled manual deployment after a full
   backup; that is a human action under the runbook.
2. **`PlatformOpsAttribute` inherits the bootstrap-open weakness.** On an install with no accounting roles configured,
   `AccountingAccessService.CanAsync("manage")` returns `true` and the monitor is reachable by any authenticated
   employee. Documented in the attribute, in ADR-009 and in doc 13.
3. **The single-worker lease fails open** when it cannot be evaluated. Deliberate, reasoned in ADR-013, and alarmed
   (Error log + red banner + diagnostics field) — but it means `RequireSingleWorkerProcess: true` is not an absolute
   guarantee.
4. **A standby process runs no background work at all.** The gate is per-process, not per-worker, so roles cannot be
   split across hosts yet.
5. **The single-worker control does not make the four non-atomic workers idempotent.** It removes the concurrency case,
   not the run-twice-in-sequence case.
6. **`platform_schema_history.sql` requires SQL Server 2016 SP1+** (`CREATE OR ALTER VIEW`). The same engine floor
   already applies to the kernel's `ISJSON` constraint.
7. **Two `deploy/sql` folders remain, with 4 colliding file names.** The manifest now *reports* the collision;
   consolidating the trees is a separate change with its own risk.
8. **No bulk retry, by design.** Re-queueing 400 rows requires 400 justified actions.
9. **No retention or archival policy for `BusinessEvents`.** Nothing is deleted; the table grows without bound. Owed as
   ADR-021.
10. **Nine of fourteen evidence CSVs were carried forward, not regenerated** — they involve judgement or were
    unaffected by the scanner defects. Listed explicitly in ADR-016.
11. **230 tests, all platform-layer.** Accounting, Inventory, POS, HR, CRM and Projects still have zero tests, and
    neither sanctioned writer has a test of its accounting or costing behaviour.

## 11.6 Related documents (Batch B)

`../architecture/CORRECTION-002-Evidence-Scanner-Defects.md` ·
`../architecture/Platform-Maturity-Model.md` · `../architecture/Platform-Maturity-Baseline.md` ·
`../architecture/maturity/Stage-002-Stage-0-Result.md` ·
`ADR-008-Operator-Dispatch-Retry.md` · `ADR-009-Platform-Operations-Authorization.md` ·
`ADR-012-Deployment-Manifest-And-Schema-History.md` · `ADR-013-Single-Worker-Process.md` ·
`ADR-016-Reproducible-Architecture-Evidence.md` ·
`../deployment/SQL-Deployment-Runbook.md` · `../deployment/Single-Worker-Process.md` ·
`../operations/Business-Event-Monitor.md`
