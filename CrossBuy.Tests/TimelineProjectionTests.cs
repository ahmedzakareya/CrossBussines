using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    public class TimelineProjectionTests
    {
        private static ITimelineProjectionService Projection(
            PlatformTestHost host, IPlatformPermissionProvider permissions,
            CrossBuy.Models.Context.CrossDbContext? db = null, bool withLegacyAdapter = true)
        {
            var context = db ?? host.Db;
            var registry = host.Registry(context);
            var adapters = withLegacyAdapter
                ? new ILegacyTimelineAdapter[] { new SalesInvoiceLegacyTimelineAdapter(context, registry) }
                : Array.Empty<ILegacyTimelineAdapter>();
            return new TimelineProjectionService(context, registry, permissions, adapters);
        }

        private static async Task AddEventAsync(
            CrossBuy.Models.Context.CrossDbContext db, int companyId, int entityId, string visibility,
            int? actorEmployeeId = null, DateTime? createdAt = null, string? eventType = null)
        {
            db.BusinessEvents.Add(new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = companyId,
                EntityType = EntityRegistry.SalesInvoice, EntityId = entityId,
                EventType = eventType ?? SalesInvoiceEvents.Created,
                ActorEmployeeId = actorEmployeeId, Visibility = visibility, PayloadVersion = 1,
                CreatedAt = createdAt ?? DateTime.UtcNow,
                Payload = "{\"referenceNumber\":\"SV-2026-00005\",\"totalAfter\":100.0}",
            });
            await db.SaveChangesAsync();
        }

        // ---- Test 11: an unauthorised user cannot read restricted timeline events ----
        [Fact]
        public async Task Visibility_filtering_hides_events_the_caller_may_not_read()
        {
            using var host = new PlatformTestHost();
            const int invoiceId = 5;

            await AddEventAsync(host.Db, 1, invoiceId, BusinessEventVisibility.Internal);
            await AddEventAsync(host.Db, 1, invoiceId, BusinessEventVisibility.Confidential);
            await AddEventAsync(host.Db, 1, invoiceId, BusinessEventVisibility.Restricted, actorEmployeeId: 99);
            await AddEventAsync(host.Db, 1, invoiceId, BusinessEventVisibility.System);

            var context = PlatformTestHost.DefaultContext(companyId: 1, employeeId: 7);

            // A plain viewer sees ONLY Internal. Confidential / Restricted-by-someone-else / System are hidden.
            var viewer = Projection(host, new StubPermissionProvider(PlatformActions.View), withLegacyAdapter: false);
            var viewerItems = await viewer.GetAsync(EntityRegistry.SalesInvoice, invoiceId, context);
            Assert.Single(viewerItems);
            Assert.Equal(BusinessEventVisibility.Internal, viewerItems[0].Visibility);

            // Confidential rights add exactly one tier.
            var accountant = Projection(host, new StubPermissionProvider(PlatformActions.View, PlatformActions.ViewConfidential), withLegacyAdapter: false);
            var accountantItems = await accountant.GetAsync(EntityRegistry.SalesInvoice, invoiceId, context);
            Assert.Equal(2, accountantItems.Count);
            Assert.DoesNotContain(accountantItems, i => i.Visibility == BusinessEventVisibility.Restricted);
            Assert.DoesNotContain(accountantItems, i => i.Visibility == BusinessEventVisibility.System);

            // A manager sees everything.
            var manager = Projection(host, new StubPermissionProvider(
                PlatformActions.View, PlatformActions.ViewConfidential, PlatformActions.ViewRestricted), withLegacyAdapter: false);
            Assert.Equal(4, (await manager.GetAsync(EntityRegistry.SalesInvoice, invoiceId, context)).Count);
        }

        [Fact]
        public async Task A_restricted_event_is_readable_by_its_own_actor()
        {
            using var host = new PlatformTestHost();
            const int invoiceId = 5;

            await AddEventAsync(host.Db, 1, invoiceId, BusinessEventVisibility.Restricted, actorEmployeeId: 7);
            await AddEventAsync(host.Db, 1, invoiceId, BusinessEventVisibility.Restricted, actorEmployeeId: 99);

            var projection = Projection(host, new StubPermissionProvider(PlatformActions.View), withLegacyAdapter: false);
            var items = await projection.GetAsync(EntityRegistry.SalesInvoice, invoiceId, PlatformTestHost.DefaultContext(employeeId: 7));

            var item = Assert.Single(items);
            Assert.Equal(7, item.ActorEmployeeId);   // own row visible, the colleague's row is not
        }

        [Fact]
        public async Task A_caller_denied_View_gets_an_access_error_not_an_empty_timeline()
        {
            using var host = new PlatformTestHost();
            await AddEventAsync(host.Db, 1, 5, BusinessEventVisibility.Internal);

            // "You may not look at this invoice" must be distinguishable from "this invoice has no history",
            // otherwise the UI silently misreports a permission problem as an empty widget.
            var projection = Projection(host, new StubPermissionProvider(), withLegacyAdapter: false);
            await Assert.ThrowsAsync<PlatformAccessDeniedException>(
                () => projection.GetAsync(EntityRegistry.SalesInvoice, 5, PlatformTestHost.DefaultContext()));
        }

        // ---- Test 12: company A cannot read company B's events ----
        [Fact]
        public async Task Company_isolation_is_enforced_on_the_timeline()
        {
            using var host = new PlatformTestHost();
            const int invoiceId = 5;

            // The SAME entity id in two companies — the case a missing CompanyID filter would leak.
            // B4: two companies' events are ARRANGED through the authorized cross-company context.
            await AddEventAsync(host.Seed, 1, invoiceId, BusinessEventVisibility.Internal);
            await AddEventAsync(host.Seed, 2, invoiceId, BusinessEventVisibility.Internal);
            await AddEventAsync(host.Seed, 2, invoiceId, BusinessEventVisibility.Internal);

            var projection = Projection(host, new StubPermissionProvider(
                PlatformActions.View, PlatformActions.ViewConfidential, PlatformActions.ViewRestricted), withLegacyAdapter: false);

            Assert.Single(await projection.GetAsync(EntityRegistry.SalesInvoice, invoiceId, PlatformTestHost.DefaultContext(companyId: 1)));

            // B2: company 2's timeline is read in COMPANY 2's own scope — see Slice2TimelineTests for why the
            // mixed arrangement (company-2 context, company-1 scope) is no longer coherent.
            var second = host.Request(2);
            var asCompanyTwo = Projection(host, new StubPermissionProvider(
                PlatformActions.View, PlatformActions.ViewConfidential, PlatformActions.ViewRestricted),
                db: second.Db, withLegacyAdapter: false);
            Assert.Equal(2, (await asCompanyTwo.GetAsync(EntityRegistry.SalesInvoice, invoiceId, PlatformTestHost.DefaultContext(companyId: 2))).Count);
        }

        [Fact]
        public async Task Branch_scoped_events_stay_visible_to_a_caller_with_no_branch()
        {
            using var host = new PlatformTestHost();
            const int invoiceId = 5;

            host.Db.BusinessEvents.Add(new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = 1, BranchID = 42,
                EntityType = EntityRegistry.SalesInvoice, EntityId = invoiceId,
                EventType = SalesInvoiceEvents.Created, Visibility = BusinessEventVisibility.Internal,
                PayloadVersion = 1, CreatedAt = DateTime.UtcNow,
            });
            await host.Db.SaveChangesAsync();

            var projection = Projection(host, new StubPermissionProvider(PlatformActions.View), withLegacyAdapter: false);

            // Head office (no branch) must not lose branch-stamped history...
            Assert.Single(await projection.GetAsync(EntityRegistry.SalesInvoice, invoiceId, PlatformTestHost.DefaultContext(branchId: null)));
            // ...a user in that branch sees it...
            Assert.Single(await projection.GetAsync(EntityRegistry.SalesInvoice, invoiceId, PlatformTestHost.DefaultContext(branchId: 42)));
            // ...and a user in another branch does not.
            Assert.Empty(await projection.GetAsync(EntityRegistry.SalesInvoice, invoiceId, PlatformTestHost.DefaultContext(branchId: 43)));
        }

        // ---- Test 13: an existing invoice's timeline stays visible ----
        [Fact]
        public async Task A_pre_kernel_invoice_still_shows_a_timeline()
        {
            using var host = new PlatformTestHost();
            var issuedAt = new DateTime(2025, 6, 1, 10, 0, 0, DateTimeKind.Utc);

            var invoice = new SalesInvoice
            {
                CompanyID = 1, CustomerId = 1, InvoiceNo = "SV-2025-00042",
                InvoiceDate = issuedAt.Date, CreatedAt = issuedAt,
                Status = "Posted", GrandTotal = 250m,
            };
            host.Db.SalesInvoices.Add(invoice);
            await host.Db.SaveChangesAsync();

            // Two GL postings against the invoice: the original, plus one re-post from an edit.
            host.Db.JournalEntries.Add(new JournalEntry
            {
                CompanyID = 1, EntryNo = "JV-2025-000100", EntryDate = issuedAt.Date, FiscalPeriodId = 1,
                JournalType = "Auto", SourceType = "SalesInvoice", SourceId = invoice.ID, CurrencyId = 1,
                Status = "Posted", CreatedAt = issuedAt,
            });
            host.Db.JournalEntries.Add(new JournalEntry
            {
                CompanyID = 1, EntryNo = "JV-2025-000180", EntryDate = issuedAt.Date.AddDays(3), FiscalPeriodId = 1,
                JournalType = "Auto", SourceType = "SalesInvoice", SourceId = invoice.ID, CurrencyId = 1,
                Status = "Posted", CreatedAt = issuedAt.AddDays(3),
            });
            // A reversal must NOT be counted as an edit — it carries SourceType 'Reversal'.
            host.Db.JournalEntries.Add(new JournalEntry
            {
                CompanyID = 1, EntryNo = "JV-2025-000179", EntryDate = issuedAt.Date.AddDays(3), FiscalPeriodId = 1,
                JournalType = "Reversing", SourceType = "Reversal", SourceId = 1, CurrencyId = 1,
                Status = "Posted", CreatedAt = issuedAt.AddDays(3),
            });
            await host.Db.SaveChangesAsync();

            var projection = Projection(host, new StubPermissionProvider(PlatformActions.View));
            var items = await projection.GetAsync(EntityRegistry.SalesInvoice, invoice.ID, PlatformTestHost.DefaultContext());

            // The invoice predates the kernel and has no events, yet its timeline is NOT empty.
            Assert.Equal(2, items.Count);
            Assert.All(items, i => Assert.Equal(TimelineItemSource.Legacy, i.Source));
            Assert.Contains(items, i => i.EventType == SalesInvoiceEvents.Created);
            Assert.Contains(items, i => i.EventType == SalesInvoiceEvents.Updated);
            // No actor is invented for reconstructed history.
            Assert.All(items, i => Assert.Null(i.ActorEmployeeId));
            // The deep link still works.
            Assert.All(items, i => Assert.Contains(invoice.ID.ToString(), i.Url));
        }

        [Fact]
        public async Task Legacy_items_are_deduplicated_once_a_real_event_covers_them()
        {
            using var host = new PlatformTestHost();
            var issuedAt = new DateTime(2025, 6, 1, 10, 0, 0, DateTimeKind.Utc);

            var invoice = new SalesInvoice
            {
                CompanyID = 1, CustomerId = 1, InvoiceNo = "SV-2025-00043",
                InvoiceDate = issuedAt.Date, CreatedAt = issuedAt, Status = "Posted", GrandTotal = 250m,
            };
            host.Db.SalesInvoices.Add(invoice);
            await host.Db.SaveChangesAsync();

            // A real Created event now exists for this invoice.
            await AddEventAsync(host.Db, 1, invoice.ID, BusinessEventVisibility.Internal,
                actorEmployeeId: 7, createdAt: issuedAt.AddYears(1));

            var projection = Projection(host, new StubPermissionProvider(PlatformActions.View));
            var items = await projection.GetAsync(EntityRegistry.SalesInvoice, invoice.ID, PlatformTestHost.DefaultContext());

            // Exactly one Created row, and it is the recorded event — not the reconstruction.
            var created = Assert.Single(items, i => i.EventType == SalesInvoiceEvents.Created);
            Assert.Equal(TimelineItemSource.Event, created.Source);
            Assert.Equal(7, created.ActorEmployeeId);
        }

        [Fact]
        public async Task Legacy_reconstruction_is_stable_across_reads()
        {
            using var host = new PlatformTestHost();
            var invoice = new SalesInvoice
            {
                CompanyID = 1, CustomerId = 1, InvoiceNo = "SV-2025-00044",
                InvoiceDate = new DateTime(2025, 7, 1), CreatedAt = new DateTime(2025, 7, 1, 9, 0, 0, DateTimeKind.Utc),
                Status = "Posted", GrandTotal = 10m,
            };
            host.Db.SalesInvoices.Add(invoice);
            await host.Db.SaveChangesAsync();

            var projection = Projection(host, new StubPermissionProvider(PlatformActions.View));
            var context = PlatformTestHost.DefaultContext();

            var first = await projection.GetAsync(EntityRegistry.SalesInvoice, invoice.ID, context);
            var second = await projection.GetAsync(EntityRegistry.SalesInvoice, invoice.ID, context);

            // Deterministic identity: a refresh must not look like new activity.
            Assert.Equal(first.Select(i => i.EventUid), second.Select(i => i.EventUid));
            Assert.NotEqual(Guid.Empty, first[0].EventUid);
        }

        [Fact]
        public async Task Asking_for_a_timeline_on_an_unsupported_entity_is_a_programming_error()
        {
            using var host = new PlatformTestHost();
            var projection = Projection(host, new StubPermissionProvider(PlatformActions.View), withLegacyAdapter: false);

            // Item is registered but has not been onboarded to the timeline (Customer WAS, in slice 2 — the
            // assertion moved rather than being weakened, which is the flag doing its job).
            Assert.False(host.Registry().GetDefinition(EntityRegistry.Item).SupportsTimeline);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => projection.GetAsync(EntityRegistry.Item, 1, PlatformTestHost.DefaultContext()));

            // An unregistered code never reaches the query at all.
            await Assert.ThrowsAsync<EntityCodeNotRegisteredException>(
                () => projection.GetAsync("NotARealEntity", 1, PlatformTestHost.DefaultContext()));
        }

        [Fact]
        public async Task Recorded_events_render_bilingual_text_and_resolve_the_actor_name()
        {
            using var host = new PlatformTestHost();
            const int invoiceId = 5;

            // Employee has several non-nullable string columns; all are filled so the insert reflects a real row.
            host.Db.Employee.Add(new CrossBuy.Models.Context.Admin.Employee
            {
                ID = 7, FirstName = "أحمد", LastName = "المحاسب",
                FullName = "أحمد المحاسب", FullNameEn = "Ahmed the accountant", EmpCompanyID = 1,
                Address = "-", PhoneNumber = "-", Email = "ahmed@example.com",
                ProfileImage = "-", Gender = "M", MaritalStatus = "Single", UserId = "user-7",
            });
            await host.Db.SaveChangesAsync();
            await AddEventAsync(host.Db, 1, invoiceId, BusinessEventVisibility.Internal, actorEmployeeId: 7);

            var projection = Projection(host, new StubPermissionProvider(PlatformActions.View), withLegacyAdapter: false);
            var item = Assert.Single(await projection.GetAsync(EntityRegistry.SalesInvoice, invoiceId, PlatformTestHost.DefaultContext()));

            Assert.False(string.IsNullOrWhiteSpace(item.TitleAr));
            Assert.False(string.IsNullOrWhiteSpace(item.TitleEn));
            Assert.NotEqual(item.TitleAr, item.TitleEn);
            Assert.Contains("SV-2026-00005", item.DescriptionEn);
            Assert.False(string.IsNullOrWhiteSpace(item.ActorDisplayName));
            Assert.Equal(TimelineItemSource.Event, item.Source);
        }

        [Fact]
        public async Task An_unknown_event_type_degrades_instead_of_breaking_the_screen()
        {
            using var host = new PlatformTestHost();
            const int invoiceId = 5;

            // An event written by a newer build than the one rendering it.
            await AddEventAsync(host.Db, 1, invoiceId, BusinessEventVisibility.Internal,
                eventType: "SalesInvoice.SomethingFromTheFuture");

            var projection = Projection(host, new StubPermissionProvider(PlatformActions.View), withLegacyAdapter: false);
            var item = Assert.Single(await projection.GetAsync(EntityRegistry.SalesInvoice, invoiceId, PlatformTestHost.DefaultContext()));

            // The read path is lenient: it shows the canonical action rather than failing the document view.
            Assert.Equal("SomethingFromTheFuture", item.TitleEn);

            // The CONSUMER, by contrast, is strict — the same event fails its dispatch row so an operator
            // sees it, which is the whole point of validating at write time.
            var stored = await host.Db.BusinessEvents.SingleAsync();
            var consumer = new TimelineProjectionConsumer(
                host.Registry(), Microsoft.Extensions.Logging.Abstractions.NullLogger<TimelineProjectionConsumer>.Instance);
            await Assert.ThrowsAsync<BusinessEventContractException>(
                () => consumer.HandleAsync(host.Events().BuildEnvelope(stored)));
        }
    }
}