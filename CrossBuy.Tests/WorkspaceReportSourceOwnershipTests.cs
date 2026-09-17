using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.BL.Workspace;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // WORKSPACE × REPORTING — ONE AUTHORITATIVE PRODUCER OF REPORT FAVOURITES.
    //
    // WHY THIS FILE EXISTS. Two IWorkspaceReportSource implementations were registered at once:
    //
    //     WorkspaceRegistration.cs   -> ReportLibraryWorkspaceReportSource   (the Workspace's own)
    //     ReportingRegistration.cs   -> ReportingWorkspaceSource             (Reporting's contribution)
    //
    // Both called IReportLibraryService.GetFavoritesAsync and both emitted Kind = Favorite, and
    // LoadReportsAsync merges its sources with AddRange and no de-duplication. So every pinned report
    // reached the Reports panel TWICE — once from Reporting with a working /Reports/Viewer url, and once
    // from the Workspace's copy with Url = null, i.e. a dead link beside a working one.
    //
    // It was invisible on a fresh database: with ReportFavorites empty, zero favourites duplicate to zero
    // rows. THAT is why these tests SEED a favourite instead of asserting against an empty table — the
    // defect shipped precisely because emptiness looked like correctness.
    //
    // Reporting owns report favourites and report-library semantics, so the Workspace's duplicate was the
    // one removed. The fix is a removed registration, NOT dedup inside LoadReportsAsync: de-duplicating two
    // producers of the same fact would hide the ownership defect rather than resolve it. These tests hold
    // that line — a reintroduced second producer fails here rather than in somebody's Reports panel.
    //
    // Predicted as H-1 in docs/platform/Stage-Reporting-UI-05-Workspace-Integration.md.
    // ============================================================================================
    public class WorkspaceReportSourceOwnershipTests
    {
        // ----------------------------------------------------------------------------------------
        // GATE 1 — the DI graph. A behavioural test cannot see a duplicate REGISTRATION, which is what
        // the defect actually was, so this asks the real container.
        // ----------------------------------------------------------------------------------------
        private static ServiceProvider BuildGraph()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<ICompanyScopeHolder, CompanyScopeHolder>();
            services.AddDbContext<CrossDbContext>(o => o.UseSqlite("DataSource=:memory:"));
            services.AddScoped<IBusinessContextAccessor, UnresolvedContextAccessor>();

            // The reporting graph's fourth external dependency - ReportOrgImageProvider needs it to find
            // the company and branch marks. See ReportingTestHostEnvironment.
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(
                new ReportingTestHostEnvironment());

            // The real registrations, in the order Program.cs calls them (Reporting at 204, Workspace at 242).
            services.AddCrossBusinessReporting();
            services.AddCrossBusinessWorkspace();

            return services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        }

        [Fact]
        public void Exactly_one_report_source_is_registered_and_it_is_Reportings()
        {
            using var provider = BuildGraph();
            using var scope = provider.CreateScope();

            var sources = scope.ServiceProvider.GetServices<IWorkspaceReportSource>().ToList();

            Assert.Single(sources);
            Assert.IsType<ReportingWorkspaceSource>(sources[0]);
        }

        // The Workspace must contribute NO report source of its own. Asserted on the descriptors rather than
        // on a built provider so it stays true even if a future source needs dependencies this test lacks.
        [Fact]
        public void The_workspace_registration_contributes_no_report_source_of_its_own()
        {
            var services = new ServiceCollection();
            services.AddCrossBusinessWorkspace();

            var declared = services
                .Where(d => d.ServiceType == typeof(IWorkspaceReportSource))
                .Select(d => d.ImplementationType?.Name ?? "(factory)")
                .ToList();

            Assert.True(declared.Count == 0,
                "AddCrossBusinessWorkspace must register no IWorkspaceReportSource — Reporting owns report " +
                "favourites and contributes them itself. Found: " + string.Join(", ", declared));
        }

        // Only ONE registered source may emit Kind = Favorite. This is the invariant; the count test above is
        // how it is currently satisfied, and this one survives a legitimate future source that emits some
        // OTHER kind.
        [Fact]
        public async Task Only_one_registered_source_emits_favourite_links()
        {
            using var host = new ReportingTestHost();
            await host.Library.AddFavoriteAsync(TestReportDefinitions.SalesCode, null, host.Ctx);

            var services = new ServiceCollection();
            services.AddCrossBusinessWorkspace();
            services.AddCrossBusinessReporting();

            var emitters = 0;
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IWorkspaceReportSource)))
            {
                var source = Instantiate(descriptor, host);
                Assert.NotNull(source);

                var links = await source!.GetAsync(host.Ctx);
                if (links.Any(l => l.Kind == WorkspaceReportKind.Favorite)) { emitters++; }
            }

            Assert.Equal(1, emitters);
        }

        // Builds a registered source using the test host's real reporting services. Returns null for a
        // descriptor this harness cannot satisfy, which keeps the test about favourite emission rather than
        // about constructor shapes.
        private static IWorkspaceReportSource? Instantiate(ServiceDescriptor descriptor, ReportingTestHost host)
        {
            if (descriptor.ImplementationType == typeof(ReportingWorkspaceSource))
                return new ReportingWorkspaceSource(host.Library, host.History, host.Templates, host.Catalog,
                    host.Authorization);

            return null;
        }

        // ----------------------------------------------------------------------------------------
        // GATE 2 — behaviour, with a favourite actually pinned.
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_pinned_favourite_produces_exactly_one_link_with_a_working_url()
        {
            using var host = new ReportingTestHost();
            await host.Library.AddFavoriteAsync(TestReportDefinitions.SalesCode, null, host.Ctx);

            var links = await ComposeAsync(host);

            var favourites = links.Where(l => l.Kind == WorkspaceReportKind.Favorite).ToList();

            Assert.Single(favourites);

            // The duplicate's tell was Url = null: one dead link beside one working link. A favourite with no
            // destination is the exact artefact this removal eliminates.
            Assert.NotNull(favourites[0].Url);
            Assert.StartsWith("/Reports/Viewer/", favourites[0].Url);
        }

        // The general invariant, not just for favourites: no two links may describe the same report identity
        // in the same kind. A second producer of ANY kind fails here.
        [Fact]
        public async Task No_two_links_share_a_report_identity_within_a_kind()
        {
            using var host = new ReportingTestHost();

            await host.Library.AddFavoriteAsync(TestReportDefinitions.SalesCode, null, host.Ctx);
            await host.Reports().GenerateAsync(new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode,
                Format = ReportOutputFormat.Csv,
            });
            await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode,
                Name = "My layout",
                Scope = ReportTemplateScope.Personal,
                Layout = new ReportLayout { VisibleColumns = new[] { "Branch" } },
            }, host.Ctx);

            var links = await ComposeAsync(host);

            var duplicates = links
                .GroupBy(l => (l.Kind, Identity: l.Url ?? l.Label))
                .Where(g => g.Count() > 1)
                .Select(g => $"{g.Key.Kind}:{g.Key.Identity} ×{g.Count()}")
                .ToList();

            Assert.True(duplicates.Count == 0,
                "The Reports panel produced duplicate rows for one report identity: " +
                string.Join(" · ", duplicates));
        }

        // Removing the Workspace's own source must not cost the kinds Reporting contributes.
        [Fact]
        public async Task Favourite_recent_and_saved_kinds_all_survive_the_removal()
        {
            using var host = new ReportingTestHost();

            await host.Library.AddFavoriteAsync(TestReportDefinitions.SalesCode, null, host.Ctx);
            await host.Reports().GenerateAsync(new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode,
                Format = ReportOutputFormat.Csv,
            });
            await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode,
                Name = "My layout",
                Scope = ReportTemplateScope.Personal,
                Layout = new ReportLayout { VisibleColumns = new[] { "Branch" } },
            }, host.Ctx);

            var links = await ComposeAsync(host);

            Assert.Contains(links, l => l.Kind == WorkspaceReportKind.Favorite);
            Assert.Contains(links, l => l.Kind == WorkspaceReportKind.Recent);
            Assert.Contains(links, l => l.Kind == WorkspaceReportKind.Saved);

            // Every surviving link still points into the Reporting product, which authorizes on arrival.
            Assert.All(links, l => Assert.StartsWith("/Reports/Viewer/", l.Url));
        }

        // ----------------------------------------------------------------------------------------
        // GATE 2b — SHORTCUT, restored inside Reporting's own adapter.
        //
        // The kind lost its only producer when the Workspace's duplicate source was withdrawn. It is required
        // by the approved contract (Stage-Workspace-Integration-UI-Review-Pack §4.1 asks a reviewer to confirm
        // all four headings can appear), and its home is Reporting because Reporting owns catalogue facts.
        // These gates pin the three properties that make it safe rather than merely present.
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task Shortcut_is_produced_and_every_entry_has_a_working_viewer_url()
        {
            using var host = new ReportingTestHost();

            var shortcuts = (await ComposeAsync(host))
                .Where(l => l.Kind == WorkspaceReportKind.Shortcut).ToList();

            Assert.NotEmpty(shortcuts);

            // The withdrawn producer's tell was Url = null. A shortcut with no destination is the artefact this
            // implementation must never reproduce.
            Assert.All(shortcuts, l =>
            {
                Assert.False(string.IsNullOrWhiteSpace(l.Url));
                Assert.NotEqual("#", l.Url);
                Assert.StartsWith("/Reports/Viewer/", l.Url);
                Assert.False(l.IsStale);   // it came FROM the catalogue, so it resolves by construction
            });
        }

        // The security property. A shortcut is a DISCOVERY surface: it names reports the caller has not used,
        // so an unfiltered catalogue here would disclose the existence of every restricted report in the
        // product on the dashboard — the exact enumeration oracle ReportsController.Viewer answers 404 to deny.
        [Fact]
        public async Task Shortcut_never_names_a_report_the_caller_may_not_open()
        {
            using var host = new ReportingTestHost();

            var shortcuts = (await ComposeAsync(host))
                .Where(l => l.Kind == WorkspaceReportKind.Shortcut).ToList();

            // The default context holds "Reports", which maps to the sales permission only. The business event
            // log's key is unmapped and the run-history report needs administration, so the fail-closed
            // evaluator denies both — and neither may appear.
            Assert.DoesNotContain(shortcuts,
                l => l.Url!.Contains(BusinessEventsReportCodes.ReportCode, StringComparison.Ordinal));
            Assert.DoesNotContain(shortcuts,
                l => l.Url!.Contains(PlatformReportCodes.ReportRunHistory, StringComparison.Ordinal));

            // ...while a report the caller CAN open is offered, so the filter is not simply empty.
            Assert.Contains(shortcuts,
                l => l.Url!.Contains(TestReportDefinitions.SalesCode, StringComparison.Ordinal));
        }

        // A report the person has already pinned or just run is not something to "discover". This is a
        // selection rule inside ONE producer, not the cross-producer dedup the duplicate-source removal fixed.
        [Fact]
        public async Task Shortcut_does_not_re_offer_a_report_already_pinned_or_recently_run()
        {
            using var host = new ReportingTestHost();

            await host.Library.AddFavoriteAsync(TestReportDefinitions.SalesCode, null, host.Ctx);

            var links = await ComposeAsync(host);

            var favourite = links.Where(l => l.Kind == WorkspaceReportKind.Favorite).ToList();
            var shortcuts = links.Where(l => l.Kind == WorkspaceReportKind.Shortcut).ToList();

            Assert.Contains(favourite,
                l => l.Url!.Contains(TestReportDefinitions.SalesCode, StringComparison.Ordinal));
            Assert.DoesNotContain(shortcuts,
                l => l.Url!.Contains(TestReportDefinitions.SalesCode, StringComparison.Ordinal));
        }

        // Reading the catalogue must stay a READ. The withdrawn producer reached the catalogue through
        // IReportService.BrowseAsync and recorded a report run on every dashboard render, which then fed back
        // into this panel's own Recent group — a dashboard that manufactures the history it displays.
        [Fact]
        public async Task Composing_the_panel_records_no_report_run()
        {
            using var host = new ReportingTestHost();

            var before = await host.Db.ReportRuns.CountAsync();
            await ComposeAsync(host);
            await ComposeAsync(host);
            await ComposeAsync(host);
            var after = await host.NewContext().ReportRuns.CountAsync();

            Assert.Equal(before, after);
        }

        // ----------------------------------------------------------------------------------------
        // GATE 3 — one source failing must not blank the others. The panel degrades to PartiallyAvailable
        // and NAMES what is missing; it does not return an empty list that reads as "you have no reports".
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task One_failing_report_source_does_not_blank_the_others()
        {
            using var host = new ReportingTestHost();
            await host.Library.AddFavoriteAsync(TestReportDefinitions.SalesCode, null, host.Ctx);

            var workspace = BuildWorkspace(host,
                new ThrowingReportSource(),
                new ReportingWorkspaceSource(host.Library, host.History, host.Templates, host.Catalog,
                    host.Authorization));

            var panel = await workspace.GetReportsAsync();

            Assert.Equal(WorkspacePanelState.PartiallyAvailable, panel.State);
            Assert.Contains("Broken", panel.MissingSources);
            Assert.Contains(panel.Items, l => l.Kind == WorkspaceReportKind.Favorite);
        }

        // Every source failing is a TemporaryFailure — never Empty. "Nothing loaded" and "you have nothing"
        // are different answers and must not render alike.
        [Fact]
        public async Task Every_source_failing_is_a_temporary_failure_not_an_empty_panel()
        {
            using var host = new ReportingTestHost();

            var workspace = BuildWorkspace(host, new ThrowingReportSource());

            var panel = await workspace.GetReportsAsync();

            Assert.Equal(WorkspacePanelState.TemporaryFailure, panel.State);
            Assert.Empty(panel.Items);
        }

        // No source at all — a deployment without Reporting — is Unavailable, not Empty.
        [Fact]
        public async Task No_report_source_at_all_is_unavailable_not_empty()
        {
            using var host = new ReportingTestHost();

            var panel = await BuildWorkspace(host).GetReportsAsync();

            Assert.Equal(WorkspacePanelState.Unavailable, panel.State);
            Assert.False(string.IsNullOrWhiteSpace(panel.Reason));
        }

        // ----------------------------------------------------------------------------------------
        // helpers
        // ----------------------------------------------------------------------------------------

        // The links the REGISTERED source set contributes, composed the way LoadReportsAsync does.
        //
        // The source list comes from the real registrations — NOT from a hand-picked instance. An earlier
        // version of this helper passed one source directly, which meant these behavioural tests stayed green
        // even with a duplicate producer registered: they proved the composition merged correctly and said
        // nothing about the defect. Reading the descriptors is what makes a reintroduced second producer show
        // up here as duplicate ROWS, not just as a failed count assertion.
        private static async Task<IReadOnlyList<WorkspaceReportLink>> ComposeAsync(ReportingTestHost host)
        {
            var panel = await BuildWorkspace(host, RegisteredSources(host).ToArray()).GetReportsAsync();
            return panel.Items;
        }

        // Every IWorkspaceReportSource the real registrations declare, built against the test host's services.
        private static IEnumerable<IWorkspaceReportSource> RegisteredSources(ReportingTestHost host)
        {
            var services = new ServiceCollection();
            services.AddCrossBusinessWorkspace();
            services.AddCrossBusinessReporting();

            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IWorkspaceReportSource)))
            {
                var source = Instantiate(descriptor, host);

                Assert.NotNull(source);   // an unbuildable source would silently shrink the set under test
                yield return source!;
            }
        }

        private static WorkspaceService BuildWorkspace(
            ReportingTestHost host, params IWorkspaceReportSource[] reportSources) =>
            new(host.Accessor,
                new ServiceCollection().BuildServiceProvider(),
                new NoNotifications(),
                new NoIdentity(),
                Array.Empty<IWorkspaceFavoritesSource>(),
                Array.Empty<IWorkspaceActivitySource>(),
                reportSources,
                NullLogger<WorkspaceService>.Instance);

        private sealed class ThrowingReportSource : IWorkspaceReportSource
        {
            public string SourceName => "Broken";
            public bool IsAvailable => true;

            public Task<IReadOnlyList<WorkspaceReportLink>> GetAsync(
                BusinessContext context, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("this source is broken");
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

        // Only needs to be RESOLVABLE — this graph is built to inspect registrations, not to serve a request.
        private sealed class UnresolvedContextAccessor : IBusinessContextAccessor
        {
            public Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken = default) =>
                throw new BusinessContextUnresolvedException("no context in this test graph");

            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<BusinessContext?>(null);
        }
    }
}
