using System.Reflection;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Workspace;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // WORKSPACE — "WHAT CHANGED?"  (TAB 6)
    //
    // The Recent Activity panel used to be built from the caller's own NOTIFICATIONS. That answered a
    // different question: a notification is something somebody decided to TELL you, a business event is
    // something that HAPPENED. It could only show the subset of activity that had a notification producer
    // wired, and it re-rendered the Notifications panel's own rows beside it — two panels, one source.
    //
    // It is now the platform business-event timeline, through ITimelineProjectionService's cross-entity
    // read. WHAT THESE TESTS ASSERT is the half TAB 6 owns:
    //
    //     * that the Workspace ASKS the platform contract rather than reaching past it;
    //     * that the resolved context reaches it untouched;
    //     * that the row is mapped without inventing navigation, tone or ordering;
    //     * that a platform fault reads as a fault, not as a quiet week;
    //     * that exactly one activity producer is registered.
    //
    // WHAT THEY DELIBERATELY DO NOT ASSERT is who may SEE a row. Company scope, entity permission,
    // restricted-payload suppression and the window/take clamps all live behind GetRecentForContextAsync
    // and are covered by TimelineRecentFeedTests. Re-testing them here would be a second copy of another
    // tab's contract, and the copy is what drifts.
    // ============================================================================================
    public class WorkspaceTimelineActivityTests
    {
        private const int Company = 1;
        private const int Employee = 5;

        // ----------------------------------------------------------------------------------------
        // 1 · THE WORKSPACE ASKS THE PLATFORM CONTRACT
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task Activity_is_read_through_the_platform_recent_feed()
        {
            var timeline = new RecordingTimeline();

            await Workspace(timeline).GetDashboardAsync();

            var call = Assert.Single(timeline.Calls);
            Assert.Equal(Company, call.Context.CompanyId);
            Assert.Equal(Employee, call.Context.EmployeeId);
        }

        // The lookback is a DASHBOARD choice and must stay inside the platform ceiling. It may be narrower
        // than the governed maximum; it may never be wider, because that would ask the platform to relax a
        // clamp this tab does not own.
        [Fact]
        public async Task The_requested_window_is_bounded_and_never_wider_than_the_platform_ceiling()
        {
            var timeline = new RecordingTimeline();
            var before = DateTime.UtcNow;

            await Workspace(timeline).GetDashboardAsync();

            var call = Assert.Single(timeline.Calls);
            var days = (before - call.SinceUtc).TotalDays;

            Assert.InRange(days, 0.5, TimelineProjectionService.MaxRecentLookbackDays);
            Assert.Equal(TimelineActivityWorkspaceSource.LookbackDays, (int)Math.Round(days));
        }

        // The panel's own cap is what reaches the platform; the platform clamps it again on its side.
        [Fact]
        public async Task The_requested_take_is_the_panel_cap_and_within_the_platform_max()
        {
            var timeline = new RecordingTimeline();

            await Workspace(timeline).GetDashboardAsync();

            var call = Assert.Single(timeline.Calls);
            Assert.InRange(call.Take, 1, TimelineProjectionService.MaxTake);
        }

        // ----------------------------------------------------------------------------------------
        // 2 · MAPPING — every field comes from the platform row, nothing is re-derived
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_timeline_row_maps_onto_the_existing_activity_row()
        {
            var at = DateTime.UtcNow.AddHours(-3);
            var timeline = new RecordingTimeline().With(
                titleEn: "Sales invoice posted", descEn: "INV-1042",
                actor: "Layla", createdAt: at, icon: "ki-outline ki-bill",
                color: "success", url: "/Accounting/SalesInvoiceDetail/1042");

            var dashboard = await Workspace(timeline).GetDashboardAsync();

            var row = Assert.Single(dashboard.Activity.Items);
            Assert.Equal("Sales invoice posted", row.Title);
            Assert.Equal("INV-1042", row.Detail);
            Assert.Equal("Layla", row.Actor);
            Assert.Equal(at, row.At);
            Assert.Equal("ki-outline ki-bill", row.Icon);
            Assert.Equal(WorkspaceTone.Ok, row.Tone);
        }

        // The URL arrives already resolved through IEntityRegistry. The Workspace must pass it through
        // untouched — the moment it rewrites one, it owns module routing.
        [Fact]
        public async Task The_navigation_target_is_passed_through_exactly_as_supplied()
        {
            var timeline = new RecordingTimeline().With(url: "/Inventory/Items?open=77");

            var dashboard = await Workspace(timeline).GetDashboardAsync();

            Assert.Equal("/Inventory/Items?open=77", Assert.Single(dashboard.Activity.Items).Url);
        }

        // A row the platform could not give a destination stays a row: it is real history, it simply has
        // nowhere to click. The view already falls back for a null Url.
        [Fact]
        public async Task A_row_without_a_navigation_target_still_renders()
        {
            var timeline = new RecordingTimeline().With(url: null);

            var dashboard = await Workspace(timeline).GetDashboardAsync();

            Assert.Null(Assert.Single(dashboard.Activity.Items).Url);
        }

        [Theory]
        [InlineData("success", WorkspaceTone.Ok)]
        [InlineData("warning", WorkspaceTone.Warn)]
        [InlineData("danger", WorkspaceTone.Critical)]
        [InlineData("info", WorkspaceTone.Info)]
        [InlineData("primary", WorkspaceTone.Info)]
        [InlineData("secondary", WorkspaceTone.Neutral)]
        public async Task The_presenters_colour_becomes_the_panels_tone(string colour, WorkspaceTone expected)
        {
            var timeline = new RecordingTimeline().With(color: colour);

            var dashboard = await Workspace(timeline).GetDashboardAsync();

            Assert.Equal(expected, Assert.Single(dashboard.Activity.Items).Tone);
        }

        // The feed is already CreatedAt DESC, EventId DESC — a total order with a unique tiebreak. Two rows
        // sharing an instant must keep the platform's sequence rather than being re-sorted on time alone.
        [Fact]
        public async Task Rows_sharing_an_instant_keep_the_platform_ordering()
        {
            var same = DateTime.UtcNow.AddMinutes(-10);
            var timeline = new RecordingTimeline()
                .With(titleEn: "first", createdAt: same)
                .With(titleEn: "second", createdAt: same)
                .With(titleEn: "third", createdAt: same);

            var dashboard = await Workspace(timeline).GetDashboardAsync();

            Assert.Equal(new[] { "first", "second", "third" },
                dashboard.Activity.Items.Select(i => i.Title).ToArray());
        }

        // ----------------------------------------------------------------------------------------
        // 3 · STATES — a fault must never read as "nothing changed"
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task No_recent_activity_is_an_empty_panel()
        {
            var dashboard = await Workspace(new RecordingTimeline()).GetDashboardAsync();

            Assert.Equal(WorkspacePanelState.Empty, dashboard.Activity.State);
            Assert.Empty(dashboard.Activity.Items);
        }

        // THE DEFECT THIS PINS. Activity now has a SINGLE authoritative producer, so any platform fault
        // leaves zero rows — and the old guard (`failed.Count > 0 && items.Count > 0`) fell through and
        // reported Empty. "Nothing changed" and "we could not find out what changed" are different answers.
        [Fact]
        public async Task A_platform_failure_is_a_temporary_failure_not_an_empty_panel()
        {
            var dashboard = await Workspace(new ThrowingTimeline()).GetDashboardAsync();

            Assert.Equal(WorkspacePanelState.TemporaryFailure, dashboard.Activity.State);
            Assert.Empty(dashboard.Activity.Items);
            Assert.False(string.IsNullOrWhiteSpace(dashboard.Activity.Reason));
        }

        // One dark panel must never take the dashboard with it.
        [Fact]
        public async Task The_rest_of_the_dashboard_still_renders_when_activity_fails()
        {
            var dashboard = await Workspace(new ThrowingTimeline()).GetDashboardAsync();

            Assert.False(dashboard.IsUnresolved);
            Assert.NotNull(dashboard.MyWork);
            Assert.NotNull(dashboard.Agenda);
            Assert.NotNull(dashboard.Approvals);
            Assert.NotNull(dashboard.Mentions);
            Assert.NotNull(dashboard.Notifications);
            Assert.NotEmpty(dashboard.Metrics);
        }

        // A session with no company resolves no workspace at all, and asks the platform nothing.
        [Fact]
        public async Task An_unresolved_company_asks_the_timeline_nothing()
        {
            var timeline = new RecordingTimeline().With(titleEn: "should never be read");

            var dashboard = await Workspace(timeline, new FixedContext(null)).GetDashboardAsync();

            Assert.True(dashboard.IsUnresolved);
            Assert.Empty(timeline.Calls);
        }

        // ----------------------------------------------------------------------------------------
        // 4 · STRUCTURE — the Workspace reaches the platform only through the read contract
        // ----------------------------------------------------------------------------------------

        // The contract is resolved OPTIONALLY rather than taken as a constructor parameter, so
        // AddCrossBusinessWorkspace still validates on its own: the platform registers
        // ITimelineProjectionService in Program.cs, and the Workspace must not register another tab's
        // implementation just to close its own graph.
        [Fact]
        public void The_activity_source_takes_only_the_service_provider()
        {
            var ctor = Assert.Single(typeof(TimelineActivityWorkspaceSource).GetConstructors());
            var parameter = Assert.Single(ctor.GetParameters());

            Assert.Equal(typeof(IServiceProvider), parameter.ParameterType);
        }

        // AddCrossBusinessWorkspace must remain independently constructible — this is the gate that caught
        // the regression when the contract was a constructor dependency.
        [Fact]
        public void The_workspace_registration_still_validates_on_its_own()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<IBusinessContextAccessor>(_ => new FixedContext(Resolved()));
            services.AddCrossBusinessWorkspace();

            var descriptor = Assert.Single(
                services.Where(d => d.ServiceType == typeof(IWorkspaceActivitySource)));
            Assert.Equal(typeof(TimelineActivityWorkspaceSource), descriptor.ImplementationType);
        }

        // A deployment without the platform timeline must say so, not report a quiet week.
        [Fact]
        public async Task An_unregistered_timeline_is_a_failure_state_not_an_empty_panel()
        {
            var empty = new ServiceCollection().BuildServiceProvider();
            var source = new TimelineActivityWorkspaceSource(empty);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => source.GetAsync(Resolved(), 10));
        }

        // Named types rather than a text scan: a DbContext, the ops monitor or the dispatch store appearing
        // in any Workspace constructor is the regression, and it would compile silently.
        [Fact]
        public void No_workspace_type_takes_a_dbcontext_or_a_platform_ops_dependency()
        {
            var forbidden = new[] { "CrossDbContext", "IBusinessEventMonitorService", "IEventDispatchStore" };

            var offenders =
                from type in typeof(WorkspaceService).Assembly.GetTypes()
                where type.Namespace == "CrossBuy.BL.Workspace"
                      && typeof(IWorkspaceActivitySource).IsAssignableFrom(type)
                      && !type.IsInterface
                from ctor in type.GetConstructors()
                from p in ctor.GetParameters()
                where forbidden.Contains(p.ParameterType.Name)
                select $"{type.Name}({p.ParameterType.Name})";

            Assert.Empty(offenders);
        }

        // The Workspace must never read the event tables itself. Asserted over SOURCE, because the point is
        // that no such query exists to be written in the first place.
        [Fact]
        public void The_workspace_tree_contains_no_business_event_query()
        {
            var dir = WorkspaceSourceDirectory();
            var offenders = new List<string>();

            foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                foreach (var banned in new[] { ".BusinessEvents", ".BusinessEventDispatch", "IEventDispatchStore",
                                               "IBusinessEventMonitorService" })
                    if (text.Contains(banned, StringComparison.Ordinal))
                        offenders.Add($"{Path.GetFileName(file)} -> {banned}");
            }

            Assert.True(offenders.Count == 0,
                "The Workspace must consume the timeline read contract, never the event tables: " +
                string.Join(", ", offenders));
        }

        // ----------------------------------------------------------------------------------------
        // 5 · REGISTRATION — exactly one authoritative producer
        // ----------------------------------------------------------------------------------------

        [Fact]
        public void Exactly_one_activity_source_is_registered_and_it_is_the_timeline()
        {
            var services = new ServiceCollection();
            services.AddCrossBusinessWorkspace();

            var declared = services
                .Where(d => d.ServiceType == typeof(IWorkspaceActivitySource))
                .Select(d => d.ImplementationType)
                .ToList();

            var only = Assert.Single(declared);
            Assert.Equal(typeof(TimelineActivityWorkspaceSource), only);
        }

        // The notification-derived producer must not come back beside it: that would put the Notifications
        // panel's own rows into the Activity panel a second time — the duplicate-producer defect the
        // Reporting favourites source was already withdrawn for.
        [Fact]
        public void No_notification_derived_activity_producer_remains()
        {
            var lingering = typeof(WorkspaceService).Assembly.GetTypes()
                .Where(t => typeof(IWorkspaceActivitySource).IsAssignableFrom(t) && !t.IsInterface)
                .Select(t => t.Name)
                .Where(n => n.Contains("Notification", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(lingering.Count == 0, "Still present: " + string.Join(", ", lingering));
        }

        // ----------------------------------------------------------------------------------------
        // 6 · THE OTHER PANELS ARE UNTOUCHED BY THIS SWAP
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task My_work_agenda_approvals_and_mentions_are_unaffected()
        {
            var timeline = new RecordingTimeline().With(titleEn: "something happened");

            var dashboard = await Workspace(timeline).GetDashboardAsync();

            // None of them resolves in this harness, so each reports its own blocked state rather than
            // being disturbed by the activity producer.
            Assert.Equal(WorkspacePanelState.Unavailable, dashboard.MyWork.State);
            Assert.Equal(WorkspacePanelState.Unavailable, dashboard.Agenda.State);
            Assert.Equal(WorkspacePanelState.Unavailable, dashboard.Mentions.State);
            Assert.Single(dashboard.Activity.Items);
        }

        // ========================================================================================
        // harness
        // ========================================================================================

        private static BusinessContext Resolved() => new() { CompanyId = Company, EmployeeId = Employee };

        private static string WorkspaceSourceDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "CrossBuy", "BL", "Workspace")))
                dir = dir.Parent;

            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "CrossBuy", "BL", "Workspace");
        }

        private static WorkspaceService Workspace(
            ITimelineProjectionService timeline, IBusinessContextAccessor? contexts = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton(timeline);

            return new WorkspaceService(
                contexts ?? new FixedContext(Resolved()),
                services.BuildServiceProvider(),
                new NoNotifications(),
                new NoIdentity(),
                Array.Empty<IWorkspaceFavoritesSource>(),
                new IWorkspaceActivitySource[] { new TimelineActivityWorkspaceSource(services.BuildServiceProvider()) },
                Array.Empty<IWorkspaceReportSource>(),
                NullLogger<WorkspaceService>.Instance);
        }

        private sealed record Call(BusinessContext Context, DateTime SinceUtc, int Take);

        /// Records what the Workspace ASKED the platform for and returns what it is given. It applies no
        /// window, no clamp and no visibility rule: those belong to the real service and are asserted in
        /// TimelineRecentFeedTests.
        private sealed class RecordingTimeline : ITimelineProjectionService
        {
            private readonly List<TimelineItemViewModel> _rows = new();
            public List<Call> Calls { get; } = new();

            public RecordingTimeline With(string titleEn = "event", string? descEn = null,
                string? actor = null, DateTime? createdAt = null, string icon = "ki-outline ki-abstract-26",
                string color = "info", string? url = "/somewhere")
            {
                _rows.Add(new TimelineItemViewModel
                {
                    EventUid = Guid.NewGuid(),
                    EntityType = "SalesInvoice",
                    EntityId = 1,
                    EventType = "SalesInvoice.Posted",
                    TitleAr = titleEn,
                    TitleEn = titleEn,
                    DescriptionAr = descEn,
                    DescriptionEn = descEn,
                    ActorEmployeeId = actor is null ? null : 9,
                    ActorDisplayName = actor,
                    Icon = icon,
                    Color = color,
                    CreatedAt = createdAt ?? DateTime.UtcNow,
                    PayloadVersion = 1,
                    Visibility = "Internal",
                    Url = url,
                    Source = TimelineItemSource.Event,
                });
                return this;
            }

            public Task<IReadOnlyList<TimelineItemViewModel>> GetAsync(
                string entityCode, int entityId, BusinessContext context, int take = 100,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("the activity feed must use the recent read, not the per-entity one");

            public Task<IReadOnlyList<TimelineItemViewModel>> GetRecentForContextAsync(
                BusinessContext context, DateTime sinceUtc, int take,
                CancellationToken cancellationToken = default)
            {
                Calls.Add(new Call(context, sinceUtc, take));
                return Task.FromResult<IReadOnlyList<TimelineItemViewModel>>(_rows.Take(take).ToList());
            }
        }

        private sealed class ThrowingTimeline : ITimelineProjectionService
        {
            public Task<IReadOnlyList<TimelineItemViewModel>> GetAsync(
                string entityCode, int entityId, BusinessContext context, int take = 100,
                CancellationToken cancellationToken = default) => throw new InvalidOperationException("unreachable");

            public Task<IReadOnlyList<TimelineItemViewModel>> GetRecentForContextAsync(
                BusinessContext context, DateTime sinceUtc, int take,
                CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("the timeline store is unreachable");
        }

        private sealed class FixedContext : IBusinessContextAccessor
        {
            private readonly BusinessContext? _context;
            public FixedContext(BusinessContext? context) { _context = context; }

            public Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken = default) =>
                _context is null
                    ? throw new BusinessContextUnresolvedException("no context in this test")
                    : Task.FromResult(_context);

            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(_context);
        }

        private sealed class NoNotifications : IWorkspaceNotificationSource
        {
            public bool IsAvailable => false;

            public Task<IReadOnlyList<WorkspaceNotification>> GetAsync(BusinessContext context,
                bool unreadOnly, int take, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<WorkspaceNotification>>(Array.Empty<WorkspaceNotification>());

            public Task<int> CountUnreadAsync(BusinessContext context,
                CancellationToken cancellationToken = default) => Task.FromResult(0);            
            // Added when IWorkspaceNotificationSource grew paging. The double still means the same thing it
            // always did - this caller has NO notifications - so a page of them is empty and the total is
            // zero. Returning anything else would make a "no notifications" fixture assert against them.
            public Task<IReadOnlyList<WorkspaceNotification>> GetPageAsync(BusinessContext context,
                bool unreadOnly, int skip, int take, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<WorkspaceNotification>>(Array.Empty<WorkspaceNotification>());
            
            public Task<int> CountAsync(BusinessContext context, bool unreadOnly,
                CancellationToken cancellationToken = default) => Task.FromResult(0);
        }

        private sealed class NoIdentity : IWorkspaceIdentityResolver
        {
            public Task<(string EmployeeName, string? CompanyName)> ResolveAsync(
                BusinessContext context, CancellationToken cancellationToken = default) =>
                Task.FromResult((string.Empty, (string?)null));
        }
    }
}
