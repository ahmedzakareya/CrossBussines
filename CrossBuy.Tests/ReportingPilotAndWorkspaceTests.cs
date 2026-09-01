using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.BL.Workspace;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // Reporting Platform — R3 PHASE 4 (the Business Events pilot experience) and PHASE 5 (Workspace).
    //
    // PHASE 4 adds the filters an investigation actually uses — event type, correlation id, branch, and the
    // DELIVERY state derived from BusinessEventDispatch. The delivery angle is the one that needed care: it
    // is a join onto a table with no CompanyID of its own, and it is filtered in SQL rather than in memory.
    // Both properties are asserted below, because getting either wrong is silent.
    //
    // PHASE 5 proves the Workspace contribution goes through the published extension point and leaks nothing.
    // =============================================================================================
    public class ReportingPilotAndWorkspaceTests
    {
        private const int Company = 1;
        private const int Me = 7;

        private static ReportingTestHost Host(params string[] heldKeys)
        {
            var host = new ReportingTestHost(companyId: Company, employeeId: Me, roles: "Auditor");
            foreach (var key in heldKeys) host.PermissionOptions.RoleMap[key] = new[] { "Auditor" };
            return host;
        }

        private static ReportingTestHost PilotHost() => Host(BusinessEventsReportPermissions.View);

        private static long SeedEvent(ReportingTestHost host, string eventType = "SalesInvoice.Created",
            int? branch = null, Guid? correlationId = null, int company = Company)
        {
            var row = new BusinessEvent
            {
                EventUid = Guid.NewGuid(),
                CompanyID = company,
                BranchID = branch,
                EntityType = eventType.Split('.')[0],
                EntityId = 1001,
                EventType = eventType,
                ActorEmployeeId = Me,
                PayloadVersion = 1,
                Visibility = BusinessEventVisibility.Internal,
                CreatedAt = ReportingTestHost.FixedNow.AddMinutes(-5),
                CorrelationId = correlationId,
            };
            host.Db.BusinessEvents.Add(row);
            host.Db.SaveChanges();
            return row.EventId;
        }

        private static void SeedDispatch(ReportingTestHost host, long eventId, string consumer, string status,
            int attempts = 1, string? error = null)
        {
            host.Db.BusinessEventDispatches.Add(new BusinessEventDispatch
            {
                EventId = eventId,
                Consumer = consumer,
                Status = status,
                Attempts = attempts,
                Error = error,
                UpdatedAt = ReportingTestHost.FixedNow,
            });
            host.Db.SaveChanges();
        }

        // The data source is exercised DIRECTLY, through the real binder, rather than through the engine.
        //
        // The engine's shaper drops fields the caller may not read and applies the layout, which is correct for
        // an end-to-end test and wrong for these: the delivery projection is a property of the SOURCE, and
        // asserting it through the shaper would leave "did the source compute it" and "did the shaper keep it"
        // indistinguishable on failure. The end-to-end path is covered by ReportingBusinessEventsDatasetTests.
        private static async Task<ReportDataSet> FetchAsync(ReportingTestHost host,
            Dictionary<string, string?>? parameters = null, BusinessContext? context = null)
        {
            var definition = BusinessEventsDataset.Report();
            var ctx = context ?? host.Ctx;

            var bound = host.Binder.Bind(definition,
                parameters ?? new Dictionary<string, string?> { ["From"] = "2020-01-01", ["To"] = "2030-12-31" },
                ctx, System.Globalization.CultureInfo.InvariantCulture);

            Assert.True(bound.IsValid,
                "The parameters must bind cleanly, or the test is measuring the binder: " +
                string.Join(" · ", bound.Diagnostics.Select(d => d.ToString())));

            return await new BusinessEventsReportDataSource(host.Db, host.PermissionEvaluator)
                .FetchAsync(new ReportDataQuery
                {
                    Definition = definition,
                    Context = ctx,
                    Parameters = bound.Parameters!,
                    RequestedColumns = definition.Columns,
                    MaxRows = BusinessEventsDataset.MaxRows,
                    Culture = System.Globalization.CultureInfo.InvariantCulture,
                });
        }

        private static object? Cell(ReportDataSet set, int row, string column) => set.Rows[row][column];

        // ============================================================================================
        // PHASE 4 — DELIVERY STATE
        // ============================================================================================

        // WORST-FIRST. Four Done consumers and one Failed makes the event FAILED. Reporting the majority (or
        // the most recently updated) is how a single broken consumer hides behind its healthy peers.
        [Fact]
        public async Task An_event_with_one_failed_consumer_among_healthy_ones_reads_as_failed()
        {
            using var host = PilotHost();

            var eventId = SeedEvent(host);
            SeedDispatch(host, eventId, "Notifications", BusinessEventDispatchStatus.Done);
            SeedDispatch(host, eventId, "Timeline", BusinessEventDispatchStatus.Done);
            SeedDispatch(host, eventId, "Search", BusinessEventDispatchStatus.Failed, attempts: 4,
                error: "connection refused");

            var set = await FetchAsync(host);

            Assert.Single(set.Rows);
            Assert.Equal(DeliveryStates.Failed, Cell(set, 0, "DeliveryState"));
            Assert.Equal("Search", Cell(set, 0, "FailedConsumers"));
            Assert.Equal(4, Cell(set, 0, "DeliveryAttempts"));
        }

        // NO CONSUMERS IS NOT DELIVERED. `All(Done)` over an empty set is true, which would quietly classify
        // every event nobody subscribes to as successfully delivered — the opposite of the operational truth.
        [Fact]
        public async Task An_event_with_no_dispatch_rows_reads_as_None_not_as_delivered()
        {
            using var host = PilotHost();
            SeedEvent(host);

            var set = await FetchAsync(host);

            Assert.Equal(DeliveryStates.None, Cell(set, 0, "DeliveryState"));
            Assert.Null(Cell(set, 0, "DeliveryAttempts"));
        }

        [Fact]
        public async Task Every_consumer_done_reads_as_delivered()
        {
            using var host = PilotHost();

            var eventId = SeedEvent(host);
            SeedDispatch(host, eventId, "Notifications", BusinessEventDispatchStatus.Done);
            SeedDispatch(host, eventId, "Timeline", BusinessEventDispatchStatus.Done);

            var set = await FetchAsync(host);

            Assert.Equal(DeliveryStates.Done, Cell(set, 0, "DeliveryState"));
            Assert.Null(Cell(set, 0, "FailedConsumers"));
        }

        // The operator's sweep: "what is stuck". Failed OR Pending, in one predicate.
        [Fact]
        public async Task The_undelivered_filter_returns_failed_and_pending_but_not_done()
        {
            using var host = PilotHost();

            var failed = SeedEvent(host, "SalesInvoice.Created");
            SeedDispatch(host, failed, "Search", BusinessEventDispatchStatus.Failed);

            var pending = SeedEvent(host, "PurchaseInvoice.Created");
            SeedDispatch(host, pending, "Search", BusinessEventDispatchStatus.Pending);

            var done = SeedEvent(host, "Quotation.Created");
            SeedDispatch(host, done, "Search", BusinessEventDispatchStatus.Done);

            var set = await FetchAsync(host, new Dictionary<string, string?>
            {
                ["From"] = "2020-01-01", ["To"] = "2030-12-31",
                ["DeliveryState"] = DeliveryStates.Undelivered,
            });

            var types = set.Rows.Select(r => r["EventType"] as string).ToList();
            Assert.Equal(2, types.Count);
            Assert.Contains("SalesInvoice.Created", types);
            Assert.Contains("PurchaseInvoice.Created", types);
            Assert.DoesNotContain("Quotation.Created", types);
        }

        // A Claimed row is IN FLIGHT, not delivered. A dispatcher that died mid-batch leaves Claimed rows
        // behind, and classifying those as done would hide exactly the incident the screen exists to surface.
        [Fact]
        public async Task A_claimed_dispatch_counts_as_pending_not_as_delivered()
        {
            using var host = PilotHost();

            var eventId = SeedEvent(host);
            SeedDispatch(host, eventId, "Search", BusinessEventDispatchStatus.Claimed);

            var set = await FetchAsync(host);
            Assert.Equal(DeliveryStates.Pending, Cell(set, 0, "DeliveryState"));
        }

        [Fact]
        public async Task The_consumer_filter_narrows_the_delivery_state_to_one_consumer()
        {
            using var host = PilotHost();

            var eventId = SeedEvent(host);
            SeedDispatch(host, eventId, "Search", BusinessEventDispatchStatus.Failed);
            SeedDispatch(host, eventId, "Notifications", BusinessEventDispatchStatus.Done);

            // Failed, as far as Search is concerned…
            var bySearch = await FetchAsync(host, new Dictionary<string, string?>
            {
                ["From"] = "2020-01-01", ["To"] = "2030-12-31",
                ["DeliveryState"] = DeliveryStates.Failed, ["Consumer"] = "Search",
            });
            Assert.Single(bySearch.Rows);

            // …and not failed as far as Notifications is concerned.
            var byNotifications = await FetchAsync(host, new Dictionary<string, string?>
            {
                ["From"] = "2020-01-01", ["To"] = "2030-12-31",
                ["DeliveryState"] = DeliveryStates.Failed, ["Consumer"] = "Notifications",
            });
            Assert.Empty(byNotifications.Rows);
        }

        // THE FAILURE REASON IS CONFIDENTIAL. An exception message quotes the data that broke it — a
        // constraint violation names the value, a serializer failure quotes the payload fragment. Treating it
        // as ordinary operational text would leak through the diagnostics column what the Payload gate withholds.
        [Fact]
        public void The_delivery_error_field_is_gated_at_the_same_tier_as_the_payload()
        {
            var dataset = BusinessEventsDataset.Definition();

            var error = Assert.Single(dataset.Fields, f => f.Key == "DeliveryError");
            var payload = Assert.Single(dataset.Fields, f => f.Key == "Payload");

            Assert.Equal(ReportFieldSensitivity.Confidential, error.Sensitivity);
            Assert.Equal(payload.RequiredPermissionKey, error.RequiredPermissionKey);
            Assert.False(error.VisibleByDefault);
        }

        // A DISPATCH ERROR FROM A CONSUMER THAT LATER SUCCEEDED IS NOT A LIVE FAILURE. Only failing consumers
        // contribute, or a delivered event would display stale error text and read as broken.
        [Fact]
        public async Task A_stale_error_on_a_now_delivered_consumer_is_not_reported()
        {
            using var host = PilotHost();

            var eventId = SeedEvent(host);
            SeedDispatch(host, eventId, "Search", BusinessEventDispatchStatus.Done, attempts: 3,
                error: "transient timeout on attempt 2");

            var set = await FetchAsync(host);

            Assert.Equal(DeliveryStates.Done, Cell(set, 0, "DeliveryState"));
            Assert.Null(Cell(set, 0, "DeliveryError"));
            Assert.Null(Cell(set, 0, "FailedConsumers"));
        }

        // ============================================================================================
        // PHASE 4 — THE INVESTIGATIVE FILTERS
        // ============================================================================================

        [Fact]
        public async Task The_event_type_filter_accepts_several_values()
        {
            using var host = PilotHost();
            SeedEvent(host, "SalesInvoice.Created");
            SeedEvent(host, "SalesInvoice.Reversed");
            SeedEvent(host, "Quotation.Created");

            var set = await FetchAsync(host, new Dictionary<string, string?>
            {
                ["From"] = "2020-01-01", ["To"] = "2030-12-31",
                ["EventType"] = "SalesInvoice.Created,SalesInvoice.Reversed",
            });

            Assert.Equal(2, set.Rows.Count);
            Assert.DoesNotContain(set.Rows, r => (r["EventType"] as string) == "Quotation.Created");
        }

        [Fact]
        public async Task The_correlation_filter_isolates_one_operation()
        {
            using var host = PilotHost();

            var correlation = Guid.NewGuid();
            SeedEvent(host, "SalesInvoice.Created", correlationId: correlation);
            SeedEvent(host, "StockMovement.Posted", correlationId: correlation);
            SeedEvent(host, "Quotation.Created", correlationId: Guid.NewGuid());

            var set = await FetchAsync(host, new Dictionary<string, string?>
            {
                ["From"] = "2020-01-01", ["To"] = "2030-12-31",
                ["CorrelationId"] = correlation.ToString(),
            });

            Assert.Equal(2, set.Rows.Count);
            Assert.All(set.Rows, r => Assert.Equal(correlation.ToString(), r["CorrelationId"]));
        }

        // A TYPO RETURNS NOTHING, NOT EVERYTHING. An unparseable correlation id that was silently ignored
        // would show the whole log under a filter the user can see in the box — and they would conclude the
        // operation touched every record in it.
        [Fact]
        public async Task An_unparseable_correlation_id_returns_no_rows_rather_than_ignoring_the_filter()
        {
            using var host = PilotHost();
            SeedEvent(host);

            var set = await FetchAsync(host, new Dictionary<string, string?>
            {
                ["From"] = "2020-01-01", ["To"] = "2030-12-31",
                ["CorrelationId"] = "not-a-guid",
            });

            Assert.Empty(set.Rows);
        }

        // THE BRANCH FILTER NARROWS AND NEVER WIDENS. A caller pinned to a branch by their context cannot use
        // the parameter to reach another one — the parameter is only read in the else-branch.
        [Fact]
        public async Task A_branch_pinned_caller_cannot_use_the_branch_filter_to_reach_another_branch()
        {
            using var host = new ReportingTestHost(companyId: Company, employeeId: Me, roles: "Auditor");
            host.PermissionOptions.RoleMap[BusinessEventsReportPermissions.View] = new[] { "Auditor" };

            SeedEvent(host, "SalesInvoice.Created", branch: 10);
            SeedEvent(host, "SalesInvoice.Created", branch: 20);

            var pinned = new BusinessContext
            {
                CompanyId = Company, BranchId = 10, EmployeeId = Me,
                UserId = "test-user", Roles = new[] { "Auditor" }, Source = BusinessContextSource.Test,
            };

            var set = await FetchAsync(host, new Dictionary<string, string?>
            {
                ["From"] = "2020-01-01", ["To"] = "2030-12-31",
                ["FilterBranchId"] = "20",       // asking for the branch they are NOT in
            }, pinned);

            // Branch 10 only. The request for branch 20 changed nothing.
            Assert.Single(set.Rows);
            Assert.Equal(10, Cell(set, 0, "BranchId"));
        }

        [Fact]
        public async Task A_head_office_caller_may_narrow_to_one_branch()
        {
            using var host = PilotHost();

            SeedEvent(host, "SalesInvoice.Created", branch: 10);
            SeedEvent(host, "SalesInvoice.Created", branch: 20);

            var set = await FetchAsync(host, new Dictionary<string, string?>
            {
                ["From"] = "2020-01-01", ["To"] = "2030-12-31",
                ["FilterBranchId"] = "20",
            });

            Assert.Single(set.Rows);
            Assert.Equal(20, Cell(set, 0, "BranchId"));
        }

        // The filter parameter must NOT be named BranchId: that key is reserved and the binder overwrites it
        // from the context, so the input would silently do nothing.
        [Fact]
        public void The_branch_filter_does_not_collide_with_the_reserved_system_parameter()
        {
            var dataset = BusinessEventsDataset.Definition();

            Assert.Contains(dataset.Parameters, p => p.Key == "FilterBranchId" && !p.SystemSupplied);

            var reserved = dataset.Parameters.SingleOrDefault(p => p.Key == ReportSystemParameters.BranchId);
            Assert.True(reserved is null || reserved.SystemSupplied,
                "BranchId is a reserved system key; a user-answerable parameter must not claim it.");
        }

        // ============================================================================================
        // PHASE 5 — THE WORKSPACE CONTRIBUTION
        // ============================================================================================

        private static ReportingWorkspaceSource WorkspaceSource(ReportingTestHost host) =>
            new(host.Library, host.History, host.Templates, host.Catalog, host.Authorization);

        [Fact]
        public async Task The_workspace_source_contributes_favourites_recent_runs_and_saved_layouts()
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

            var links = await WorkspaceSource(host).GetAsync(host.Ctx);

            Assert.Contains(links, l => l.Kind == WorkspaceReportKind.Favorite);
            Assert.Contains(links, l => l.Kind == WorkspaceReportKind.Recent);
            Assert.Contains(links, l => l.Kind == WorkspaceReportKind.Saved);

            // Every link points into the Reporting product, which authorizes on arrival.
            Assert.All(links, l => Assert.StartsWith("/Reports/Viewer/", l.Url));
        }

        // A TILE DOES NOT RUN A REPORT. A dashboard panel whose every tile executes a query on click is a load
        // test with a friendly face.
        [Fact]
        public async Task No_workspace_link_triggers_a_run()
        {
            using var host = new ReportingTestHost();
            await host.Library.AddFavoriteAsync(TestReportDefinitions.SalesCode, null, host.Ctx);

            var links = await WorkspaceSource(host).GetAsync(host.Ctx);

            Assert.NotEmpty(links);
            Assert.All(links, l => Assert.DoesNotContain("run=true", l.Url ?? "",
                StringComparison.OrdinalIgnoreCase));
        }

        // A run that FAILED is contributed and flagged, not hidden — but it carries the error CODE, never the
        // engine's message, which can quote a parameter value onto a shared dashboard.
        [Fact]
        public async Task A_failed_run_is_surfaced_as_needing_attention_without_its_error_text()
        {
            using var host = new ReportingTestHost();

            // A run the engine refuses: a required parameter deliberately blanked.
            await host.Reports().GenerateAsync(new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode,
                Format = ReportOutputFormat.Csv,
                Parameters = new Dictionary<string, string?> { ["From"] = "not-a-date" },
            });

            var links = await WorkspaceSource(host).GetAsync(host.Ctx);
            var recent = links.Where(l => l.Kind == WorkspaceReportKind.Recent).ToList();

            if (recent.Count > 0)
            {
                // Whatever the engine decided, the sub-line is short and code-shaped, never a sentence with a
                // value in it. 60 characters is comfortably longer than any ErrorCode and far shorter than a
                // diagnostic message.
                Assert.All(recent, l => Assert.True((l.Sub?.Length ?? 0) <= 60,
                    "A workspace tile must not carry a diagnostic message: " + l.Sub));
            }
        }

        // Fail closed and QUIETLY. Throwing would push the Workspace's Reports panel into TemporaryFailure and
        // invite a retry that cannot succeed.
        [Fact]
        public async Task An_unresolved_company_contributes_nothing_and_does_not_throw()
        {
            using var host = new ReportingTestHost();

            var unresolved = new BusinessContext
            {
                CompanyId = 0, EmployeeId = Me, UserId = "nobody",
                Roles = new[] { "Reports" }, Source = BusinessContextSource.Test,
            };

            Assert.Empty(await WorkspaceSource(host).GetAsync(unresolved));
        }

        // A report the caller may not run contributes no link — the adapter reads through the gated services,
        // so this follows rather than being separately enforced. Asserted because "follows from" is exactly
        // the kind of claim that stops being true when somebody optimises a query.
        [Fact]
        public async Task A_report_the_caller_cannot_run_contributes_no_workspace_link()
        {
            using var host = new ReportingTestHost();
            await host.Library.AddFavoriteAsync(TestReportDefinitions.SalesCode, null, host.Ctx);

            host.PermissionOptions.RoleMap.Remove(TestReportDefinitions.SalesPermission);

            var links = await WorkspaceSource(host).GetAsync(host.Ctx);
            Assert.DoesNotContain(links, l => l.Kind == WorkspaceReportKind.Favorite);
        }

        // THE CONTAINMENT RULE. Reporting touches the Workspace in ONE file. If a second appears, a contract
        // change over there stops costing one file here — which is the whole reason the adapter exists.
        [Fact]
        public void Only_the_adapter_references_the_workspace_from_the_reporting_platform()
        {
            var reportingDirectory = Path.Combine(RepoRoot(), "CrossBuy", "BL", "Reporting");
            Assert.True(Directory.Exists(reportingDirectory), reportingDirectory);

            // COMMENTS ARE EXCLUDED. The first version of this test matched raw text and flagged
            // ReportsCenterPresenter, whose header says it deliberately does NOT reference the Workspace —
            // a comment explaining the rule was being read as a violation of it.
            var referencing = Directory.GetFiles(reportingDirectory, "*.cs")
                .Where(f => File.ReadLines(f).Any(line =>
                {
                    var code = line.TrimStart();
                    return !code.StartsWith("//", StringComparison.Ordinal)
                           && !code.StartsWith("*", StringComparison.Ordinal)
                           && code.Contains("CrossBuy.BL.Workspace", StringComparison.Ordinal);
                }))
                .Select(Path.GetFileName)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            // ReportingRegistration names the interface in its one AddScoped line; the adapter implements it.
            Assert.Equal(
                new[] { "ReportingRegistration.cs", "ReportingWorkspaceSource.cs" },
                referencing);
        }

        private static string RepoRoot()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("CROSSBUY_REPO_ROOT");
            if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment))
                return fromEnvironment;

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "CrossBuy.sln"))) return directory.FullName;
                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                "Could not locate the repository root above " + AppContext.BaseDirectory +
                ". Set CROSSBUY_REPO_ROOT.");
        }
    }
}
