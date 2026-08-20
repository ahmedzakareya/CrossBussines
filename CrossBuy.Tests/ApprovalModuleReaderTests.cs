using CrossBuy.BL;
using CrossBuy.BL.Approvals;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// Pins the MODULE pending-approval readers that feed the cross-silo approval inbox.
	//
	// WHY THESE EXIST. The inbox used to be three inline queries inside ApprovalsController.Index, each
	// carrying its own company boundary. A second consumer (Workspace) could only have re-implemented
	// them — including the boundaries, which is precisely the code that already needed two security
	// fixes. These readers move each boundary into the module that owns the rows, and these tests are
	// what stop a boundary being lost in that move.
	//
	// The boundaries are NOT the same shape in the two silos, and that asymmetry is the point:
	//   * EmployeeRequest carries CompanyID, written from the requester's own Employee.EmpCompanyID, so
	//     the predicate sits directly on the row.
	//   * LeaveRequest has NO CompanyID column, so the boundary is the REQUESTER's Employee row.
	// Neither table is covered by CompanyQueryFilters, so in both cases the predicate is the only thing
	// standing between an approver and another company's HR data.
	// =================================================================================================
	public class ApprovalModuleReaderTests
	{
		private const int OwnCompany = 1;
		private const int OtherCompany = 2;

		private const int Approver = 10;      // company 1, the approver under test
		private const int OtherApprover = 11; // company 1, someone else
		private const int Requester = 12;     // company 1 requester
		private const int ForeignRequester = 20;  // company 2 requester

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

		private static void SeedPeople(PlatformTestHost host)
		{
			host.Seed.Employee.AddRange(
				Emp(Approver, OwnCompany), Emp(OtherApprover, OwnCompany),
				Emp(Requester, OwnCompany), Emp(ForeignRequester, OtherCompany));
			host.Seed.LeaveTypes.Add(new LeaveTypes { ID = 1, NameAr = "سنوية", NameEn = "Annual" });
			host.Seed.SaveChanges();
		}

		private static ILeaveWorkflowService Leave(PlatformTestHost host)
			=> new LeaveWorkflowService(host.Db, new NoopNotifications(), new StubLeaveDashboard(),
				NullLogger<LeaveWorkflowService>.Instance);

		private static IEmployeeRequestService Requests(PlatformTestHost host)
			=> new EmployeeRequestService(host.Db, new NoopNotifications(), Leave(host));

		private static LeaveRequest LeaveRow(int requesterId, int? approverId, int status = 0, int id = 0)
			=> new()
			{
				ID = id, EmployeeID = requesterId, LeaveTypeID = 1, Status = status,
				CurrentApproverEmployeeID = approverId, CurrentLevel = status == 0 ? 1 : 0,
				StartDate = new DateTime(2026, 9, 1), EndDate = new DateTime(2026, 9, 3), Days = 3,
				CreatedAt = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc),
			};

		private static EmployeeRequest RequestRow(int requesterId, int? approverId, int companyId, int status = 0)
			=> new()
			{
				CompanyID = companyId, EmployeeID = requesterId, RequestType = "Letter", LetterType = "Salary",
				Status = status, CurrentApproverEmployeeID = approverId, CurrentLevel = status == 0 ? 1 : 0,
				CreatedAt = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc),
			};

		// ---------------------------------------------------------------------------------------------
		// LEAVE READER
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public async Task Leave_returns_a_pending_request_awaiting_this_approver()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			host.Seed.LeaveRequests.Add(LeaveRow(Requester, Approver));
			await host.Seed.SaveChangesAsync();

			var rows = await Leave(host).PendingForApproverAsync(Context(OwnCompany), Approver);

			var row = Assert.Single(rows);
			Assert.Equal(ApprovalSilos.Leave, row.Silo);
			Assert.Equal("Annual", row.TitleEn);
			Assert.Equal(Requester, row.RequesterEmployeeId);
			Assert.Equal(3, row.Days);
			Assert.Equal("Pending", row.Status);
		}

		[Fact]
		public async Task Leave_excludes_a_request_awaiting_a_different_approver()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			host.Seed.LeaveRequests.Add(LeaveRow(Requester, OtherApprover));
			await host.Seed.SaveChangesAsync();

			Assert.Empty(await Leave(host).PendingForApproverAsync(Context(OwnCompany), Approver));
		}

		[Fact]
		public async Task Leave_excludes_a_request_from_another_company()
		{
			// The requester is in company 2 while the approver's context is company 1. This is the shape a
			// pre-87ec8fa cross-company approver assignment left behind, and it must not surface.
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			host.Seed.LeaveRequests.Add(LeaveRow(ForeignRequester, Approver));
			await host.Seed.SaveChangesAsync();

			Assert.Empty(await Leave(host).PendingForApproverAsync(Context(OwnCompany), Approver));
		}

		[Fact]
		public async Task Leave_returns_only_this_companys_row_when_both_exist()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			host.Seed.LeaveRequests.AddRange(
				LeaveRow(Requester, Approver),
				LeaveRow(ForeignRequester, Approver));
			await host.Seed.SaveChangesAsync();

			var rows = await Leave(host).PendingForApproverAsync(Context(OwnCompany), Approver);

			Assert.Single(rows);
			Assert.Equal(Requester, rows[0].RequesterEmployeeId);
		}

		[Theory]
		[InlineData(1)]   // approved
		[InlineData(2)]   // rejected
		public async Task Leave_excludes_a_decided_request(int status)
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			host.Seed.LeaveRequests.Add(LeaveRow(Requester, Approver, status: status));
			await host.Seed.SaveChangesAsync();

			Assert.Empty(await Leave(host).PendingForApproverAsync(Context(OwnCompany), Approver));
		}

		[Theory]
		[InlineData(0)]
		[InlineData(-1)]
		public async Task Leave_fails_closed_on_an_unresolved_company(int companyId)
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			host.Seed.LeaveRequests.Add(LeaveRow(Requester, Approver));
			await host.Seed.SaveChangesAsync();

			Assert.Empty(await Leave(host).PendingForApproverAsync(Context(companyId), Approver));
		}

		[Fact]
		public async Task Leave_fails_closed_on_an_unresolved_approver_and_a_null_context()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			host.Seed.LeaveRequests.Add(LeaveRow(Requester, Approver));
			await host.Seed.SaveChangesAsync();

			Assert.Empty(await Leave(host).PendingForApproverAsync(Context(OwnCompany), 0));
			await Assert.ThrowsAsync<ArgumentNullException>(
				() => Leave(host).PendingForApproverAsync(null!, Approver));
		}

		[Fact]
		public async Task Leave_carries_module_supplied_navigation()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			host.Seed.LeaveRequests.Add(LeaveRow(Requester, Approver));
			await host.Seed.SaveChangesAsync();

			var row = Assert.Single(await Leave(host).PendingForApproverAsync(Context(OwnCompany), Approver));

			// The module owns the route, so no consumer hardcodes People/Leaves.
			Assert.Equal("People", row.Navigation.Controller);
			Assert.Equal("Leaves", row.Navigation.Action);
		}

		[Fact]
		public async Task Leave_is_deterministic_newest_first()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			host.Seed.LeaveRequests.AddRange(
				LeaveRow(Requester, Approver), LeaveRow(Requester, Approver), LeaveRow(Requester, Approver));
			await host.Seed.SaveChangesAsync();

			var first = await Leave(host).PendingForApproverAsync(Context(OwnCompany), Approver);
			var second = await Leave(host).PendingForApproverAsync(Context(OwnCompany), Approver);

			Assert.Equal(3, first.Count);
			Assert.Equal(first.Select(r => r.EntityId), second.Select(r => r.EntityId));
			Assert.Equal(first.Select(r => r.EntityId).OrderByDescending(i => i), first.Select(r => r.EntityId));
		}

		// ---------------------------------------------------------------------------------------------
		// EMPLOYEE REQUEST READER
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public async Task Request_returns_a_pending_request_awaiting_this_approver()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			host.Seed.EmployeeRequests.Add(RequestRow(Requester, Approver, OwnCompany));
			await host.Seed.SaveChangesAsync();

			var row = Assert.Single(await Requests(host).PendingForApproverAsync(Context(OwnCompany), Approver));
			Assert.Equal(ApprovalSilos.Request, row.Silo);
			Assert.Equal("Letter", row.ApprovalType);
			Assert.Equal(Requester, row.RequesterEmployeeId);
			Assert.Equal("Pending", row.Status);
		}

		[Fact]
		public async Task Request_excludes_a_request_awaiting_a_different_approver()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			host.Seed.EmployeeRequests.Add(RequestRow(Requester, OtherApprover, OwnCompany));
			await host.Seed.SaveChangesAsync();

			Assert.Empty(await Requests(host).PendingForApproverAsync(Context(OwnCompany), Approver));
		}

		[Fact]
		public async Task Request_excludes_another_companys_row_even_when_pending_on_me()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			host.Seed.EmployeeRequests.AddRange(
				RequestRow(Requester, Approver, OwnCompany),
				RequestRow(ForeignRequester, Approver, OtherCompany));
			await host.Seed.SaveChangesAsync();

			var rows = await Requests(host).PendingForApproverAsync(Context(OwnCompany), Approver);

			Assert.Single(rows);
			Assert.Equal(Requester, rows[0].RequesterEmployeeId);
		}

		[Theory]
		[InlineData(1)]
		[InlineData(2)]
		public async Task Request_excludes_a_decided_request(int status)
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			host.Seed.EmployeeRequests.Add(RequestRow(Requester, Approver, OwnCompany, status: status));
			await host.Seed.SaveChangesAsync();

			Assert.Empty(await Requests(host).PendingForApproverAsync(Context(OwnCompany), Approver));
		}

		[Theory]
		[InlineData(0)]
		[InlineData(-1)]
		public async Task Request_fails_closed_on_an_unresolved_company(int companyId)
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			host.Seed.EmployeeRequests.Add(RequestRow(Requester, Approver, OwnCompany));
			await host.Seed.SaveChangesAsync();

			Assert.Empty(await Requests(host).PendingForApproverAsync(Context(companyId), Approver));
		}

		[Fact]
		public async Task Request_fails_closed_on_an_unresolved_approver_and_a_null_context()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			host.Seed.EmployeeRequests.Add(RequestRow(Requester, Approver, OwnCompany));
			await host.Seed.SaveChangesAsync();

			Assert.Empty(await Requests(host).PendingForApproverAsync(Context(OwnCompany), 0));
			await Assert.ThrowsAsync<ArgumentNullException>(
				() => Requests(host).PendingForApproverAsync(null!, Approver));
		}

		[Fact]
		public async Task Request_carries_module_supplied_navigation()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			host.Seed.EmployeeRequests.Add(RequestRow(Requester, Approver, OwnCompany));
			await host.Seed.SaveChangesAsync();

			var row = Assert.Single(await Requests(host).PendingForApproverAsync(Context(OwnCompany), Approver));
			Assert.Equal("People", row.Navigation.Controller);
			Assert.Equal("Requests", row.Navigation.Action);
		}

		// ---------------------------------------------------------------------------------------------
		// Contract shape
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public async Task A_row_carries_a_stable_reference_and_no_ef_entity()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			host.Seed.LeaveRequests.Add(LeaveRow(Requester, Approver));
			await host.Seed.SaveChangesAsync();

			var row = Assert.Single(await Leave(host).PendingForApproverAsync(Context(OwnCompany), Approver));

			Assert.Equal($"Leave:{row.EntityId}", row.Reference);

			// Nothing on the row may be an entity the DbContext tracks: a consumer holding one could
			// re-query through a navigation property, or mutate it.
			foreach (var property in typeof(PendingApprovalRow).GetProperties())
			{
				var name = property.PropertyType.FullName ?? "";
				Assert.DoesNotContain("CrossBuy.Models.Context", name);
			}
		}

		[Fact]
		public async Task Reading_pending_approvals_writes_nothing()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedPeople(host);
			host.Seed.LeaveRequests.Add(LeaveRow(Requester, Approver));
			host.Seed.EmployeeRequests.Add(RequestRow(Requester, Approver, OwnCompany));
			await host.Seed.SaveChangesAsync();

			var before = await CountsAsync(host);
			await Leave(host).PendingForApproverAsync(Context(OwnCompany), Approver);
			await Requests(host).PendingForApproverAsync(Context(OwnCompany), Approver);
			var after = await CountsAsync(host);

			Assert.Equal(before, after);
			Assert.False(host.Db.ChangeTracker.HasChanges());
		}

		private static async Task<string> CountsAsync(PlatformTestHost host) => string.Join("|",
			await host.Seed.LeaveRequests.CountAsync(),
			await host.Seed.LeaveApprovalSteps.CountAsync(),
			await host.Seed.EmployeeRequests.CountAsync(),
			await host.Seed.EmployeeRequestSteps.CountAsync(),
			await host.Seed.Employee.CountAsync());

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
