# ADR-007 — Dispatch locking is verified against a real SQL Server, on a disposable database

**Status:** Accepted, implemented in Platform Kernel slice 2.

## Context

`SqlEventDispatchStore` claims outbox work with one SQL-Server-specific statement:

```sql
UPDATE TOP (@take) d
   SET d.Status = 'Claimed', d.Attempts = d.Attempts + 1, d.UpdatedAt = SYSUTCDATETIME()
OUTPUT inserted.ID, inserted.EventId, inserted.Consumer, inserted.Attempts
  FROM BusinessEventDispatch AS d WITH (ROWLOCK, READPAST, UPDLOCK)
 WHERE <eligible>
```

`UPDLOCK` takes the update lock as the row is read, `READPAST` skips rows another worker holds instead of
blocking, and `OUTPUT` returns exactly the rows this caller won.

Slice 1's suite ran on SQLite, which has **none** of that. Its store therefore carries a portable fallback
(select-then-conditional-update), and slice 1 said so plainly in its known limitations: the two-worker test
proved *single-ownership sequentially*, not skip-locked concurrency. The statement that actually runs in
production had never been executed by a test.

The same gap hid three other properties: whether a lower `EventId` committing late is really picked up under
genuine transaction interleaving, whether the filtered unique `DedupKey` index behaves under a concurrent race,
and whether the `CHECK` constraints and FK in the deployment script exist at all — EF never creates them, so
SQLite runs never exercised them.

## Decision

**Add a SQL Server integration suite that runs the production statement, on a database it creates and drops
itself, and that reports SKIPPED rather than passing when no instance is configured.**

1. **Opt-in by environment variable.** `CROSSBUY_TEST_SQL` must hold a connection string to a SQL Server
   *instance*. Absent → every test reports **Skipped**, via `Xunit.SkippableFact`. It never silently passes and
   never falls back to another database. xUnit v2 can only skip statically with `[Fact(Skip=…)]`, which is why
   the small package was added: a suite that quietly passes without exercising anything is worse than no suite.
2. **A disposable database per run.** The fixture creates `CrossBuyPlatformTest_<guid>` and drops it in
   teardown. The connection string must name the instance, not a database to use.
3. **A refusal list.** A connection string whose `Initial Catalog` is `CrossBuyDB`, `CrossBuyDB2` or `CrossBuy`
   is rejected with a skip reason. Testing against real data is forbidden in this project, and here that rule is
   enforced in code rather than trusted to whoever sets the variable.
4. **Schema from the real deployment script.** The fixture reads `deploy/sql/platform_business_events.sql` from
   source (walking up from the test binary) and executes it batch-by-batch on `GO`. The tests therefore run
   against the DDL that actually ships — the filtered dedup index, the filtered claiming index, the `CHECK`
   constraints and the FK — not an EF-generated approximation. Slice 2's script is not run: it only alters
   `Notifications`, which no dispatch test touches.
5. **The real statement, always.** Every test drives `SqlEventDispatchStore.ClaimPendingAsync`, which takes the
   SQL Server path because `IsSqlServer()` is true. No test reimplements a simplified query.
6. **Sequential within one collection.** The tests assert on lock behaviour; parallel execution against the same
   tables would make them non-deterministic. One `ICollectionFixture` gives one database and ordered execution.

## Consequences

* The production claiming path is now executed by tests, including the two behaviours SQLite cannot express:
  a worker **skipping** a row another worker holds (rather than blocking), and four workers **partitioning** a
  backlog with no row claimed twice and no `Attempts` inflated past 1.
* Out-of-order commit is now reproduced properly: transaction A takes `EventId` 100 and stays open while B
  takes 101 and commits first; B is dispatched; only then does A commit, making 100 visible *below* the highest
  already processed. It is still claimed. This is the concrete failure a cursor-based dispatcher would have.
* CI without SQL Server still gets a green, honest run — 9 skipped, clearly named.
* The portable fallback in `SqlEventDispatchStore` remains, and remains documented as **not a production path**.
  It exists so the fast in-process suite can run at all.
* Cost: the suite needs a reachable instance and permission to `CREATE DATABASE`. Acceptable for an integration
  tier that is opt-in by construction.

## Alternatives rejected

* **Trust the SQLite suite.** Rejected: it cannot execute `UPDLOCK`/`READPAST`/`OUTPUT` at all, so the
  production statement was untested.
* **Run against `CrossBuyDB2` (the dev database).** Rejected outright — testing on live data is forbidden in
  this project, and these tests insert, claim, fail and delete rows. The fixture actively refuses it.
* **A shared, pre-created test database.** Rejected: state leaks between runs, and a failed teardown poisons the
  next run. A per-run database is cheap and self-cleaning.
* **Transactional rollback per test instead of a fresh database.** Rejected: the tests deliberately open
  *overlapping* transactions on separate connections, so an ambient rollback wrapper would change the very
  behaviour under test.
* **`ExecuteSqlRaw` / `FromSqlRaw` for claiming instead of raw ADO.** Rejected in slice 1 and unchanged: EF can
  wrap `FromSqlRaw` in a subquery, and `ExecuteSqlRaw` cannot return `OUTPUT` rows. Raw ADO is predictable.
* **Silently pass when the variable is missing.** Rejected: it would report coverage that does not exist.

## Verification

All nine pass against SQL Server; all nine skip cleanly without `CROSSBUY_TEST_SQL`.

| Test | Property |
|---|---|
| `Only_one_worker_can_own_a_dispatch_row` | `UPDLOCK`+`READPAST` — worker two skips a held row and returns promptly |
| `Concurrent_workers_partition_pending_rows_instead_of_colliding` | 4 workers, 10 rows, no duplicate claim, no `Attempts > 1` |
| `An_event_that_commits_after_a_higher_EventId_is_not_lost` | out-of-order commit with explicit identity values |
| `A_failed_notification_row_leaves_the_completed_timeline_row_alone` | per-consumer independence + 400-char truncation + `CompletedAt` waiting |
| `A_claim_abandoned_by_a_dead_worker_is_reclaimed_after_the_timeout` | stale-claim reclaim, `Attempts` incremented again |
| `A_Done_row_is_never_claimed_again_even_when_stale` | `Done` is terminal; validates the filtered claiming index |
| `Concurrent_recordings_with_the_same_DedupKey_create_exactly_one_event` | the real filtered unique index; loser returns the winner's id; NULL keys stay unconstrained |
| `An_event_and_its_dispatch_rows_roll_back_together_on_SQL_Server` | ADR-001 on the real engine |
| `The_deployed_check_constraints_reject_values_outside_the_frozen_vocabularies` | `CK_BusinessEvents_Visibility`, `CK_BusinessEventDispatch_Status`, `FK_BusinessEventDispatch_Event` |

Run with:

```
$env:CROSSBUY_TEST_SQL = "Server=.;Trusted_Connection=True;TrustServerCertificate=True"
dotnet test CrossBuy.Tests\CrossBuy.Tests.csproj -c TestRun
```
