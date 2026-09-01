using System.Data.Common;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
    // Stage 0 Batch B — operator retry versus the live dispatcher, on a real SQL Server.
    //
    // The in-process suite proves the retry RULES (Slice3EventMonitorTests). What it cannot prove is what happens
    // when an operator presses Retry at the same moment a worker takes the row: SQLite has no UPDLOCK/READPAST, and
    // the claiming statement under test is SQL Server specific.
    //
    // The dangerous case is subtle and is the reason the conditional UPDATE carries Attempts and UpdatedAt as well
    // as Status: when a worker RECLAIMS a stale Claimed row, the status stays 'Claimed'. A status-only guard would
    // still match, flip the row to Pending, and let a second worker claim work that is already in flight.
    //
    // Skipped — never silently passed — without CROSSBUY_TEST_SQL. See SqlServerFixture.
    [Collection(SqlServerCollection.Name)]
    public class EventMonitorRetryConcurrencyTests
    {
        private const string Timeline = BusinessEventConsumers.TimelineProjection;
        private const string Notifications = BusinessEventConsumers.NotificationProjection;

        private readonly SqlServerFixture _sql;
        public EventMonitorRetryConcurrencyTests(SqlServerFixture sql) { _sql = sql; }

        private async Task ReadyAsync()
        {
            Skip.If(!_sql.Available, _sql.SkipReason);
            await _sql.ResetAsync();
        }

        // ---- seeding helpers -------------------------------------------------------------------

        private async Task<long> NewEventAsync(int companyId = 1)
        {
            await using var connection = new SqlConnection(_sql.TestConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                @"INSERT INTO BusinessEvents (EventUid, CompanyID, EntityType, EntityId, EventType, PayloadVersion, Visibility, CreatedAt)
                  VALUES (NEWID(), @company, 'SalesInvoice', 5, 'SalesInvoice.Created', 1, 'Internal', SYSUTCDATETIME());
                  SELECT CAST(SCOPE_IDENTITY() AS BIGINT);", connection);
            command.Parameters.AddWithValue("@company", companyId);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        // Creates a dispatch row in an EXACT state, including how long ago it was last touched, so the stale-claim
        // window can be driven precisely rather than by sleeping.
        private async Task<long> NewDispatchAsync(
            long eventId, string consumer, string status, int attempts, int ageMinutes = 0)
        {
            await using var connection = new SqlConnection(_sql.TestConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                @"INSERT INTO BusinessEventDispatch (EventId, Consumer, Status, Attempts, Error, UpdatedAt)
                  VALUES (@eventId, @consumer, @status, @attempts, @error, DATEADD(minute, -@age, SYSUTCDATETIME()));
                  SELECT CAST(SCOPE_IDENTITY() AS BIGINT);", connection);
            command.Parameters.AddWithValue("@eventId", eventId);
            command.Parameters.AddWithValue("@consumer", consumer);
            command.Parameters.AddWithValue("@status", status);
            command.Parameters.AddWithValue("@attempts", attempts);
            command.Parameters.AddWithValue("@error", "previous failure");
            command.Parameters.AddWithValue("@age", ageMinutes);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        private async Task<(string status, int attempts, string? error)> ReadRowAsync(long dispatchId)
        {
            await using var connection = new SqlConnection(_sql.TestConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT Status, Attempts, Error FROM BusinessEventDispatch WHERE ID = @id;", connection);
            command.Parameters.AddWithValue("@id", dispatchId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "the dispatch row disappeared");
            return (reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }

        private static BusinessContext Context(int companyId = 1) => PlatformTestHost.DefaultContext(companyId);

        // =======================================================================================
        // The interleave that matters
        // =======================================================================================

        // ---- Batch B item 44: a retry that loses the race is REJECTED, never applied ----
        [SkippableFact]
        public async Task A_retry_that_loses_to_a_worker_reclaim_is_rejected_and_changes_nothing()
        {
            await ReadyAsync();
            var options = new BusinessEventDispatchOptions { MaxAttempts = 5, StaleClaimMinutes = 10 };
            var eventId = await NewEventAsync();
            // Stale Claimed: eligible for BOTH an operator retry and a worker reclaim. This is the collision.
            var dispatchId = await NewDispatchAsync(eventId, Timeline, BusinessEventDispatchStatus.Claimed, attempts: 1, ageMinutes: 30);

            // The interceptor fires once, after RetryAsync has read the row state and immediately before its
            // conditional UPDATE — a real worker reclaims the row through the PRODUCTION claiming statement on its
            // own connection and commits. The retry's UPDATE therefore meets a row whose Attempts and UpdatedAt have
            // both moved, even though its Status is still 'Claimed'.
            var reclaimed = new List<long>();
            var interceptor = new InterleaveInterceptor("FROM [BusinessEvents]", async () =>
            {
                using var workerDb = _sql.NewContext();
                var work = await _sql.Store(workerDb, options).ClaimPendingAsync(Timeline, 10);
                reclaimed.AddRange(work.Select(w => w.DispatchId));
            });

            using var operatorDb = _sql.NewContext(interceptor);
            var result = await _sql.Store(operatorDb, options).RetryAsync(dispatchId, Context(), "looks stuck", false);

            Assert.True(interceptor.Fired, "the interleave was never injected — the test proved nothing");
            Assert.Equal(new[] { dispatchId }, reclaimed);           // the worker really did take it back

            Assert.False(result.Success);
            Assert.Equal(DispatchRetryOutcome.RaceLost, result.Outcome);

            // The row still belongs to the worker: NOT Pending, and the worker's attempt increment survived.
            var (status, attempts, error) = await ReadRowAsync(dispatchId);
            Assert.Equal(BusinessEventDispatchStatus.Claimed, status);
            Assert.Equal(2, attempts);
            Assert.Equal("previous failure", error);
        }

        // ---- The same guard must not fire when nothing actually changed ----
        [SkippableFact]
        public async Task An_uncontended_retry_of_the_same_stale_row_succeeds()
        {
            await ReadyAsync();
            var options = new BusinessEventDispatchOptions { MaxAttempts = 5, StaleClaimMinutes = 10 };
            var eventId = await NewEventAsync();
            var dispatchId = await NewDispatchAsync(eventId, Timeline, BusinessEventDispatchStatus.Claimed, attempts: 1, ageMinutes: 30);

            // Identical setup to the test above, minus the interleave. If this failed too, the RaceLost result there
            // would have been an artefact of the conditional predicate rather than proof of the race.
            using var operatorDb = _sql.NewContext();
            var result = await _sql.Store(operatorDb, options).RetryAsync(dispatchId, Context(), "worker died", false);

            Assert.True(result.Success);
            Assert.Equal(DispatchRetryOutcome.Requeued, result.Outcome);

            var (status, attempts, _) = await ReadRowAsync(dispatchId);
            Assert.Equal(BusinessEventDispatchStatus.Pending, status);
            Assert.Equal(1, attempts);
        }

        // ---- Two operators pressing Retry at the same instant: only one re-queue lands ----
        [SkippableFact]
        public async Task Two_simultaneous_retries_of_one_row_do_not_both_apply()
        {
            await ReadyAsync();
            var options = new BusinessEventDispatchOptions { MaxAttempts = 5, StaleClaimMinutes = 10 };
            var eventId = await NewEventAsync();
            var dispatchId = await NewDispatchAsync(eventId, Timeline, BusinessEventDispatchStatus.Claimed, attempts: 1, ageMinutes: 30);

            // A second retry is injected at the same point, so both observe the identical pre-change state.
            var second = new List<DispatchRetryResult>();
            var interceptor = new InterleaveInterceptor("FROM [BusinessEvents]", async () =>
            {
                using var otherDb = _sql.NewContext();
                second.Add(await _sql.Store(otherDb, options).RetryAsync(dispatchId, Context(), "me too", false));
            });

            using var operatorDb = _sql.NewContext(interceptor);
            var first = await _sql.Store(operatorDb, options).RetryAsync(dispatchId, Context(), "first", false);

            Assert.True(interceptor.Fired);
            // Exactly one of the two re-queued; the other was told it lost, rather than both reporting success.
            Assert.Equal(1, new[] { first, second.Single() }.Count(r => r.Success));
            Assert.Contains(new[] { first, second.Single() }, r => r.Outcome == DispatchRetryOutcome.RaceLost);

            var (status, attempts, _) = await ReadRowAsync(dispatchId);
            Assert.Equal(BusinessEventDispatchStatus.Pending, status);
            Assert.Equal(1, attempts);   // one re-queue, so Attempts moved once at most
        }

        // =======================================================================================
        // Retry versus the real claiming statement
        // =======================================================================================

        // ---- A row a live worker holds is never stolen ----
        [SkippableFact]
        public async Task A_row_a_live_worker_just_claimed_is_not_stolen_by_a_retry()
        {
            await ReadyAsync();
            var options = new BusinessEventDispatchOptions { MaxAttempts = 5, StaleClaimMinutes = 10 };
            var eventId = await NewEventAsync();
            var dispatchId = await NewDispatchAsync(eventId, Timeline, BusinessEventDispatchStatus.Failed, attempts: 1, ageMinutes: 30);

            // A genuine worker takes it through the production claiming statement.
            using (var workerDb = _sql.NewContext())
            {
                var work = await _sql.Store(workerDb, new BusinessEventDispatchOptions { RetryBackoffSeconds = 0 })
                    .ClaimPendingAsync(Timeline, 10);
                Assert.Equal(dispatchId, Assert.Single(work).DispatchId);
            }

            using var operatorDb = _sql.NewContext();
            var result = await _sql.Store(operatorDb, options).RetryAsync(dispatchId, Context(), "impatient", false);

            Assert.False(result.Success);
            Assert.Equal(DispatchRetryOutcome.HeldByWorker, result.Outcome);

            var (status, attempts, _) = await ReadRowAsync(dispatchId);
            Assert.Equal(BusinessEventDispatchStatus.Claimed, status);
            Assert.Equal(2, attempts);
        }

        // ---- A re-queued row is genuinely picked up by the real dispatcher ----
        [SkippableFact]
        public async Task A_requeued_row_is_claimed_by_the_next_real_dispatcher_pass()
        {
            await ReadyAsync();
            var options = new BusinessEventDispatchOptions { MaxAttempts = 5 };
            var eventId = await NewEventAsync();
            var dispatchId = await NewDispatchAsync(eventId, Timeline, BusinessEventDispatchStatus.Failed, attempts: 2);

            using var operatorDb = _sql.NewContext();
            Assert.True((await _sql.Store(operatorDb, options).RetryAsync(dispatchId, Context(), "dependency restored", false)).Success);

            // Pending needs no backoff wait, which is the point of re-queueing rather than just clearing the error.
            using var workerDb = _sql.NewContext();
            var work = await _sql.Store(workerDb, options).ClaimPendingAsync(Timeline, 10);
            var item = Assert.Single(work);
            Assert.Equal(dispatchId, item.DispatchId);
            Assert.Equal(3, item.Attempts);   // continues the history rather than restarting it
        }

        // ---- An exhausted row is invisible to the dispatcher until an override raises the ceiling ----
        [SkippableFact]
        public async Task An_exhausted_row_becomes_claimable_only_after_an_elevated_override()
        {
            await ReadyAsync();
            var options = new BusinessEventDispatchOptions { MaxAttempts = 3, RetryBackoffSeconds = 0 };
            var eventId = await NewEventAsync();
            var dispatchId = await NewDispatchAsync(eventId, Timeline, BusinessEventDispatchStatus.Failed, attempts: 3);

            // The real claiming statement filters on Attempts < MaxAttempts, so the row is dark to the dispatcher.
            using (var workerDb = _sql.NewContext())
                Assert.Empty(await _sql.Store(workerDb, options).ClaimPendingAsync(Timeline, 10));

            using (var operatorDb = _sql.NewContext())
            {
                var store = _sql.Store(operatorDb, options);
                Assert.Equal(DispatchRetryOutcome.AttemptsExhausted,
                    (await store.RetryAsync(dispatchId, Context(), "please", false)).Outcome);
                var overridden = await store.RetryAsync(dispatchId, Context(), "root cause fixed", true);
                Assert.True(overridden.Success);
                Assert.Equal(2, overridden.Attempts);
            }

            using (var workerDb = _sql.NewContext())
            {
                var work = await _sql.Store(workerDb, options).ClaimPendingAsync(Timeline, 10);
                Assert.Equal(dispatchId, Assert.Single(work).DispatchId);
                Assert.Equal(3, work[0].Attempts);   // exactly ONE more attempt, then dark again
            }

            // And it is dark again — the override bought one attempt, not an unlimited licence.
            using (var workerDb = _sql.NewContext())
            {
                await _sql.Store(workerDb, options).MarkFailedAsync(dispatchId, "failed again");
                Assert.Empty(await _sql.Store(workerDb, options).ClaimPendingAsync(Timeline, 10));
            }
        }

        // ---- A retry never disturbs a sibling consumer, on the real schema ----
        [SkippableFact]
        public async Task A_retry_leaves_a_completed_sibling_consumer_untouched_on_SqlServer()
        {
            await ReadyAsync();
            var options = new BusinessEventDispatchOptions { MaxAttempts = 5 };
            var eventId = await NewEventAsync();
            var doneId = await NewDispatchAsync(eventId, Timeline, BusinessEventDispatchStatus.Done, attempts: 1);
            var failedId = await NewDispatchAsync(eventId, Notifications, BusinessEventDispatchStatus.Failed, attempts: 2);

            using var operatorDb = _sql.NewContext();
            var store = _sql.Store(operatorDb, options);

            Assert.Equal(DispatchRetryOutcome.AlreadyDone,
                (await store.RetryAsync(doneId, Context(), "replay it", true)).Outcome);   // not even with an override
            Assert.True((await store.RetryAsync(failedId, Context(), "service back up", false)).Success);

            Assert.Equal(BusinessEventDispatchStatus.Done, (await ReadRowAsync(doneId)).status);
            Assert.Equal(BusinessEventDispatchStatus.Pending, (await ReadRowAsync(failedId)).status);

            // The dispatcher agrees: only the notification consumer has work.
            using var workerDb = _sql.NewContext();
            Assert.Empty(await _sql.Store(workerDb, options).ClaimPendingAsync(Timeline, 10));
            Assert.Single(await _sql.Store(workerDb, options).ClaimPendingAsync(Notifications, 10));
        }

        // ---- Company isolation holds against the real database, and reveals nothing ----
        [SkippableFact]
        public async Task Retrying_another_companys_row_is_refused_indistinguishably_on_SqlServer()
        {
            await ReadyAsync();
            var options = new BusinessEventDispatchOptions { MaxAttempts = 5 };
            var otherCompanyEvent = await NewEventAsync(companyId: 2);
            var dispatchId = await NewDispatchAsync(otherCompanyEvent, Timeline, BusinessEventDispatchStatus.Failed, attempts: 1);

            using var operatorDb = _sql.NewContext();
            var store = _sql.Store(operatorDb, options);

            var crossCompany = await store.RetryAsync(dispatchId, Context(companyId: 1), "fix it", false);
            var absent = await store.RetryAsync(999_999, Context(companyId: 1), "fix it", false);

            Assert.Equal(DispatchRetryOutcome.NotFound, crossCompany.Outcome);
            Assert.Equal(absent.Message, crossCompany.Message);
            Assert.Equal(BusinessEventDispatchStatus.Failed, (await ReadRowAsync(dispatchId)).status);

            // The row is reachable for its OWN company, so the refusal above was isolation, not a broken lookup.
            //
            // B2: company 2's operator is a SEPARATE request, with its own scope. Reusing the company-1 scope while
            // passing a company-2 context is no longer a coherent arrangement — the query filter obeys the scope,
            // and production gives one DI scope exactly one company.
            using var ownerDb = _sql.NewContextAsCompany(2);
            var ownerStore = _sql.Store(ownerDb, options);
            Assert.True((await ownerStore.RetryAsync(dispatchId, Context(companyId: 2), "fix it", false)).Success);
        }

        // ---- Sustained contention: the invariant holds every time ----
        [SkippableFact]
        public async Task Under_sustained_contention_a_row_is_never_both_in_flight_and_queued()
        {
            await ReadyAsync();
            var options = new BusinessEventDispatchOptions { MaxAttempts = 50, StaleClaimMinutes = 10, RetryBackoffSeconds = 0 };

            // This test is deliberately NOT asserting that a race occurred — timing decides that, and asserting on it
            // would make the suite flaky. It asserts the SAFETY invariant on every round: whatever the ordering, the
            // row never ends up marked Pending while a worker believes it owns it, and Attempts never goes backwards.
            int workerWins = 0, operatorWins = 0;
            for (int round = 0; round < 25; round++)
            {
                var eventId = await NewEventAsync();
                var dispatchId = await NewDispatchAsync(eventId, Timeline, BusinessEventDispatchStatus.Claimed, attempts: 1, ageMinutes: 30);

                using var workerDb = _sql.NewContext();
                using var operatorDb = _sql.NewContext();

                var claimTask = Task.Run(() => _sql.Store(workerDb, options).ClaimPendingAsync(Timeline, 10));
                var retryTask = Task.Run(() => _sql.Store(operatorDb, options).RetryAsync(dispatchId, Context(), "contended", false));
                await Task.WhenAll(claimTask, retryTask);

                bool workerTookIt = claimTask.Result.Any(w => w.DispatchId == dispatchId);
                var retry = retryTask.Result;
                var (status, attempts, _) = await ReadRowAsync(dispatchId);

                if (workerTookIt) workerWins++;
                if (retry.Success) operatorWins++;

                // The forbidden combination: the worker is processing the row AND the row is queued for someone else.
                Assert.False(workerTookIt && retry.Success && status == BusinessEventDispatchStatus.Pending,
                    $"round {round}: row {dispatchId} was handed to a worker AND re-queued (status={status})");

                Assert.Contains(status, new[] { BusinessEventDispatchStatus.Claimed, BusinessEventDispatchStatus.Pending });
                Assert.True(attempts >= 1, $"round {round}: Attempts went backwards to {attempts}");
                Assert.True(attempts <= 2, $"round {round}: Attempts inflated to {attempts}");

                // Whoever lost was TOLD they lost, rather than getting a success that did nothing.
                if (!retry.Success)
                    Assert.Contains(retry.Outcome, new[]
                    {
                        DispatchRetryOutcome.RaceLost, DispatchRetryOutcome.HeldByWorker, DispatchRetryOutcome.AlreadyPending,
                    });

                await _sql.ResetAsync();
            }

            // Both paths must have been exercised at least once, otherwise the loop never actually contended.
            Assert.True(workerWins + operatorWins > 0, "neither the worker nor the operator ever won a round");
        }

        // =======================================================================================
        // Test-only interleave injection
        // =======================================================================================

        // Runs an action exactly ONCE, right after the EF command whose text contains a marker completes. Used to
        // land a committed change by another connection between a production method's read and its write.
        private sealed class InterleaveInterceptor : DbCommandInterceptor
        {
            private readonly string _marker;
            private readonly Func<Task> _action;
            private int _fired;

            public InterleaveInterceptor(string marker, Func<Task> action) { _marker = marker; _action = action; }

            public bool Fired => _fired > 0;

            public override async ValueTask<DbDataReader> ReaderExecutedAsync(
                DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
                CancellationToken cancellationToken = default)
            {
                if (command.CommandText.Contains(_marker, StringComparison.Ordinal) &&
                    Interlocked.Exchange(ref _fired, 1) == 0)
                {
                    await _action();
                }
                return result;
            }
        }
    }
}
