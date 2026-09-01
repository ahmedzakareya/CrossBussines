using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // Platform Kernel slice 2 — the NotificationProjection consumer (ADR-006).
    public class NotificationProjectionTests
    {
        private const int ChiefAccountantId = 21;
        private const int ActorId = 7;

        // A test double for the notification writer. The real NotificationService needs a SignalR
        // IHubContext, and the push is not what these tests are about — what matters is that the consumer
        // persists exactly one row per (event, recipient) and populates the platform fields.
        private sealed class RecordingNotificationService : INotificationService
        {
            private readonly CrossBuy.Models.Context.CrossDbContext _db;
            public RecordingNotificationService(CrossBuy.Models.Context.CrossDbContext db) { _db = db; }

            public int Failures { get; set; }          // when > 0, the next N calls throw
            public List<Notification> Written { get; } = new();

            public async Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn,
                string? bodyAr, string? bodyEn, string type, int? refId = null,
                string? url = null, int? companyId = null, int? actorEmployeeId = null,
                string? priority = null, string? category = null, string? dedupKey = null,
                DateTime? expiresAt = null, string? icon = null,
                string? entityType = null, int? entityId = null)
            {
                if (Failures > 0) { Failures--; throw new InvalidOperationException("notification sink unavailable"); }

                var meta = NotificationTypes.Meta(type);
                var n = new Notification
                {
                    RecipientEmployeeID = recipientEmployeeId,
                    TitleAr = titleAr, TitleEn = titleEn, BodyAr = bodyAr, BodyEn = bodyEn,
                    Type = type, RefId = refId, IsRead = false, CreatedAt = DateTime.UtcNow,
                    CompanyID = companyId, Url = url, ActorEmployeeID = actorEmployeeId,
                    Priority = priority ?? meta.priority, Category = category ?? meta.category,
                    Icon = icon ?? meta.icon, DedupKey = dedupKey, ExpiresAt = expiresAt,
                    EntityType = entityType, EntityId = entityId,
                };
                _db.Notifications.Add(n);
                await _db.SaveChangesAsync();
                Written.Add(n);
            }

            public Task<int> NotifyRoleAsync(int companyId, string scope, string[] roles,
                string? titleAr, string? titleEn, string? bodyAr, string? bodyEn,
                string type, int? refId = null, int? exceptEmployeeId = null)
                => throw new InvalidOperationException(
                    "NotificationProjection must resolve recipients itself so each one gets its own dedup key and permission check.");
        }

        private static async Task<PlatformTestHost> SeedAsync(bool withAccountingRole = true)
        {
            var host = new PlatformTestHost();

            host.Db.Employee.Add(Employee(ChiefAccountantId, "رئيس الحسابات", "Chief accountant"));
            host.Db.Employee.Add(Employee(ActorId, "المحاسب", "The accountant"));
            await host.Db.SaveChangesAsync();

            if (withAccountingRole)
            {
                host.Db.AccountingUserRoles.Add(new CrossBuy.Models.Context.Accounting.AccountingUserRole
                {
                    CompanyID = 1, EmployeeId = ChiefAccountantId, Role = "ChiefAccountant",
                });
                await host.Db.SaveChangesAsync();
            }
            return host;
        }

        // Stage 1 Batch A: IsActive = true added. The consumer now resolves each recipient through
        // IBusinessContextFactory.ForEmployeeAsync, which EXCLUDES inactive employees — a notification to a
        // leaver is both a dead link and a data leak. These fixtures model current staff, so they must say so;
        // an inactive recipient is covered by its own test in Stage1NotificationAuthorizationTests.
        private static Employee Employee(int id, string nameAr, string nameEn) => new()
        {
            ID = id, FirstName = nameAr, LastName = "-", FullName = nameAr, FullNameEn = nameEn,
            EmpCompanyID = 1, IsActive = true, Address = "-", PhoneNumber = "-", Email = $"e{id}@example.com",
            ProfileImage = "-", Gender = "M", MaritalStatus = "Single", UserId = "user-" + id,
        };

        private static (NotificationProjectionConsumer consumer, RecordingNotificationService sink) Consumer(
            PlatformTestHost host, IPlatformPermissionProvider? permissions = null)
        {
            var sink = new RecordingNotificationService(host.Db);
            var consumer = new NotificationProjectionConsumer(
                host.Db,
                new BusinessEventNotificationMapper(),
                sink,
                host.Registry(),
                permissions ?? new StubPermissionProvider(PlatformActions.View),
                host.Contexts(),
                host.HostBypass(), host.Holder,
                NullLogger<NotificationProjectionConsumer>.Instance);
            return (consumer, sink);
        }

        private static async Task<BusinessEvent> AddPurchaseInvoiceEventAsync(PlatformTestHost host, int? actorId = ActorId)
        {
            var ev = new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = 1,
                EntityType = EntityRegistry.PurchaseInvoice, EntityId = 21,
                EventType = PurchaseInvoiceEvents.Created,
                ActorEmployeeId = actorId, Visibility = BusinessEventVisibility.Internal,
                PayloadVersion = PurchaseInvoiceEventPayload.Version, CreatedAt = DateTime.UtcNow,
                Payload = "{\"invoiceNumber\":\"PV-2026-00021\",\"supplierId\":3,\"supplierName\":\"مورّد\",\"totalAfter\":1500.0}",
            };
            host.Db.BusinessEvents.Add(ev);
            await host.Db.SaveChangesAsync();
            return ev;
        }

        // ---- Test 22: one notification is created ----
        [Fact]
        public async Task It_creates_exactly_one_notification_with_the_platform_fields_populated()
        {
            using var host = await SeedAsync();
            var (consumer, sink) = Consumer(host);
            var stored = await AddPurchaseInvoiceEventAsync(host);

            await consumer.HandleAsync(host.Events().BuildEnvelope(stored));

            var notification = Assert.Single(sink.Written);
            Assert.Equal(ChiefAccountantId, notification.RecipientEmployeeID);
            Assert.Equal(NotificationTypes.PurchaseInvoice, notification.Type);

            // ---- Test 28: the URL comes from IEntityRegistry, not a hand-built string ----
            Assert.Equal("/Accounting/PurchaseInvoiceDetail?id=21", notification.Url);

            // ---- Test 29: CompanyID is preserved ----
            Assert.Equal(1, notification.CompanyID);

            // Entity addressing + actor + dedup key are all populated.
            Assert.Equal(EntityRegistry.PurchaseInvoice, notification.EntityType);
            Assert.Equal(21, notification.EntityId);
            Assert.Equal(ActorId, notification.ActorEmployeeID);
            Assert.StartsWith("evt:" + stored.EventUid.ToString("N"), notification.DedupKey);
            Assert.Equal("Purchasing", notification.Category);   // from the NotificationTypes catalog

            // Bilingual, and carrying the real document reference.
            Assert.Contains("PV-2026-00021", notification.BodyEn);
            Assert.Contains("PV-2026-00021", notification.BodyAr);
            Assert.NotEqual(notification.TitleAr, notification.TitleEn);
        }

        // ---- Test 23: a retry does not create duplicates ----
        [Fact]
        public async Task Redelivery_is_idempotent_even_after_the_notification_was_read()
        {
            using var host = await SeedAsync();
            var (consumer, sink) = Consumer(host);
            var stored = await AddPurchaseInvoiceEventAsync(host);
            var envelope = host.Events().BuildEnvelope(stored);

            await consumer.HandleAsync(envelope);
            Assert.Single(sink.Written);

            // Mark it READ, which is exactly the case NotificationService's own dedup misses (it filters on
            // unread rows), then redeliver as a stale-claim retry would.
            var first = await host.Db.Notifications.SingleAsync();
            first.IsRead = true; first.ReadAt = DateTime.UtcNow;
            await host.Db.SaveChangesAsync();

            await consumer.HandleAsync(envelope);
            await consumer.HandleAsync(envelope);

            Assert.Equal(1, await host.NewContext().Notifications.CountAsync());
            Assert.Single(sink.Written);
        }

        // ---- Test 24: actor exclusion ----
        [Fact]
        public async Task The_actor_is_not_notified_about_their_own_action()
        {
            using var host = await SeedAsync();

            // The ChiefAccountant is now also the actor, so the only candidate is excluded.
            var (consumer, sink) = Consumer(host);
            var stored = await AddPurchaseInvoiceEventAsync(host, actorId: ChiefAccountantId);

            await consumer.HandleAsync(host.Events().BuildEnvelope(stored));

            Assert.Empty(sink.Written);
            Assert.Equal(0, await host.NewContext().Notifications.CountAsync());
        }

        // ---- Test 25: recipient permission is enforced ----
        [Fact]
        public async Task A_recipient_who_cannot_open_the_record_is_not_notified()
        {
            using var host = await SeedAsync();

            // The audience resolves, but nobody may View the entity — so no dead-end link is ever sent.
            var (consumer, sink) = Consumer(host, new StubPermissionProvider());
            var stored = await AddPurchaseInvoiceEventAsync(host);

            await consumer.HandleAsync(host.Events().BuildEnvelope(stored));

            Assert.Empty(sink.Written);
        }

        [Fact]
        public async Task An_event_with_no_audience_is_a_successful_no_op()
        {
            using var host = await SeedAsync(withAccountingRole: false);   // nobody holds the role
            var (consumer, sink) = Consumer(host);
            var stored = await AddPurchaseInvoiceEventAsync(host);

            await consumer.HandleAsync(host.Events().BuildEnvelope(stored));   // must not throw

            Assert.Empty(sink.Written);
        }

        [Fact]
        public async Task A_timeline_only_event_produces_no_notification_and_does_not_fail()
        {
            using var host = await SeedAsync();
            var (consumer, sink) = Consumer(host);

            // Customer events are deliberately timeline-only: master-data edits are routine and had no legacy
            // notification, so notifying on every one would be noise.
            var ev = new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = 1,
                EntityType = EntityRegistry.Customer, EntityId = 11, EventType = CustomerEvents.Updated,
                ActorEmployeeId = ActorId, Visibility = BusinessEventVisibility.Internal,
                PayloadVersion = 1, CreatedAt = DateTime.UtcNow,
            };
            host.Db.BusinessEvents.Add(ev);
            await host.Db.SaveChangesAsync();

            await consumer.HandleAsync(host.Events().BuildEnvelope(ev));

            Assert.Empty(sink.Written);
            Assert.Empty(new BusinessEventNotificationMapper().Map(host.Events().BuildEnvelope(ev)));
        }

        // ---- Tests 26 + 27 + 30: per-consumer independence through the real dispatch store ----
        [Fact]
        public async Task A_notification_failure_leaves_the_timeline_consumer_Done_and_retries_only_itself()
        {
            using var host = await SeedAsync();
            var store = host.DispatchStore(options: new BusinessEventDispatchOptions { RetryBackoffSeconds = 0 });
            var stored = await AddPurchaseInvoiceEventAsync(host);

            // Both consumers get their own row, exactly as RecordAsync would have created them.
            foreach (var consumerName in BusinessEventConsumers.Registered)
            {
                host.Db.BusinessEventDispatches.Add(new BusinessEventDispatch
                {
                    EventId = stored.EventId, Consumer = consumerName,
                    Status = BusinessEventDispatchStatus.Pending, Attempts = 0, UpdatedAt = DateTime.UtcNow,
                });
            }
            await host.Db.SaveChangesAsync();

            // Timeline succeeds.
            var timelineWork = await store.ClaimPendingAsync(BusinessEventConsumers.TimelineProjection, 10);
            await store.MarkDoneAsync(timelineWork.Single().DispatchId);
            await store.TryCompleteEventAsync(stored.EventId);

            // ---- Test 30: CompletedAt waits for ALL consumers ----
            Assert.Null((await host.NewContext().BusinessEvents.SingleAsync()).CompletedAt);

            // Notifications fail once.
            var (consumer, sink) = Consumer(host);
            sink.Failures = 1;
            var notifyWork = await store.ClaimPendingAsync(BusinessEventConsumers.NotificationProjection, 10);
            var notifyDispatchId = notifyWork.Single().DispatchId;
            try
            {
                await consumer.HandleAsync(host.Events().BuildEnvelope(stored));
                Assert.Fail("the sink was primed to throw");
            }
            catch (InvalidOperationException ex)
            {
                await store.MarkFailedAsync(notifyDispatchId, ex.Message);
            }

            // ---- Test 26: the timeline consumer stays Done ----
            using (var verify = host.NewContext())
            {
                var rows = await verify.BusinessEventDispatches.ToDictionaryAsync(d => d.Consumer);
                Assert.Equal(BusinessEventDispatchStatus.Done, rows[BusinessEventConsumers.TimelineProjection].Status);
                Assert.Equal(BusinessEventDispatchStatus.Failed, rows[BusinessEventConsumers.NotificationProjection].Status);
                Assert.Contains("notification sink unavailable", rows[BusinessEventConsumers.NotificationProjection].Error);
            }

            // ---- Test 27: only the notification consumer is retried ----
            Assert.Empty(await store.ClaimPendingAsync(BusinessEventConsumers.TimelineProjection, 10));
            var retry = await store.ClaimPendingAsync(BusinessEventConsumers.NotificationProjection, 10);
            Assert.Equal(notifyDispatchId, Assert.Single(retry).DispatchId);

            // The retry succeeds and delivers exactly one notification.
            await consumer.HandleAsync(host.Events().BuildEnvelope(stored));
            await store.MarkDoneAsync(notifyDispatchId);
            await store.TryCompleteEventAsync(stored.EventId);

            Assert.Single(sink.Written);
            Assert.Equal(1, await host.NewContext().Notifications.CountAsync());

            // The event does NOT complete yet, because a THIRD consumer now exists: AI Foundation
            // Increment 1 registered AiProjection, so every event carries a third dispatch row. That is
            // exactly the rule this test asserts — an event completes only when EVERY consumer is Done —
            // so the assertion is unchanged and the test now drives all three consumers rather than two.
            Assert.Null((await host.NewContext().BusinessEvents.SingleAsync()).CompletedAt);

            var ai = await store.ClaimPendingAsync(BusinessEventConsumers.AiProjection, 10);
            await store.MarkDoneAsync(Assert.Single(ai).DispatchId);
            await store.TryCompleteEventAsync(stored.EventId);

            // NOW every consumer is Done, so the event completes.
            Assert.NotNull((await host.NewContext().BusinessEvents.SingleAsync()).CompletedAt);
        }

        [Fact]
        public async Task Work_order_events_notify_the_inventory_audience()
        {
            using var host = await SeedAsync(withAccountingRole: false);

            host.Db.InventoryUserRoles.Add(new CrossBuy.Models.Context.Inventory.InventoryUserRole
            {
                CompanyID = 1, EmployeeId = ChiefAccountantId, Role = "InventoryManager",
            });
            await host.Db.SaveChangesAsync();

            var (consumer, sink) = Consumer(host);
            var ev = new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = 1,
                EntityType = EntityRegistry.ManufWorkOrder, EntityId = 31,
                EventType = ManufWorkOrderEvents.Released,
                ActorEmployeeId = ActorId, Visibility = BusinessEventVisibility.Internal,
                PayloadVersion = ManufWorkOrderEventPayload.Version, CreatedAt = DateTime.UtcNow,
                Payload = "{\"workOrderNumber\":\"WO-00031\",\"itemName\":\"FG-1\",\"plannedQuantity\":10.0}",
            };
            host.Db.BusinessEvents.Add(ev);
            await host.Db.SaveChangesAsync();

            await consumer.HandleAsync(host.Events().BuildEnvelope(ev));

            var notification = Assert.Single(sink.Written);
            Assert.Equal(NotificationTypes.WorkOrderReleased, notification.Type);
            Assert.Equal("Manufacturing", notification.Category);
            Assert.Equal("/Inventory/WorkOrderDetails?id=31", notification.Url);
            Assert.Contains("WO-00031", notification.BodyEn);
        }

        [Fact]
        public async Task The_mapper_covers_exactly_the_event_types_this_slice_converted()
        {
            var mapper = new BusinessEventNotificationMapper();
            using var host = new PlatformTestHost();

            (string type, bool expectNotification)[] cases =
            {
                (SalesInvoiceEvents.Created, true),        // legacy producer removed from ReceivableService
                (PurchaseInvoiceEvents.Created, true),     // legacy producer removed from PayableService
                (ManufWorkOrderEvents.Released, true),      // new
                (ManufWorkOrderEvents.Completed, true),     // new
                (SalesInvoiceEvents.Updated, false),
                (PurchaseInvoiceEvents.Updated, false),
                (CustomerEvents.Created, false),
                (CustomerEvents.Updated, false),
                (ManufWorkOrderEvents.Created, false),
                (ManufWorkOrderEvents.Updated, false),
                (ManufWorkOrderEvents.Produced, false),
                (ManufWorkOrderEvents.Cancelled, false),
            };

            foreach (var (type, expected) in cases)
            {
                var envelope = new BusinessEventEnvelope
                {
                    EventUid = Guid.NewGuid(), EventType = type,
                    Entity = new BusinessEventEntity { Code = type.Split('.')[0], Id = 1 },
                    Actor = new BusinessEventActor { EmployeeId = ActorId },
                    Context = new BusinessEventContext { CompanyId = 1 },
                    PayloadVersion = 1, Visibility = BusinessEventVisibility.Internal,
                    OccurredAt = DateTime.UtcNow,
                };
                var mapped = mapper.Map(envelope);
                Assert.Equal(expected, mapped.Count > 0);

                // Every produced command must name a registered entity and a supported audience scope.
                foreach (var command in mapped)
                {
                    Assert.True(host.Registry().IsValid(command.EntityType));
                    Assert.True(command.IsRoleTargeted);
                    Assert.Contains(command.RecipientScope, new[] { "acc", "inv" });
                }
            }
        }
    }
}