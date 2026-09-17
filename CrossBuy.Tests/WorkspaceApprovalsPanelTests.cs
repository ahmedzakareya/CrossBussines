using CrossBuy.BL.Approvals;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Workspace;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// Pins the Workspace Approvals panel.
	//
	// THE POINT OF THESE TESTS IS WHAT WORKSPACE DOES *NOT* DO. The panel consumes
	// IApprovalInboxService and nothing else: it never queries LeaveRequests, EmployeeRequests or
	// InventoryApprovals, never re-derives a company boundary, and never offers an approve/reject
	// action. Each of those would be a second copy of code that has already needed two security fixes
	// (96cb210, 87ec8fa), and a copy is what drifts.
	//
	// So the suite is deliberately split: behaviour (total, first N, mapping, states) plus structural
	// guards that a behavioural suite cannot protect — the constructor's dependency list and the
	// Workspace source tree's complete absence of the three silo DbSets.
	// =================================================================================================
	public class WorkspaceApprovalsPanelTests
	{
		private const int Company = 1;
		private const int Employee = 7;

		private static BusinessContext Resolved(int companyId = Company, int? employeeId = Employee) => new()
		{
			CompanyId = companyId, EmployeeId = employeeId, UserId = "u", Source = BusinessContextSource.Test,
		};

		private static PendingApprovalRow Row(string silo, int id, DateTime submitted,
			string controller, string action, string? titleEn = null, string? type = null) => new()
		{
			Silo = silo, EntityId = id, Status = "Pending", SubmittedAt = submitted,
			TitleEn = titleEn, ApprovalType = type,
			Navigation = new ApprovalNavigationTarget(controller, action),
		};

		private static WorkspaceService Workspace(IApprovalInboxService? inbox, BusinessContext? context = null)
			=> Build(inbox, new FixedContext(context ?? Resolved()));

		// The "nobody resolved" request. Deliberately a separate factory: routing it through the optional
		// `context` parameter would be swallowed by the `?? Resolved()` default and the test would silently
		// assert the resolved path instead.
		private static WorkspaceService WorkspaceWithNoContext(IApprovalInboxService inbox)
			=> Build(inbox, new FixedContext(null));

		private static WorkspaceService Build(IApprovalInboxService? inbox, IBusinessContextAccessor contexts)
		{
			var services = new ServiceCollection();
			if (inbox != null) services.AddSingleton(inbox);

			return new WorkspaceService(
				contexts,
				services.BuildServiceProvider(),
				new NoNotifications(), new NoIdentity(),
				Array.Empty<IWorkspaceFavoritesSource>(),
				Array.Empty<IWorkspaceActivitySource>(),
				Array.Empty<IWorkspaceReportSource>(),
				NullLogger<WorkspaceService>.Instance);
		}

		// ---------------------------------------------------------------------------------------------
		// Behaviour
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public async Task The_panel_shows_the_total_and_the_first_rows()
		{
			// 12 pending, 5 shown. The count and the list must not contradict each other: a five-row panel
			// that reported "5" would tell the approver their queue is empty when it is not.
			var inbox = new StubInbox(total: 12, rows: new[]
			{
				Row(ApprovalSilos.Inventory, 3, new DateTime(2026, 8, 3), "Inventory", "Approvals", type: "PurchaseOrder"),
				Row(ApprovalSilos.Request, 2, new DateTime(2026, 8, 2), "People", "Requests", type: "Letter"),
				Row(ApprovalSilos.Leave, 1, new DateTime(2026, 8, 1), "People", "Leaves", titleEn: "Annual"),
			});

			var dashboard = await Workspace(inbox).GetDashboardAsync();

			Assert.Equal(WorkspacePanelState.Ready, dashboard.Approvals.State);
			Assert.Equal(3, dashboard.Approvals.Items.Count);
			Assert.Equal(12, dashboard.Approvals.Total);
			Assert.Equal(12, dashboard.PendingApprovals);
		}

		[Fact]
		public async Task The_panel_asks_the_read_platform_for_a_short_list_not_the_whole_inbox()
		{
			var inbox = new StubInbox(total: 40, rows: Array.Empty<PendingApprovalRow>());

			await Workspace(inbox).GetDashboardAsync();

			// Paging is the read platform's job, so the newest rows ACROSS silos are chosen rather than the
			// newest of whichever silo happened to be listed first.
			Assert.Equal(1, inbox.Calls);
			Assert.True(inbox.LastTake is > 0, "the dashboard must pass a take, not request everything");
			Assert.Equal(Company, inbox.LastContext!.CompanyId);
			Assert.Equal(Employee, inbox.LastEmployeeId);
		}

		[Fact]
		public async Task Module_identity_title_age_and_navigation_are_mapped()
		{
			var submitted = DateTime.UtcNow.Date.AddDays(-4);
			var inbox = new StubInbox(total: 1, rows: new[]
			{
				Row(ApprovalSilos.Leave, 42, submitted, "People", "Leaves", titleEn: "Annual"),
			});

			var dashboard = await Workspace(inbox).GetDashboardAsync();
			var item = Assert.Single(dashboard.Approvals.Items);

			Assert.Equal("Leave:42", item.Reference);
			Assert.Equal(ApprovalSilos.Leave, item.Silo);
			Assert.Equal("Annual", item.Title);
			Assert.Equal(submitted, item.Submitted);
			Assert.Equal(4, item.AgeDays);
			// Built from the MODULE's navigation target — Workspace names no module route of its own.
			Assert.Equal("/People/Leaves", item.Url);
		}

		[Fact]
		public async Task A_row_with_no_title_falls_back_to_the_modules_type_rather_than_rendering_blank()
		{
			// Employee requests carry their kind as the discriminator, not a title; inventory carries a
			// DocType. Inventing a localized label in the BL layer would be the wrong fix.
			var inbox = new StubInbox(total: 2, rows: new[]
			{
				Row(ApprovalSilos.Request, 5, new DateTime(2026, 8, 2), "People", "Requests", type: "Permission"),
				Row(ApprovalSilos.Inventory, 6, new DateTime(2026, 8, 1), "Inventory", "Approvals", type: "WriteOff"),
			});

			var dashboard = await Workspace(inbox).GetDashboardAsync();

			Assert.Equal(new[] { "Permission", "WriteOff" }, dashboard.Approvals.Items.Select(i => i.Title).ToArray());
			Assert.Equal(new[] { "/People/Requests", "/Inventory/Approvals" }, dashboard.Approvals.Items.Select(i => i.Url).ToArray());
		}

		[Fact]
		public async Task An_empty_inbox_is_empty_not_a_failure()
		{
			var dashboard = await Workspace(new StubInbox(total: 0, rows: Array.Empty<PendingApprovalRow>())).GetDashboardAsync();

			Assert.Equal(WorkspacePanelState.Empty, dashboard.Approvals.State);
			Assert.Empty(dashboard.Approvals.Items);
			Assert.Equal(0, dashboard.PendingApprovals);
			// Empty is not "dark": nothing is broken, so it must not appear in the diagnostics strip.
			Assert.DoesNotContain("Approvals", dashboard.UnavailablePanels);
		}

		[Fact]
		public async Task A_deployment_without_the_read_platform_reports_unavailable_not_empty()
		{
			var dashboard = await Workspace(inbox: null).GetDashboardAsync();

			Assert.Equal(WorkspacePanelState.Unavailable, dashboard.Approvals.State);
			Assert.False(string.IsNullOrWhiteSpace(dashboard.Approvals.Reason));
			Assert.Contains("Approvals", dashboard.UnavailablePanels);
		}

		[Fact]
		public async Task A_session_with_no_employee_is_access_denied_and_asks_the_platform_nothing()
		{
			var inbox = new StubInbox(total: 5, rows: Array.Empty<PendingApprovalRow>());

			var dashboard = await Workspace(inbox, Resolved(employeeId: null)).GetDashboardAsync();

			Assert.Equal(WorkspacePanelState.AccessDenied, dashboard.Approvals.State);
			Assert.Equal(0, inbox.Calls);          // fail closed BEFORE reaching the platform
			Assert.Equal(0, dashboard.PendingApprovals);
		}

		[Fact]
		public async Task An_unresolved_company_renders_no_approvals_at_all()
		{
			var inbox = new StubInbox(total: 9, rows: Array.Empty<PendingApprovalRow>());

			var dashboard = await WorkspaceWithNoContext(inbox).GetDashboardAsync();

			// FixedContext(null) is the "nobody resolved" request. The dashboard short-circuits, so the
			// inbox is never asked and no rows can reach the screen.
			Assert.True(dashboard.IsUnresolved);
			Assert.Empty(dashboard.Approvals.Items);
			Assert.Equal(0, inbox.Calls);
		}

		[Fact]
		public async Task A_failing_read_platform_degrades_the_panel_instead_of_the_whole_dashboard()
		{
			var dashboard = await Workspace(new ThrowingInbox()).GetDashboardAsync();

			Assert.Equal(WorkspacePanelState.TemporaryFailure, dashboard.Approvals.State);
			Assert.False(dashboard.IsUnresolved);          // the rest of the dashboard still rendered
			Assert.Equal(0, dashboard.PendingApprovals);
		}

		[Fact]
		public async Task The_context_the_platform_receives_is_the_servers_own()
		{
			// Workspace passes the resolved BusinessContext straight through. It never constructs a company
			// or an employee id, so nothing request-supplied can influence which rows come back.
			var inbox = new StubInbox(total: 1, rows: Array.Empty<PendingApprovalRow>());

			await Workspace(inbox, Resolved(companyId: 65, employeeId: 2052)).GetDashboardAsync();

			Assert.Equal(65, inbox.LastContext!.CompanyId);
			Assert.Equal(2052, inbox.LastEmployeeId);
		}

		// ---------------------------------------------------------------------------------------------
		// Structure — the guards a behavioural suite cannot provide
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public void Workspace_never_queries_an_approval_table()
		{
			// The whole Workspace tree, not just the panel: a query added to any of these files would be a
			// duplicated company boundary regardless of which method it sat in.
			foreach (var file in WorkspaceSourceFiles())
			{
				var code = StripComments(File.ReadAllText(file.Path));

				foreach (var table in new[]
					{ "LeaveRequests", "EmployeeRequests", "InventoryApprovals", "InventoryUserRoles", "LeaveApprovalSteps" })
					Assert.DoesNotContain(table, code);
			}
		}

		[Fact]
		public void Workspace_offers_no_approval_mutation()
		{
			foreach (var file in WorkspaceSourceFiles())
			{
				var code = StripComments(File.ReadAllText(file.Path));

				// Read-only increment: the module services keep every write. Matched with the parenthesis so
				// "ApproverEmployeeId"-style identifiers cannot make this fire spuriously.
				foreach (var verb in new[] { "ApproveAsync(", "RejectAsync(", "DecideAsync(", "PostAsync(", "ReleaseAsync(", "ConfirmAsync(", "EscalateAsync(" })
					Assert.DoesNotContain(verb, code);
			}
		}

		[Fact]
		public void The_panel_consumes_the_read_platform_and_nothing_lower()
		{
			var service = Path.Combine(WorkspaceDirectory(), "WorkspaceService.cs");
			var code = StripComments(File.ReadAllText(service));

			Assert.Contains("IApprovalInboxService", code);
			Assert.Contains("GetPendingForCurrentApproverAsync", code);

			// No module reader is called directly: going around the aggregator would mean Workspace deciding
			// how three silos merge, which is exactly what the platform exists to own.
			Assert.DoesNotContain("ILeaveWorkflowService", code);
			Assert.DoesNotContain("IEmployeeRequestService", code);
			Assert.DoesNotContain("IInventoryApprovalService", code);
		}

		private static string StripComments(string text)
		{
			const char lineFeed = (char)10;
			return string.Join(lineFeed.ToString(), text.Split(lineFeed)
				.Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
		}

		private static string WorkspaceDirectory()
		{
			for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
			{
				var candidate = Path.Combine(directory.FullName, "CrossBuy", "BL", "Workspace");
				if (Directory.Exists(candidate)) return candidate;
			}

			Assert.Fail("CrossBuy/BL/Workspace could not be located from the test assembly directory. The " +
						"structural guards cannot run, and an empty sweep would pass vacuously.");
			return "";
		}

		private static IReadOnlyList<(string Name, string Path)> WorkspaceSourceFiles()
		{
			var files = Directory.GetFiles(WorkspaceDirectory(), "*.cs")
				.Select(p => (Name: Path.GetFileName(p), Path: p)).ToList();
			Assert.NotEmpty(files);
			return files;
		}

		// ---------------------------------------------------------------------------------------------
		// Test doubles
		// ---------------------------------------------------------------------------------------------

		private sealed class StubInbox : IApprovalInboxService
		{
			private readonly int _total;
			private readonly IReadOnlyList<PendingApprovalRow> _rows;

			public StubInbox(int total, IReadOnlyList<PendingApprovalRow> rows) { _total = total; _rows = rows; }

			public int Calls { get; private set; }
			public BusinessContext? LastContext { get; private set; }
			public int LastEmployeeId { get; private set; }
			public int? LastTake { get; private set; }

			public Task<ApprovalInboxPage> GetPendingForCurrentApproverAsync(
				BusinessContext context, int employeeId, int take = 0, CancellationToken cancellationToken = default)
			{
				Calls++;
				LastContext = context; LastEmployeeId = employeeId; LastTake = take;
				return Task.FromResult(new ApprovalInboxPage
				{
					Rows = _rows,
					TotalPending = _total,
					CountsBySilo = new Dictionary<string, int>(),
					VisibleSilos = new HashSet<string> { ApprovalSilos.Leave, ApprovalSilos.Request },
				});
			}
		}

		private sealed class ThrowingInbox : IApprovalInboxService
		{
			public Task<ApprovalInboxPage> GetPendingForCurrentApproverAsync(
				BusinessContext context, int employeeId, int take = 0, CancellationToken cancellationToken = default)
				=> throw new InvalidOperationException("the read platform is having a bad day");
		}

		private sealed class FixedContext : IBusinessContextAccessor
		{
			private readonly BusinessContext? _context;
			public FixedContext(BusinessContext? context) { _context = context; }

			public Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken = default)
				=> _context != null ? Task.FromResult(_context)
					: throw new BusinessContextUnresolvedException("no context");

			public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default)
				=> Task.FromResult(_context);
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
				Task.FromResult(("Tester", (string?)"Test Co"));
		}
	}
}
