using CrossBuy.BL.Reporting;
using CrossBuy.BL.Workspace;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // SCR-49 — THE WORK KPIs AND THE WORKSPACE KPI SOURCE.
    //
    // NOT ONE TEST IN THIS FILE TOUCHES A DATABASE OF ANY KIND — no SQL Server, no probe database,
    // and not even the in-memory SQLite ReportingTestHost builds. Everything under test is a
    // registry, a definition and a mapping, so a database would add nothing but a dependency. The
    // dataset registry is constructed from dataset definitions directly and the evaluator is stubbed.
    //
    // THE PROPERTY THIS FILE EXISTS FOR is the one in Startup_does_not_break_when_the_work_pack_is_off.
    // KpiRegistry's constructor runs while the DI graph is being built and IReportDatasetRegistry
    // .Resolve THROWS for an unregistered code. The Work pack is opt-in and switched OFF in every
    // appsettings file today, so an unconditionally-admitted Work KPI would turn a disabled optional
    // feature into an application that cannot start.
    // ============================================================================================
    public class ReportingWorkspaceKpiTests
    {
        private const string Overdue = "work.tasks.overdue";
        private const string WaitingApproval = "work.tasks.waiting-approval";
        private const string OnHold = "work.tasks.on-hold";
        private const string PendingApprovals = "work.approvals.pending";
        private const string OldestApproval = "work.approvals.oldest-age-days";

        private static readonly string[] WorkKpis =
            { Overdue, WaitingApproval, OnHold, PendingApprovals, OldestApproval };

        /// Allows every key. Authority is the evaluator's job and is asserted through the stub below;
        /// the registry only needs something non-null to construct.
        private sealed class AllowAll : IReportPermissionEvaluator
        {
            public Task<bool> HasPermissionAsync(string permissionKey, BusinessContext context,
                CancellationToken cancellationToken = default) => Task.FromResult(true);
        }

        private static IReportDatasetRegistry Datasets(bool withWork) =>
            new ReportDatasetRegistry(
                AccountingDatasets.All()
                    .Concat(ProjectsDatasets.All())
                    .Concat(withWork ? WorkReportingDatasets.All() : Enumerable.Empty<ReportDatasetDefinition>())
                    .Cast<IReportDatasetDefinition>(),
                new AllowAll());

        private static KpiRegistry Registry(bool withWork) => new(Datasets(withWork));

        // ---- A. Work dataset recognition --------------------------------------------------------

        // THE POSITIVE CONTROL for everything below: with the pack registered, all five Work KPIs
        // survive the registry's full validation — dataset code admitted, value field present and
        // aggregatable, filters legal, nothing calculated or Never-sensitive.
        [Fact]
        public void Every_work_kpi_is_admitted_when_the_pack_is_registered()
        {
            var keys = Registry(withWork: true).Definitions.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);

            foreach (var kpi in WorkKpis)
                Assert.Contains(kpi, keys);
        }

        // THE STARTUP-SAFETY PROPERTY. Constructing with the pack absent must not throw, and must
        // simply omit the Work KPIs. If this regresses, every deployment with the default
        // configuration fails to start — a far worse outcome than a missing dashboard tile.
        [Fact]
        public void Startup_does_not_break_when_the_work_pack_is_off()
        {
            var registry = Registry(withWork: false);          // must not throw

            var keys = registry.Definitions.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var kpi in WorkKpis)
                Assert.DoesNotContain(kpi, keys);
        }

        // ---- B. existing Accounting and Projects behaviour is preserved --------------------------

        // The pre-existing KPIs must be present and IDENTICAL in both worlds. SCR-49 adds; it does not
        // get to rearrange what was already there.
        //
        // ASSERTED AS AN EQUALITY BETWEEN THE TWO REGISTRIES, NOT AGAINST A LITERAL COUNT. The first
        // version of this test hardcoded "8 accounting, 3 projects" and failed on the real 7 — and had
        // the number been right, it would have become a tripwire that fires the next time somebody
        // legitimately adds an Accounting KPI, teaching them to edit the number rather than think.
        // What actually matters is that turning the Work pack on changes NOTHING about the other two.
        [Fact]
        public void Accounting_and_projects_kpis_are_identical_with_and_without_the_work_pack()
        {
            static string[] NonWork(KpiRegistry registry) => registry.Definitions
                .Select(d => d.Key)
                .Where(k => !k.StartsWith("work.", StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

            var without = NonWork(Registry(withWork: false));
            var with = NonWork(Registry(withWork: true));

            Assert.Equal(without, with);

            // Non-trivial on both sides, so the equality above cannot be satisfied by two empty sets.
            Assert.Contains("accounting.posted-sales-base", with);
            Assert.Contains("accounting.customer-margin", with);
            Assert.Contains("projects.active", with);
            Assert.Contains("projects.overdue-milestones", with);
        }

        // THE LENIENCY IS NARROW, AND THIS PROVES IT. A missing ACCOUNTING dataset must still fail
        // loudly at construction: Accounting is an unconditional pack, so its absence is a wiring bug,
        // not a configuration choice. Without this test the conditional admission above could have
        // been written to swallow every missing dataset and nothing would have noticed.
        [Fact]
        public void A_missing_unconditional_dataset_still_fails_loudly()
        {
            var onlyProjects = new ReportDatasetRegistry(
                ProjectsDatasets.All().Cast<IReportDatasetDefinition>(), new AllowAll());

            Assert.ThrowsAny<Exception>(() => new KpiRegistry(onlyProjects));
        }

        // ---- C. KPI definition content -----------------------------------------------------------

        // Every Work KPI carries the Work permission key. A KPI that named a weaker key would be a
        // privilege downgrade performed in a catalogue.
        [Fact]
        public void Every_work_kpi_requires_the_work_reporting_permission()
        {
            var registry = Registry(withWork: true);

            foreach (var kpi in WorkKpis)
                Assert.Equal(WorkReportPermissions.View, registry.Resolve(kpi).RequiredPermissionKey);
        }

        // The oldest pending approval is a MAX, not an average: a mean age hides the one request that
        // has been sitting for three weeks behind a dozen opened this morning.
        [Fact]
        public void The_oldest_approval_kpi_is_a_maximum_not_an_average()
        {
            var definition = Registry(withWork: true).Resolve(OldestApproval);

            Assert.Equal(ReportAggregate.Max, definition.Aggregate);
            Assert.Equal("AgeDays", definition.ValueField);
        }

        // Pending approvals carries NO filter, because the dataset is already "Pending approvals".
        [Fact]
        public void The_pending_approvals_kpi_adds_no_filter_of_its_own()
        {
            Assert.Empty(Registry(withWork: true).Resolve(PendingApprovals).Filters);
        }

        // ---- D. the Workspace KPI source ---------------------------------------------------------

        /// Returns a scripted result per key, and records what it was asked for.
        private sealed class StubEvaluator : IKpiEvaluator
        {
            private readonly Func<string, KpiResult> _answer;
            public List<string> Asked { get; } = new();

            public StubEvaluator(Func<string, KpiResult> answer) => _answer = answer;

            public Task<KpiResult> EvaluateAsync(string key, IReadOnlyDictionary<string, string?> parameters,
                CancellationToken ct = default)
            {
                Asked.Add(key);
                return Task.FromResult(_answer(key));
            }
        }

        private static KpiResult Ready(string key, decimal value) => new(key, "READY", value, 7, DateTime.UnixEpoch);
        private static KpiResult Denied(string key) => new(key, "DENIED", null, 7, DateTime.UnixEpoch);

        private static ReportingWorkspaceKpiSource Source(bool withWork, Func<string, KpiResult> answer) =>
            new(Registry(withWork), new StubEvaluator(answer));

        private static BusinessContext Ctx() => new() { CompanyId = 7, EmployeeId = 11 };

        // Registration discovery: with the pack off there is nothing to contribute, so the source
        // stands down and the Workspace keeps its module tiles rather than showing an empty strip.
        [Fact]
        public void The_source_is_unavailable_when_no_work_kpi_is_registered()
        {
            Assert.False(Source(withWork: false, k => Ready(k, 1)).IsAvailable);
        }

        [Fact]
        public void The_source_is_available_once_the_work_kpis_exist()
        {
            Assert.True(Source(withWork: true, k => Ready(k, 1)).IsAvailable);
        }

        // A READY result becomes a tile.
        [Fact]
        public async Task A_ready_kpi_becomes_a_metric()
        {
            var metrics = await Source(withWork: true, k => Ready(k, 3)).GetAsync(Ctx());

            var overdue = Assert.Single(metrics, m => m.LabelEn == "Overdue");
            Assert.Equal("3", overdue.Value);
            Assert.Equal(WorkspaceTone.Critical, overdue.Tone);
        }

        // THE DEFECT THIS PREVENTS. A refused KPI must be ABSENT, never rendered as "0". A tile
        // reading zero is a factual claim that there is nothing to see — the single most actionable
        // thing a dashboard can say — and saying it because authorization failed would be a lie.
        [Theory]
        [InlineData("DENIED")]
        [InlineData("TRUNCATED")]
        [InlineData("INVALID_SOURCE")]
        [InlineData("INVALID_PARAMETERS")]
        public async Task A_non_ready_kpi_contributes_nothing_rather_than_zero(string status)
        {
            var metrics = await Source(withWork: true,
                k => new KpiResult(k, status, null, 7, DateTime.UnixEpoch)).GetAsync(Ctx());

            Assert.Empty(metrics);
        }

        // A READY row with no value is equally not a zero.
        [Fact]
        public async Task A_ready_result_with_no_value_contributes_nothing()
        {
            var metrics = await Source(withWork: true,
                k => new KpiResult(k, "READY", null, 7, DateTime.UnixEpoch)).GetAsync(Ctx());

            Assert.Empty(metrics);
        }

        // One refusal must not suppress the others — a per-KPI decision, not an all-or-nothing strip.
        [Fact]
        public async Task One_denied_kpi_does_not_suppress_the_rest()
        {
            var metrics = await Source(withWork: true,
                k => k == Overdue ? Denied(k) : Ready(k, 2)).GetAsync(Ctx());

            Assert.DoesNotContain(metrics, m => m.LabelEn == "Overdue");
            Assert.NotEmpty(metrics);
        }

        // A throwing evaluator must not blank the dashboard.
        [Fact]
        public async Task A_throwing_evaluator_does_not_blank_the_strip()
        {
            var source = new ReportingWorkspaceKpiSource(Registry(withWork: true),
                new StubEvaluator(k => k == Overdue ? throw new InvalidOperationException("boom") : Ready(k, 1)));

            var metrics = await source.GetAsync(Ctx());

            Assert.DoesNotContain(metrics, m => m.LabelEn == "Overdue");
            Assert.NotEmpty(metrics);
        }

        // ---- E. displacement and provenance -------------------------------------------------------

        // THE LABEL IS LOAD-BEARING. WorkspaceService drops a module tile when an approved dataset
        // answered the same question, matching on LabelEn. "Overdue" is the ITaskService tile's exact
        // label, so this is what makes the governed number REPLACE it instead of sitting beside it.
        // Rename it and SCR-49 silently stops being satisfied while every test still passes.
        [Fact]
        public async Task The_overdue_metric_uses_the_label_that_displaces_the_module_tile()
        {
            var metrics = await Source(withWork: true, k => Ready(k, 1)).GetAsync(Ctx());

            Assert.Contains(metrics, m => m.LabelEn == "Overdue");
        }

        // A contributor cannot award itself approved standing. WorkspaceService stamps provenance when
        // it reads from a registered source; anything this class set would be overwritten anyway, and
        // setting it here would invite a future reader to trust the field at its origin.
        [Fact]
        public async Task The_source_does_not_stamp_its_own_provenance()
        {
            var metrics = await Source(withWork: true, k => Ready(k, 1)).GetAsync(Ctx());

            Assert.All(metrics, m =>
                Assert.Equal(WorkspaceMetricProvenance.ModuleService, m.Provenance));
        }

        // Zero overdue is a GOOD state, not a neutral one — the same reading the module tile used, so
        // swapping the source does not change what a glance means.
        [Fact]
        public async Task Zero_on_a_critical_tile_reads_as_ok_not_as_alarm()
        {
            var metrics = await Source(withWork: true, k => Ready(k, 0)).GetAsync(Ctx());

            var overdue = Assert.Single(metrics, m => m.LabelEn == "Overdue");
            Assert.Equal("0", overdue.Value);
            Assert.Equal(WorkspaceTone.Ok, overdue.Tone);
        }

        // Only the tiles this source claims are evaluated: it must not run every KPI in the registry
        // on every dashboard load, which would put Accounting's queries on the Workspace's critical path.
        [Fact]
        public async Task Only_the_dashboard_tiles_are_evaluated()
        {
            var evaluator = new StubEvaluator(k => Ready(k, 1));
            var source = new ReportingWorkspaceKpiSource(Registry(withWork: true), evaluator);

            await source.GetAsync(Ctx());

            Assert.DoesNotContain(evaluator.Asked, k => k.StartsWith("accounting.", StringComparison.Ordinal));
            Assert.DoesNotContain(evaluator.Asked, k => k.StartsWith("projects.", StringComparison.Ordinal));
            Assert.All(evaluator.Asked, k => Assert.StartsWith("work.", k, StringComparison.Ordinal));
        }
    }
}
