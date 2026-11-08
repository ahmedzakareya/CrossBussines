using CrossBuy.BL.Workspace;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// SCR-49 — WHERE THE PERSONAL DASHBOARD'S NUMBERS COME FROM.
	//
	// THE REQUIREMENT: "per-user metrics from approved datasets only", with dataset-level authorization.
	// THE REALITY: the task tiles are computed by ITaskService, a MODULE service. That is correctly
	// authorized — it delegates to ITaskReadAuthorizer and fails closed — but it is not a governed
	// reporting dataset, so the screen does not satisfy the requirement.
	//
	// WHY THAT GAP CANNOT BE CLOSED HERE, and why this suite asserts provenance rather than compliance:
	// KpiRegistry admits dataset codes from AccountingDatasets and ProjectsDatasets ONLY and throws
	// "Unsupported KPI dataset" for anything else. No approved KPI over TASK data can therefore exist,
	// and both that registry and the Work datasets belong to Reporting. Inventing a Workspace-authored
	// "approved" metric would be the Workspace deciding what an approved metric IS — the ADR-037
	// inversion, and the same mistake the withdrawn ReportLibraryWorkspaceReportSource made.
	//
	// So what TAB-6 can own, and what these tests pin:
	//   1. The SEAM exists, so Reporting can close the gap later with no Workspace edit.
	//   2. Provenance is a FACT ON EACH TILE, decided by the service and not claimable by a contributor.
	//   3. The remaining distance is PUBLISHED (UnapprovedMetrics), so an owner can measure it.
	//   4. An approved number, when one exists, WINS over the module number for the same question.
	// =================================================================================================
	public class WorkspaceMetricProvenanceTests
	{
		private const int Company = 1;
		private const int Employee = 11;

		// ==========================================================================================
		// Provenance is recorded, and the service decides it
		// ==========================================================================================

		[Fact]
		public async Task A_metric_from_a_registered_kpi_source_is_marked_approved()
		{
			var dash = await DashboardAsync(new StubKpis("Reporting.Work",
				Metric("Open work", "7")));

			var approved = Assert.Single(dash.Metrics,
				m => m.Provenance == WorkspaceMetricProvenance.ApprovedDataset);
			Assert.Equal("Open work", approved.LabelEn);
			Assert.Equal("Reporting.Work", approved.SourceName);
		}

		[Fact]
		public async Task A_module_metric_is_marked_module_service_and_names_its_producer()
		{
			var dash = await DashboardAsync();

			var module = Assert.Single(dash.Metrics,
				m => m.Provenance == WorkspaceMetricProvenance.ModuleService);
			Assert.Equal("Unread notifications", module.LabelEn);
			Assert.False(string.IsNullOrWhiteSpace(module.SourceName));
		}

		[Fact]
		public async Task A_contributor_cannot_award_itself_approved_provenance()
		{
			// The stub returns a metric STAMPED ModuleService. It still comes back approved, because
			// provenance is decided by being a registered KPI source — not by what the payload claims.
			// If a contributor could set this field, the published gap below would be worthless.
			var dash = await DashboardAsync(new StubKpis("Reporting.Work",
				new WorkspaceMetric
				{
					LabelAr = "مزعوم", LabelEn = "Claimed",
					Value = "1",
					Provenance = WorkspaceMetricProvenance.ModuleService,
					SourceName = "pretending-to-be-a-module",
				}));

			var claimed = Assert.Single(dash.Metrics, m => m.LabelEn == "Claimed");
			Assert.Equal(WorkspaceMetricProvenance.ApprovedDataset, claimed.Provenance);
			Assert.Equal("Reporting.Work", claimed.SourceName);
		}

		// ==========================================================================================
		// The gap is published, not implied
		// ==========================================================================================

		[Fact]
		public async Task UnapprovedMetrics_names_exactly_the_tiles_that_are_not_from_a_dataset()
		{
			var dash = await DashboardAsync(new StubKpis("Reporting.Work", Metric("Open work", "7")));

			Assert.Contains("Unread notifications", dash.UnapprovedMetrics);
			Assert.DoesNotContain("Open work", dash.UnapprovedMetrics);
			Assert.Equal(
				dash.Metrics.Count(m => m.Provenance != WorkspaceMetricProvenance.ApprovedDataset),
				dash.UnapprovedMetrics.Count);
		}

		[Fact]
		public async Task The_dashboard_does_not_claim_approved_only_while_a_module_tile_remains()
		{
			var dash = await DashboardAsync(new StubKpis("Reporting.Work", Metric("Open work", "7")));

			Assert.False(dash.MetricsAreApprovedOnly);
			Assert.NotEmpty(dash.UnapprovedMetrics);
		}

		[Fact]
		public async Task The_dashboard_reports_approved_only_once_every_tile_comes_from_a_dataset()
		{
			// The shape SCR-49 is asking for. Reached here by the approved source answering the only
			// question the modules were answering — which is exactly how the real gap closes.
			var dash = await DashboardAsync(new StubKpis("Reporting.Work",
				Metric("Unread notifications", "3")));

			Assert.True(dash.MetricsAreApprovedOnly);
			Assert.Empty(dash.UnapprovedMetrics);
			Assert.All(dash.Metrics,
				m => Assert.Equal(WorkspaceMetricProvenance.ApprovedDataset, m.Provenance));
		}

		// ==========================================================================================
		// An approved number wins; two producers of one fact is the defect
		// ==========================================================================================

		[Fact]
		public async Task An_approved_dataset_replaces_the_module_tile_for_the_same_question()
		{
			var dash = await DashboardAsync(new StubKpis("Reporting.Work",
				Metric("Unread notifications", "42")));

			// ONE tile for the fact, not two side by side — the duplicate-producer defect the Reporting
			// favourites source was withdrawn for.
			var tile = Assert.Single(dash.Metrics, m => m.LabelEn == "Unread notifications");
			Assert.Equal("42", tile.Value);
			Assert.Equal(WorkspaceMetricProvenance.ApprovedDataset, tile.Provenance);
		}

		// ==========================================================================================
		// Degradation
		// ==========================================================================================

		[Fact]
		public async Task A_throwing_kpi_source_does_not_blank_the_metric_strip()
		{
			var dash = await DashboardAsync(new ThrowingKpis());

			// The module tiles still render. One module's broken contribution must not empty the strip.
			Assert.NotEmpty(dash.Metrics);
			Assert.Contains(dash.Metrics, m => m.LabelEn == "Unread notifications");
		}

		[Fact]
		public async Task An_unavailable_kpi_source_is_never_asked()
		{
			var source = new StubKpis("Reporting.Work", Metric("Open work", "7")) { Available = false };

			var dash = await DashboardAsync(source);

			Assert.False(source.WasAsked);
			Assert.DoesNotContain(dash.Metrics, m => m.LabelEn == "Open work");
		}

		// ==========================================================================================
		// The ADR-037 boundary
		// ==========================================================================================

		[Fact]
		public void The_workspace_registers_no_kpi_source_of_its_own()
		{
			// Reporting owns datasets, KPI definitions and their authority. A Workspace-authored
			// implementation would be the Workspace deciding what an approved metric is. The seam is
			// declared here and filled THERE — this test is what stops that boundary being crossed
			// quietly, the way the withdrawn report source crossed it.
			var services = new ServiceCollection();
			services.AddCrossBusinessWorkspace();

			Assert.DoesNotContain(services,
				d => d.ServiceType == typeof(IWorkspaceKpiSource));
		}

		// ==========================================================================================
		// harness
		// ==========================================================================================

		private static WorkspaceMetric Metric(string labelEn, string value) => new()
		{
			LabelAr = labelEn, LabelEn = labelEn, Value = value,
		};

		/// An EMPTY service provider on purpose: no ITaskService resolves, so the task tiles are absent
		/// and the only module metric left is the notification count. That keeps the assertions about
		/// PROVENANCE rather than about how many tiles Tasks happens to produce today.
		private static async Task<WorkspaceDashboard> DashboardAsync(params IWorkspaceKpiSource[] kpiSources)
		{
			var context = new BusinessContext
			{
				CompanyId = Company, EmployeeId = Employee, UserId = "u" + Employee,
			};

			var workspace = new WorkspaceService(
				new StubContextAccessor(context),
				new ServiceCollection().BuildServiceProvider(),
				new NoNotifications(),
				new NoIdentity(),
				Array.Empty<IWorkspaceFavoritesSource>(),
				Array.Empty<IWorkspaceActivitySource>(),
				Array.Empty<IWorkspaceReportSource>(),
				kpiSources,
				NullLogger<WorkspaceService>.Instance);

			return await workspace.GetDashboardAsync();
		}

		private sealed class StubKpis : IWorkspaceKpiSource
		{
			private readonly WorkspaceMetric[] _metrics;
			public StubKpis(string name, params WorkspaceMetric[] metrics)
			{ SourceName = name; _metrics = metrics; }

			public string SourceName { get; }
			public bool Available { get; init; } = true;
			public bool IsAvailable => Available;
			public bool WasAsked { get; private set; }

			public Task<IReadOnlyList<WorkspaceMetric>> GetAsync(
				BusinessContext context, CancellationToken cancellationToken = default)
			{
				WasAsked = true;
				return Task.FromResult<IReadOnlyList<WorkspaceMetric>>(_metrics);
			}
		}

		private sealed class ThrowingKpis : IWorkspaceKpiSource
		{
			public string SourceName => "Reporting.Broken";
			public bool IsAvailable => true;
			public Task<IReadOnlyList<WorkspaceMetric>> GetAsync(
				BusinessContext context, CancellationToken cancellationToken = default)
				=> throw new InvalidOperationException("the dataset is having a bad day");
		}

		private sealed class NoNotifications : IWorkspaceNotificationSource
		{
			public bool IsAvailable => true;
			public Task<IReadOnlyList<WorkspaceNotification>> GetAsync(
				BusinessContext context, bool unreadOnly, int take, CancellationToken cancellationToken = default)
				=> Task.FromResult<IReadOnlyList<WorkspaceNotification>>(Array.Empty<WorkspaceNotification>());
			public Task<IReadOnlyList<WorkspaceNotification>> GetPageAsync(
				BusinessContext context, bool unreadOnly, int skip, int take, CancellationToken cancellationToken = default)
				=> Task.FromResult<IReadOnlyList<WorkspaceNotification>>(Array.Empty<WorkspaceNotification>());
			public Task<int> CountAsync(BusinessContext context, bool unreadOnly, CancellationToken cancellationToken = default)
				=> Task.FromResult(0);
			public Task<int> CountUnreadAsync(BusinessContext context, CancellationToken cancellationToken = default)
				=> Task.FromResult(0);
		}

		private sealed class NoIdentity : IWorkspaceIdentityResolver
		{
			public Task<(string EmployeeName, string? CompanyName)> ResolveAsync(
				BusinessContext context, CancellationToken cancellationToken = default)
				=> Task.FromResult(("tester", (string?)"Test Co"));
		}
	}
}
