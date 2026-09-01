using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Crm;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // Platform Kernel slice 2 — timeline for the three pilot entities: legacy history, isolation and merging.
    public class Slice2TimelineTests
    {
        private static ITimelineProjectionService Projection(
            PlatformTestHost host, IPlatformPermissionProvider permissions, bool withLegacy = true,
            CrossBuy.Models.Context.CrossDbContext? db = null)
        {
            var ctx = db ?? host.Db;
            var registry = host.Registry(ctx);
            var adapters = withLegacy
                ? new ILegacyTimelineAdapter[]
                {
                    new SalesInvoiceLegacyTimelineAdapter(ctx, registry),
                    new CustomerLegacyTimelineAdapter(ctx, registry),
                    new PurchaseInvoiceLegacyTimelineAdapter(ctx, registry),
                    new ManufWorkOrderLegacyTimelineAdapter(ctx, registry),
                }
                : Array.Empty<ILegacyTimelineAdapter>();
            return new TimelineProjectionService(ctx, registry, permissions, adapters);
        }

        private static Task AddEventAsync(
            PlatformTestHost host, string entityCode, int entityId, string eventType,
            int companyId = 1, string visibility = BusinessEventVisibility.Internal,
            DateTime? createdAt = null, int? actorEmployeeId = null, string? payload = null)
        {
            // B4: events are seeded through the authorized arrangement context because the isolation tests
            // deliberately create OTHER companies' events. Reading stays filtered.
            host.Seed.BusinessEvents.Add(new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = companyId,
                EntityType = entityCode, EntityId = entityId, EventType = eventType,
                Visibility = visibility, PayloadVersion = 1,
                CreatedAt = createdAt ?? DateTime.UtcNow,
                ActorEmployeeId = actorEmployeeId,
                Payload = payload,
            });
            return host.Seed.SaveChangesAsync();
        }

        // ---- Test 15: existing Customer history remains visible ----
        [Fact]
        public async Task A_pre_kernel_customer_shows_its_creation_and_its_CRM_activities()
        {
            using var host = new PlatformTestHost();
            var createdAt = new DateTime(2025, 4, 1, 9, 0, 0, DateTimeKind.Utc);

            var customer = new Customer { CompanyID = 1, Name = "عميل قديم", Segment = "VIP", CreatedAt = createdAt, IsActive = true };
            host.Db.Customers.Add(customer);
            await host.Db.SaveChangesAsync();

            // Two CRM activities, linked the two different ways the schema allows.
            host.Db.Activities.Add(new Activity
            {
                CompanyID = 1, Type = "Call", Subject = "مكالمة متابعة", SubjectEn = "Follow-up call",
                CustomerId = customer.ID, CreatedAt = createdAt.AddDays(5), Done = true,
            });
            host.Db.Activities.Add(new Activity
            {
                CompanyID = 1, Type = "Meeting", Subject = "زيارة", SubjectEn = "Site visit",
                EntityType = EntityRegistry.Customer, EntityId = customer.ID, CreatedAt = createdAt.AddDays(10),
            });
            await host.Db.SaveChangesAsync();

            var projection = Projection(host, new StubPermissionProvider(PlatformActions.View));
            var items = await projection.GetAsync(EntityRegistry.Customer, customer.ID, PlatformTestHost.DefaultContext());

            Assert.Equal(3, items.Count);
            Assert.All(items, i => Assert.Equal(TimelineItemSource.Legacy, i.Source));
            Assert.Contains(items, i => i.TitleEn == "Customer added");
            Assert.Contains(items, i => i.TitleEn == "Call");
            Assert.Contains(items, i => i.TitleEn == "Meeting");
            Assert.All(items, i => Assert.Contains(customer.ID.ToString(), i.Url));
        }

        // ---- Test 16: existing Purchase Invoice history remains visible ----
        [Fact]
        public async Task A_pre_kernel_purchase_invoice_shows_its_recording_and_re_posts()
        {
            using var host = new PlatformTestHost();
            var recordedAt = new DateTime(2025, 5, 1, 11, 0, 0, DateTimeKind.Utc);

            host.Db.Vendors.Add(new Vendor { CompanyID = 1, Name = "مورّد قديم" });
            await host.Db.SaveChangesAsync();
            var vendorId = host.Db.Vendors.Single().ID;

            var invoice = new PurchaseInvoice
            {
                CompanyID = 1, VendorId = vendorId, InvoiceNo = "PV-2025-00007",
                InvoiceDate = recordedAt.Date, CreatedAt = recordedAt, Status = "Posted", GrandTotal = 900m,
            };
            host.Db.PurchaseInvoices.Add(invoice);
            await host.Db.SaveChangesAsync();

            host.Db.JournalEntries.Add(new JournalEntry
            {
                CompanyID = 1, EntryNo = "JV-2025-000300", EntryDate = recordedAt.Date, FiscalPeriodId = 1,
                JournalType = "Auto", SourceType = "PurchaseInvoice", SourceId = invoice.ID, CurrencyId = 1,
                Status = "Posted", CreatedAt = recordedAt,
            });
            host.Db.JournalEntries.Add(new JournalEntry
            {
                CompanyID = 1, EntryNo = "JV-2025-000380", EntryDate = recordedAt.Date.AddDays(2), FiscalPeriodId = 1,
                JournalType = "Auto", SourceType = "PurchaseInvoice", SourceId = invoice.ID, CurrencyId = 1,
                Status = "Posted", CreatedAt = recordedAt.AddDays(2),
            });
            // A reversal must NOT count as an edit.
            host.Db.JournalEntries.Add(new JournalEntry
            {
                CompanyID = 1, EntryNo = "JV-2025-000379", EntryDate = recordedAt.Date.AddDays(2), FiscalPeriodId = 1,
                JournalType = "Reversing", SourceType = "Reversal", SourceId = 1, CurrencyId = 1,
                Status = "Posted", CreatedAt = recordedAt.AddDays(2),
            });
            await host.Db.SaveChangesAsync();

            var projection = Projection(host, new StubPermissionProvider(PlatformActions.View));
            var items = await projection.GetAsync(EntityRegistry.PurchaseInvoice, invoice.ID, PlatformTestHost.DefaultContext());

            Assert.Equal(2, items.Count);
            Assert.All(items, i => Assert.Equal(TimelineItemSource.Legacy, i.Source));
            Assert.Contains(items, i => i.EventType == PurchaseInvoiceEvents.Created);
            Assert.Contains(items, i => i.EventType == PurchaseInvoiceEvents.Updated);
            Assert.Contains(items, i => i.DescriptionEn!.Contains("مورّد قديم"));
            Assert.All(items, i => Assert.Null(i.ActorEmployeeId));   // no actor invented
        }

        // ---- Test 17: existing Work Order history remains visible ----
        [Fact]
        public async Task A_pre_kernel_work_order_shows_its_own_lifecycle_stamps()
        {
            using var host = new PlatformTestHost();
            var createdAt = new DateTime(2025, 6, 1, 8, 0, 0, DateTimeKind.Utc);

            host.Db.ManufWorkOrders.Add(new ManufWorkOrder
            {
                CompanyID = 1, ItemId = 5, Qty = 20m, ProducedQty = 20m, WarehouseId = 1,
                WoNo = "WO-00099", Status = "Completed",
                CreatedAt = createdAt,
                ReleasedAt = createdAt.AddDays(1),
                CompletedAt = createdAt.AddDays(4),
                ClosedAt = createdAt.AddDays(4),   // same moment as completion -> must NOT be a second row
            });
            await host.Db.SaveChangesAsync();
            var id = host.Db.ManufWorkOrders.Single().ID;

            var projection = Projection(host, new StubPermissionProvider(PlatformActions.View));
            var items = await projection.GetAsync(EntityRegistry.ManufWorkOrder, id, PlatformTestHost.DefaultContext());

            // Created + Released + Completed. ClosedAt equals CompletedAt, so it is suppressed.
            Assert.Equal(3, items.Count);
            Assert.All(items, i => Assert.Equal(TimelineItemSource.Legacy, i.Source));
            Assert.Contains(items, i => i.EventType == ManufWorkOrderEvents.Created);
            Assert.Contains(items, i => i.EventType == ManufWorkOrderEvents.Released);
            Assert.Contains(items, i => i.EventType == ManufWorkOrderEvents.Completed);
            Assert.DoesNotContain(items, i => i.TitleEn == "Work order closed");
        }

        [Fact]
        public async Task A_cancelled_pre_kernel_work_order_is_not_given_an_invented_date()
        {
            using var host = new PlatformTestHost();
            var createdAt = new DateTime(2025, 6, 1, 8, 0, 0, DateTimeKind.Utc);

            host.Db.ManufWorkOrders.Add(new ManufWorkOrder
            {
                CompanyID = 1, ItemId = 5, Qty = 20m, WarehouseId = 1, WoNo = "WO-00100",
                Status = "Cancelled", CreatedAt = createdAt,   // cancellation has NO timestamp in the schema
            });
            await host.Db.SaveChangesAsync();
            var id = host.Db.ManufWorkOrders.Single().ID;

            var projection = Projection(host, new StubPermissionProvider(PlatformActions.View));
            var items = await projection.GetAsync(EntityRegistry.ManufWorkOrder, id, PlatformTestHost.DefaultContext());

            // Only creation can be dated honestly. The cancellation is real but undateable, so it is NOT
            // fabricated onto the timeline — documented in ADR-005.
            var item = Assert.Single(items);
            Assert.Equal(ManufWorkOrderEvents.Created, item.EventType);
            Assert.DoesNotContain(items, i => i.EventType == ManufWorkOrderEvents.Cancelled);
        }

        // ---- Test 18: unauthorized users cannot read timeline events, for every pilot ----
        [Theory]
        [InlineData(EntityRegistry.Customer)]
        [InlineData(EntityRegistry.PurchaseInvoice)]
        [InlineData(EntityRegistry.ManufWorkOrder)]
        public async Task View_denial_blocks_the_timeline_for_every_pilot_entity(string code)
        {
            using var host = new PlatformTestHost();
            await AddEventAsync(host, code, 5, EventTypeFor(code));

            var denied = Projection(host, new StubPermissionProvider(), withLegacy: false);
            await Assert.ThrowsAsync<PlatformAccessDeniedException>(
                () => denied.GetAsync(code, 5, PlatformTestHost.DefaultContext()));

            // ...and a viewer sees only the Internal tier.
            await AddEventAsync(host, code, 5, EventTypeFor(code), visibility: BusinessEventVisibility.Confidential);
            var viewer = Projection(host, new StubPermissionProvider(PlatformActions.View), withLegacy: false);
            var items = await viewer.GetAsync(code, 5, PlatformTestHost.DefaultContext());
            Assert.Equal(BusinessEventVisibility.Internal, Assert.Single(items).Visibility);
        }

        // ---- Test 19: company isolation, for every pilot ----
        [Theory]
        [InlineData(EntityRegistry.Customer)]
        [InlineData(EntityRegistry.PurchaseInvoice)]
        [InlineData(EntityRegistry.ManufWorkOrder)]
        public async Task Company_isolation_holds_for_every_pilot_entity(string code)
        {
            using var host = new PlatformTestHost();
            const int entityId = 5;

            await AddEventAsync(host, code, entityId, EventTypeFor(code), companyId: 1);
            await AddEventAsync(host, code, entityId, EventTypeFor(code), companyId: 2);
            await AddEventAsync(host, code, entityId, EventTypeFor(code), companyId: 2);

            var projection = Projection(host, new StubPermissionProvider(
                PlatformActions.View, PlatformActions.ViewConfidential, PlatformActions.ViewRestricted), withLegacy: false);

            Assert.Single(await projection.GetAsync(code, entityId, PlatformTestHost.DefaultContext(companyId: 1)));

            // B2: company 2's timeline is read in COMPANY 2's own scope. Passing a company-2 BusinessContext to a
            // service running in a company-1 scope is no longer a coherent arrangement — the query filter obeys
            // the scope, and production never mixes the two (one DI scope holds one company).
            var second = host.Request(2);
            var asCompanyTwo = Projection(host, new StubPermissionProvider(
                PlatformActions.View, PlatformActions.ViewConfidential, PlatformActions.ViewRestricted),
                withLegacy: false, db: second.Db);
            Assert.Equal(2, (await asCompanyTwo.GetAsync(code, entityId, PlatformTestHost.DefaultContext(companyId: 2))).Count);
        }

        // ---- Test 20: events and legacy entries merge newest first ----
        [Fact]
        public async Task Events_and_legacy_entries_merge_newest_first()
        {
            using var host = new PlatformTestHost();
            var createdAt = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc);

            host.Db.ManufWorkOrders.Add(new ManufWorkOrder
            {
                CompanyID = 1, ItemId = 5, Qty = 20m, WarehouseId = 1, WoNo = "WO-00101",
                Status = "Released", CreatedAt = createdAt, ReleasedAt = createdAt.AddDays(1),
            });
            await host.Db.SaveChangesAsync();
            var id = host.Db.ManufWorkOrders.Single().ID;

            // A real event recorded LATER than both legacy stamps.
            await AddEventAsync(host, EntityRegistry.ManufWorkOrder, id, ManufWorkOrderEvents.Produced,
                createdAt: createdAt.AddDays(10));

            var projection = Projection(host, new StubPermissionProvider(PlatformActions.View));
            var items = await projection.GetAsync(EntityRegistry.ManufWorkOrder, id, PlatformTestHost.DefaultContext());

            Assert.Equal(3, items.Count);
            // Strictly descending by time, regardless of which source each row came from.
            for (int i = 1; i < items.Count; i++)
                Assert.True(items[i - 1].CreatedAt >= items[i].CreatedAt, "timeline is not newest-first");

            Assert.Equal(TimelineItemSource.Event, items[0].Source);
            Assert.Equal(ManufWorkOrderEvents.Produced, items[0].EventType);
        }

        // ---- Test 21: duplicate entries are suppressed ----
        [Fact]
        public async Task Legacy_entries_are_suppressed_once_real_events_cover_them()
        {
            using var host = new PlatformTestHost();
            var createdAt = new DateTime(2025, 6, 1, 8, 0, 0, DateTimeKind.Utc);

            host.Db.ManufWorkOrders.Add(new ManufWorkOrder
            {
                CompanyID = 1, ItemId = 5, Qty = 20m, WarehouseId = 1, WoNo = "WO-00102",
                Status = "Released", CreatedAt = createdAt, ReleasedAt = createdAt.AddDays(1),
            });
            await host.Db.SaveChangesAsync();
            var id = host.Db.ManufWorkOrders.Single().ID;

            // Real events for BOTH facts the adapter would otherwise reconstruct.
            await AddEventAsync(host, EntityRegistry.ManufWorkOrder, id, ManufWorkOrderEvents.Created, createdAt: createdAt.AddYears(1));
            await AddEventAsync(host, EntityRegistry.ManufWorkOrder, id, ManufWorkOrderEvents.Released, createdAt: createdAt.AddYears(1).AddMinutes(5));

            var projection = Projection(host, new StubPermissionProvider(PlatformActions.View));
            var items = await projection.GetAsync(EntityRegistry.ManufWorkOrder, id, PlatformTestHost.DefaultContext());

            // Exactly one row per fact, and the recorded event wins over the reconstruction.
            Assert.Equal(2, items.Count);
            Assert.All(items, i => Assert.Equal(TimelineItemSource.Event, i.Source));
            Assert.Single(items, i => i.EventType == ManufWorkOrderEvents.Created);
            Assert.Single(items, i => i.EventType == ManufWorkOrderEvents.Released);
        }

        [Fact]
        public async Task Legacy_reconstruction_is_stable_across_reads_for_every_pilot()
        {
            using var host = new PlatformTestHost();
            var when = new DateTime(2025, 7, 1, 9, 0, 0, DateTimeKind.Utc);

            host.Db.Customers.Add(new Customer { CompanyID = 1, Name = "ثابت", CreatedAt = when, IsActive = true });
            host.Db.PurchaseInvoices.Add(new PurchaseInvoice
            {
                CompanyID = 1, VendorId = 1, InvoiceNo = "PV-STABLE", InvoiceDate = when.Date,
                CreatedAt = when, Status = "Posted", GrandTotal = 5m,
            });
            host.Db.ManufWorkOrders.Add(new ManufWorkOrder
            {
                CompanyID = 1, ItemId = 5, Qty = 1m, WarehouseId = 1, WoNo = "WO-STABLE",
                Status = "Draft", CreatedAt = when,
            });
            await host.Db.SaveChangesAsync();

            var projection = Projection(host, new StubPermissionProvider(PlatformActions.View));
            var context = PlatformTestHost.DefaultContext();

            foreach (var (code, id) in new[]
            {
                (EntityRegistry.Customer, host.Db.Customers.Single().ID),
                (EntityRegistry.PurchaseInvoice, host.Db.PurchaseInvoices.Single().ID),
                (EntityRegistry.ManufWorkOrder, host.Db.ManufWorkOrders.Single().ID),
            })
            {
                var first = await projection.GetAsync(code, id, context);
                var second = await projection.GetAsync(code, id, context);
                Assert.NotEmpty(first);
                Assert.Equal(first.Select(i => i.EventUid), second.Select(i => i.EventUid));
            }
        }

        [Fact]
        public async Task An_entity_with_no_legacy_data_returns_an_empty_result_safely()
        {
            using var host = new PlatformTestHost();

            // No customer / invoice / work-order row exists at all: every adapter must return empty rather
            // than throwing or fabricating a row.
            var projection = Projection(host, new StubPermissionProvider(PlatformActions.View));
            var context = PlatformTestHost.DefaultContext();

            Assert.Empty(await projection.GetAsync(EntityRegistry.Customer, 4242, context));
            Assert.Empty(await projection.GetAsync(EntityRegistry.PurchaseInvoice, 4242, context));
            Assert.Empty(await projection.GetAsync(EntityRegistry.ManufWorkOrder, 4242, context));
        }

        [Fact]
        public async Task A_customer_row_with_no_CreatedAt_is_not_dated_to_now()
        {
            using var host = new PlatformTestHost();

            // Older customer rows can have a NULL CreatedAt. There is no honest timestamp, so no item.
            host.Db.Customers.Add(new Customer { CompanyID = 1, Name = "بلا تاريخ", CreatedAt = null, IsActive = true });
            await host.Db.SaveChangesAsync();
            var id = host.Db.Customers.Single().ID;

            var projection = Projection(host, new StubPermissionProvider(PlatformActions.View));
            Assert.Empty(await projection.GetAsync(EntityRegistry.Customer, id, PlatformTestHost.DefaultContext()));
        }

        private static string EventTypeFor(string code) => code switch
        {
            EntityRegistry.Customer => CustomerEvents.Created,
            EntityRegistry.PurchaseInvoice => PurchaseInvoiceEvents.Created,
            EntityRegistry.ManufWorkOrder => ManufWorkOrderEvents.Created,
            _ => throw new ArgumentOutOfRangeException(nameof(code)),
        };
    }
}