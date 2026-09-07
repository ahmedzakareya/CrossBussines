# 17 — Test and Quality Map

## Scope
All automated tests, plus the in-application verification mechanisms that substitute for tests in the rest of the
codebase.

## Evidence
`CrossBuy.Tests/*` (7 files) · `dotnet test` output · `Controllers/Api/DevSeedController.cs` (the `*-test`
endpoints) · `BL/IntegrityCheckService.cs` · `BL/IntegrityCheckHostedService.cs`.

## Test projects

| Project | Type | Framework | Storage |
|---|---|---|---|
`CrossBuy.Tests` | xUnit 2.9.2 | net8.0 + `FrameworkReference Microsoft.AspNetCore.App` | SQLite in-memory (real transactions) + **opt-in SQL Server** |

**This is the only test project, and it was created during Platform Kernel slice 1.** Before that the solution
had no automated tests at all.

## Test inventory

| File | Tests | Covers |
|---|---|---|
`SmokeTests.cs` | 1 | the real 218-DbSet `CrossDbContext` materialises on the test provider |
`EntityRegistryTests.cs` | 5 | unknown-code rejection, TM-2 `TaskLinkResolver` compatibility, URL contract, company scoping, event-name convention |
`BusinessEventServiceTests.cs` | 10 | transaction rollback, commit-together, contract failure propagation, no-ambient-transaction refusal, dedup (per company), payload size limit, non-JSON payload, envelope wire contract |
`EventDispatchStoreTests.cs` | 9 | per-consumer independence, failed≠completed, error truncation, max-attempts exhaustion, ordering-independence, late-commit, single-ownership, stale reclaim, `CompletedAt` gating |
`TimelineProjectionTests.cs` | 11 | visibility tiers, own-actor rule, 403≠empty, company isolation, branch rule, legacy history, dedup, stable identity, unsupported entity, bilingual render, unknown-event degradation |
`Slice2RegistryTests.cs` | 9 | 3 pilot definitions, no duplicates, real scopes, `BuildUrl`, real routes, `PurchaseInvoice` search/resolve, cross-company rejection, alias non-canonicality, picker unchanged, slice-2 naming |
`Slice2ProducerTests.cs` | 9 | Customer created/updated/rollback, PurchaseInvoice events + payload hygiene, non-existent transitions refused, WO lifecycle order, rejected transition records nothing, real-service transition, payload size headroom |
`Slice2TimelineTests.cs` | 12 | pre-kernel history for all 3 pilots, undateable cancellation not fabricated, null-`CreatedAt` not dated, newest-first merge, dedup, stability, empty-safe, view-denial per pilot, company isolation per pilot |
`NotificationProjectionTests.cs` | 11 | one notification, idempotent redelivery **after read**, actor exclusion, recipient permission, no-audience no-op, timeline-only events, failure independence + retry, inventory audience, mapper scope |
`SqlServer/DispatchConcurrencyTests.cs` | 9 | **SQL Server only** — see below |

### Added in Stage 0 (Slice-003)

