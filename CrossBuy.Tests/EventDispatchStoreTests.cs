using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    public class EventDispatchStoreTests
    {
        // A second consumer name, used to prove per-consumer independence. It is deliberately NOT in
        // BusinessEventConsumers.Registered — registering a consumer with no implementation would leave
        // undrained rows in production; a test can still create its rows directly.
        private const string OtherConsumer = "SearchIndex";

        private static async Task<long> AddEventAsync(
            CrossBuy.Models.Context.CrossDbContext db, int companyId = 1, int entityId = 5,
            string visibility = BusinessEventVisibility.Internal, DateTime? createdAt = null)
        {
            var ev = new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = companyId,
                EntityType = EntityRegistry.SalesInvoice, EntityId = entityId,
                EventType = SalesInvoiceEvents.Created, Visibility = visibility,
                PayloadVersion = 1, CreatedAt = createdAt ?? DateTime.UtcNow,
            };
            db.BusinessEvents.Add(ev);
            await db.SaveChangesAsync();
            return ev.EventId;
        }

        private static async Task AddDispatchAsync(
            CrossBuy.Models.Context.CrossDbContext db, long eventId, string consumer, string status = BusinessEventDispatchStatus.Pending)
        {
            db.BusinessEventDispatches.Add(new BusinessEventDispatch
            {
                EventId = eventId, Consumer = consumer, Status = status, Attempts = 0, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // ---- Test 6: different consumers have independent dispatch states ----
        [Fact]
        public async Task Consumers_have_independent_dispatch_state()
        {
            using var host = new PlatformTestHost();
            var store = host.DispatchStore();

            var eventId = await AddEventAsync(host.Db);
            await AddDispatchAsync(host.Db, eventId, BusinessEventConsumers.TimelineProjection);
            await AddDispatchAsync(host.Db, eventId, OtherConsumer);

            // One consumer finishing must not touch the other's work.
            var timelineWork = await store.GetPendingAsync(BusinessEventConsumers.TimelineProjection, 10);
            await store.MarkDoneAsync(timelineWork.Single().DispatchId);

            Assert.Empty(await store.GetPendingAsync(BusinessEventConsumers.TimelineProjection, 10));
            Assert.Single(await store.GetPendingAsync(OtherConsumer, 10));

            // And the event is NOT complete while a consumer is still outstanding.
            await store.TryCompleteEventAsync(eventId);
            using var verify = host.NewContext();
            Assert.Null((await verify.BusinessEvents.SingleAsync(e => e.EventId == eventId)).CompletedAt);
        }

        // ---- Test 7: a failed consumer does not reset a completed consumer ----
        [Fact]
        public async Task A_failing_consumer_does_not_reset_a_completed_one()
        {
            using var host = new PlatformTestHost();
            var store = host.DispatchStore();

            var eventId = await AddEventAsync(host.Db);
            await AddDispatchAsync(host.Db, eventId, BusinessEventConsumers.TimelineProjection);
            await AddDispatchAsync(host.Db, eventId, OtherConsumer);

            var timelineId = (await store.GetPendingAsync(BusinessEventConsumers.TimelineProjection, 10)).Single().DispatchId;
            var otherId = (await store.GetPendingAsync(OtherConsumer, 10)).Single().DispatchId;

            await store.MarkDoneAsync(timelineId);
            await store.MarkFailedAsync(otherId, "downstream index unavailable");

            using var verify = host.NewContext();
            var rows = await verify.BusinessEventDispatches.ToDictionaryAsync(d => d.Consumer);

            Assert.Equal(BusinessEventDispatchStatus.Done, rows[BusinessEventConsumers.TimelineProjection].Status);
            Assert.Null(rows[BusinessEventConsumers.TimelineProjection].Error);
            Assert.Equal(BusinessEventDispatchStatus.Failed, rows[OtherConsumer].Status);
            Assert.Equal("downstream index unavailable", rows[OtherConsumer].Error);

            // The completed consumer is not offered the work again.
            Assert.Empty(await store.GetPendingAsync(BusinessEventConsumers.TimelineProjection, 10));
        }

        [Fact]
        public async Task Error_text_is_truncated_to_the_column_width()
        {
            using var host = new PlatformTestHost();
            var store = host.DispatchStore();

            var eventId = await AddEventAsync(host.Db);
            await AddDispatchAsync(host.Db, eventId, BusinessEventConsumers.TimelineProjection);
            var dispatchId = (await store.GetPendingAsync(BusinessEventConsumers.TimelineProjection, 10)).Single().DispatchId;

            await store.MarkFailedAsync(dispatchId, new string('e', 5000));

            using var verify = host.NewContext();
            var row = await verify.BusinessEventDispatches.SingleAsync();
            Assert.Equal(400, row.Error!.Length);   // nvarchar(400) — a long stack trace cannot break the write
        }

        [Fact]
        public async Task A_row_stops_being_retried_after_max_attempts()
        {
            using var host = new PlatformTestHost();
            var options = new BusinessEventDispatchOptions { MaxAttempts = 2, RetryBackoffSeconds = 0 };
            var store = host.DispatchStore(options: options);

            var eventId = await AddEventAsync(host.Db);
            await AddDispatchAsync(host.Db, eventId, BusinessEventConsumers.TimelineProjection);

            for (int attempt = 0; attempt < options.MaxAttempts; attempt++)
            {
                var claimed = await store.ClaimPendingAsync(BusinessEventConsumers.TimelineProjection, 10);
                Assert.Single(claimed);
                await store.MarkFailedAsync(claimed[0].DispatchId, "still failing");
            }

            // Exhausted: it is no longer claimable, and it is still THERE (Failed, with its reason) rather
            // than deleted or silently dropped.
            Assert.Empty(await store.ClaimPendingAsync(BusinessEventConsumers.TimelineProjection, 10));
            using var verify = host.NewContext();
            var row = await verify.BusinessEventDispatches.SingleAsync();
            Assert.Equal(BusinessEventDispatchStatus.Failed, row.Status);
            Assert.Equal(options.MaxAttempts, row.Attempts);
            Assert.Equal("still failing", row.Error);
        }

        // ---- Test 8: pending work does not depend on EventId ordering ----
        [Fact]
        public async Task Pending_work_is_found_regardless_of_EventId_order()
        {
            using var host = new PlatformTestHost();
            var store = host.DispatchStore();

            var first = await AddEventAsync(host.Db, entityId: 1);
            var second = await AddEventAsync(host.Db, entityId: 2);
            var third = await AddEventAsync(host.Db, entityId: 3);
            foreach (var id in new[] { first, second, third })
                await AddDispatchAsync(host.Db, id, BusinessEventConsumers.TimelineProjection);

            // Finish the HIGHEST EventId first. A high-water cursor would now be sitting past `third` and
            // would never come back for `first` and `second`.
            var all = await store.GetPendingAsync(BusinessEventConsumers.TimelineProjection, 10);
            await store.MarkDoneAsync(all.Single(w => w.EventId == third).DispatchId);

            var afterThird = await store.GetPendingAsync(BusinessEventConsumers.TimelineProjection, 10);
            Assert.Equal(new[] { first, second }, afterThird.Select(w => w.EventId).OrderBy(x => x).ToArray());

            // Finish the lowest next; the middle one is still found.
            await store.MarkDoneAsync(afterThird.Single(w => w.EventId == first).DispatchId);
            var remaining = await store.GetPendingAsync(BusinessEventConsumers.TimelineProjection, 10);
            Assert.Equal(second, remaining.Single().EventId);
        }

        // ---- Test 9: transactions committing out of order do not lose events ----
        [Fact]
        public async Task An_event_whose_transaction_commits_late_is_still_dispatched()
        {
            using var host = new PlatformTestHost();
            var store = host.DispatchStore();

            // Reproduce the exact hazard: identity values are handed out at INSERT but become visible at
            // COMMIT. Transaction A takes EventId 100 and is still open; transaction B takes 101 and commits
            // FIRST. B is dispatched. Only then does A commit, making EventId 100 visible — i.e. a NEW row
            // appears BELOW the highest id already processed.
            //
            // Explicit ids are written with raw SQL because the point of the test is to control the id
            // relative to processing order, which is exactly what EF's value generation hides.
            await InsertEventWithExplicitIdAsync(host, eventId: 101);
            await AddDispatchAsync(host.Db, 101, BusinessEventConsumers.TimelineProjection);

            var bWork = await store.ClaimPendingAsync(BusinessEventConsumers.TimelineProjection, 10);
            Assert.Equal(101, bWork.Single().EventId);
            await store.MarkDoneAsync(bWork[0].DispatchId);
            await store.TryCompleteEventAsync(101);

            // ...now A commits, appearing with the LOWER id.
            await InsertEventWithExplicitIdAsync(host, eventId: 100);
            await AddDispatchAsync(host.Db, 100, BusinessEventConsumers.TimelineProjection);

            var aWork = await store.ClaimPendingAsync(BusinessEventConsumers.TimelineProjection, 10);
            Assert.Equal(100, Assert.Single(aWork).EventId);   // a cursor-based dispatcher would have lost this
        }

        // ---- Test 10: two workers cannot claim the same dispatch row ----
        [Fact]
        public async Task A_claimed_row_cannot_be_claimed_again()
        {
            using var host = new PlatformTestHost();

            var eventId = await AddEventAsync(host.Db);
            await AddDispatchAsync(host.Db, eventId, BusinessEventConsumers.TimelineProjection);

            // Two independent workers = two DbContexts over the same database, each with its own store.
            using var workerOneDb = host.NewContext();
            using var workerTwoDb = host.NewContext();
            var workerOne = host.DispatchStore(workerOneDb);
            var workerTwo = host.DispatchStore(workerTwoDb);

            var claimedByOne = await workerOne.ClaimPendingAsync(BusinessEventConsumers.TimelineProjection, 10);
            var claimedByTwo = await workerTwo.ClaimPendingAsync(BusinessEventConsumers.TimelineProjection, 10);

            Assert.Single(claimedByOne);
            Assert.Empty(claimedByTwo);   // the row is Claimed and not yet stale, so worker two gets nothing

            // Claiming increments Attempts once, so retry accounting cannot be inflated by contention.
            using var verify = host.NewContext();
            var row = await verify.BusinessEventDispatches.SingleAsync();
            Assert.Equal(BusinessEventDispatchStatus.Claimed, row.Status);
            Assert.Equal(1, row.Attempts);

            // NOTE: this proves single-ownership. It does not reproduce SQL Server's READPAST skip-locked
            // behaviour under genuine parallelism, which needs a real SQL Server instance — see the
            // "Known limitations" section of PKS-001.
        }

        [Fact]
        public async Task A_claim_abandoned_by_a_dead_worker_is_reclaimed_after_the_stale_window()
        {
            using var host = new PlatformTestHost();
            var store = host.DispatchStore(options: new BusinessEventDispatchOptions { StaleClaimMinutes = 10 });

            var eventId = await AddEventAsync(host.Db);
            await AddDispatchAsync(host.Db, eventId, BusinessEventConsumers.TimelineProjection);

            var claimed = await store.ClaimPendingAsync(BusinessEventConsumers.TimelineProjection, 10);
            Assert.Single(claimed);
            Assert.Empty(await store.ClaimPendingAsync(BusinessEventConsumers.TimelineProjection, 10));

            // Simulate the owning worker dying: its claim ages past the stale window.
            using (var age = host.NewContext())
            {
                var row = await age.BusinessEventDispatches.SingleAsync();
                row.UpdatedAt = DateTime.UtcNow.AddMinutes(-30);
                await age.SaveChangesAsync();
            }

            var reclaimed = await host.DispatchStore(host.NewContext()).ClaimPendingAsync(BusinessEventConsumers.TimelineProjection, 10);
            Assert.Single(reclaimed);   // work is never stranded by a crashed worker
        }

        [Fact]
        public async Task An_event_is_completed_only_when_every_consumer_is_done()
        {
            using var host = new PlatformTestHost();
            var store = host.DispatchStore();

            var eventId = await AddEventAsync(host.Db);
            await AddDispatchAsync(host.Db, eventId, BusinessEventConsumers.TimelineProjection);
            await AddDispatchAsync(host.Db, eventId, OtherConsumer);

            var work = await store.GetPendingAsync(BusinessEventConsumers.TimelineProjection, 10);
            await store.MarkDoneAsync(work.Single().DispatchId);
            await store.TryCompleteEventAsync(eventId);
            Assert.Null((await host.NewContext().BusinessEvents.SingleAsync()).CompletedAt);

            var other = await store.GetPendingAsync(OtherConsumer, 10);
            await store.MarkDoneAsync(other.Single().DispatchId);
            await store.TryCompleteEventAsync(eventId);
            Assert.NotNull((await host.NewContext().BusinessEvents.SingleAsync()).CompletedAt);
        }

        // Raw insert so the test controls EventId. Guid and DateTime are passed as parameters so the SQLite
        // provider applies the same conversions EF uses when reading the row back.
        private static async Task InsertEventWithExplicitIdAsync(PlatformTestHost host, long eventId)
        {
            await host.Db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO BusinessEvents
                    (EventId, EventUid, CompanyID, BranchID, EntityType, EntityId, EventType, ActorEmployeeId,
                     Payload, PayloadVersion, CorrelationId, DedupKey, Visibility, CreatedAt, CompletedAt)
                  VALUES ({0}, {1}, 1, NULL, {2}, 5, {3}, NULL, NULL, 1, NULL, NULL, {4}, {5}, NULL)",
                eventId,
                Guid.NewGuid().ToString(),
                EntityRegistry.SalesInvoice,
                SalesInvoiceEvents.Created,
                BusinessEventVisibility.Internal,
                DateTime.UtcNow);
        }
    }
}