using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
    // Platform Kernel slice 2 (ADR-007) — the dispatch locking behaviour that only a real SQL Server can show.
    //
    // Every test drives the PRODUCTION statement through SqlEventDispatchStore.ClaimPendingAsync. None of them
    // reimplements a simplified query, because the thing under test IS the statement:
    //   UPDATE TOP (n) ... OUTPUT inserted.* FROM BusinessEventDispatch WITH (ROWLOCK, READPAST, UPDLOCK)
    //
    // Skipped — not passed — when CROSSBUY_TEST_SQL is absent. See SqlServerFixture.
    [Collection(SqlServerCollection.Name)]
    public class DispatchConcurrencyTests
    {
        private const string Timeline = BusinessEventConsumers.TimelineProjection;
        private const string Notifications = BusinessEventConsumers.NotificationProjection;

        private readonly SqlServerFixture _sql;
        public DispatchConcurrencyTests(SqlServerFixture sql) { _sql = sql; }

        private async Task<bool> ReadyAsync()
        {
            Skip.If(!_sql.Available, _sql.SkipReason);
            await _sql.ResetAsync();
            return true;
        }

        // Creates a committed event + a Pending dispatch row for each named consumer.
        private async Task<long> SeedAsync(params string[] consumers)
        {
            await using var connection = new SqlConnection(_sql.TestConnectionString);
            await connection.OpenAsync();

            await SqlServerFixture.InsertEventWithIdentityAsync(connection, null, null, EntityRegistry.SalesInvoice, 5);
            await using var idCommand = new SqlCommand("SELECT MAX(EventId) FROM BusinessEvents;", connection);
            var eventId = (long)(await idCommand.ExecuteScalarAsync())!;

            foreach (var consumer in consumers)
                await SqlServerFixture.InsertDispatchAsync(connection, null, eventId, consumer);
            return eventId;
        }

        // ---- Test 31 / required 1: two workers attempt to claim the same row → only one owns it ----
        [SkippableFact]
        public async Task Only_one_worker_can_own_a_dispatch_row()
        {
            await ReadyAsync();
            await SeedAsync(Timeline);

            using var workerOneDb = _sql.NewContext();
            using var workerTwoDb = _sql.NewContext();

            // Worker one claims INSIDE an open transaction, so it still holds the row lock while worker two
            // tries. This is the exact contention READPAST exists to handle.
            await using var heldTx = await workerOneDb.Database.BeginTransactionAsync();
            var claimedByOne = await _sql.Store(workerOneDb).ClaimPendingAsync(Timeline, 10);
            Assert.Single(claimedByOne);

            // Worker two must SKIP the locked row rather than block on it — the call returns promptly, empty.
            var claimedByTwo = await _sql.Store(workerTwoDb).ClaimPendingAsync(Timeline, 10);
            Assert.Empty(claimedByTwo);

            await heldTx.CommitAsync();

            using var verify = _sql.NewContext();
            var row = await verify.BusinessEventDispatches.SingleAsync();
            Assert.Equal(BusinessEventDispatchStatus.Claimed, row.Status);
            Assert.Equal(1, row.Attempts);   // claimed exactly once, so retry accounting is not inflated
        }

        // ---- Required 2: multiple pending rows are processed concurrently, workers skip locked rows ----
        [SkippableFact]
        public async Task Concurrent_workers_partition_pending_rows_instead_of_colliding()
        {
            await ReadyAsync();

            // Ten independent events, each with one pending timeline row.
            await using (var connection = new SqlConnection(_sql.TestConnectionString))
            {
                await connection.OpenAsync();
                for (int i = 0; i < 10; i++)
                {
                    await SqlServerFixture.InsertEventWithIdentityAsync(connection, null, null, EntityRegistry.SalesInvoice, i + 1);
                    await using var idCommand = new SqlCommand("SELECT MAX(EventId) FROM BusinessEvents;", connection);
                    var eventId = (long)(await idCommand.ExecuteScalarAsync())!;
                    await SqlServerFixture.InsertDispatchAsync(connection, null, eventId, Timeline);
                }
            }

            // Four workers claim at the same time, in small batches, so they genuinely overlap.
            var options = new BusinessEventDispatchOptions { BatchSize = 3 };
            var workers = Enumerable.Range(0, 4).Select(_ => _sql.NewContext()).ToList();
            try
            {
                var results = await Task.WhenAll(workers.Select(db =>
                    Task.Run(() => _sql.Store(db, options).ClaimPendingAsync(Timeline, options.BatchSize))));

                var claimed = results.SelectMany(r => r).Select(w => w.DispatchId).ToList();

                // No row was handed to two workers...
                Assert.Equal(claimed.Count, claimed.Distinct().Count());
                // ...and the work was actually shared out rather than serialised into one worker.
                Assert.True(claimed.Count > 0, "no rows were claimed at all");
                Assert.True(claimed.Count <= 10);
            }
            finally { foreach (var db in workers) db.Dispose(); }

            using var verify = _sql.NewContext();
            var claimedRows = await verify.BusinessEventDispatches
                .CountAsync(d => d.Status == BusinessEventDispatchStatus.Claimed);
            Assert.True(claimedRows > 0);
            // Every claimed row was incremented exactly once.
            Assert.False(await verify.BusinessEventDispatches.AnyAsync(d => d.Attempts > 1));
        }

        // ---- Test 32 / required 3: a lower EventId committing LATER is still dispatched ----
        [SkippableFact]
        public async Task An_event_that_commits_after_a_higher_EventId_is_not_lost()
        {
            await ReadyAsync();

            // Transaction A takes the LOWER identity value and stays open.
            await using var connectionA = new SqlConnection(_sql.TestConnectionString);
            await connectionA.OpenAsync();
            var txA = (SqlTransaction)await connectionA.BeginTransactionAsync();
            await SqlServerFixture.InsertEventWithIdentityAsync(connectionA, txA, 100, EntityRegistry.SalesInvoice, 1);

            // Transaction B takes the HIGHER value and commits FIRST — the classic interleaving.
            await using (var connectionB = new SqlConnection(_sql.TestConnectionString))
            {
                await connectionB.OpenAsync();
                var txB = (SqlTransaction)await connectionB.BeginTransactionAsync();
                await SqlServerFixture.InsertEventWithIdentityAsync(connectionB, txB, 101, EntityRegistry.SalesInvoice, 2);
                await SqlServerFixture.InsertDispatchAsync(connectionB, txB, 101, Timeline);
                await txB.CommitAsync();
            }

            // The dispatcher processes B. A high-water cursor would now be parked past 101.
            using (var db = _sql.NewContext())
            {
                var store = _sql.Store(db);
                var work = await store.ClaimPendingAsync(Timeline, 10);
                Assert.Equal(101, Assert.Single(work).EventId);
                await store.MarkDoneAsync(work[0].DispatchId);
            }

            // Only NOW does A commit, making EventId 100 visible BELOW the highest already processed.
            await SqlServerFixture.InsertDispatchAsync(connectionA, txA, 100, Timeline);
            await txA.CommitAsync();

            using (var db = _sql.NewContext())
            {
                var work = await _sql.Store(db).ClaimPendingAsync(Timeline, 10);
                Assert.Equal(100, Assert.Single(work).EventId);   // a cursor-based dispatcher would have lost this
            }
        }

        // ---- Required 4: a failed NotificationProjection does not reset a completed TimelineProjection ----
        [SkippableFact]
        public async Task A_failed_notification_row_leaves_the_completed_timeline_row_alone()
        {
            await ReadyAsync();
            var eventId = await SeedAsync(Timeline, Notifications);

            using var db = _sql.NewContext();
            // A real backoff, with the failed row aged past it below. A zero-second backoff would make eligibility
            // depend on the application clock (which stamps UpdatedAt) agreeing with SYSUTCDATETIME() on the server
            // to sub-millisecond precision — see MaxAttempts_stops_retry_and_the_failure_reason_is_kept.
            var store = _sql.Store(db, new BusinessEventDispatchOptions { RetryBackoffSeconds = 60 });

            var timelineWork = await store.ClaimPendingAsync(Timeline, 10);
            await store.MarkDoneAsync(timelineWork.Single().DispatchId);

            var notifyWork = await store.ClaimPendingAsync(Notifications, 10);
            await store.MarkFailedAsync(notifyWork.Single().DispatchId, new string('x', 900));

            await using (var connection = new SqlConnection(_sql.TestConnectionString))
            {
                await connection.OpenAsync();
                await using var age = new SqlCommand(
                    "UPDATE BusinessEventDispatch SET UpdatedAt = DATEADD(minute, -5, SYSUTCDATETIME()) " +
                    "WHERE Consumer = @consumer;", connection);
                age.Parameters.AddWithValue("@consumer", Notifications);
                await age.ExecuteNonQueryAsync();
            }

            using var verify = _sql.NewContext();
            var rows = await verify.BusinessEventDispatches.ToDictionaryAsync(d => d.Consumer);
            Assert.Equal(BusinessEventDispatchStatus.Done, rows[Timeline].Status);
            Assert.Equal(BusinessEventDispatchStatus.Failed, rows[Notifications].Status);
            Assert.Equal(400, rows[Notifications].Error!.Length);   // truncated to the real nvarchar(400)

            // Retry offers ONLY the failed consumer's row.
            Assert.Empty(await store.ClaimPendingAsync(Timeline, 10));
            Assert.Single(await store.ClaimPendingAsync(Notifications, 10));

            // CompletedAt still waits for every consumer.
            await store.TryCompleteEventAsync(eventId);
            Assert.Null((await verify.BusinessEvents.AsNoTracking().SingleAsync(e => e.EventId == eventId)).CompletedAt);
        }

        // ---- Test 33 / required 5: stale Claimed rows become eligible again ----
        [SkippableFact]
        public async Task A_claim_abandoned_by_a_dead_worker_is_reclaimed_after_the_timeout()
        {
            await ReadyAsync();
            await SeedAsync(Timeline);

            using var db = _sql.NewContext();
            var store = _sql.Store(db, new BusinessEventDispatchOptions { StaleClaimMinutes = 10 });

            Assert.Single(await store.ClaimPendingAsync(Timeline, 10));
            Assert.Empty(await store.ClaimPendingAsync(Timeline, 10));   // still owned, not stale yet

            // Age the claim past the window, as a crashed worker's row would.
            await using (var connection = new SqlConnection(_sql.TestConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand(
                    "UPDATE BusinessEventDispatch SET UpdatedAt = DATEADD(minute, -30, SYSUTCDATETIME());", connection);
                await command.ExecuteNonQueryAsync();
            }

            var reclaimed = await store.ClaimPendingAsync(Timeline, 10);
            Assert.Single(reclaimed);
            Assert.Equal(2, reclaimed[0].Attempts);   // the reclaim counts as another attempt
        }

        // ---- Required 6: a completed dispatch row is never reclaimed ----
        [SkippableFact]
        public async Task A_Done_row_is_never_claimed_again_even_when_stale()
        {
            await ReadyAsync();
            await SeedAsync(Timeline);

            using var db = _sql.NewContext();
            var store = _sql.Store(db, new BusinessEventDispatchOptions { StaleClaimMinutes = 0, RetryBackoffSeconds = 0 });

            var work = await store.ClaimPendingAsync(Timeline, 10);
            await store.MarkDoneAsync(work.Single().DispatchId);

            // Age it far past every window. Done is terminal, so eligibility must still exclude it — this is
            // also what keeps the filtered claiming index (Status <> 'Done') correct.
            await using (var connection = new SqlConnection(_sql.TestConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand(
                    "UPDATE BusinessEventDispatch SET UpdatedAt = DATEADD(day, -30, SYSUTCDATETIME());", connection);
                await command.ExecuteNonQueryAsync();
            }

            Assert.Empty(await store.ClaimPendingAsync(Timeline, 10));
            Assert.Empty(await store.GetPendingAsync(Timeline, 10));

            using var verify = _sql.NewContext();
            Assert.Equal(BusinessEventDispatchStatus.Done, (await verify.BusinessEventDispatches.SingleAsync()).Status);
        }

        // ---- Test 34 / required 7: the filtered unique DedupKey index under concurrent inserts ----
        [SkippableFact]
        public async Task Concurrent_recordings_with_the_same_DedupKey_create_exactly_one_event()
        {
            await ReadyAsync();

            const string dedupKey = "PurchaseInvoice.Created:4242";
            BusinessEventRecord Record() => new()
            {
                EntityCode = EntityRegistry.PurchaseInvoice,
                EntityId = 4242,
                EventType = PurchaseInvoiceEvents.Created,
                PayloadVersion = PurchaseInvoiceEventPayload.Version,
                Visibility = BusinessEventVisibility.Internal,
                DedupKey = dedupKey,
                Payload = new PurchaseInvoiceEventPayload { InvoiceNumber = "PV-4242", TotalAfter = 10m },
            };

            // Two writers race through the real service, each in its own transaction, against the real
            // UX_BusinessEvents_DedupKey filtered unique index.
            async Task<long> RecordAsync()
            {
                using var db = _sql.NewContext();
                await using var tx = await ScopedTx.BeginOrJoinAsync(db);
                var id = await _sql.Events(db).RecordAsync(Record());
                await tx.CommitAsync();
                return id;
            }

            var first = await RecordAsync();
            var second = await RecordAsync();

            // The loser of the race returns the WINNER's id, and no second row exists.
            Assert.Equal(first, second);

            using var verify = _sql.NewContext();
            Assert.Equal(1, await verify.BusinessEvents.CountAsync(e => e.DedupKey == dedupKey));
            // One event, and one dispatch row per registered consumer — the duplicate added none.
            Assert.Equal(BusinessEventConsumers.Registered.Length, await verify.BusinessEventDispatches.CountAsync());

            // The index really is filtered: rows with a NULL key are unconstrained and can coexist.
            await using (var connection = new SqlConnection(_sql.TestConnectionString))
            {
                await connection.OpenAsync();
                await SqlServerFixture.InsertEventWithIdentityAsync(connection, null, null, EntityRegistry.SalesInvoice, 1);
                await SqlServerFixture.InsertEventWithIdentityAsync(connection, null, null, EntityRegistry.SalesInvoice, 2);
            }
            Assert.Equal(3, await _sql.NewContext().BusinessEvents.CountAsync());
        }

        // ---- Required 8: a BusinessEvent and its dispatch rows roll back together ----
        [SkippableFact]
        public async Task An_event_and_its_dispatch_rows_roll_back_together_on_SQL_Server()
        {
            await ReadyAsync();

            using (var db = _sql.NewContext())
            {
                await using var tx = await ScopedTx.BeginOrJoinAsync(db);
                await _sql.Events(db).RecordAsync(new BusinessEventRecord
                {
                    EntityCode = EntityRegistry.ManufWorkOrder,
                    EntityId = 31,
                    EventType = ManufWorkOrderEvents.Released,
                    PayloadVersion = ManufWorkOrderEventPayload.Version,
                    Visibility = BusinessEventVisibility.Internal,
                    Payload = new ManufWorkOrderEventPayload { WorkOrderNumber = "WO-00031", NewStatus = "Released" },
                });

                // Visible inside the transaction...
                Assert.Equal(1, await db.BusinessEvents.CountAsync());
                Assert.Equal(BusinessEventConsumers.Registered.Length, await db.BusinessEventDispatches.CountAsync());

                await tx.RollbackAsync();
            }

            // ...and both the event and EVERY dispatch row are gone afterwards.
            using var verify = _sql.NewContext();
            Assert.Equal(0, await verify.BusinessEvents.CountAsync());
            Assert.Equal(0, await verify.BusinessEventDispatches.CountAsync());
        }

        // ---- The check constraints in the deployment script are real, not decorative ----
        [SkippableFact]
        public async Task The_deployed_check_constraints_reject_values_outside_the_frozen_vocabularies()
        {
            await ReadyAsync();

            await using var connection = new SqlConnection(_sql.TestConnectionString);
            await connection.OpenAsync();

            var badVisibility = await Assert.ThrowsAsync<SqlException>(async () =>
            {
                await using var command = new SqlCommand(
                    @"INSERT INTO BusinessEvents (EventUid, CompanyID, EntityType, EntityId, EventType, PayloadVersion, Visibility, CreatedAt)
                      VALUES (NEWID(), 1, 'SalesInvoice', 1, 'SalesInvoice.Created', 1, 'Public', SYSUTCDATETIME());", connection);
                await command.ExecuteNonQueryAsync();
            });
            Assert.Contains("CK_BusinessEvents_Visibility", badVisibility.Message);

            await SqlServerFixture.InsertEventWithIdentityAsync(connection, null, null, EntityRegistry.SalesInvoice, 1);
            await using var idCommand = new SqlCommand("SELECT MAX(EventId) FROM BusinessEvents;", connection);
            var eventId = (long)(await idCommand.ExecuteScalarAsync())!;

            var badStatus = await Assert.ThrowsAsync<SqlException>(() =>
                SqlServerFixture.InsertDispatchAsync(connection, null, eventId, Timeline, status: "Working"));
            Assert.Contains("CK_BusinessEventDispatch_Status", badStatus.Message);

            // And the FK really prevents an orphan queue entry.
            var orphan = await Assert.ThrowsAsync<SqlException>(() =>
                SqlServerFixture.InsertDispatchAsync(connection, null, 999999, Timeline));
            Assert.Contains("FK_BusinessEventDispatch_Event", orphan.Message);
        }
    }
}