| File | Tests | Covers |
|---|---|---|
`Slice3ManufacturingSecurityTests.cs` | 13 | **Batch A.** All nine work-order write actions carry `[InvPerm("doc")]` + `[HttpPost]` + anti-forgery, and the five read actions carry `[InvPerm("read")]` — asserted off **compiled metadata**, so source formatting cannot fool them. A new work-order POST added without a permission fails the suite |
`Slice3JournalReversalTests.cs` | 8 | **Batch A.** `JournalEntry.Reversed` recorded inside `ReverseAsync`'s transaction, before commit; rollback-together; `Confidential` visibility; dedup key; payload contents |
`Slice3QuotationCommentTests.cs` | 11 | **Batch A.** `Quotation` registered; `DocCommentService` validates `EntityType` on **write only**, so historical rows keep loading |
`Slice3WorkerCompanyTests.cs` | 10 | **Batch A.** `IWorkerCompanyScope` iterates real companies with per-company failure isolation and **no fallback to company 1**; includes a source-level regression guard against `const int CompanyId = 1` returning |
`Slice3CommOutboxTests.cs` | 13 | **Batch A.** Email outbox state machine, `Attempts` counted exactly once by the claim, terminal `Sent`, stale reclaim, error truncation |
`Slice3EventMonitorTests.cs` | 30 | **Batch B.** Company isolation (including that a hand-edited `CompanyId` cannot widen scope); details indistinguishable from not-found; `Restricted`/`System` payload masking both ways; oversized and corrupt payloads; server paging stable across a shared timestamp; page-size cap; unregistered filter values return nothing; summary follows the filter; every retry refusal; attempts and error preserved; the override grants exactly one attempt; siblings untouched; the grid's flags match what the store accepts, row by row |
`Slice3RuntimeGateTests.cs` | 12 | **Batch B.** Runtime identity; the opt-out really opts out; a missing connection string and an unreachable database both fail **open with a named warning**; six concurrent callers evaluate the lease once; the reported mode never claims enforcement that is not happening |
`SqlServer/CommOutboxConcurrencyTests.cs` | 9 | **Batch A.** SQL Server only — email claim locking, terminal `Sent`, attachment non-duplication, company isolation, the deployed script's own constraints |
`SqlServer/EventMonitorRetryConcurrencyTests.cs` | 9 | **Batch B.** SQL Server only — the retry-versus-reclaim race proven **deterministically** by injecting the reclaim between the read and the write; two simultaneous retries apply once; a fresh claim is never stolen; a 25-round contention loop asserting the safety invariant |
`SqlServer/PlatformSchemaDeploymentTests.cs` | 9 | **Batch B.** SQL Server only — the deployment **scripts** themselves: slice 1 applied ×3 and slices 2–3 ×2, then every table, PK, FK, CHECK, unique and **filtered** index asserted from `sys.*`; `ISJSON` proven to reject and to be version-gated; dedup index unique per company; slice 2 proven to perform **no** backfill; no journal/stock/invoice table created |
`SqlServer/WorkerLeaseTests.cs` | 6 | **Batch B.** SQL Server only — two processes and one primary; the lease survives unrelated work (`LockOwner='Session'`); handover on release; six workers share one lock (verified with `APPLOCK_TEST` from an outside session); different lease names do not contend; the opt-out takes no lock |

**Totals: 230 tests. All pass. 0 failures.** Verified stable across **8 consecutive full runs** with
`CROSSBUY_TEST_SQL` set.
Without `CROSSBUY_TEST_SQL` the SQL Server tests report **skipped**, never silently passing.

### The honest limit

All 230 tests exercise the **platform layer and Stage 0's own work**. **Accounting, Inventory, POS, HR, CRM and
Projects still have zero tests**, and neither of the two sanctioned writers has a test of its accounting or costing
behaviour — `JournalEntryService` is tested only for the event it now emits. Test *volume* is not coverage, and the
maturity model scores this dimension accordingly ([Platform-Maturity-Baseline.md](Platform-Maturity-Baseline.md)).

### A flaky test found and fixed in Batch B

`CommOutboxConcurrencyTests.MaxAttempts_stops_retry_and_the_failure_reason_is_kept` failed roughly **half** the time.
Cause: with `RetryBackoffSeconds = 0`, eligibility compares `UpdatedAt` — stamped from the **application** clock by
`MarkFailedAsync` — against `SYSUTCDATETIME()` on the **server**. With a zero window, a server clock even
fractionally behind makes the row look like it lives in the future and the claim skips it. Production uses a
120-second backoff, so this was only ever a test artefact; two other tests carrying the same latent pattern were
fixed the same way, by using a real backoff and ageing the row explicitly.

**The mixed-clock comparison itself is recorded as a Stage 1 item:** `UpdatedAt` is written client-side and compared
server-side. With a 120 s backoff a few seconds of skew is irrelevant, but if the application and database servers
ever drift by minutes, retry and stale-claim timing shift with them.

## SQL Server integration-test map

