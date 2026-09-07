using CrossBuy.BL.Comm;
using CrossBuy.Models.Context.Comm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 0 (Slice-003) — email outbox dispatch state machine.
    //
    // These run in-process (SQLite, real transactions) and cover the state machine and eligibility rules.
    // SQL Server's UPDLOCK/READPAST skip-locked behaviour cannot be reproduced here and is covered separately by
    // CrossBuy.Tests/SqlServer/CommOutboxConcurrencyTests.cs.
    public class Slice3CommOutboxTests
    {
        private static ICommMessageDispatchStore Store(PlatformTestHost host, CommMessageDispatchOptions? o = null)
            => new SqlCommMessageDispatchStore(host.Db, Options.Create(o ?? new CommMessageDispatchOptions()));

        private static ICommMessageDispatchStore StoreOn(CrossBuy.Models.Context.CrossDbContext db, CommMessageDispatchOptions? o = null)
            => new SqlCommMessageDispatchStore(db, Options.Create(o ?? new CommMessageDispatchOptions()));

        private static async Task<int> QueueAsync(PlatformTestHost host, int companyId = 1, string status = CommMessageStatus.Queued,
            int attempts = 0, DateTime? updatedAt = null, DateTime? claimedAt = null, DateTime? deletedAt = null)
        {
            var m = new CommMessage
            {
                CompanyID = companyId, ToAddress = "customer@example.com", Subject = "Invoice",
                Body = "<p>body</p>", Status = status, Attempts = attempts,
                CreatedAt = DateTime.UtcNow, UpdatedAt = updatedAt, ClaimedAt = claimedAt, DeletedAt = deletedAt,
            };
            host.Db.CommMessages.Add(m);
            await host.Db.SaveChangesAsync();
            return m.Id;
        }

        // ---- Test 11: one worker claims a message once ----
        [Fact]
        public async Task A_queued_message_is_claimed_once_and_then_not_offered_again()
        {
            using var host = new PlatformTestHost();
            var id = await QueueAsync(host);
            var store = Store(host);

            var first = await store.ClaimPendingAsync(10);
            Assert.Equal(id, Assert.Single(first).MessageId);
            Assert.Equal(1, first[0].Attempts);   // the claim increments Attempts exactly once

            // Still held and not yet stale, so it is not offered again.
            Assert.Empty(await store.ClaimPendingAsync(10));

            using var verify = host.NewContext();
            var row = await verify.CommMessages.SingleAsync();
            Assert.Equal(CommMessageStatus.Claimed, row.Status);
            Assert.Equal(1, row.Attempts);
            Assert.NotNull(row.ClaimedAt);
            Assert.NotNull(row.UpdatedAt);
        }

        // ---- Test 12: multiple workers partition queued rows ----
        [Fact]
        public async Task Two_workers_partition_the_queue_and_never_share_a_row()
        {
            using var host = new PlatformTestHost();
            for (int i = 0; i < 6; i++) await QueueAsync(host);

            using var dbA = host.NewContext();
            using var dbB = host.NewContext();
            var options = new CommMessageDispatchOptions { BatchSize = 3 };

            var a = await StoreOn(dbA, options).ClaimPendingAsync(options.BatchSize);
            var b = await StoreOn(dbB, options).ClaimPendingAsync(options.BatchSize);

            var all = a.Concat(b).Select(w => w.MessageId).ToList();
            Assert.Equal(6, all.Count);
            Assert.Equal(6, all.Distinct().Count());        // no row handed to both workers
            Assert.All(a.Concat(b), w => Assert.Equal(1, w.Attempts));
        }

        // ---- Test 13: a stale claim is reclaimed ----
        [Fact]
        public async Task A_claim_abandoned_by_a_dead_worker_is_reclaimed_after_the_timeout()
        {
            using var host = new PlatformTestHost();
            var options = new CommMessageDispatchOptions { StaleClaimMinutes = 10 };
            var store = Store(host, options);
            await QueueAsync(host);

            Assert.Single(await store.ClaimPendingAsync(10));
            Assert.Empty(await store.ClaimPendingAsync(10));   // owned, not stale

            using (var age = host.NewContext())
            {
                var row = await age.CommMessages.SingleAsync();
                row.ClaimedAt = DateTime.UtcNow.AddMinutes(-30);
                await age.SaveChangesAsync();
            }

            var reclaimed = await StoreOn(host.NewContext(), options).ClaimPendingAsync(10);
            Assert.Single(reclaimed);
            Assert.Equal(2, reclaimed[0].Attempts);   // the reclaim counts as another attempt
        }

        // ---- Test 14: Sent is terminal ----
        [Fact]
        public async Task A_sent_message_is_never_reclaimed_even_when_its_clocks_are_ancient()
        {
            using var host = new PlatformTestHost();
            var store = Store(host, new CommMessageDispatchOptions { StaleClaimMinutes = 0, RetryBackoffSeconds = 0 });
            var id = await QueueAsync(host);

            var work = await store.ClaimPendingAsync(10);
            await store.MarkSentAsync(work.Single().MessageId);

            using (var afterSend = host.NewContext())
            {
                var sent = await afterSend.CommMessages.SingleAsync(m => m.Id == id);
                Assert.Equal(CommMessageStatus.Sent, sent.Status);
                Assert.NotNull(sent.SentAt);
                Assert.Null(sent.Error);
                Assert.Null(sent.ClaimedAt);   // the claim clock is released on success
            }

            // Now age every clock far past both windows. Sent must STILL be terminal — that is what keeps the
            // filtered claiming index (Status <> 'Sent') correct and a customer from receiving a second copy.
            using (var age = host.NewContext())
            {
                var row = await age.CommMessages.SingleAsync();
                row.UpdatedAt = DateTime.UtcNow.AddDays(-30);
                row.ClaimedAt = DateTime.UtcNow.AddDays(-30);
                await age.SaveChangesAsync();
            }

            Assert.Empty(await store.ClaimPendingAsync(10));
            Assert.Empty(await store.GetPendingAsync(10));
            Assert.Equal(CommMessageStatus.Sent, (await host.NewContext().CommMessages.SingleAsync(m => m.Id == id)).Status);
        }

        // ---- Test 15: failed messages retry independently ----
        [Fact]
        public async Task A_failed_message_retries_after_the_backoff_and_another_message_is_unaffected()
        {
            using var host = new PlatformTestHost();
            var options = new CommMessageDispatchOptions { RetryBackoffSeconds = 60 };
            var store = Store(host, options);

            var failing = await QueueAsync(host);
            var healthy = await QueueAsync(host);

            var batch = await store.ClaimPendingAsync(10);
            Assert.Equal(2, batch.Count);
            await store.MarkFailedAsync(failing, "550 mailbox unavailable");
            await store.MarkSentAsync(healthy);

            // Inside the backoff window neither is offered.
            Assert.Empty(await store.ClaimPendingAsync(10));

            using (var age = host.NewContext())
            {
                var row = await age.CommMessages.SingleAsync(m => m.Id == failing);
                row.UpdatedAt = DateTime.UtcNow.AddSeconds(-120);
                await age.SaveChangesAsync();
            }

            var retry = await StoreOn(host.NewContext(), options).ClaimPendingAsync(10);
            Assert.Equal(failing, Assert.Single(retry).MessageId);   // only the failed one comes back
            Assert.Equal(2, retry[0].Attempts);
        }

        // ---- Test 16: MaxAttempts is enforced ----
        [Fact]
        public async Task A_message_stops_being_retried_at_MaxAttempts_and_keeps_its_error()
        {
            using var host = new PlatformTestHost();
            var options = new CommMessageDispatchOptions { MaxAttempts = 2, RetryBackoffSeconds = 0 };
            var store = Store(host, options);
            await QueueAsync(host);

            for (int i = 0; i < options.MaxAttempts; i++)
            {
                var claimed = await store.ClaimPendingAsync(10);
                Assert.Single(claimed);
                await store.MarkFailedAsync(claimed[0].MessageId, "smtp down");
            }

            Assert.Empty(await store.ClaimPendingAsync(10));

            using var verify = host.NewContext();
            var row = await verify.CommMessages.SingleAsync();
            Assert.Equal(CommMessageStatus.Failed, row.Status);
            Assert.Equal(options.MaxAttempts, row.Attempts);
            Assert.Equal("smtp down", row.Error);   // retained, never cleared or deleted
        }

        // ---- Test 17: error is truncated ----
        [Fact]
        public async Task A_very_long_smtp_error_is_truncated_to_fit_the_column()
        {
            using var host = new PlatformTestHost();
            var store = Store(host);
            var id = await QueueAsync(host);
            await store.ClaimPendingAsync(10);

            await store.MarkFailedAsync(id, new string('x', 5000));

            var row = await host.NewContext().CommMessages.SingleAsync();
            Assert.Equal(900, row.Error!.Length);
        }

        // ---- Test 18: attachments are not duplicated ----
        [Fact]
        public async Task Retrying_a_message_never_duplicates_its_attachments()
        {
            using var host = new PlatformTestHost();
            var options = new CommMessageDispatchOptions { RetryBackoffSeconds = 0, MaxAttempts = 5 };
            var store = Store(host, options);
            var id = await QueueAsync(host);

            host.Db.CommAttachments.Add(new CommAttachment
            { CommMessageId = id, FilePath = "/uploads/mail/a.pdf", FileName = "a.pdf", Size = 10, CreatedAt = DateTime.UtcNow });
            await host.Db.SaveChangesAsync();

            // Three claim/fail cycles. The dispatcher reads attachments at send time and never writes them, so
            // the count must be unchanged — this is what protects a customer from N copies of the same file.
            for (int i = 0; i < 3; i++)
            {
                var claimed = await store.ClaimPendingAsync(10);
                Assert.Single(claimed);
                await store.MarkFailedAsync(claimed[0].MessageId, "transient");
            }

            using var verify = host.NewContext();
            Assert.Equal(1, await verify.CommAttachments.CountAsync(a => a.CommMessageId == id));
        }

        // ---- Test 19: company isolation ----
        [Fact]
        public async Task Claims_carry_the_owning_company_and_do_not_mix_companies()
        {
            using var host = new PlatformTestHost();
            var one = await QueueAsync(host, companyId: 1);
            var two = await QueueAsync(host, companyId: 2);

            var claimed = await Store(host).ClaimPendingAsync(10);
            Assert.Equal(2, claimed.Count);
            Assert.Equal(1, claimed.Single(w => w.MessageId == one).CompanyId);
            Assert.Equal(2, claimed.Single(w => w.MessageId == two).CompanyId);

            // Each row keeps its own company — the dispatcher never rewrites it.
            using var verify = host.NewContext();
            Assert.Equal(1, (await verify.CommMessages.SingleAsync(m => m.Id == one)).CompanyID);
            Assert.Equal(2, (await verify.CommMessages.SingleAsync(m => m.Id == two)).CompanyID);
        }

        [Fact]
        public async Task Trashed_messages_are_never_claimed()
        {
            using var host = new PlatformTestHost();
            await QueueAsync(host, deletedAt: DateTime.UtcNow);
            Assert.Empty(await Store(host).ClaimPendingAsync(10));
            Assert.Empty(await Store(host).GetPendingAsync(10));
        }

        [Fact]
        public async Task A_message_queued_before_the_dispatcher_existed_becomes_eligible_immediately()
        {
            using var host = new PlatformTestHost();
            // Pre-Stage-0 row: Queued with NULL dispatch clocks, stuck because nothing drained the table.
            await QueueAsync(host, status: CommMessageStatus.Queued, updatedAt: null, claimedAt: null);

            var claimed = await Store(host).ClaimPendingAsync(10);
            Assert.Single(claimed);
        }

        [Fact]
        public void The_status_vocabulary_is_frozen_and_matches_the_deployed_check_constraint()
        {
            // The CHECK constraint in deploy/sql/comm_outbox_slice_003.sql allows exactly these four.
            Assert.Equal("Queued", CommMessageStatus.Queued);
            Assert.Equal("Claimed", CommMessageStatus.Claimed);
            Assert.Equal("Sent", CommMessageStatus.Sent);
            Assert.Equal("Failed", CommMessageStatus.Failed);

            var script = File.ReadAllText(Path.Combine(
                Slice3WorkerCompanyTests.FindRepoRoot(), "CrossBuy", "deploy", "sql", "comm_outbox_slice_003.sql"));
            Assert.Contains("CK_CommMessages_Status", script);
            Assert.Contains("'Queued','Claimed','Sent','Failed'", script);
            // Idempotency guards must be present for every object the script creates.
            Assert.Contains("COL_LENGTH('dbo.CommMessages', 'ClaimedAt')", script);
            Assert.Contains("COL_LENGTH('dbo.CommMessages', 'UpdatedAt')", script);
            Assert.Contains("IX_CommMessages_Dispatch", script);
        }
    }
}
