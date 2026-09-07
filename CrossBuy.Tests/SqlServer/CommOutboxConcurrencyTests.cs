using CrossBuy.BL.Comm;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
    // Stage 0 (Slice-003) — email outbox locking, against a REAL SQL Server.
    //
    // WHY THIS EXISTS SEPARATELY: SqlCommMessageDispatchStore's claim is a SQL-Server-specific statement
    // (UPDLOCK + READPAST + OUTPUT in one UPDATE). SQLite has none of it, so the in-process suite can only prove
    // the state machine — not that two workers genuinely cannot send the same email. A duplicate send is visible
    // to a customer and cannot be undone, which is why this is verified on the real engine.
    //
    // Skipped — never silently passed — without CROSSBUY_TEST_SQL. The fixture creates and drops its own scratch
    // database and REFUSES a connection string pointing at CrossBuyDB / CrossBuyDB2 / CrossBuy.
    [Collection(SqlServerCollection.Name)]
    public class CommOutboxConcurrencyTests
    {
        private readonly SqlServerFixture _sql;
        public CommOutboxConcurrencyTests(SqlServerFixture sql) { _sql = sql; }

        private async Task ReadyAsync()
        {
            Skip.If(!_sql.Available, _sql.SkipReason);
            await _sql.EnsureCommOutboxAsync();
            await _sql.ResetCommOutboxAsync();
        }

        private async Task<int> QueueAsync(int companyId = 1)
        {
            await using var connection = new SqlConnection(_sql.TestConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(@"
INSERT INTO CommMessages (CompanyID, ToAddress, Subject, Body, Status, Attempts, Kind, Starred, CreatedAt)
VALUES (@c, 'customer@example.com', 'Invoice', '<p>body</p>', 'Queued', 0, 'New', 0, SYSUTCDATETIME());
SELECT CAST(SCOPE_IDENTITY() AS INT);", connection);
            command.Parameters.AddWithValue("@c", companyId);
            return (int)(await command.ExecuteScalarAsync())!;
        }

        private async Task AgeAsync(int id, string column, TimeSpan back)
        {
            await using var connection = new SqlConnection(_sql.TestConnectionString);
            await connection.OpenAsync();
            // Column name is from a fixed internal set, never user input.
            await using var command = new SqlCommand(
                $"UPDATE CommMessages SET {column} = DATEADD(second, @s, SYSUTCDATETIME()) WHERE Id = @id;", connection);
            command.Parameters.AddWithValue("@s", -(int)back.TotalSeconds);
            command.Parameters.AddWithValue("@id", id);
            await command.ExecuteNonQueryAsync();
        }

        // ---- One row is claimed by one worker only ----
        [SkippableFact]
        public async Task Only_one_worker_can_own_a_queued_message()
        {
            await ReadyAsync();
            await QueueAsync();

            using var dbA = _sql.NewContext();
            using var dbB = _sql.NewContext();

            // Worker A claims INSIDE an open transaction and keeps holding the row lock while B tries.
            await using var held = await dbA.Database.BeginTransactionAsync();
            var a = await _sql.CommStore.On(dbA).ClaimPendingAsync(10);
            Assert.Single(a);

            // READPAST means B SKIPS the locked row and returns promptly instead of blocking.
            var b = await _sql.CommStore.On(dbB).ClaimPendingAsync(10);
            Assert.Empty(b);

            await held.CommitAsync();

            using var verify = _sql.NewContext();
            var row = await verify.CommMessages.SingleAsync();
            Assert.Equal(CommMessageStatus.Claimed, row.Status);
            Assert.Equal(1, row.Attempts);   // claimed exactly once — no inflated retry budget from contention
        }

        // ---- Multiple workers partition queued rows ----
        [SkippableFact]
        public async Task Concurrent_workers_partition_the_queue_without_double_sending()
        {
            await ReadyAsync();
            for (int i = 0; i < 12; i++) await QueueAsync();

            var options = new CommMessageDispatchOptions { BatchSize = 4 };
            var contexts = Enumerable.Range(0, 4).Select(_ => _sql.NewContext()).ToList();
            try
            {
                var results = await Task.WhenAll(contexts.Select(db =>
                    Task.Run(() => _sql.CommStore.On(db, options).ClaimPendingAsync(options.BatchSize))));

                var claimed = results.SelectMany(r => r).Select(w => w.MessageId).ToList();
                Assert.NotEmpty(claimed);
                // THE property: no message handed to two workers, so no customer receives two copies.
                Assert.Equal(claimed.Count, claimed.Distinct().Count());
            }
            finally { foreach (var db in contexts) db.Dispose(); }

            using var verify = _sql.NewContext();
            Assert.False(await verify.CommMessages.AnyAsync(m => m.Attempts > 1));
        }

        // ---- A stale claim is reclaimed ----
        [SkippableFact]
        public async Task A_claim_abandoned_by_a_dead_worker_is_reclaimed_after_the_timeout()
        {
            await ReadyAsync();
            var id = await QueueAsync();
            var options = new CommMessageDispatchOptions { StaleClaimMinutes = 10 };

            using var db = _sql.NewContext();
            var store = _sql.CommStore.On(db, options);

            Assert.Single(await store.ClaimPendingAsync(10));
            Assert.Empty(await store.ClaimPendingAsync(10));   // owned, not stale yet

            await AgeAsync(id, "ClaimedAt", TimeSpan.FromMinutes(30));

            var reclaimed = await _sql.CommStore.On(_sql.NewContext(), options).ClaimPendingAsync(10);
            Assert.Single(reclaimed);
            Assert.Equal(2, reclaimed[0].Attempts);
        }

        // ---- A Sent row is never reclaimed ----
        [SkippableFact]
        public async Task A_sent_message_is_never_reclaimed_even_when_every_clock_is_ancient()
        {
            await ReadyAsync();
            var id = await QueueAsync();
            var options = new CommMessageDispatchOptions { StaleClaimMinutes = 0, RetryBackoffSeconds = 0 };

            using var db = _sql.NewContext();
            var store = _sql.CommStore.On(db, options);
            var work = await store.ClaimPendingAsync(10);
            await store.MarkSentAsync(work.Single().MessageId);

            await AgeAsync(id, "UpdatedAt", TimeSpan.FromDays(30));
            await AgeAsync(id, "ClaimedAt", TimeSpan.FromDays(30));

            // Sent is terminal. This is also what keeps the filtered claim index (Status <> 'Sent') correct.
            Assert.Empty(await store.ClaimPendingAsync(10));
            Assert.Empty(await store.GetPendingAsync(10));
            Assert.Equal(CommMessageStatus.Sent, (await _sql.NewContext().CommMessages.SingleAsync()).Status);
        }

        // ---- Failed rows retry independently ----
        [SkippableFact]
        public async Task A_failed_message_retries_while_a_sent_one_stays_done()
        {
            await ReadyAsync();
            var failing = await QueueAsync();
            var healthy = await QueueAsync();
            var options = new CommMessageDispatchOptions { RetryBackoffSeconds = 60 };

            using var db = _sql.NewContext();
            var store = _sql.CommStore.On(db, options);

            Assert.Equal(2, (await store.ClaimPendingAsync(10)).Count);
            await store.MarkFailedAsync(failing, "550 mailbox unavailable");
            await store.MarkSentAsync(healthy);

            Assert.Empty(await store.ClaimPendingAsync(10));   // inside the backoff window
            await AgeAsync(failing, "UpdatedAt", TimeSpan.FromMinutes(5));

            var retry = await _sql.CommStore.On(_sql.NewContext(), options).ClaimPendingAsync(10);
            Assert.Equal(failing, Assert.Single(retry).MessageId);
        }

        // ---- MaxAttempts stops retry; the error is retained and truncated ----
        [SkippableFact]
        public async Task MaxAttempts_stops_retry_and_the_failure_reason_is_kept()
        {
            await ReadyAsync();
            var id = await QueueAsync();
            // A 60-second backoff, with the row explicitly aged past it between attempts.
            //
            // RetryBackoffSeconds = 0 looks simpler but is genuinely unreliable: eligibility compares the row's
            // UpdatedAt — stamped from the APPLICATION clock by MarkFailedAsync — against SYSUTCDATETIME() on the
            // SERVER. With a zero window, a server clock even fractionally behind the app's makes the row appear to
            // live in the future and the claim skips it. Production uses a 120-second backoff, so this only ever
            // showed up as a flaky test; driving the clock explicitly tests the intended rule instead of the skew.
            var options = new CommMessageDispatchOptions { MaxAttempts = 2, RetryBackoffSeconds = 60 };

            using var db = _sql.NewContext();
            var store = _sql.CommStore.On(db, options);

            for (int i = 0; i < options.MaxAttempts; i++)
            {
                var claimed = await store.ClaimPendingAsync(10);
                Assert.Single(claimed);
                await store.MarkFailedAsync(claimed[0].MessageId, new string('x', 5000));
                await AgeAsync(id, "UpdatedAt", TimeSpan.FromMinutes(5));
            }

            // Aged well past the backoff, so the row is dark because ATTEMPTS ran out — not because it is waiting.
            Assert.Empty(await store.ClaimPendingAsync(10));

            using var verify = _sql.NewContext();
            var row = await verify.CommMessages.SingleAsync();
            Assert.Equal(CommMessageStatus.Failed, row.Status);
            Assert.Equal(options.MaxAttempts, row.Attempts);
            Assert.Equal(900, row.Error!.Length);   // truncated to fit, never dropped
        }

        // ---- Attachments are not duplicated by retries ----
        [SkippableFact]
        public async Task Retrying_never_duplicates_attachments()
        {
            await ReadyAsync();
            var id = await QueueAsync();
            await using (var connection = new SqlConnection(_sql.TestConnectionString))
            {
                await connection.OpenAsync();
                await using var cmd = new SqlCommand(
                    "INSERT INTO CommAttachments (CommMessageId, FilePath, FileName, Size, CreatedAt) " +
                    "VALUES (@id, '/uploads/mail/a.pdf', 'a.pdf', 10, SYSUTCDATETIME());", connection);
                cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync();
            }

            // Real backoff plus explicit ageing, for the clock-skew reason spelled out in
            // MaxAttempts_stops_retry_and_the_failure_reason_is_kept: UpdatedAt is stamped from the application
            // clock while eligibility is evaluated against SYSUTCDATETIME() on the server.
            var options = new CommMessageDispatchOptions { RetryBackoffSeconds = 60, MaxAttempts = 5 };
            using var db = _sql.NewContext();
            var store = _sql.CommStore.On(db, options);
            for (int i = 0; i < 3; i++)
            {
                var claimed = await store.ClaimPendingAsync(10);
                Assert.Single(claimed);
                await store.MarkFailedAsync(claimed[0].MessageId, "transient");
                await AgeAsync(id, "UpdatedAt", TimeSpan.FromMinutes(5));
            }

            using var verify = _sql.NewContext();
            Assert.Equal(1, await verify.CommAttachments.CountAsync(a => a.CommMessageId == id));
        }

        // ---- Company isolation ----
        [SkippableFact]
        public async Task Each_claim_carries_its_own_company()
        {
            await ReadyAsync();
            var one = await QueueAsync(companyId: 1);
            var two = await QueueAsync(companyId: 2);

            using var db = _sql.NewContext();
            var claimed = await _sql.CommStore.On(db).ClaimPendingAsync(10);

            Assert.Equal(2, claimed.Count);
            Assert.Equal(1, claimed.Single(w => w.MessageId == one).CompanyId);
            Assert.Equal(2, claimed.Single(w => w.MessageId == two).CompanyId);
        }

        // ---- The deployed script's own guarantees ----
        [SkippableFact]
        public async Task The_deployed_outbox_script_is_idempotent_and_its_constraints_are_real()
        {
            await ReadyAsync();   // EnsureCommOutboxAsync already ran the script TWICE

            await using var connection = new SqlConnection(_sql.TestConnectionString);
            await connection.OpenAsync();

            // Columns, index and constraints exist exactly once.
            await using (var cmd = new SqlCommand(@"
SELECT
  (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('CommMessages') AND name='ClaimedAt'),
  (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('CommMessages') AND name='UpdatedAt'),
  (SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID('CommMessages') AND name='IX_CommMessages_Dispatch'),
  (SELECT COUNT(*) FROM sys.check_constraints WHERE name='CK_CommMessages_Status'),
  (SELECT COUNT(*) FROM sys.check_constraints WHERE name='CK_CommMessages_Attempts');", connection))
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                for (int i = 0; i < 5; i++) Assert.Equal(1, reader.GetInt32(i));
            }

            // The status CHECK is enforced, not decorative.
            var bad = await Assert.ThrowsAsync<SqlException>(async () =>
            {
                await using var cmd = new SqlCommand(
                    "INSERT INTO CommMessages (CompanyID, ToAddress, Subject, Status, Attempts, Kind, Starred) " +
                    "VALUES (1, 'x@example.com', 's', 'Sending', 0, 'New', 0);", connection);
                await cmd.ExecuteNonQueryAsync();
            });
            Assert.Contains("CK_CommMessages_Status", bad.Message);

            // The claim index is filtered on Status <> 'Sent', so finished mail leaves it entirely.
            await using var filterCmd = new SqlCommand(
                "SELECT has_filter, filter_definition FROM sys.indexes WHERE object_id=OBJECT_ID('CommMessages') AND name='IX_CommMessages_Dispatch';", connection);
            await using var filterReader = await filterCmd.ExecuteReaderAsync();
            Assert.True(await filterReader.ReadAsync());
            Assert.True(filterReader.GetBoolean(0));
            Assert.Contains("Sent", filterReader.GetString(1));
        }

        // ---- Production databases are refused ----
        [Fact]
        public void The_fixture_refuses_to_target_a_real_CrossBuy_database()
        {
            // Not skippable: this guard must hold whether or not an integration server is configured.
            var forbidden = typeof(SqlServerFixture)
                .GetField("ForbiddenCatalogs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(forbidden);
            var names = (string[])forbidden!.GetValue(null)!;
            Assert.Contains("CrossBuyDB2", names);
            Assert.Contains("CrossBuyDB", names);
            Assert.Contains("CrossBuy", names);
        }
    }
}
