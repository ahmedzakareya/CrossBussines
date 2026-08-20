using CrossBuy.BL;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// Pins the company boundary of leave/request APPROVER ASSIGNMENT.
	//
	// WHY THESE EXIST. The upward hierarchy climb that picks approvers had no company predicate.
	// `Hierarchical` carries no CompanyID — it is one shared org tree, which is why Batch B's global
	// query filters deliberately skip it — so on a multi-company install the node above an employee's
	// position could be ANOTHER company's employee. That person became a real approver: written into
	// LeaveRequest.CurrentApproverEmployeeID, notified, shown the request (requester name, dates,
	// reason), and able to approve or reject it. A leave request is HR data about a person in a company
	// that approver has no relationship to.
	//
	// The read-side filter added in 96cb210 keeps such a row off a foreign approver's inbox. These tests
	// cover the other half — that the foreign approver is never ASSIGNED or NOTIFIED in the first place,
	// which is the primary fix. Both halves are asserted, because either alone leaves a hole: assignment
	// alone still leaves pre-existing rows exposed, and the read filter alone still notifies.
	//
	// THE TRAP THIS ALSO PINS. An empty chain means "the requester is at the top of the tree" and
	// AUTO-APPROVES. A chain emptied by a cross-company graft must be REFUSED instead — otherwise
	// closing the isolation leak would turn it into a worse defect: leave approved with no approver at
	// all. That distinction is what ApproverChain.HierarchyDefect exists for, and it is asserted for
	// BOTH consumers of the shared climb (leave and employee requests).
	// =================================================================================================
	public class LeaveApproverCompanyBoundaryTests
	{
		private const int CompanyOne = 1;
		private const int CompanyTwo = 2;

		// ---------------------------------------------------------------------------------------------
		// Arrangement
		// ---------------------------------------------------------------------------------------------

		private static Employee Emp(int id, int companyId, bool active = true) => new()
		{
			ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
			EmpCompanyID = companyId, IsActive = active, Address = "-", PhoneNumber = "-",
			Email = $"e{id}@example.invalid", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
			UserId = "user-" + id,
		};

		// employee node -> position node -> manager employee node, the exact shape the climb walks.
		private static void SeedLink(PlatformTestHost host, int subordinate, int manager)
		{
			host.Seed.Hierarchicals.AddRange(
				new Hierarchical { H_ID = 100 + subordinate, H_Type = 5, H_ObjectID = subordinate, H_Parent = 200 + subordinate },
				new Hierarchical { H_ID = 200 + subordinate, H_Type = 4, H_ObjectID = null, H_Parent = 100 + manager });
		}

		// A top node with no parent: the climb reaches it and stops. Deliberately NOT a unit head, so the
		// stop comes from "no manager above" rather than from IsUnitHead — that keeps these tests about the
		// company boundary rather than about unit-head detection, which has its own coverage.
		private static void SeedTop(PlatformTestHost host, int employeeId)
			=> host.Seed.Hierarchicals.Add(
				new Hierarchical { H_ID = 100 + employeeId, H_Type = 5, H_ObjectID = employeeId, H_Parent = null });

		private static LeaveWorkflowService Leave(PlatformTestHost host, INotificationService? notifications = null)
			=> new(host.Db, notifications ?? new RecordingNotifications(), new StubLeaveDashboard(),
				NullLogger<LeaveWorkflowService>.Instance);

		private static void SeedAnnualLeaveType(PlatformTestHost host)
			=> host.Seed.LeaveTypes.Add(new LeaveTypes { ID = 1, NameAr = "سنوية", NameEn = "Annual" });

		// ---------------------------------------------------------------------------------------------
		// 1 + 2 — the same-company chain still resolves, at one level and at two
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public async Task A_same_company_direct_manager_is_selected()
		{
			using var host = new PlatformTestHost(companyId: CompanyOne);
			host.Seed.Employee.AddRange(Emp(11, CompanyOne), Emp(10, CompanyOne));
			SeedTop(host, 10);
			SeedLink(host, 11, 10);
			await host.Seed.SaveChangesAsync();

			var result = await Leave(host).ApproverChainAsync(11);

			Assert.Equal(new[] { 10 }, result.Approvers.ToArray());
			Assert.False(result.HierarchyDefect);
			Assert.Equal(0, result.DroppedNodes);
		}

		[Fact]
		public async Task A_same_company_manager_two_levels_up_is_selected()
		{
			using var host = new PlatformTestHost(companyId: CompanyOne);
			host.Seed.Employee.AddRange(Emp(11, CompanyOne), Emp(10, CompanyOne), Emp(30, CompanyOne));
			SeedTop(host, 30);
			SeedLink(host, 10, 30);
			SeedLink(host, 11, 10);
			await host.Seed.SaveChangesAsync();

			var result = await Leave(host).ApproverChainAsync(11);

			// Ordered: level 1 is the direct manager, level 2 the one above.
			Assert.Equal(new[] { 10, 30 }, result.Approvers.ToArray());
			Assert.False(result.HierarchyDefect);
		}

		// ---------------------------------------------------------------------------------------------
		// 3 + 4 + 5 — a foreign node is refused, at any depth, and stops the climb
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public async Task A_foreign_company_direct_manager_is_rejected()
		{
			using var host = new PlatformTestHost(companyId: CompanyOne);
			host.Seed.Employee.AddRange(Emp(11, CompanyOne), Emp(20, CompanyTwo));
			SeedTop(host, 20);
			SeedLink(host, 11, 20);
			await host.Seed.SaveChangesAsync();

			var result = await Leave(host).ApproverChainAsync(11);

			Assert.Empty(result.Approvers);
			Assert.DoesNotContain(20, result.Approvers);
			Assert.True(result.HierarchyDefect);
			Assert.Equal(1, result.DroppedNodes);
		}

		[Fact]
		public async Task A_foreign_company_manager_met_during_the_climb_is_rejected()
		{
			using var host = new PlatformTestHost(companyId: CompanyOne);
			host.Seed.Employee.AddRange(Emp(11, CompanyOne), Emp(10, CompanyOne), Emp(20, CompanyTwo));
			SeedTop(host, 20);
			SeedLink(host, 10, 20);
			SeedLink(host, 11, 10);
			await host.Seed.SaveChangesAsync();

			var result = await Leave(host).ApproverChainAsync(11);

			// Level 1 is valid and kept; the foreign level 2 is refused.
			Assert.Equal(new[] { 10 }, result.Approvers.ToArray());
			Assert.True(result.HierarchyDefect);
		}

		[Fact]
		public async Task The_climb_does_not_continue_past_a_foreign_company_node()
		{
			// 11 (co 1) -> 20 (co 2) -> 30 (co 1). Employee 30 is in the RIGHT company, but reaching them
			// means walking THROUGH a subtree that belongs to someone else, so everything above the foreign
			// node is equally untrustworthy. Skipping over 20 and accepting 30 would be the tempting bug.
			using var host = new PlatformTestHost(companyId: CompanyOne);
			host.Seed.Employee.AddRange(Emp(11, CompanyOne), Emp(20, CompanyTwo), Emp(30, CompanyOne));
			SeedTop(host, 30);
			SeedLink(host, 20, 30);
			SeedLink(host, 11, 20);
			await host.Seed.SaveChangesAsync();

			var result = await Leave(host).ApproverChainAsync(11);

			Assert.Empty(result.Approvers);
			Assert.DoesNotContain(30, result.Approvers);
			Assert.DoesNotContain(20, result.Approvers);
			Assert.True(result.HierarchyDefect);
		}

		// ---------------------------------------------------------------------------------------------
		// 6 + 7 — nothing foreign is persisted, and nothing foreign is notified
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public async Task No_foreign_approver_is_ever_written_to_the_request()
		{
			using var host = new PlatformTestHost(companyId: CompanyOne);
			host.Seed.Employee.AddRange(Emp(11, CompanyOne), Emp(10, CompanyOne), Emp(20, CompanyTwo));
			SeedTop(host, 20);
			SeedLink(host, 10, 20);
			SeedLink(host, 11, 10);
			SeedAnnualLeaveType(host);
			await host.Seed.SaveChangesAsync();

			var (ok, _, req) = await Leave(host).CreateAsync(
				11, 1, DateTime.Today.AddDays(3), DateTime.Today.AddDays(4), "r");

			Assert.True(ok);
			Assert.NotNull(req);
			Assert.Equal(10, req!.CurrentApproverEmployeeID);

			// Every persisted step, not just the current one: the foreign manager must not appear as a
			// LATER level either, because DecideAsync advances straight into those rows.
			var steps = await host.Seed.LeaveApprovalSteps.AsNoTracking()
				.Where(s => s.LeaveRequestID == req.ID).ToListAsync();
			Assert.Equal(new[] { 10 }, steps.Select(s => s.ApproverEmployeeID).ToArray());
			Assert.DoesNotContain(20, steps.Select(s => s.ApproverEmployeeID));
		}

		[Fact]
		public async Task No_foreign_employee_is_notified_about_the_request()
		{
			using var host = new PlatformTestHost(companyId: CompanyOne);
			host.Seed.Employee.AddRange(Emp(11, CompanyOne), Emp(10, CompanyOne), Emp(20, CompanyTwo));
			SeedTop(host, 20);
			SeedLink(host, 10, 20);
			SeedLink(host, 11, 10);
			SeedAnnualLeaveType(host);
			await host.Seed.SaveChangesAsync();

			var notifications = new RecordingNotifications();
			await Leave(host, notifications).CreateAsync(
				11, 1, DateTime.Today.AddDays(3), DateTime.Today.AddDays(4), "r");

			// The foreign manager gets nothing: no title, no dates, no requester identity, no deep link.
			Assert.DoesNotContain(20, notifications.Recipients);
			Assert.Contains(10, notifications.Recipients);
		}

		[Fact]
		public async Task Escalation_cannot_reach_a_foreign_approver()
		{
			// DecideAsync advances by reading the stored LeaveApprovalSteps, so a chain that was
			// company-safe at creation stays company-safe all the way up. This drives the real escalation
			// path rather than asserting it from the chain alone.
			using var host = new PlatformTestHost(companyId: CompanyOne);
			host.Seed.Employee.AddRange(
				Emp(11, CompanyOne), Emp(10, CompanyOne), Emp(30, CompanyOne), Emp(20, CompanyTwo));
			SeedTop(host, 30);
			SeedLink(host, 10, 30);
			SeedLink(host, 11, 10);
			// A foreign employee exists in the tree elsewhere, reachable only by a defective graft.
			host.Seed.Hierarchicals.Add(new Hierarchical { H_ID = 900, H_Type = 5, H_ObjectID = 20, H_Parent = null });
			SeedAnnualLeaveType(host);
			await host.Seed.SaveChangesAsync();

			var notifications = new RecordingNotifications();
			var service = Leave(host, notifications);
			var (ok, _, req) = await service.CreateAsync(
				11, 1, DateTime.Today.AddDays(3), DateTime.Today.AddDays(4), "r");
			Assert.True(ok);

			// level 1 approves -> advances to level 2
			var (advanced, error) = await service.DecideAsync(req!.ID, 10, approve: true, note: null);
			Assert.True(advanced, error);

			var reread = await host.Seed.LeaveRequests.AsNoTracking().FirstAsync(r => r.ID == req.ID);
			Assert.Equal(30, reread.CurrentApproverEmployeeID);
			Assert.DoesNotContain(20, notifications.Recipients);
		}

		// ---------------------------------------------------------------------------------------------
		// 8 — the ordinary path is untouched
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public async Task A_same_company_request_still_completes_the_normal_approval_path()
		{
			using var host = new PlatformTestHost(companyId: CompanyOne);
			host.Seed.Employee.AddRange(Emp(11, CompanyOne), Emp(10, CompanyOne));
			SeedTop(host, 10);
			SeedLink(host, 11, 10);
			SeedAnnualLeaveType(host);
			await host.Seed.SaveChangesAsync();

			var service = Leave(host);
			var (ok, _, req) = await service.CreateAsync(
				11, 1, DateTime.Today.AddDays(3), DateTime.Today.AddDays(4), "r");
			Assert.True(ok);
			Assert.Equal(0, req!.Status);                       // pending
			Assert.Equal(1, req.CurrentLevel);
			Assert.Equal(10, req.CurrentApproverEmployeeID);

			var (decided, error) = await service.DecideAsync(req.ID, 10, approve: true, note: "ok");
			Assert.True(decided, error);

			var reread = await host.Seed.LeaveRequests.AsNoTracking().FirstAsync(r => r.ID == req.ID);
			Assert.Equal(1, reread.Status);                     // approved
			Assert.Equal(0, reread.CurrentLevel);
			Assert.Null(reread.CurrentApproverEmployeeID);
			Assert.Equal(10, reread.ApproverEmployeeID);
		}

		// ---------------------------------------------------------------------------------------------
		// 9 + 10 — fail closed, and the genuine top-of-tree case still auto-approves
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public async Task A_requester_with_no_company_on_their_row_produces_no_chain_and_is_refused()
		{
			using var host = new PlatformTestHost(companyId: CompanyOne);
			host.Seed.Employee.AddRange(Emp(11, 0), Emp(10, CompanyOne));
			SeedTop(host, 10);
			SeedLink(host, 11, 10);
			SeedAnnualLeaveType(host);
			await host.Seed.SaveChangesAsync();

			var result = await Leave(host).ApproverChainAsync(11);
			Assert.Empty(result.Approvers);
			Assert.True(result.HierarchyDefect);

			// No company on the row means no basis to validate ANY approver, so the request is refused
			// rather than auto-approved.
			var (ok, error, req) = await Leave(host).CreateAsync(
				11, 1, DateTime.Today.AddDays(3), DateTime.Today.AddDays(4), "r");
			Assert.False(ok);
			Assert.NotNull(error);
			Assert.Null(req);
			Assert.Empty(await host.Seed.LeaveRequests.AsNoTracking().ToListAsync());
		}

		[Fact]
		public async Task A_requester_with_no_employee_row_at_all_is_refused()
		{
			using var host = new PlatformTestHost(companyId: CompanyOne);
			host.Seed.Employee.Add(Emp(10, CompanyOne));
			SeedTop(host, 10);
			SeedLink(host, 11, 10);           // the tree references employee 11, who does not exist
			await host.Seed.SaveChangesAsync();

			var result = await Leave(host).ApproverChainAsync(11);

			Assert.Empty(result.Approvers);
			Assert.True(result.HierarchyDefect);
		}

		[Fact]
		public async Task A_genuine_top_of_tree_requester_is_still_auto_approved()
		{
			// The existing policy for "no manager above me", preserved: an empty chain with NO defect is
			// still an auto-approval. This is the case the defect flag exists to keep separate, so it is
			// asserted rather than assumed.
			using var host = new PlatformTestHost(companyId: CompanyOne);
			host.Seed.Employee.Add(Emp(11, CompanyOne));
			SeedTop(host, 11);                 // employee 11 is the top node; nobody above
			SeedAnnualLeaveType(host);
			await host.Seed.SaveChangesAsync();

			var result = await Leave(host).ApproverChainAsync(11);
			Assert.Empty(result.Approvers);
			Assert.False(result.HierarchyDefect);

			var (ok, _, req) = await Leave(host).CreateAsync(
				11, 1, DateTime.Today.AddDays(3), DateTime.Today.AddDays(4), "r");

			Assert.True(ok);
			Assert.Equal(1, req!.Status);                       // auto-approved, as before
			Assert.Null(req.CurrentApproverEmployeeID);
		}

		[Fact]
		public async Task A_cyclic_org_tree_still_terminates()
		{
			using var host = new PlatformTestHost(companyId: CompanyOne);
			host.Seed.Employee.AddRange(Emp(11, CompanyOne), Emp(10, CompanyOne));

			// A cycle cannot be INSERTED in one pass — Hierarchical's self-referencing FK makes EF refuse to
			// order the batch — so the nodes go in parentless and the cycle is closed by an UPDATE. That is
			// also how a cycle appears in reality: someone re-parents an existing node.
			var nodes = new[]
			{
				new Hierarchical { H_ID = 111, H_Type = 5, H_ObjectID = 11 },
				new Hierarchical { H_ID = 211, H_Type = 4, H_ObjectID = null },
				new Hierarchical { H_ID = 110, H_Type = 5, H_ObjectID = 10 },
				new Hierarchical { H_ID = 210, H_Type = 4, H_ObjectID = null },
			};
			host.Seed.Hierarchicals.AddRange(nodes);
			await host.Seed.SaveChangesAsync();

			nodes[0].H_Parent = 211; nodes[1].H_Parent = 110;   // 11 reports to 10
			nodes[2].H_Parent = 210; nodes[3].H_Parent = 111;   // ...and 10 reports to 11
			await host.Seed.SaveChangesAsync();

			var result = await Leave(host).ApproverChainAsync(11);

			Assert.True(result.Approvers.Count <= 2);
			Assert.Contains(10, result.Approvers);
		}

		// ---------------------------------------------------------------------------------------------
		// 12 — the caller cannot steer the company
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public async Task The_ambient_request_company_cannot_influence_approver_selection()
		{
			// The host's ambient scope is company TWO while the requester belongs to company ONE. If
			// anything in the climb read a request-supplied or ambient company instead of the requester's
			// own row, employee 20 would become a valid approver here. The requester's row is the only
			// permitted basis, so the answer must be identical to the company-one case.
			using var host = new PlatformTestHost(companyId: CompanyTwo);
			host.Seed.Employee.AddRange(Emp(11, CompanyOne), Emp(10, CompanyOne), Emp(20, CompanyTwo));
			SeedTop(host, 20);
			SeedLink(host, 10, 20);
			SeedLink(host, 11, 10);
			await host.Seed.SaveChangesAsync();

			var result = await Leave(host).ApproverChainAsync(11);

			Assert.Equal(new[] { 10 }, result.Approvers.ToArray());
			Assert.DoesNotContain(20, result.Approvers);
			Assert.True(result.HierarchyDefect);
		}

		// ---------------------------------------------------------------------------------------------
		// The SHARED climb: employee requests consume the same selection and must fail closed too
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public async Task An_employee_request_does_not_get_a_foreign_approver()
		{
			using var host = new PlatformTestHost(companyId: CompanyOne);
			host.Seed.Employee.AddRange(Emp(11, CompanyOne), Emp(10, CompanyOne), Emp(20, CompanyTwo));
			SeedTop(host, 20);
			SeedLink(host, 10, 20);
			SeedLink(host, 11, 10);
			await host.Seed.SaveChangesAsync();

			var notifications = new RecordingNotifications();
			var (ok, _, req) = await EmployeeRequests(host, notifications).CreateAsync(new EmployeeRequest
			{
				CompanyID = CompanyOne, EmployeeID = 11, RequestType = "Letter", LetterType = "Salary",
			});

			Assert.True(ok);
			Assert.Equal(10, req!.CurrentApproverEmployeeID);
			Assert.DoesNotContain(20, notifications.Recipients);
		}

		[Fact]
		public async Task An_employee_request_with_a_broken_chain_is_refused_not_auto_approved()
		{
			// The regression this guard exists for. EmployeeRequestService shares the climb and has its own
			// `chain.Count == 0 -> Status = 1` auto-approve branch, so without the defect check the
			// isolation fix would silently approve requests that have no approver at all.
			using var host = new PlatformTestHost(companyId: CompanyOne);
			host.Seed.Employee.AddRange(Emp(11, CompanyOne), Emp(20, CompanyTwo));
			SeedTop(host, 20);
			SeedLink(host, 11, 20);
			await host.Seed.SaveChangesAsync();

			var (ok, error, req) = await EmployeeRequests(host).CreateAsync(new EmployeeRequest
			{
				CompanyID = CompanyOne, EmployeeID = 11, RequestType = "Letter", LetterType = "Salary",
			});

			Assert.False(ok);
			Assert.NotNull(error);
			Assert.Null(req);
			Assert.Empty(await host.Seed.EmployeeRequests.AsNoTracking().ToListAsync());
		}

		private static EmployeeRequestService EmployeeRequests(
			PlatformTestHost host, INotificationService? notifications = null)
		{
			var recorder = notifications ?? new RecordingNotifications();
			return new EmployeeRequestService(host.Db, recorder, Leave(host, recorder));
		}

		// ---------------------------------------------------------------------------------------------
		// 11 — structural: no company constant, no request-controlled company, in the climb itself
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public void The_approver_climb_derives_its_company_only_from_the_requesters_own_row()
		{
			var code = SourceCode("CrossBuy", "BL", "LeaveWorkflowService.cs");

			// The requester's own Employee row is the basis.
			Assert.Contains("e.ID == employeeId", code);
			Assert.Contains("EmpCompanyID", code);

			// No defaulted company, and nothing request-controlled can reach this file.
			Assert.DoesNotContain("CompanyId = 1", code);
			Assert.DoesNotContain("companyId = 1", code);
			Assert.DoesNotContain("FallbackCompanyId", code);
			Assert.DoesNotContain("Request.Query", code);
			Assert.DoesNotContain("Request.Form", code);
			Assert.DoesNotContain("RouteData", code);
			Assert.DoesNotContain("Session", code);
			Assert.DoesNotContain("HttpContext", code);
		}

		[Fact]
		public void Both_consumers_of_the_shared_climb_refuse_a_defective_chain()
		{
			// The auto-approve branch is reachable from two services. A guard in only one of them is the
			// failure mode this asserts against.
			foreach (var file in new[] { "LeaveWorkflowService.cs", "EmployeeRequestService.cs" })
			{
				var code = SourceCode("CrossBuy", "BL", file);
				Assert.Contains("HierarchyDefect", code);
				Assert.Contains("ApproverChainAsync", code);
			}
		}

		// Comment lines are stripped: these files DESCRIBE the defaults they removed, and scanning raw text
		// would report that explanation as a live violation.
		private static string SourceCode(params string[] relativeParts)
		{
			const char lineFeed = (char)10;
			var lines = SourceText(relativeParts).Split(lineFeed)
				.Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
			return string.Join(lineFeed.ToString(), lines);
		}

		private static string SourceText(string[] relativeParts)
		{
			for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
			{
				var candidate = Path.Combine(new[] { directory.FullName }.Concat(relativeParts).ToArray());
				if (File.Exists(candidate)) return File.ReadAllText(candidate);
			}

			Assert.Fail($"{string.Join("/", relativeParts)} could not be located from the test assembly " +
						"directory. The structural guards cannot run, and a sweep over an empty string " +
						"would pass vacuously.");
			return string.Empty;
		}

		// ---------------------------------------------------------------------------------------------
		// Test doubles
		// ---------------------------------------------------------------------------------------------

		// Records WHO was notified. The recipient id is the whole point: a foreign approver receiving any
		// of these notifications would learn the requester's identity, the leave dates and the deep link.
		private sealed class RecordingNotifications : INotificationService
		{
			public List<int> Recipients { get; } = new();

			public Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn,
				string? bodyAr, string? bodyEn, string type, int? refId = null,
				string? url = null, int? companyId = null, int? actorEmployeeId = null,
				string? priority = null, string? category = null, string? dedupKey = null,
				DateTime? expiresAt = null, string? icon = null,
				string? entityType = null, int? entityId = null)
			{
				Recipients.Add(recipientEmployeeId);
				return Task.CompletedTask;
			}

			public Task<int> NotifyRoleAsync(int companyId, string scope, string[] roles,
				string? titleAr, string? titleEn, string? bodyAr, string? bodyEn,
				string type, int? refId = null, int? exceptEmployeeId = null) => Task.FromResult(0);
		}

		// Enough working days and balance that CreateAsync reaches the approver chain — the only part these
		// tests are about. Anything the chain never exercises throws rather than returning a plausible zero.
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
