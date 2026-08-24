using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // ITimelineProjectionService.GetRecentForContextAsync — the governed "what changed recently" read.
    //
    // These tests exist because the recent feed is the first kernel read that crosses ENTITIES. The per-record
    // contract answers about one record the caller already named, so its authorization is one decision. A feed
    // answers about the company, so every filter it forgets becomes a disclosure across every module at once.
    // Each test below pins one filter, and several of them pin the SKIP-not-throw rule that separates a feed
    // from GetAsync — a feed that threw on the first unreadable record would leak existence by its own failure.
    public class TimelineRecentFeedTests
    {
        private static ITimelineProjectionService Feed(
            PlatformTestHost host, IPlatformPermissionProvider permissions,
            CrossBuy.Models.Context.CrossDbContext? db = null)
        {
            var context = db ?? host.Db;
            // No legacy adapters: the recent feed is platform Business Events only, by design. See the
            // LEGACY ADAPTERS comment on GetRecentForContextAsync for why they cannot be merged company-wide.
            return new TimelineProjectionService(
                context, host.Registry(context), permissions, Array.Empty<ILegacyTimelineAdapter>());
        }

        private static IPlatformPermissionProvider Viewer()
            => new StubPermissionProvider(PlatformActions.View);

        private static IPlatformPermissionProvider Manager()
            => new StubPermissionProvider(PlatformActions.View, PlatformActions.ViewConfidential, PlatformActions.ViewRestricted);

        // The feed drops any row whose entity cannot be resolved, so unlike the GetAsync tests these must seed a
        // REAL record. That is the point of the existence filter, not a fixture detail.
        private static async Task<int> SeedInvoiceAsync(
            CrossBuy.Models.Context.CrossDbContext db, int companyId, string invoiceNo)
        {
            var invoice = new SalesInvoice
            {
                CompanyID = companyId, CustomerId = 1, InvoiceDate = DateTime.UtcNow.Date,
                InvoiceNo = invoiceNo, Status = "Posted", GrandTotal = 100m,
            };
            db.SalesInvoices.Add(invoice);
            await db.SaveChangesAsync();
            return invoice.ID;
        }

        private static BusinessEvent NewEvent(
            int companyId, int entityId, string visibility, int? actorEmployeeId, DateTime createdAt,
            string? eventType = null, string? entityType = null, int? branchId = null)
            => new()
            {
                EventUid = Guid.NewGuid(), CompanyID = companyId, BranchID = branchId,
                EntityType = entityType ?? EntityRegistry.SalesInvoice, EntityId = entityId,
                EventType = eventType ?? SalesInvoiceEvents.Created,
                ActorEmployeeId = actorEmployeeId, Visibility = visibility, PayloadVersion = 1,
                CreatedAt = createdAt,
                Payload = "{\"referenceNumber\":\"SV-2026-00005\",\"totalAfter\":100.0}",
            };

        private static async Task AddEventAsync(
            CrossBuy.Models.Context.CrossDbContext db, int companyId, int entityId, string visibility,
            int? actorEmployeeId = null, DateTime? createdAt = null,
            string? eventType = null, string? entityType = null, int? branchId = null)
        {
            db.BusinessEvents.Add(NewEvent(
                companyId, entityId, visibility, actorEmployeeId,
                createdAt ?? DateTime.UtcNow, eventType, entityType, branchId));
            await db.SaveChangesAsync();
        }

        // Every event these tests write must land INSIDE the server-side lookback window, or the window filter
        // would silently be the thing under test in all eighteen of them.
        private static DateTime Recent(double hoursAgo = 1) => DateTime.UtcNow.AddHours(-hoursAgo);

        private static DateTime WindowFloor() => DateTime.UtcNow.AddDays(-TimelineProjectionService.MaxRecentLookbackDays);

        // ---- 1. company isolation ------------------------------------------------------------------------

        [Fact]
        public async Task Company_isolation_keeps_another_companys_activity_out_of_the_feed()
        {
            using var host = new PlatformTestHost();
            int mine = await SeedInvoiceAsync(host.Db, 1, "SV-1");

            // The same entity id in two companies is the case a missing CompanyID filter would leak.
            await AddEventAsync(host.Seed, 1, mine, BusinessEventVisibility.Internal, createdAt: Recent(2));
            await AddEventAsync(host.Seed, 2, mine, BusinessEventVisibility.Internal, createdAt: Recent(1));
            await AddEventAsync(host.Seed, 2, mine, BusinessEventVisibility.Internal, createdAt: Recent(1));

            var items = await Feed(host, Manager()).GetRecentForContextAsync(
                PlatformTestHost.DefaultContext(companyId: 1), WindowFloor(), 50);

            Assert.Single(items);
            Assert.Equal(mine, items[0].EntityId);
        }

        // ---- 2. fail closed ------------------------------------------------------------------------------

        [Fact]
        public async Task An_unresolved_company_returns_nothing_instead_of_everything()
        {
            using var host = new PlatformTestHost();
            int invoice = await SeedInvoiceAsync(host.Db, 1, "SV-1");
            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Internal, createdAt: Recent());

            // CompanyId 0 is "nobody resolved", which must never widen into "every company".
            var items = await Feed(host, Manager()).GetRecentForContextAsync(
                PlatformTestHost.DefaultContext(companyId: 0), WindowFloor(), 50);

            Assert.Empty(items);
        }

        // ---- 3 + 4. take clamp ---------------------------------------------------------------------------

        [Fact]
        public async Task Take_is_clamped_to_the_server_maximum()
        {
            using var host = new PlatformTestHost();
            int invoice = await SeedInvoiceAsync(host.Db, 1, "SV-1");

            for (int i = 0; i < TimelineProjectionService.MaxTake + 5; i++)
                host.Db.BusinessEvents.Add(NewEvent(1, invoice, BusinessEventVisibility.Internal, 7, Recent(i + 1)));
            await host.Db.SaveChangesAsync();

            var items = await Feed(host, Manager()).GetRecentForContextAsync(
                PlatformTestHost.DefaultContext(), WindowFloor(), take: 5000);

            Assert.Equal(TimelineProjectionService.MaxTake, items.Count);
        }

        [Fact]
        public async Task A_non_positive_take_is_clamped_up_rather_than_returning_everything()
        {
            using var host = new PlatformTestHost();
            int invoice = await SeedInvoiceAsync(host.Db, 1, "SV-1");
            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Internal, createdAt: Recent(3));
            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Internal, createdAt: Recent(2));
            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Internal, createdAt: Recent(1));

            var feed = Feed(host, Manager());
            Assert.Single(await feed.GetRecentForContextAsync(PlatformTestHost.DefaultContext(), WindowFloor(), take: 0));
            Assert.Single(await feed.GetRecentForContextAsync(PlatformTestHost.DefaultContext(), WindowFloor(), take: -5));
        }

        // ---- 5 + 6. the lookback window ------------------------------------------------------------------

        [Fact]
        public async Task The_lookback_window_cannot_be_widened_past_the_server_maximum()
        {
            using var host = new PlatformTestHost();
            int invoice = await SeedInvoiceAsync(host.Db, 1, "SV-1");

            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Internal,
                createdAt: DateTime.UtcNow.AddDays(-(TimelineProjectionService.MaxRecentLookbackDays + 30)));
            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Internal, createdAt: Recent());

            // A caller asking for a year still gets the server's window: the parameter narrows, never widens.
            var items = await Feed(host, Manager()).GetRecentForContextAsync(
                PlatformTestHost.DefaultContext(), DateTime.UtcNow.AddYears(-1), 50);

            Assert.Single(items);
            Assert.True(items[0].CreatedAt > WindowFloor());
        }

        [Fact]
        public async Task A_narrower_since_excludes_older_events()
        {
            using var host = new PlatformTestHost();
            int invoice = await SeedInvoiceAsync(host.Db, 1, "SV-1");

            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Internal, createdAt: DateTime.UtcNow.AddDays(-10));
            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Internal, createdAt: Recent());

            var items = await Feed(host, Manager()).GetRecentForContextAsync(
                PlatformTestHost.DefaultContext(), DateTime.UtcNow.AddDays(-2), 50);

            Assert.Single(items);
        }

        // ---- 7 + 8. deterministic ordering ---------------------------------------------------------------

        [Fact]
        public async Task The_feed_is_ordered_newest_first_across_entities()
        {
            using var host = new PlatformTestHost();
            int first = await SeedInvoiceAsync(host.Db, 1, "SV-1");
            int second = await SeedInvoiceAsync(host.Db, 1, "SV-2");

            await AddEventAsync(host.Db, 1, first, BusinessEventVisibility.Internal, createdAt: Recent(5));
            await AddEventAsync(host.Db, 1, second, BusinessEventVisibility.Internal, createdAt: Recent(1));
            await AddEventAsync(host.Db, 1, first, BusinessEventVisibility.Internal, createdAt: Recent(3));

            var items = await Feed(host, Manager()).GetRecentForContextAsync(
                PlatformTestHost.DefaultContext(), WindowFloor(), 50);

            Assert.Equal(3, items.Count);
            Assert.True(items[0].CreatedAt >= items[1].CreatedAt);
            Assert.True(items[1].CreatedAt >= items[2].CreatedAt);
            Assert.Equal(second, items[0].EntityId);
        }

        [Fact]
        public async Task Events_sharing_a_timestamp_are_broken_by_EventId_descending()
        {
            using var host = new PlatformTestHost();
            int invoice = await SeedInvoiceAsync(host.Db, 1, "SV-1");

            // Identical timestamps: without the EventId tiebreak the page order would be provider-dependent,
            // and a paged feed whose order is unstable silently drops and repeats rows between pages.
            var sameMoment = Recent();
            host.Db.BusinessEvents.Add(NewEvent(1, invoice, BusinessEventVisibility.Internal, 7, sameMoment));
            host.Db.BusinessEvents.Add(NewEvent(1, invoice, BusinessEventVisibility.Internal, 7, sameMoment));
            await host.Db.SaveChangesAsync();

            var newestEventUid = await host.Db.BusinessEvents.AsNoTracking()
                .OrderByDescending(e => e.EventId).Select(e => e.EventUid).FirstAsync();

            var items = await Feed(host, Manager()).GetRecentForContextAsync(
                PlatformTestHost.DefaultContext(), WindowFloor(), 50);

            Assert.Equal(2, items.Count);
            Assert.Equal(newestEventUid, items[0].EventUid);
        }

        // ---- 9. permission filtering: SKIP, never throw --------------------------------------------------

        [Fact]
        public async Task An_entity_the_caller_may_not_view_is_skipped_not_thrown()
        {
            using var host = new PlatformTestHost();
            int allowed = await SeedInvoiceAsync(host.Db, 1, "SV-1");
            int denied = await SeedInvoiceAsync(host.Db, 1, "SV-2");

            await AddEventAsync(host.Db, 1, allowed, BusinessEventVisibility.Internal, createdAt: Recent(2));
            await AddEventAsync(host.Db, 1, denied, BusinessEventVisibility.Internal, createdAt: Recent(1));

            var items = await Feed(host, new DenyOneEntityPermissionProvider(denied, PlatformActions.View))
                .GetRecentForContextAsync(PlatformTestHost.DefaultContext(), WindowFloor(), 50);

            // The denied record contributes nothing, and its denial does not take the readable record with it.
            Assert.Single(items);
            Assert.Equal(allowed, items[0].EntityId);
        }

        [Fact]
        public async Task A_feed_of_entirely_unreadable_records_is_empty_rather_than_an_access_error()
        {
            using var host = new PlatformTestHost();
            int invoice = await SeedInvoiceAsync(host.Db, 1, "SV-1");
            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Internal, createdAt: Recent());

            // GetAsync throws PlatformAccessDeniedException here, on purpose. The feed must not: a thrown
            // dashboard tells the caller something exists that they were not allowed to know about.
            var items = await Feed(host, new StubPermissionProvider())
                .GetRecentForContextAsync(PlatformTestHost.DefaultContext(), WindowFloor(), 50);

            Assert.Empty(items);
        }

        // ---- 10 + 11 + 12. event visibility --------------------------------------------------------------

        [Fact]
        public async Task Confidential_and_system_events_are_hidden_from_a_plain_viewer()
        {
            using var host = new PlatformTestHost();
            int invoice = await SeedInvoiceAsync(host.Db, 1, "SV-1");

            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Internal, createdAt: Recent(4));
            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Confidential, createdAt: Recent(3));
            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.System, createdAt: Recent(2));

            var items = await Feed(host, Viewer()).GetRecentForContextAsync(
                PlatformTestHost.DefaultContext(employeeId: 7), WindowFloor(), 50);

            Assert.Single(items);
            Assert.Equal(BusinessEventVisibility.Internal, items[0].Visibility);
        }

        [Fact]
        public async Task A_restricted_event_reaches_only_its_own_actor()
        {
            using var host = new PlatformTestHost();
            int invoice = await SeedInvoiceAsync(host.Db, 1, "SV-1");

            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Restricted, actorEmployeeId: 7, createdAt: Recent(2));
            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Restricted, actorEmployeeId: 99, createdAt: Recent(1));

            var items = await Feed(host, Viewer()).GetRecentForContextAsync(
                PlatformTestHost.DefaultContext(employeeId: 7), WindowFloor(), 50);

            // Exactly the own-actor row. Testing "Restricted is in the allowed set" instead of the manager grant
            // would hand every viewer every restricted row, which is why the row-level check is separate.
            Assert.Single(items);
            Assert.Equal(7, items[0].ActorEmployeeId);
        }

        [Fact]
        public async Task A_manager_sees_the_confidential_restricted_and_system_tiers()
        {
            using var host = new PlatformTestHost();
            int invoice = await SeedInvoiceAsync(host.Db, 1, "SV-1");

            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Internal, createdAt: Recent(4));
            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Confidential, createdAt: Recent(3));
            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Restricted, actorEmployeeId: 99, createdAt: Recent(2));
            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.System, createdAt: Recent(1));

            var items = await Feed(host, Manager()).GetRecentForContextAsync(
                PlatformTestHost.DefaultContext(employeeId: 7), WindowFloor(), 50);

            Assert.Equal(4, items.Count);
        }

        // ---- 13 + 14. rows whose entity is gone or unknown -----------------------------------------------

        [Fact]
        public async Task Events_whose_record_no_longer_exists_are_dropped()
        {
            using var host = new PlatformTestHost();
            int invoice = await SeedInvoiceAsync(host.Db, 1, "SV-1");
            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Internal, createdAt: Recent());

            // History outlives the record. A feed row pointing at a deleted invoice is a dead link on a
            // dashboard, so the row goes even though the event is still perfectly valid history.
            host.Db.SalesInvoices.Remove(await host.Db.SalesInvoices.FirstAsync(i => i.ID == invoice));
            await host.Db.SaveChangesAsync();

            var items = await Feed(host, Manager()).GetRecentForContextAsync(
                PlatformTestHost.DefaultContext(), WindowFloor(), 50);

            Assert.Empty(items);
        }

        [Fact]
        public async Task An_entity_code_the_registry_does_not_know_is_dropped_without_throwing()
        {
            using var host = new PlatformTestHost();
            int invoice = await SeedInvoiceAsync(host.Db, 1, "SV-1");

            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Internal, createdAt: Recent(1));
            // A code left behind by a module that no longer exists. GetAsync treats an unknown code as a
            // programming error; stored history is data, and one stale row must not end the feed.
            await AddEventAsync(host.Db, 1, 4242, BusinessEventVisibility.Internal,
                createdAt: Recent(2), entityType: "GhostModuleEntity");

            var items = await Feed(host, Manager()).GetRecentForContextAsync(
                PlatformTestHost.DefaultContext(), WindowFloor(), 50);

            Assert.Single(items);
            Assert.Equal(EntityRegistry.SalesInvoice, items[0].EntityType);
        }

        // ---- 15. branch isolation ------------------------------------------------------------------------

        [Fact]
        public async Task Branch_isolation_matches_the_per_record_contract()
        {
            using var host = new PlatformTestHost();
            int invoice = await SeedInvoiceAsync(host.Db, 1, "SV-1");

            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Internal, createdAt: Recent(3), branchId: null);
            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Internal, createdAt: Recent(2), branchId: 5);
            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Internal, createdAt: Recent(1), branchId: 9);

            var feed = Feed(host, Manager());

            // A branch user keeps branch-less events and loses only OTHER branches.
            var branchFive = await feed.GetRecentForContextAsync(
                PlatformTestHost.DefaultContext(branchId: 5), WindowFloor(), 50);
            Assert.Equal(2, branchFive.Count);

            // A caller with no branch loses nothing.
            var headOffice = await feed.GetRecentForContextAsync(
                PlatformTestHost.DefaultContext(branchId: null), WindowFloor(), 50);
            Assert.Equal(3, headOffice.Count);
        }

        // ---- 16. what the contract hands to a caller -----------------------------------------------------

        [Fact]
        public async Task Every_item_carries_registry_navigation_and_never_a_raw_payload()
        {
            using var host = new PlatformTestHost();
            int invoice = await SeedInvoiceAsync(host.Db, 1, "SV-1");
            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Internal, actorEmployeeId: 7, createdAt: Recent());

            var items = await Feed(host, Manager()).GetRecentForContextAsync(
                PlatformTestHost.DefaultContext(), WindowFloor(), 50);

            Assert.Single(items);
            // Navigation comes from the registry, so no consumer needs to know a route.
            Assert.False(string.IsNullOrWhiteSpace(items[0].Url));
            Assert.Contains(invoice.ToString(), items[0].Url);
            Assert.False(string.IsNullOrWhiteSpace(items[0].TitleAr));
            Assert.False(string.IsNullOrWhiteSpace(items[0].TitleEn));

            // Structural, not incidental: the view model has no member that could carry stored payload JSON.
            // PayloadVersion is a number and is the only member allowed to mention the word.
            var payloadCarrying = typeof(TimelineItemViewModel).GetProperties()
                .Where(p => p.Name.Contains("Payload") && p.PropertyType == typeof(string))
                .ToList();
            Assert.Empty(payloadCarrying);
        }

        // ---- 17 + 18. failure isolation ------------------------------------------------------------------

        [Fact]
        public async Task One_failing_entity_does_not_blank_the_whole_feed()
        {
            using var host = new PlatformTestHost();
            int healthy = await SeedInvoiceAsync(host.Db, 1, "SV-1");
            int broken = await SeedInvoiceAsync(host.Db, 1, "SV-2");

            await AddEventAsync(host.Db, 1, healthy, BusinessEventVisibility.Internal, createdAt: Recent(2));
            await AddEventAsync(host.Db, 1, broken, BusinessEventVisibility.Internal, createdAt: Recent(1));

            var items = await Feed(host, new FaultingPermissionProvider(broken))
                .GetRecentForContextAsync(PlatformTestHost.DefaultContext(), WindowFloor(), 50);

            Assert.Single(items);
            Assert.Equal(healthy, items[0].EntityId);
        }

        [Fact]
        public async Task A_systemic_failure_is_raised_instead_of_being_reported_as_no_activity()
        {
            using var host = new PlatformTestHost();
            int invoice = await SeedInvoiceAsync(host.Db, 1, "SV-1");
            await AddEventAsync(host.Db, 1, invoice, BusinessEventVisibility.Internal, createdAt: Recent());

            // Nothing resolved and something faulted. An empty list here would render as a calm
            // "no recent activity" over a broken permission subsystem — the failure must stay visible.
            var feed = Feed(host, new FaultingPermissionProvider(faultingEntityId: null));

            await Assert.ThrowsAsync<InvalidOperationException>(() => feed.GetRecentForContextAsync(
                PlatformTestHost.DefaultContext(), WindowFloor(), 50));
        }

        // ---- Workspace handoff contract ------------------------------------------------------------------

        [Fact]
        public void The_workspace_handoff_contract_is_the_one_that_was_agreed()
        {
            // Workspace binds to THIS shape. Pinning it here means a later Platform edit that renames a
            // parameter, drops the cancellation token, or widens the return type fails in the kernel's own
            // suite rather than in a module that cannot fix it.
            var method = typeof(ITimelineProjectionService).GetMethod(nameof(ITimelineProjectionService.GetRecentForContextAsync));
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<IReadOnlyList<TimelineItemViewModel>>), method!.ReturnType);

            var parameters = method.GetParameters();
            Assert.Equal(4, parameters.Length);
            Assert.Equal(("context", typeof(BusinessContext)), (parameters[0].Name, parameters[0].ParameterType));
            Assert.Equal(("sinceUtc", typeof(DateTime)), (parameters[1].Name, parameters[1].ParameterType));
            Assert.Equal(("take", typeof(int)), (parameters[2].Name, parameters[2].ParameterType));
            Assert.Equal(("cancellationToken", typeof(CancellationToken)), (parameters[3].Name, parameters[3].ParameterType));

            // sinceUtc and take are REQUIRED of the caller: a defaulted window is how an unbounded feed gets
            // introduced by accident later.
            Assert.False(parameters[1].HasDefaultValue);
            Assert.False(parameters[2].HasDefaultValue);

            // The per-record contract is untouched by this phase.
            var perRecord = typeof(ITimelineProjectionService).GetMethod(nameof(ITimelineProjectionService.GetAsync));
            Assert.NotNull(perRecord);
            Assert.Equal(5, perRecord!.GetParameters().Length);

            // The bounds are public so a caller can honour them instead of discovering them by truncation.
            Assert.Equal(200, TimelineProjectionService.MaxTake);
            Assert.Equal(30, TimelineProjectionService.MaxRecentLookbackDays);
            Assert.Equal(1000, TimelineProjectionService.MaxRecentCandidates);
        }

        // ---- test doubles --------------------------------------------------------------------------------

        // Allows the listed actions everywhere EXCEPT one entity, which is denied outright — the "you may read
        // this module but not this record" case that a company-only feed would miss.
        private sealed class DenyOneEntityPermissionProvider : IPlatformPermissionProvider
        {
            private readonly int _deniedEntityId;
            private readonly HashSet<string> _allowed;

            public DenyOneEntityPermissionProvider(int deniedEntityId, params string[] allowedActions)
            {
                _deniedEntityId = deniedEntityId;
                _allowed = new HashSet<string>(allowedActions, StringComparer.Ordinal);
            }

            public Task<PermissionDecision> CanAsync(
                BusinessContext context, string entityType, int entityId, string action, CancellationToken cancellationToken = default)
                => Task.FromResult(entityId != _deniedEntityId && _allowed.Contains(action)
                    ? PermissionDecision.Allow("test")
                    : PermissionDecision.Deny("test denies " + action));
        }

        // Stands in for a module adapter that throws. faultingEntityId null means every entity fails, which is
        // how a systemic outage looks from inside the loop.
        private sealed class FaultingPermissionProvider : IPlatformPermissionProvider
        {
            private readonly int? _faultingEntityId;

            public FaultingPermissionProvider(int? faultingEntityId) => _faultingEntityId = faultingEntityId;

            public Task<PermissionDecision> CanAsync(
                BusinessContext context, string entityType, int entityId, string action, CancellationToken cancellationToken = default)
            {
                if (_faultingEntityId == null || entityId == _faultingEntityId)
                    throw new InvalidOperationException("module permission adapter failed for entity " + entityId);
                return Task.FromResult(PermissionDecision.Allow("test"));
            }
        }
    }
}
