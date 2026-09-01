using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Data.SqlClient;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
    /// <summary>
    /// DEFECT B — the abandoned-claim reaper, proved against a REAL SQL Server.
    ///
    /// The portable (SQLite) tests in AbandonedClaimRecoveryTests pin the RULE. These pin the behaviour only
    /// the production statement can show:
    ///
    ///     UPDATE d ... FROM BusinessEventDispatch AS d WITH (ROWLOCK, READPAST, UPDLOCK)
    ///
    /// Two reapers running at once must skip each other's rows rather than block on them or classify one row
    /// twice — the same discipline ClaimPendingAsync already relies on, applied to the sweep that releases a
    /// claim abandoned on its final attempt.
    ///
    /// Skipped — not passed — when CROSSBUY_TEST_SQL is absent. See SqlServerFixture.
    /// </summary>
    [Collection(SqlServerCollection.Name)]
    public class AbandonedClaimReaperConcurrencyTests
    {
        private const string Timeline = BusinessEventConsumers.TimelineProjection;

        private readonly SqlServerFixture _sql;
        public AbandonedClaimReaperConcurrencyTests(SqlServerFixture sql) { _sql = sql; }

        private async Task ReadyAsync()
        {
            Skip.If(!_sql.Available, _sql.SkipReason);
            await _sql.ResetAsync();
        }

        /// A dispatch row in the exact shape the defect produced: Claimed, attempts exhausted, lease long expired.
        private async Task<long> SeedAbandonedAsync(int attempts = 5, int ageMinutes = 4320)
        {
            await using var connection = new SqlConnection(_sql.TestConnectionString);
            await connection.OpenAsync();

            await SqlServerFixture.InsertEventWithIdentityAsync(connection, null, null, EntityRegistry.SalesInvoice, 5);
            await using var idCommand = new SqlCommand("SELECT MAX(EventId) FROM BusinessEvents;", connection);
            var eventId = (long)(await idCommand.ExecuteScalarAsync())!;

            await SqlServerFixture.InsertDispatchAsync(connection, null, eventId, Timeline, BusinessEventDispatchStatus.Claimed);

            await using var age = new SqlCommand(
                @"UPDATE BusinessEventDispatch
                     SET Attempts = @attempts, UpdatedAt = DATEADD(minute, -@age, SYSUTCDATETIME())
                   WHERE EventId = @eventId AND Consumer = @consumer;", connection);
            age.Parameters.AddWithValue("@attempts", attempts);
            age.Parameters.AddWithValue("@age", ageMinutes);
            age.Parameters.AddWithValue("@eventId", eventId);
            age.Parameters.AddWithValue("@consumer", Timeline);
            await age.ExecuteNonQueryAsync();

            return eventId;
        }

        private async Task<(string Status, int Attempts)> StateAsync(long eventId)
        {
            await using var connection = new SqlConnection(_sql.TestConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT Status, Attempts FROM BusinessEventDispatch WHERE EventId=@e AND Consumer=@c;", connection);
            command.Parameters.AddWithValue("@e", eventId);
            command.Parameters.AddWithValue("@c", Timeline);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetString(0), reader.GetInt32(1));
        }

        // =========================================================================================

        [SkippableFact]
        public async Task The_production_statement_releases_an_abandoned_claim_the_claim_loop_cannot_reach()
        {
            await ReadyAsync();
            var eventId = await SeedAbandonedAsync();

            using var db = _sql.NewContext();
            var store = _sql.Store(db);

            // The defect, asserted on real SQL Server: `Attempts < MaxAttempts` excludes the row from every
            // branch of the claim statement, including its stale-claim branch.
            Assert.Empty(await store.ClaimPendingAsync(Timeline, 10));

            Assert.Equal(1, await store.ReleaseAbandonedClaimsAsync(Timeline));

            var state = await StateAsync(eventId);
            Assert.Equal(BusinessEventDispatchStatus.Failed, state.Status);
            Assert.Equal(5, state.Attempts);          // classified, not retried
        }

        [SkippableFact]
        public async Task Two_concurrent_reapers_release_the_row_exactly_once()
        {
            await ReadyAsync();
            var eventId = await SeedAbandonedAsync();

            using var reaperOneDb = _sql.NewContext();
            using var reaperTwoDb = _sql.NewContext();

            // Reaper one holds the row lock inside an open transaction — the exact contention READPAST exists
            // for. Reaper two must skip it and return promptly rather than block or double-classify.
            await using var heldTx = await reaperOneDb.Database.BeginTransactionAsync();
            int releasedByOne = await _sql.Store(reaperOneDb).ReleaseAbandonedClaimsAsync(Timeline);
            Assert.Equal(1, releasedByOne);

            int releasedByTwo = await _sql.Store(reaperTwoDb).ReleaseAbandonedClaimsAsync(Timeline);
            Assert.Equal(0, releasedByTwo);

            await heldTx.CommitAsync();

            // One row, one transition — not two, and not a row left mid-flight.
            var state = await StateAsync(eventId);
            Assert.Equal(BusinessEventDispatchStatus.Failed, state.Status);
            Assert.Equal(5, state.Attempts);
        }

        [SkippableFact]
        public async Task Reapers_racing_in_parallel_still_release_exactly_one_row_each_at_most_once()
        {
            await ReadyAsync();
            var eventId = await SeedAbandonedAsync();

            using var dbOne = _sql.NewContext();
            using var dbTwo = _sql.NewContext();
            using var dbThree = _sql.NewContext();

            var results = await Task.WhenAll(
                _sql.Store(dbOne).ReleaseAbandonedClaimsAsync(Timeline),
                _sql.Store(dbTwo).ReleaseAbandonedClaimsAsync(Timeline),
                _sql.Store(dbThree).ReleaseAbandonedClaimsAsync(Timeline));

            // Exactly one reaper may count the row. The others must see zero — never a second classification.
            Assert.Equal(1, results.Sum());

            var state = await StateAsync(eventId);
            Assert.Equal(BusinessEventDispatchStatus.Failed, state.Status);
            Assert.Equal(5, state.Attempts);
        }

        [SkippableFact]
        public async Task A_claim_a_worker_is_holding_right_now_is_never_released()
        {
            await ReadyAsync();

            // Fresh claim, attempts exhausted: the lease has NOT expired, so the row is a live claim.
            var eventId = await SeedAbandonedAsync(attempts: 5, ageMinutes: 0);

            using var db = _sql.NewContext();
            Assert.Equal(0, await _sql.Store(db).ReleaseAbandonedClaimsAsync(Timeline));

            var state = await StateAsync(eventId);
            Assert.Equal(BusinessEventDispatchStatus.Claimed, state.Status);
        }

        [SkippableFact]
        public async Task A_stale_claim_with_attempts_remaining_is_left_for_the_claim_loop_to_retry()
        {
            await ReadyAsync();
            var eventId = await SeedAbandonedAsync(attempts: 2, ageMinutes: 4320);

            using var db = _sql.NewContext();
            var store = _sql.Store(db);

            // Not the reaper's row: the claim path can and should retry it, with an attempt increment.
            Assert.Equal(0, await store.ReleaseAbandonedClaimsAsync(Timeline));

            var claimed = await store.ClaimPendingAsync(Timeline, 10);
            Assert.Single(claimed);
            Assert.Equal(3, claimed[0].Attempts);
        }

        [SkippableFact]
        public async Task A_released_row_can_be_replayed_by_the_operator_path_and_reach_Done()
        {
            await ReadyAsync();
            var eventId = await SeedAbandonedAsync();

            using var db = _sql.NewContext();
            var store = _sql.Store(db);

            await store.ReleaseAbandonedClaimsAsync(Timeline);

            var retry = await store.RetryAsync(
                (await PendingIdAsync(eventId)), BusinessContext.ForSystem(1), "post-recovery replay", elevatedOverride: true);
            Assert.True(retry.Success, retry.Message);

            // Re-queued and workable again — the whole point of making the stuck row terminal.
            var claimed = await store.ClaimPendingAsync(Timeline, 10);
            Assert.Single(claimed);

            await store.MarkDoneAsync(claimed[0].DispatchId);
            Assert.Equal(BusinessEventDispatchStatus.Done, (await StateAsync(eventId)).Status);
        }

        private async Task<long> PendingIdAsync(long eventId)
        {
            await using var connection = new SqlConnection(_sql.TestConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT ID FROM BusinessEventDispatch WHERE EventId=@e AND Consumer=@c;", connection);
            command.Parameters.AddWithValue("@e", eventId);
            command.Parameters.AddWithValue("@c", Timeline);
            return (long)(await command.ExecuteScalarAsync())!;
        }
    }
}
