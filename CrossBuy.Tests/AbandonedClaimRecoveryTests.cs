using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    /// <summary>
    /// DEFECT B — recovery of a dispatch claim abandoned on its LAST attempt.
    ///
    /// WHAT WAS BROKEN, and it was not a missing reaper. ClaimPendingAsync already reclaims a stale Claimed
    /// row; its WHERE has always carried
    ///     OR (Status = Claimed AND UpdatedAt &lt;= DATEADD(minute, -@stale, SYSUTCDATETIME()))
    /// — but every branch of it sits under a single top-level `AND d.Attempts &lt; @maxAttempts`. A worker that
    /// dies mid-batch on the FINAL attempt therefore leaves the row Claimed with Attempts = MaxAttempts, and
    /// no predicate can ever match it again. It is not retryable and it is not terminal: it reads as work in
    /// progress forever. 23 such rows sat in CrossBuyDev from 2026-08-03 (15 JournalEntry.Reversed,
    /// 8 SalesInvoice.Created), and BusinessEventMonitor could not tell them from a live claim.
    ///
    /// The recovery is a CLASSIFICATION, not a retry: the row moves to the same terminal Failed state an
    /// attempts-exhausted row reaches by the normal route, with Attempts untouched.
    /// </summary>
    public class AbandonedClaimRecoveryTests
    {
        private const string Consumer = BusinessEventConsumers.TimelineProjection;
        private const string OtherConsumer = "SearchIndex";

        private static async Task<long> AddEventAsync(CrossBuy.Models.Context.CrossDbContext db, int companyId = 1)
        {
            var ev = new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = companyId,
                EntityType = EntityRegistry.SalesInvoice, EntityId = 5,
                EventType = SalesInvoiceEvents.Created, Visibility = BusinessEventVisibility.Internal,
                PayloadVersion = 1, CreatedAt = DateTime.UtcNow,
            };
            db.BusinessEvents.Add(ev);
            await db.SaveChangesAsync();
            return ev.EventId;
        }

        private static async Task<long> AddDispatchAsync(
            CrossBuy.Models.Context.CrossDbContext db, long eventId, string status, int attempts,
            DateTime updatedAt, string consumer = Consumer)
        {
            var row = new BusinessEventDispatch
            {
                EventId = eventId, Consumer = consumer, Status = status, Attempts = attempts, UpdatedAt = updatedAt,
            };
            db.BusinessEventDispatches.Add(row);
            await db.SaveChangesAsync();
            return row.ID;
        }

        private static async Task<BusinessEventDispatch> RowAsync(CrossBuy.Models.Context.CrossDbContext db, long id)
            => await db.BusinessEventDispatches.AsNoTracking().FirstAsync(d => d.ID == id);

        // Older than StaleClaimMinutes (default 10).
        private static DateTime Stale => DateTime.UtcNow.AddHours(-3);
        private static DateTime Fresh => DateTime.UtcNow;

        // =========================================================================================
        // The defect
        // =========================================================================================

        [Fact]
        public async Task An_abandoned_claim_on_the_last_attempt_is_released_to_a_terminal_state()
        {
            using var host = new PlatformTestHost();
            var store = host.DispatchStore();
            var eventId = await AddEventAsync(host.Db);
            var id = await AddDispatchAsync(host.Db, eventId, BusinessEventDispatchStatus.Claimed, attempts: 5, updatedAt: Stale);

            // Precondition: the claim loop genuinely cannot see it. This is the defect, asserted rather than
            // described — if a future change made it claimable, this test would stop testing anything.
            Assert.Empty(await store.ClaimPendingAsync(Consumer, 10));

            int released = await store.ReleaseAbandonedClaimsAsync(Consumer);

            Assert.Equal(1, released);
            var row = await RowAsync(host.Db, id);
            Assert.Equal(BusinessEventDispatchStatus.Failed, row.Status);
            Assert.False(string.IsNullOrWhiteSpace(row.Error));
        }

        [Fact]
        public async Task Releasing_does_not_refund_or_burn_an_attempt()
        {
            // Attempt accounting must stay truthful: an operator reading Attempts = 5 is reading what really
            // happened. Incrementing would overstate the failures; resetting would hide them.
            using var host = new PlatformTestHost();
            var store = host.DispatchStore();
            var id = await AddDispatchAsync(host.Db, await AddEventAsync(host.Db), BusinessEventDispatchStatus.Claimed, 5, Stale);

            await store.ReleaseAbandonedClaimsAsync(Consumer);

            Assert.Equal(5, (await RowAsync(host.Db, id)).Attempts);
        }

        [Fact]
        public async Task A_released_row_is_terminal_and_does_not_start_retrying_by_itself()
        {
            // Failed + attempts exhausted is the existing dead-letter shape. The row must NOT become
            // self-retrying, or the reaper would have converted a stuck row into an infinite loop.
            using var host = new PlatformTestHost();
            var store = host.DispatchStore();
            await AddDispatchAsync(host.Db, await AddEventAsync(host.Db), BusinessEventDispatchStatus.Claimed, 5, Stale);

            await store.ReleaseAbandonedClaimsAsync(Consumer);

            Assert.Empty(await store.ClaimPendingAsync(Consumer, 10));
            Assert.Empty(await store.GetPendingAsync(Consumer, 10));
        }

        // =========================================================================================
        // A LIVE CLAIM IS NEVER STOLEN — the lease
        // =========================================================================================

        [Fact]
        public async Task A_fresh_claim_is_never_released_however_many_attempts_it_has()
        {
            using var host = new PlatformTestHost();
            var store = host.DispatchStore();
            var id = await AddDispatchAsync(host.Db, await AddEventAsync(host.Db), BusinessEventDispatchStatus.Claimed, 5, Fresh);

            Assert.Equal(0, await store.ReleaseAbandonedClaimsAsync(Consumer));
            Assert.Equal(BusinessEventDispatchStatus.Claimed, (await RowAsync(host.Db, id)).Status);
        }

        [Fact]
        public async Task A_stale_claim_with_attempts_REMAINING_is_left_to_the_claim_loop()
        {
            // Deliberate division of labour: that row is already reclaimable, and the claim path retries it
            // properly with an attempt increment. Two owners for one transition is how double-processing starts.
            using var host = new PlatformTestHost();
            var store = host.DispatchStore();
            var id = await AddDispatchAsync(host.Db, await AddEventAsync(host.Db), BusinessEventDispatchStatus.Claimed, 2, Stale);

            Assert.Equal(0, await store.ReleaseAbandonedClaimsAsync(Consumer));

            var claimed = await store.ClaimPendingAsync(Consumer, 10);
            Assert.Single(claimed);
            Assert.Equal(id, claimed[0].DispatchId);
            Assert.Equal(3, claimed[0].Attempts);   // reclaimed AND counted
        }

        [Theory]
        [InlineData(BusinessEventDispatchStatus.Pending)]
        [InlineData(BusinessEventDispatchStatus.Failed)]
        [InlineData(BusinessEventDispatchStatus.Done)]
        public async Task No_other_status_is_touched(string status)
        {
            using var host = new PlatformTestHost();
            var store = host.DispatchStore();
            var id = await AddDispatchAsync(host.Db, await AddEventAsync(host.Db), status, attempts: 5, updatedAt: Stale);

            Assert.Equal(0, await store.ReleaseAbandonedClaimsAsync(Consumer));
            Assert.Equal(status, (await RowAsync(host.Db, id)).Status);
        }

        [Fact]
        public async Task Another_consumers_abandoned_row_is_not_released()
        {
            using var host = new PlatformTestHost();
            var store = host.DispatchStore();
            var id = await AddDispatchAsync(host.Db, await AddEventAsync(host.Db),
                BusinessEventDispatchStatus.Claimed, 5, Stale, consumer: OtherConsumer);

            Assert.Equal(0, await store.ReleaseAbandonedClaimsAsync(Consumer));
            Assert.Equal(BusinessEventDispatchStatus.Claimed, (await RowAsync(host.Db, id)).Status);

            // ...and the right consumer does release it.
            Assert.Equal(1, await store.ReleaseAbandonedClaimsAsync(OtherConsumer));
        }

        // =========================================================================================
        // Idempotency and company reach
        // =========================================================================================

        [Fact]
        public async Task Running_the_reaper_twice_releases_nothing_the_second_time()
        {
            using var host = new PlatformTestHost();
            var store = host.DispatchStore();
            await AddDispatchAsync(host.Db, await AddEventAsync(host.Db), BusinessEventDispatchStatus.Claimed, 5, Stale);

            Assert.Equal(1, await store.ReleaseAbandonedClaimsAsync(Consumer));
            Assert.Equal(0, await store.ReleaseAbandonedClaimsAsync(Consumer));
        }

        [Fact]
        public async Task The_reaper_spans_companies_exactly_as_the_queue_does_without_mixing_their_events()
        {
            // BusinessEventDispatch is ONE queue across companies — ClaimPendingAsync claims in a single
            // statement and never filters by company, so the reaper must reach every company's stuck row too.
            // What must NOT happen is a row being re-pointed at another company's event: the reaper touches
            // Status/UpdatedAt/Error only, so each row still names the EventId it always named.
            using var host = new PlatformTestHost();
            var store = host.DispatchStore();

            // Arranged through host.Seed (the all-companies context), not host.Db. host.Db operates AS company
            // 1, and CompanyWriteGuardInterceptor correctly refuses to let it insert a company-65 event —
            // "a company id from a request, a view model or a route may not redirect a write". That refusal is
            // the isolation guard doing its job; the fixture has to arrange two tenants the legitimate way.
            var eventOne = await AddEventAsync(host.Seed, companyId: 1);
            var eventSixtyFive = await AddEventAsync(host.Seed, companyId: 65);
            var idOne = await AddDispatchAsync(host.Seed, eventOne, BusinessEventDispatchStatus.Claimed, 5, Stale);
            var idSixtyFive = await AddDispatchAsync(host.Seed, eventSixtyFive, BusinessEventDispatchStatus.Claimed, 5, Stale);

            Assert.Equal(2, await store.ReleaseAbandonedClaimsAsync(Consumer));

            var rowOne = await RowAsync(host.Seed, idOne);
            var rowSixtyFive = await RowAsync(host.Seed, idSixtyFive);

            Assert.Equal(BusinessEventDispatchStatus.Failed, rowOne.Status);
            Assert.Equal(BusinessEventDispatchStatus.Failed, rowSixtyFive.Status);
            Assert.Equal(eventOne, rowOne.EventId);
            Assert.Equal(eventSixtyFive, rowSixtyFive.EventId);
        }

        [Fact]
        public async Task An_operator_retry_still_works_on_a_released_row_which_is_the_point_of_releasing_it()
        {
            // Releasing turns an invisible stuck row into a normal dead-lettered one — and a dead-lettered row
            // is exactly what the sanctioned operator retry path (RetryAsync with an elevated override and a
            // reason) is built to re-queue. Before the fix there was nothing to retry: the row was Claimed, and
            // RetryAsync refuses a claim it believes a worker is holding.
            using var host = new PlatformTestHost();
            var store = host.DispatchStore();
            var id = await AddDispatchAsync(host.Db, await AddEventAsync(host.Db), BusinessEventDispatchStatus.Claimed, 5, Stale);

            await store.ReleaseAbandonedClaimsAsync(Consumer);

            var result = await store.RetryAsync(
                id, BusinessContext.ForSystem(1), "post-recovery replay", elevatedOverride: true);

            Assert.True(result.Success, result.Message);
        }
    }
}