| Test | Property proven |
|---|---|
`Only_one_worker_can_own_a_dispatch_row` | `UPDLOCK`+`READPAST` — worker two skips a held row and returns promptly |
`Concurrent_workers_partition_pending_rows_instead_of_colliding` | 4 workers / 10 rows, no duplicate claim, no `Attempts > 1` |
`An_event_that_commits_after_a_higher_EventId_is_not_lost` | out-of-order commit with explicit identity values |
`A_failed_notification_row_leaves_the_completed_timeline_row_alone` | per-consumer independence + 400-char truncation + `CompletedAt` gating |
`A_claim_abandoned_by_a_dead_worker_is_reclaimed_after_the_timeout` | stale-claim reclaim |
`A_Done_row_is_never_claimed_again_even_when_stale` | `Done` terminal; validates the filtered claiming index |
`Concurrent_recordings_with_the_same_DedupKey_create_exactly_one_event` | the real filtered unique index; loser returns the winner's id |
`An_event_and_its_dispatch_rows_roll_back_together_on_SQL_Server` | ADR-001 on the real engine |
`The_deployed_check_constraints_reject_values_outside_the_frozen_vocabularies` | `CK_BusinessEvents_Visibility`, `CK_BusinessEventDispatch_Status`, `FK_BusinessEventDispatch_Event` |

### Safety protections (verified)
- Requires `CROSSBUY_TEST_SQL`; **skips** otherwise.
- Creates `CrossBuyPlatformTest_<guid>` per run and drops it in teardown (verified: 0 scratch DBs remain).
- **Refuses** a connection string whose `Initial Catalog` is `CrossBuyDB`, `CrossBuyDB2` or `CrossBuy`.
- Runs the **real** `deploy/sql/platform_business_events.sql`, batch-split on `GO`, so the tests exercise
  shipped DDL rather than an EF approximation.
- One collection fixture → sequential execution (lock assertions require it).

### Test-host concession (recorded, not hidden)
`PlatformTestHost` executes `PRAGMA foreign_keys = OFF` on the SQLite database. The kernel tables sit at the end
of long FK chains (`Employee` → `AspNetUsers` → …, `Employee` → `Companies` → `CompanyTypes`); seeding them
would add dozens of unrelated rows. The FK that matters — `BusinessEventDispatch.EventId` →
`BusinessEvents.EventId` — **is** verified, on real SQL Server.

## Coverage map by module

| Module | Automated tests | In-app verification | Verdict |
|---|---|---|---|
**Platform Kernel** | **91** | — | **Strong** |
Accounting / AR / AP | 0 | `inv-test-integrity` (`IntegrityCheckService`) asserts AR/AP/GL/stock reconciliation + decimal precision parity; `/api/dev/*-test` endpoints | **Functional via integrity checks, not unit tests** |
Inventory | 0 | `inv-test-*` dev endpoints, integrity runs, `failedCount == 0` assertions | same |
Manufacturing | 0 (except WO events) | dev endpoints | Partial |
POS | 0 | `pos-9e-test` and siblings assert offline sync, shift close, variance JE, idempotency | Functional via dev endpoints |
Tasks | 0 | `tm1-test` … `tm9-test` | Functional via dev endpoints |
Projects | 0 | `p0-test` etc. | Functional via dev endpoints |
HR / Payroll / Recruitment | 0 | `r0-test`…`r3-test` | Partial |
CRM | 0 | some dev endpoints | Partial |
Communication | 0 | none | **Untested** |
Security / authorization | 0 | none | **Untested** |
Approvals (4 silos) | 0 | none | **Untested** |

**No coverage percentage is claimed** — no coverage tool was executed.

## The dev-endpoint mechanism (the de-facto test suite)
`DevSeedController` contains ~250 actions, many named `*-test`, each returning
`{ allPass, log[], failedCount }` after seeding data, exercising a flow and asserting invariants (including
`inv-test-integrity failedCount == 0`). They are **real, valuable verification** — they run against a live
database through the real services — but they are:
- **manually invoked** (HTTP GET with `key=seed123`), not run in CI;
- **not isolated** — they write to the working database;
- **not assertable by a build**; a regression is only found if someone browses the endpoint.

## Critical-flow verification matrix

