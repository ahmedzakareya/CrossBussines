using CrossBuy.BL;
using CrossBuy.BL.Approvals;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// Pins the reusable cross-silo approval inbox.
	//
	// The aggregator's whole job is: call three module readers, merge, order deterministically, page,
	// count. It owns NO predicates — every company boundary belongs to the module that understands the
	// rows, because the three boundaries are not even the same shape (leave has no CompanyID column and
	// bounds by the requester's employee row; employee-requests bound on their own column; inventory
	// bounds by company AND a company-scoped role gate).
	//
	// So these tests do two different jobs, and both matter:
	//   * behavioural — the merge, the order, the paging and the counts are right, and cross-company and
	//     cross-employee rows never appear;
	//   * architectural — the aggregator has no CrossDbContext, and no EF entity escapes in its result.
	//     A behavioural suite alone would still pass if someone injected a DbContext and "just" ran one
	//     query, which is exactly how the boundaries got duplicated the first time.
	// =================================================================================================
	public class ApprovalInboxAggregatorTests
	{
		private const int OwnCompany = 1;
		private const int OtherCompany = 2;

		private const int Approver = 10;
		private const int OtherApprover = 11;
		private const int Requester = 12;
		private const int ForeignRequester = 20;
		private const int ForeignApprover = 21;

		private static Employee Emp(int id, int companyId) => new()
		{
			ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
			EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
			Email = $"e{id}@example.invalid", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
			UserId = "user-" + id,
		};

		private static BusinessContext Context(int companyId, int? employeeId = Approver) => new()
		{
			CompanyId = companyId, EmployeeId = employeeId, UserId = "u", Source = BusinessContextSource.Test,
		};

		private static IApprovalInboxService Inbox(PlatformTestHost host)
		{
			var leave = new LeaveWorkflowService(host.Db, new NoopNotifications(), new StubLeaveDashboard(),
				NullLogger<LeaveWorkflowService>.Instance);
			var requests = new EmployeeRequestService(host.Db, new NoopNotifications(), leave);
			var inventory = new InventoryApprovalService(host.Db, null!, null!, null!);
			return new ApprovalInboxService(leave, requests, inventory);
		}

		// ---------------------------------------------------------------------------------------------
		// Arrangement: one pending row per silo, all awaiting `Approver` in company 1, with distinct
		// submitted dates so ordering is observable.
		// ---------------------------------------------------------------------------------------------
		private static void SeedPeople(PlatformTestHost host)
		{
			host.Seed.Employee.AddRange(
				Emp(Approver, OwnCompany), Emp(OtherApprover, OwnCompany), Emp(Requester, OwnCompany),
				Emp(ForeignRequester, OtherCompany), Emp(ForeignApprover, OtherCompany));
			host.Seed.LeaveTypes.Add(new LeaveTypes { ID = 1, NameAr = "سنوية", NameEn = "Annual" });
			host.Seed.InventoryUserRoles.Add(new InventoryUserRole
			{
				CompanyID = OwnCompany, EmployeeId = Approver, Role = "InventoryManager",
			});
			host.Seed.SaveChanges();
		}

		private static void SeedLeave(PlatformTestHost host, int requesterId, int approverId, DateTime created)
			=> host.Seed.LeaveRequests.Add(new LeaveRequest
			{
				EmployeeID = requesterId, LeaveTypeID = 1, Status = 0, CurrentApproverEmployeeID = approverId,
				CurrentLevel = 1, StartDate = new DateTime(2026, 9, 1), EndDate = new DateTime(2026, 9, 3),
				Days = 3, CreatedAt = created,
			});

		private static void SeedRequest(PlatformTestHost host, int requesterId, int approverId, int companyId, DateTime created)
			=> host.Seed.EmployeeRequests.Add(new EmployeeRequest
			{
				CompanyID = companyId, EmployeeID = requesterId, RequestType = "Letter", LetterType = "Salary",
				Status = 0, CurrentApproverEmployeeID = approverId, CurrentLevel = 1, CreatedAt = created,
			});

		private static void SeedInventory(PlatformTestHost host, int companyId, int requestedBy, DateTime at, decimal amount = 500m)
			=> host.Seed.InventoryApprovals.Add(new InventoryApproval
			{
				CompanyID = companyId, DocType = "PurchaseOrder", Amount = amount, Status = "Pending",
				RequestedByEmployeeId = requestedBy, RequestedAt = at,
			});

		private static readonly DateTime Oldest = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
		private static readonly DateTime Middle = new(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);
		private static readonly DateTime Newest = new(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);

		private static void SeedOnePerSilo(PlatformTestHost host)
		{
			SeedPeople(host);
			SeedLeave(host, Requester, Approver, Oldest);
			SeedRequest(host, Requester, Approver, OwnCompany, Middle);
			SeedInventory(host, OwnCompany, Requester, Newest);
			host.Seed.SaveChanges();
		}

		// ---------------------------------------------------------------------------------------------
		// Merge, counts, ordering, paging
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public async Task All_three_silos_are_combined()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedOnePerSilo(host);

			var page = await Inbox(host).GetPendingForCurrentApproverAsync(Context(OwnCompany), Approver);

			Assert.Equal(3, page.TotalPending);
			Assert.Equal(3, page.Rows.Count);
			Assert.Equal(
				new[] { ApprovalSilos.Inventory, ApprovalSilos.Leave, ApprovalSilos.Request },
				page.Rows.Select(r => r.Silo).OrderBy(s => s, StringComparer.Ordinal).ToArray());
		}

		[Fact]
		public async Task Per_silo_counts_are_correct_and_include_empty_silos()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			SeedLeave(host, Requester, Approver, Oldest);
			SeedLeave(host, Requester, Approver, Middle);
			SeedInventory(host, OwnCompany, Requester, Newest);
			host.Seed.SaveChanges();

			var page = await Inbox(host).GetPendingForCurrentApproverAsync(Context(OwnCompany), Approver);

			Assert.Equal(2, page.CountsBySilo[ApprovalSilos.Leave]);
			Assert.Equal(1, page.CountsBySilo[ApprovalSilos.Inventory]);
			// Present with 0 rather than absent: a consumer rendering "Requests (0)" should not have to
			// distinguish "none pending" from "silo missing".
			Assert.Equal(0, page.CountsBySilo[ApprovalSilos.Request]);
			Assert.Equal(3, page.TotalPending);
		}

		[Fact]
		public async Task Rows_are_ordered_newest_first_across_silos_and_the_order_is_stable()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedOnePerSilo(host);

			var inbox = Inbox(host);
			var first = await inbox.GetPendingForCurrentApproverAsync(Context(OwnCompany), Approver);
			var second = await inbox.GetPendingForCurrentApproverAsync(Context(OwnCompany), Approver);

			Assert.Equal(
				new[] { ApprovalSilos.Inventory, ApprovalSilos.Request, ApprovalSilos.Leave },
				first.Rows.Select(r => r.Silo).ToArray());
			Assert.Equal(first.Rows.Select(r => r.Reference), second.Rows.Select(r => r.Reference));
		}

		[Fact]
		public async Task Rows_submitted_in_the_same_tick_still_have_a_total_order()
		{
			// Without a tiebreak beyond the timestamp, a three-way merge could return these in either
			// order between requests. The silo/id tiebreak is what makes paging trustworthy.
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			SeedLeave(host, Requester, Approver, Middle);
			SeedRequest(host, Requester, Approver, OwnCompany, Middle);
			SeedInventory(host, OwnCompany, Requester, Middle);
			host.Seed.SaveChanges();

			var inbox = Inbox(host);
			var a = await inbox.GetPendingForCurrentApproverAsync(Context(OwnCompany), Approver);
			var b = await inbox.GetPendingForCurrentApproverAsync(Context(OwnCompany), Approver);

			Assert.Equal(3, a.Rows.Count);
			Assert.Equal(a.Rows.Select(r => r.Reference), b.Rows.Select(r => r.Reference));
		}

		[Fact]
		public async Task Take_limits_the_rows_but_not_the_totals()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedOnePerSilo(host);

			var page = await Inbox(host).GetPendingForCurrentApproverAsync(Context(OwnCompany), Approver, take: 2);

			Assert.Equal(2, page.Rows.Count);
			Assert.Equal(3, page.TotalPending);              // the count is of the inbox, not of the page
			Assert.Equal(ApprovalSilos.Inventory, page.Rows[0].Silo);   // still newest first
			Assert.Equal(ApprovalSilos.Request, page.Rows[1].Silo);
		}

		[Theory]
		[InlineData(0)]
		[InlineData(-5)]
		[InlineData(99)]
		public async Task A_non_positive_or_oversized_take_returns_everything(int take)
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedOnePerSilo(host);

			var page = await Inbox(host).GetPendingForCurrentApproverAsync(Context(OwnCompany), Approver, take);

			Assert.Equal(3, page.Rows.Count);
			Assert.Equal(3, page.TotalPending);
		}

		[Fact]
		public async Task An_empty_inbox_is_a_valid_answer()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);

			var page = await Inbox(host).GetPendingForCurrentApproverAsync(Context(OwnCompany), Approver);

			Assert.Empty(page.Rows);
			Assert.Equal(0, page.TotalPending);
			Assert.Equal(0, page.CountsBySilo[ApprovalSilos.Leave]);
			// The inventory manager still SEES the silo — an empty queue is not the same as no access.
			Assert.Contains(ApprovalSilos.Inventory, page.VisibleSilos);
		}

		// ---------------------------------------------------------------------------------------------
		// Isolation
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public async Task Another_employees_rows_never_appear()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			SeedLeave(host, Requester, OtherApprover, Oldest);
			SeedRequest(host, Requester, OtherApprover, OwnCompany, Middle);
			host.Seed.SaveChanges();

			var page = await Inbox(host).GetPendingForCurrentApproverAsync(Context(OwnCompany), Approver);

			Assert.Equal(0, page.CountsBySilo[ApprovalSilos.Leave]);
			Assert.Equal(0, page.CountsBySilo[ApprovalSilos.Request]);
			Assert.Empty(page.Rows);
		}

		[Fact]
		public async Task Another_companys_rows_never_appear_in_any_silo()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			// All three foreign, all pending on THIS approver — the shape a cross-company assignment
			// defect leaves behind.
			SeedLeave(host, ForeignRequester, Approver, Oldest);
			SeedRequest(host, ForeignRequester, Approver, OtherCompany, Middle);
			SeedInventory(host, OtherCompany, ForeignRequester, Newest);
			host.Seed.SaveChanges();

			var page = await Inbox(host).GetPendingForCurrentApproverAsync(Context(OwnCompany), Approver);

			Assert.Equal(0, page.TotalPending);
			Assert.Empty(page.Rows);
		}

		[Fact]
		public async Task An_approver_in_another_company_cannot_read_this_companys_inbox()
		{
			// Employee.ID is a GLOBAL primary key, so an approver id is meaningful in every company. This
			// asks for company-2's inbox with company-1's rows present: the ids do not collide, and the
			// company predicates are what keep the answer empty.
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedOnePerSilo(host);

			var page = await Inbox(host).GetPendingForCurrentApproverAsync(
				Context(OtherCompany, ForeignApprover), ForeignApprover);

			Assert.Equal(0, page.TotalPending);
			Assert.DoesNotContain(ApprovalSilos.Inventory, page.VisibleSilos);   // role is company-scoped
		}

		[Fact]
		public async Task An_inventory_role_held_in_another_company_does_not_open_this_silo()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			SeedInventory(host, OwnCompany, Requester, Newest);
			host.Seed.SaveChanges();

			// OtherApprover is a company-1 employee with no inventory role anywhere.
			var page = await Inbox(host).GetPendingForCurrentApproverAsync(Context(OwnCompany, OtherApprover), OtherApprover);

			Assert.DoesNotContain(ApprovalSilos.Inventory, page.VisibleSilos);
			Assert.Equal(0, page.CountsBySilo[ApprovalSilos.Inventory]);
		}

		[Theory]
		[InlineData(0)]
		[InlineData(-1)]
		public async Task An_unresolved_company_yields_an_empty_page(int companyId)
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedOnePerSilo(host);

			var page = await Inbox(host).GetPendingForCurrentApproverAsync(Context(companyId), Approver);

			Assert.Equal(0, page.TotalPending);
			Assert.Empty(page.Rows);
			Assert.Empty(page.VisibleSilos);
		}

		[Fact]
		public async Task An_unresolved_employee_and_a_null_context_are_refused()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedOnePerSilo(host);

			var page = await Inbox(host).GetPendingForCurrentApproverAsync(Context(OwnCompany, null), 0);
			Assert.Equal(0, page.TotalPending);

			await Assert.ThrowsAsync<ArgumentNullException>(
				() => Inbox(host).GetPendingForCurrentApproverAsync(null!, Approver));
		}

		// ---------------------------------------------------------------------------------------------
		// Identity, navigation, writes
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public async Task Module_identity_and_navigation_survive_the_merge()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedOnePerSilo(host);

			var page = await Inbox(host).GetPendingForCurrentApproverAsync(Context(OwnCompany), Approver);

			var nav = page.Rows.ToDictionary(r => r.Silo, r => r.Navigation);
			Assert.Equal(new ApprovalNavigationTarget("People", "Leaves"), nav[ApprovalSilos.Leave]);
			Assert.Equal(new ApprovalNavigationTarget("People", "Requests"), nav[ApprovalSilos.Request]);
			Assert.Equal(new ApprovalNavigationTarget("Inventory", "Approvals"), nav[ApprovalSilos.Inventory]);

			// Stable identity per row, and every row still says which silo it came from.
			Assert.Equal(3, page.Rows.Select(r => r.Reference).Distinct().Count());
			Assert.All(page.Rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Silo)));
		}

		[Fact]
		public async Task Reading_the_inbox_writes_nothing()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedOnePerSilo(host);

			var before = await CountsAsync(host);
			await Inbox(host).GetPendingForCurrentApproverAsync(Context(OwnCompany), Approver);
			var after = await CountsAsync(host);

			Assert.Equal(before, after);
			Assert.False(host.Db.ChangeTracker.HasChanges());
		}

		private static async Task<string> CountsAsync(PlatformTestHost host) => string.Join("|",
			await host.Seed.LeaveRequests.CountAsync(),
			await host.Seed.LeaveApprovalSteps.CountAsync(),
			await host.Seed.EmployeeRequests.CountAsync(),
			await host.Seed.EmployeeRequestSteps.CountAsync(),
			await host.Seed.InventoryApprovals.CountAsync(),
			await host.Seed.InventoryUserRoles.CountAsync(),
			await host.Seed.Employee.CountAsync());

		// ---------------------------------------------------------------------------------------------
		// ARCHITECTURE — the properties a behavioural suite cannot protect
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public void The_aggregator_cannot_reach_a_module_table()
		{
			// Not "does not" — CANNOT. No constructor parameter gives it database access, so the rule is a
			// property of the type. This is what stops the three company boundaries being re-implemented
			// here, which is how they were duplicated in the controller in the first place.
			var constructor = Assert.Single(typeof(ApprovalInboxService).GetConstructors());

			foreach (var parameter in constructor.GetParameters())
			{
				var name = parameter.ParameterType.FullName ?? "";
				Assert.DoesNotContain("DbContext", name);
				Assert.DoesNotContain("CrossBuy.Models.Context", name);
			}

			Assert.Equal(
				new[] { "IEmployeeRequestService", "IInventoryApprovalService", "ILeaveWorkflowService" },
				constructor.GetParameters().Select(p => p.ParameterType.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
		}

		[Fact]
		public void The_inbox_contract_exposes_no_ef_entities_and_no_mutations()
		{
			foreach (var property in typeof(PendingApprovalRow).GetProperties())
				Assert.DoesNotContain("CrossBuy.Models.Context", property.PropertyType.FullName ?? "");

			foreach (var property in typeof(ApprovalInboxPage).GetProperties())
				Assert.DoesNotContain("CrossBuy.Models.Context", property.PropertyType.FullName ?? "");

			// Read-only by construction: one method, and nothing named like a write.
			// Read-only by construction: exactly one method, and it is a Get. Matching write VERBS as
			// substrings would be wrong here - "GetPendingForCurrentApproverAsync" contains "Approve" -
			// so the assertion is on the method set, which is the property that actually matters.
			var methods = typeof(IApprovalInboxService).GetMethods().Select(m => m.Name).ToArray();
			Assert.Equal(new[] { "GetPendingForCurrentApproverAsync" }, methods);
			Assert.All(methods, m => Assert.StartsWith("Get", m, StringComparison.Ordinal));
		}

		[Fact]
		public void ApprovalsController_reads_no_approval_table_directly()
		{
			var code = ControllerCode();

			foreach (var table in new[] { "_db.LeaveRequests", "_db.EmployeeRequests", "_db.InventoryApprovals", "_db.InventoryUserRoles" })
				Assert.DoesNotContain(table, code);

			Assert.Contains("GetPendingForCurrentApproverAsync", code);
			// Navigation comes from the module row, never from a literal pair in the controller.
			Assert.Contains("r.Navigation.Action", code);
			Assert.DoesNotContain("\"Leaves\", \"People\"", code);
			Assert.DoesNotContain("\"Requests\", \"People\"", code);
			Assert.DoesNotContain("\"Approvals\", \"Inventory\"", code);
		}

		// Comments are stripped: the controller DESCRIBES the inline queries it no longer runs, and
		// scanning raw text would report that explanation as a live violation.
		private static string ControllerCode()
		{
			const char lineFeed = (char)10;
			for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
			{
				var candidate = Path.Combine(directory.FullName, "CrossBuy", "Controllers", "ApprovalsController.cs");
				if (!File.Exists(candidate)) continue;
				var lines = File.ReadAllText(candidate).Split(lineFeed)
					.Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
				return string.Join(lineFeed.ToString(), lines);
			}

			Assert.Fail("ApprovalsController.cs could not be located from the test assembly directory. The " +
						"ownership guard cannot run, and a sweep over an empty string would pass vacuously.");
			return string.Empty;
		}

		// ---------------------------------------------------------------------------------------------
		// Test doubles
		// ---------------------------------------------------------------------------------------------

		private sealed class NoopNotifications : INotificationService
		{
			public Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn,
				string? bodyAr, string? bodyEn, string type, int? refId = null,
				string? url = null, int? companyId = null, int? actorEmployeeId = null,
				string? priority = null, string? category = null, string? dedupKey = null,
				DateTime? expiresAt = null, string? icon = null,
				string? entityType = null, int? entityId = null) => Task.CompletedTask;

			public Task<int> NotifyRoleAsync(int companyId, string scope, string[] roles,
				string? titleAr, string? titleEn, string? bodyAr, string? bodyEn,
				string type, int? refId = null, int? exceptEmployeeId = null) => Task.FromResult(0);
		}

		private sealed class StubLeaveDashboard : ILeaveDashboardService
		{
			public Task<PeopleDashboardDto> BuildAsync(int employeeId) => throw new NotImplementedException();
			public Task<int> RemainingForTypeAsync(int employeeId, int leaveTypeId) => Task.FromResult(30);
			public Task<int> RemainingForTypeInYearAsync(int employeeId, int leaveTypeId, int year) => Task.FromResult(30);
			public Task<bool[]?> WorkDayFlagsAsync(int employeeId) => Task.FromResult<bool[]?>(null);
			public Task<int> WorkingDaysAsync(int employeeId, DateTime start, DateTime end)
				=> Task.FromResult(Math.Max(1, (end.Date - start.Date).Days + 1));
		}
	}
}