| Flow | Automated | Dev endpoint | Integrity check | Gap |
|---|---|---|---|---|
Sales invoice create/edit | events only | ✅ | ✅ | no unit test of GL/stock math |
Purchase invoice create/edit | events only | ✅ | ✅ | same |
Journal reversal | ✖ | ◐ | ✅ | **no test; no event either** |
Stock movement + costing | ✖ | ✅ | ✅ | 15-transaction-site service untested by unit tests |
WO release/produce/complete | events only | ✅ | ✅ | authorization untested (there is none) |
POS settle / shift close | ✖ | ✅ | ✅ | — |
Payroll + settlement JE | ✖ | ◐ | ✅ | — |
**Leave / request approval chains** | ✖ | ✖ | ✖ | **completely untested** |
**Inventory approval + deferred execution** | ✖ | ✖ | ✖ | **completely untested — and it posts stock+GL** |
**Discount approval gate** | ✖ | ✖ | ✖ | untested |
Notification delivery | kernel path only | ✖ | ✖ | legacy producers untested |
Email send | ✖ | ✖ | ✖ | untested |
Company isolation | kernel path only | ✖ | ✖ | **no test anywhere else** |
Permission enforcement | kernel path only | ✖ | ✖ | **no test anywhere else** |

## Missing-test priority list

| # | Target | Why | Size |
|---|---|---|---|
1 | **Approval silos 1–3** | zero coverage, and silo 3 posts stock + GL from a `PayloadJson` command. This is the next thing to be replaced by an engine — tests are the safety net for that migration | Medium |
2 | **Company isolation** | the top data-integrity risk (08/13) with no test | Small |
3 | **Permission enforcement on write actions** | 254 unguarded writes and no test asserting which are intended | Medium |
4 | `JournalEntryService.ReverseAsync` | the most audit-relevant untested operation | Small |
5 | `StockService` costing math | 15 transaction sites, integrity-checked but not unit-tested | Large |
6 | Email outbox behaviour | once a dispatcher exists | Small |

## Gaps
- Only the Platform Kernel has automated tests.
- No CI integration for the dev endpoints.
- No coverage tooling.
- No test of any authorization or isolation rule outside the kernel.

## Risks
| Risk | Severity |
|---|---|
Approval silos untested while being the migration target | **High** |
`StockService` (highest blast radius) has no unit tests | High |
Dev test endpoints write to the working database | Medium |
No regression gate on 1002 actions | Medium |

## Dependencies
09, 13, 14, 21.

## Recommendations
1. Write approval-silo tests **before** designing the engine — they define the behaviour to preserve.
2. Add a company-isolation test class (cheap, high value).
3. Consider converting the highest-value `*-test` dev endpoints into xUnit integration tests against the
   opt-in scratch-database fixture that already exists.

---

## Stage 1 Batch C — test additions (2026-08-04)

**Suite: 606 passed / 0 failed / 0 skipped on SQL Server** (564 passed / 42 skipped on SQLite alone). **+114** tests.

| File | Tests | Covers |
|---|---|---|
| `BatchCAccessServiceTests` | 70 | the role directory (company intersection, active/inactive, future/expired, branch, unknown scope, unsupported principal, bootstrap-open per company **and** scope); the six shared gates across all four services; HR self/confidential/cross-company/approver-chain/inactive; the cross-company hierarchy exclusion; Projects membership/ended-membership/budget-denied/project-row-company/billing delegation; Tasks assignee/creator/unrelated/reassign-stronger/linked-entity/casing/scope-agreement; Communication participant-only/group-management/cross-company/outbox/branch-publisher |
| `BatchCAdapterForwardingTests` | 41 | **one regression test per existing adapter** proving the target stays null and the action mapping is unchanged; the four new adapters forwarding their record id unchanged; a malformed target denying without asking the module; startup scope validation |

**A real bug found by these tests, not by review:** the Projects service honoured a `ProjectMembers` row's
denormalised `CompanyID` without verifying the **project's** company, so a row naming company 1 for a company-2
project granted access. Caught by `A_project_in_another_company_is_refused_even_with_a_membership_row` and fixed.

**Two disclosed gaps:** the `AccessScope` "no per-record call" demonstration test, and hub-level SignalR
authorization tests — delivery report §6.
