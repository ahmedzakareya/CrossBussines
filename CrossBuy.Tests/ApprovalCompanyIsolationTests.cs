using System.Globalization;
using System.Security.Claims;
using CrossBuy.BL;
using CrossBuy.Controllers;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// Pins the company isolation of the approvals inbox — all three silos.
	//
	// WHY THESE EXIST. ApprovalsController.Index united three approval silos and named a company in NONE
	// of them:
	//
	//   * Inventory: visibility was gated on "does this employee hold InventoryManager" with no company
	//     predicate, and the rows were read with "Status == Pending" and no company predicate. So an
	//     inventory manager saw EVERY company's pending approvals — requester, document type, amount —
	//     and a role granted in ONE company unlocked the inbox in ALL of them.
	//   * Employee requests: the row carries CompanyID and it was never applied.
	//   * Leave: no CompanyID column exists, and nothing tied the requester to the approver's company.
	//
	// None of these had a backstop. CompanyQueryFilters.DeliberatelyUnfiltered lists InventoryUserRole
	// ("read to decide permission, and for another company's audience") and Employee ("the company is
	// resolved FROM it — filtering is circular"); InventoryApproval, LeaveRequest and EmployeeRequest are
	// not filtered either. So the missing predicates were the ONLY thing standing between an approver and
	// another company's data, which is why each one is asserted here directly rather than through a
	// filtered context that could make a missing predicate look harmless.
	//
	// Company 1 is the "own" company throughout and company 2 the foreign one. Employee ids are distinct
	// across companies on purpose: Employee.ID is a GLOBAL primary key, so a test that reused an id
	// across companies would prove collision-safety rather than isolation.
	// =================================================================================================
	public class ApprovalCompanyIsolationTests
	{
		private const int OwnCompany = 1;
		private const int OtherCompany = 2;

		private const int Manager1 = 11;      // InventoryManager in company 1
		private const int Manager2 = 21;      // InventoryManager in company 2
		private const int Clerk1 = 12;        // company 1, no inventory role
		private const int Requester1 = 13;    // company 1 requester
		private const int Requester2 = 23;    // company 2 requester

		// ---------------------------------------------------------------------------------------------
		// Arrangement
		// ---------------------------------------------------------------------------------------------

		// stock/procurement/notification are never reached by the inbox read, so they are deliberately
		// null: a stub would imply this path may call them, and a NullReferenceException would be a
		// louder, more useful failure than a stub quietly absorbing a call.
		private static IInventoryApprovalService Service(CrossBuy.Models.Context.CrossDbContext db)
			=> new InventoryApprovalService(db, null!, null!, null!);

		private static BusinessContext Context(int companyId, int? employeeId) => new()
		{
			CompanyId = companyId,
			EmployeeId = employeeId,
			UserId = "u" + employeeId,
			Source = BusinessContextSource.Test,
		};

		// Employee carries several NOT NULL string columns that have nothing to do with isolation; they are
		// filled from the id so every row is valid and distinguishable without adding noise to the tests.
		private static Employee Person(int id, int companyId, string nameEn) => new()
		{
			ID = id, EmpCompanyID = companyId,
			FullName = "م" + id, FullNameEn = nameEn,
			FirstName = nameEn, LastName = "T", Address = "-", PhoneNumber = "-",
			Email = "e" + id + "@example.invalid", ProfileImage = "-", Gender = "-", MaritalStatus = "-",
			UserId = "u" + id,
		};

		private static void SeedEmployees(PlatformTestHost host)
		{
			host.Seed.Employee.AddRange(
				Person(Manager1, OwnCompany, "Manager One"),
				Person(Clerk1, OwnCompany, "Clerk One"),
				Person(Requester1, OwnCompany, "Requester One"),
				Person(Manager2, OtherCompany, "Manager Two"),
				Person(Requester2, OtherCompany, "Requester Two"));
			host.Seed.SaveChanges();
		}

		private static void SeedInventoryRole(PlatformTestHost host, int companyId, int employeeId)
		{
			host.Seed.InventoryUserRoles.Add(new InventoryUserRole
			{
				CompanyID = companyId, EmployeeId = employeeId, Role = "InventoryManager",
			});
			host.Seed.SaveChanges();
		}

		private static InventoryApproval SeedApproval(
			PlatformTestHost host, int companyId, int requestedBy, string status = "Pending", decimal amount = 500m)
		{
			var row = new InventoryApproval
			{
				CompanyID = companyId, DocType = "PurchaseOrder", Amount = amount, Status = status,
				RequestedByEmployeeId = requestedBy, RequestedAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
			};
			host.Seed.InventoryApprovals.Add(row);
			host.Seed.SaveChanges();
			return row;
		}

		// ---------------------------------------------------------------------------------------------
		// INVENTORY SILO
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public async Task Inventory_manager_sees_a_pending_approval_in_their_own_company()
		{
			using var host = new PlatformTestHost();
			SeedEmployees(host);
			SeedInventoryRole(host, OwnCompany, Manager1);
			var row = SeedApproval(host, OwnCompany, Requester1);

			var (isManager, pending) = await Service(host.Db)
				.ApprovalInboxAsync(Context(OwnCompany, Manager1), Manager1);

			Assert.True(isManager);
			Assert.Equal(new[] { row.ID }, pending.Select(a => a.ID).ToArray());
		}

		[Fact]
		public async Task Inventory_manager_cannot_see_another_companys_pending_approval()
		{
			using var host = new PlatformTestHost();
			SeedEmployees(host);
			SeedInventoryRole(host, OwnCompany, Manager1);
			var mine = SeedApproval(host, OwnCompany, Requester1);
			var theirs = SeedApproval(host, OtherCompany, Requester2);

			var (isManager, pending) = await Service(host.Db)
				.ApprovalInboxAsync(Context(OwnCompany, Manager1), Manager1);

			Assert.True(isManager);
			Assert.Equal(new[] { mine.ID }, pending.Select(a => a.ID).ToArray());
			Assert.DoesNotContain(theirs.ID, pending.Select(a => a.ID));
		}

		[Fact]
		public async Task An_inventory_role_held_in_another_company_does_not_open_this_companys_inbox()
		{
			// THE AUTHORIZATION WIDENING, stated as a test. Manager2 genuinely holds InventoryManager — in
			// company 2. The old gate asked only "does this employee hold the role anywhere", so this
			// employee could read company 1's queue. The company-scoped gate must refuse.
			using var host = new PlatformTestHost();
			SeedEmployees(host);
			SeedInventoryRole(host, OtherCompany, Manager2);
			SeedApproval(host, OwnCompany, Requester1);

			var (isManager, pending) = await Service(host.Db)
				.ApprovalInboxAsync(Context(OwnCompany, Manager2), Manager2);

			Assert.False(isManager);
			Assert.Empty(pending);
		}

		[Fact]
		public async Task An_employee_with_no_inventory_role_gets_no_inbox()
		{
			using var host = new PlatformTestHost();
			SeedEmployees(host);
			SeedInventoryRole(host, OwnCompany, Manager1);
			SeedApproval(host, OwnCompany, Requester1);

			var (isManager, pending) = await Service(host.Db)
				.ApprovalInboxAsync(Context(OwnCompany, Clerk1), Clerk1);

			Assert.False(isManager);
			Assert.Empty(pending);
		}

		[Fact]
		public async Task A_managers_own_request_is_excluded_by_separation_of_duties()
		{
			using var host = new PlatformTestHost();
			SeedEmployees(host);
			SeedInventoryRole(host, OwnCompany, Manager1);
			var own = SeedApproval(host, OwnCompany, Manager1);
			var other = SeedApproval(host, OwnCompany, Requester1);

			var (_, pending) = await Service(host.Db)
				.ApprovalInboxAsync(Context(OwnCompany, Manager1), Manager1);

			Assert.Equal(new[] { other.ID }, pending.Select(a => a.ID).ToArray());
			Assert.DoesNotContain(own.ID, pending.Select(a => a.ID));
		}

		[Fact]
		public async Task A_request_with_no_recorded_requester_still_appears()
		{
			// Guards the `!=` comparison against a nullable column: EF applies null semantics, so a row
			// with no requester must NOT be filtered out by the separation-of-duties predicate. Rewriting
			// that predicate in a way that drops NULLs would hide approvals from the only screen that
			// surfaces them.
			using var host = new PlatformTestHost();
			SeedEmployees(host);
			SeedInventoryRole(host, OwnCompany, Manager1);
			var orphan = SeedApproval(host, OwnCompany, requestedBy: 0);
			orphan.RequestedByEmployeeId = null;
			host.Seed.SaveChanges();

			var (_, pending) = await Service(host.Db)
				.ApprovalInboxAsync(Context(OwnCompany, Manager1), Manager1);

			Assert.Contains(orphan.ID, pending.Select(a => a.ID));
		}

		[Theory]
		[InlineData("Approved")]
		[InlineData("Rejected")]
		public async Task A_decided_approval_is_not_pending(string status)
		{
			using var host = new PlatformTestHost();
			SeedEmployees(host);
			SeedInventoryRole(host, OwnCompany, Manager1);
			SeedApproval(host, OwnCompany, Requester1, status: status);

			var (_, pending) = await Service(host.Db)
				.ApprovalInboxAsync(Context(OwnCompany, Manager1), Manager1);

			Assert.Empty(pending);
		}

		[Theory]
		[InlineData(0)]
		[InlineData(-1)]
		public async Task An_unresolved_company_yields_no_inbox_rather_than_an_unfiltered_one(int companyId)
		{
			using var host = new PlatformTestHost();
			SeedEmployees(host);
			SeedInventoryRole(host, OwnCompany, Manager1);
			SeedApproval(host, OwnCompany, Requester1);

			var (isManager, pending) = await Service(host.Db)
				.ApprovalInboxAsync(Context(companyId, Manager1), Manager1);

			Assert.False(isManager);
			Assert.Empty(pending);
		}

		[Fact]
		public async Task An_unresolved_employee_yields_no_inbox()
		{
			using var host = new PlatformTestHost();
			SeedEmployees(host);
			SeedInventoryRole(host, OwnCompany, Manager1);
			SeedApproval(host, OwnCompany, Requester1);

			var (isManager, pending) = await Service(host.Db)
				.ApprovalInboxAsync(Context(OwnCompany, null), 0);

			Assert.False(isManager);
			Assert.Empty(pending);
		}

		[Fact]
		public async Task A_null_context_is_refused_rather_than_treated_as_no_company()
		{
			using var host = new PlatformTestHost();
			await Assert.ThrowsAsync<ArgumentNullException>(
				() => Service(host.Db).ApprovalInboxAsync(null!, Manager1));
		}

		[Fact]
		public async Task Pending_rows_are_returned_newest_first()
		{
			using var host = new PlatformTestHost();
			SeedEmployees(host);
			SeedInventoryRole(host, OwnCompany, Manager1);
			var first = SeedApproval(host, OwnCompany, Requester1, amount: 100m);
			var second = SeedApproval(host, OwnCompany, Requester1, amount: 200m);
			var third = SeedApproval(host, OwnCompany, Requester1, amount: 300m);

			var (_, pending) = await Service(host.Db)
				.ApprovalInboxAsync(Context(OwnCompany, Manager1), Manager1);

			Assert.Equal(new[] { third.ID, second.ID, first.ID }, pending.Select(a => a.ID).ToArray());
		}

		// ---------------------------------------------------------------------------------------------
		// THE CONTROLLER — all three silos, end to end through Index()
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public async Task The_inbox_shows_only_this_companys_rows_across_all_three_silos()
		{
			using var host = new PlatformTestHost();
			SeedEmployees(host);
			SeedInventoryRole(host, OwnCompany, Manager1);

			var mineInv = SeedApproval(host, OwnCompany, Requester1);
			var theirsInv = SeedApproval(host, OtherCompany, Requester2);

			// Employee requests: one in each company, both pending on Manager1. The foreign one is the
			// shape an approver-assignment defect would produce, and it must not surface.
			host.Seed.EmployeeRequests.AddRange(
				new EmployeeRequest
				{
					CompanyID = OwnCompany, EmployeeID = Requester1, RequestType = "Letter", LetterType = "Salary",
					Status = 0, CurrentApproverEmployeeID = Manager1, CreatedAt = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
				},
				new EmployeeRequest
				{
					CompanyID = OtherCompany, EmployeeID = Requester2, RequestType = "Letter", LetterType = "Salary",
					Status = 0, CurrentApproverEmployeeID = Manager1, CreatedAt = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc),
				});

			// Leave: same arrangement. LeaveRequests has no CompanyID, so the boundary is the requester's
			// own Employee row.
			host.Seed.LeaveTypes.Add(new LeaveTypes { ID = 1, NameAr = "سنوية", NameEn = "Annual" });
			host.Seed.SaveChanges();
			host.Seed.LeaveRequests.AddRange(
				new LeaveRequest
				{
					EmployeeID = Requester1, LeaveTypeID = 1, Status = 0, CurrentApproverEmployeeID = Manager1,
					StartDate = new DateTime(2026, 9, 1), EndDate = new DateTime(2026, 9, 3), Days = 3,
					CreatedAt = new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc),
				},
				new LeaveRequest
				{
					EmployeeID = Requester2, LeaveTypeID = 1, Status = 0, CurrentApproverEmployeeID = Manager1,
					StartDate = new DateTime(2026, 9, 5), EndDate = new DateTime(2026, 9, 6), Days = 2,
					CreatedAt = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc),
				});
			host.Seed.SaveChanges();

			var result = await Controller(host, Manager1, Context(OwnCompany, Manager1)).Index();

			var view = Assert.IsType<ViewResult>(result);
			var rows = Assert.IsAssignableFrom<List<ApprovalInboxRow>>(view.Model);

			Assert.Equal(1, Count(view, "LeaveCount"));
			Assert.Equal(1, Count(view, "RequestCount"));
			Assert.Equal(1, Count(view, "InventoryCount"));
			Assert.Equal(3, rows.Count);

			// The foreign inventory row is the one with a distinguishable identity; assert its absence by
			// the requester name resolved onto the row.
			Assert.DoesNotContain("Requester Two", rows.Select(r => r.Requester));
			Assert.All(rows, r => Assert.NotEqual("Requester Two", r.Requester));
			Assert.Contains(mineInv.ID.ToString(), new[] { mineInv.ID.ToString() });
			Assert.NotEqual(mineInv.ID, theirsInv.ID);

			// Deterministic ordering: newest requested-at first, across silos.
			var dates = rows.Select(r => r.Date ?? DateTime.MinValue).ToList();
			Assert.Equal(dates.OrderByDescending(d => d).ToList(), dates);
		}

		[Fact]
		public async Task Another_employees_pending_rows_never_appear()
		{
			using var host = new PlatformTestHost();
			SeedEmployees(host);
			SeedInventoryRole(host, OwnCompany, Manager1);

			// Everything below is pending on Clerk1, not on Manager1.
			host.Seed.EmployeeRequests.Add(new EmployeeRequest
			{
				CompanyID = OwnCompany, EmployeeID = Requester1, RequestType = "Letter",
				Status = 0, CurrentApproverEmployeeID = Clerk1, CreatedAt = DateTime.UnixEpoch,
			});
			host.Seed.LeaveTypes.Add(new LeaveTypes { ID = 1, NameAr = "سنوية", NameEn = "Annual" });
			host.Seed.SaveChanges();
			host.Seed.LeaveRequests.Add(new LeaveRequest
			{
				EmployeeID = Requester1, LeaveTypeID = 1, Status = 0, CurrentApproverEmployeeID = Clerk1,
				StartDate = new DateTime(2026, 9, 1), EndDate = new DateTime(2026, 9, 2), Days = 2,
				CreatedAt = DateTime.UnixEpoch,
			});
			host.Seed.SaveChanges();

			var result = await Controller(host, Manager1, Context(OwnCompany, Manager1)).Index();
			var view = Assert.IsType<ViewResult>(result);

			Assert.Equal(0, Count(view, "LeaveCount"));
			Assert.Equal(0, Count(view, "RequestCount"));
			Assert.Empty(Assert.IsAssignableFrom<List<ApprovalInboxRow>>(view.Model));
		}

		[Fact]
		public async Task Decided_rows_never_appear()
		{
			using var host = new PlatformTestHost();
			SeedEmployees(host);
			SeedInventoryRole(host, OwnCompany, Manager1);

			host.Seed.EmployeeRequests.Add(new EmployeeRequest
			{
				CompanyID = OwnCompany, EmployeeID = Requester1, RequestType = "Letter",
				Status = 1, CurrentApproverEmployeeID = Manager1, CreatedAt = DateTime.UnixEpoch,
			});
			host.Seed.LeaveTypes.Add(new LeaveTypes { ID = 1, NameAr = "سنوية", NameEn = "Annual" });
			host.Seed.SaveChanges();
			host.Seed.LeaveRequests.Add(new LeaveRequest
			{
				EmployeeID = Requester1, LeaveTypeID = 1, Status = 1, CurrentApproverEmployeeID = Manager1,
				StartDate = new DateTime(2026, 9, 1), EndDate = new DateTime(2026, 9, 2), Days = 2,
				CreatedAt = DateTime.UnixEpoch,
			});
			host.Seed.SaveChanges();

			var view = Assert.IsType<ViewResult>(await Controller(host, Manager1, Context(OwnCompany, Manager1)).Index());

			Assert.Equal(0, Count(view, "LeaveCount"));
			Assert.Equal(0, Count(view, "RequestCount"));
		}

		[Fact]
		public async Task An_unresolved_business_context_does_not_render_an_unfiltered_inbox()
		{
			using var host = new PlatformTestHost();
			SeedEmployees(host);
			SeedInventoryRole(host, OwnCompany, Manager1);
			SeedApproval(host, OwnCompany, Requester1);

			var controller = Controller(host, Manager1, context: null);
			var result = await controller.Index();

			// Fail closed: back to sign-in, NOT a view over every company's approvals.
			var redirect = Assert.IsType<RedirectToActionResult>(result);
			Assert.Equal("Login", redirect.ActionName);
			Assert.Equal("Account", redirect.ControllerName);
		}

		[Fact]
		public async Task Reading_the_inbox_writes_nothing()
		{
			using var host = new PlatformTestHost();
			SeedEmployees(host);
			SeedInventoryRole(host, OwnCompany, Manager1);
			SeedApproval(host, OwnCompany, Requester1);
			SeedApproval(host, OtherCompany, Requester2);

			var before = await RowCountsAsync(host);
			await Controller(host, Manager1, Context(OwnCompany, Manager1)).Index();
			var after = await RowCountsAsync(host);

			Assert.Equal(before, after);
			Assert.False(host.Db.ChangeTracker.HasChanges(), "the inbox read must not stage any change");
		}

		private static async Task<string> RowCountsAsync(PlatformTestHost host)
		{
			var db = host.Seed;
			return string.Join("|",
				await db.InventoryApprovals.CountAsync(),
				await db.InventoryUserRoles.CountAsync(),
				await db.EmployeeRequests.CountAsync(),
				await db.LeaveRequests.CountAsync(),
				await db.Employee.CountAsync());
		}

		private static int Count(ViewResult view, string key)
			=> Convert.ToInt32(view.ViewData[key], CultureInfo.InvariantCulture);

		// ---------------------------------------------------------------------------------------------
		// OWNERSHIP — the bypass must not come back
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public void ApprovalsController_does_not_query_the_inventory_tables_directly()
		{
			// A STRUCTURAL guard, not a behavioural one. The behavioural tests above would still pass if
			// someone reintroduced an inline unscoped query alongside the delegation, so this asserts the
			// controller cannot reach either table at all: the company predicate and the role gate live in
			// the owner service, and that is where they must stay.
			var source = ApprovalsControllerCode();

			Assert.DoesNotContain("_db.InventoryApprovals", source);
			Assert.DoesNotContain("_db.InventoryUserRoles", source);
			Assert.DoesNotContain("InventoryUserRoles", source);
			// After the read-platform migration the controller reaches NONE of the three silo tables and
			// delegates the whole union. Stricter than the original assertion, which only covered inventory.
			Assert.DoesNotContain("_db.LeaveRequests", source);
			Assert.DoesNotContain("_db.EmployeeRequests", source);
			Assert.Contains("GetPendingForCurrentApproverAsync", source);
		}

		[Fact]
		public void Every_company_sensitive_silo_query_names_a_company()
		{
			var source = ApprovalsControllerCode();

			// The company comes from the resolved context and nowhere else.
			Assert.Contains("_context.TryGetCurrentAsync()", source);
			Assert.Contains("context.CompanyId", source);

			// The silo predicates now live in the MODULE readers that own the rows, so this asserts them
			// where they are rather than where they used to be - and covers all three silos, not two.
			var leaveCode = SourceCode("CrossBuy", "BL", "LeaveWorkflowService.cs");
			var requestCode = SourceCode("CrossBuy", "BL", "EmployeeRequestService.cs");
			var inventoryCode = SourceCode("CrossBuy", "BL", "InventoryApprovalService.cs");

			// Leave has no CompanyID column: the boundary is the requester's own Employee row.
			Assert.Contains("r.Employee.EmpCompanyID == context.CompanyId", leaveCode);
			// EmployeeRequest states its own CompanyID.
			Assert.Contains("r.CompanyID == context.CompanyId", requestCode);
			// Inventory bounds the rows AND the role gate by company.
			Assert.Contains("a.CompanyID == context.CompanyId", inventoryCode);
			Assert.Contains("r.CompanyID == context.CompanyId", inventoryCode);

			foreach (var moduleCode in new[] { leaveCode, requestCode, inventoryCode })
			{
				Assert.DoesNotContain("Request.Query", moduleCode);
				Assert.DoesNotContain("Request.Form", moduleCode);
				Assert.DoesNotContain("Session.Get", moduleCode);
			}

			// The leave and employee-request modules carry NO defaulted company at all.
			Assert.DoesNotContain("CompanyId = 1", leaveCode);
			Assert.DoesNotContain("CompanyId = 1", requestCode);

			// InventoryApprovalService is the honest exception, and the assertion is scoped rather than
			// dropped. Its file still declares `private const int CompanyId = 1` for the LEGACY WRITE paths
			// (RequiresApprovalAsync / SubmitAsync / ApproveAsync / RejectAsync), which 96cb210 deliberately
			// left to the Stage 1 Batch A conversion that owns them. What this phase must guarantee is that
			// the READ path never touches it: ApprovalInboxAsync resolves the company from the passed
			// BusinessContext, asserted above. This narrows to the read method's own body so the constant
			// cannot leak into it without failing here.
			var inboxBody = MethodBody(inventoryCode, "public async Task<(bool isInventoryManager, List<InventoryApproval> pending)> ApprovalInboxAsync");
			Assert.Contains("context.CompanyId", inboxBody);
			Assert.DoesNotContain("CompanyId = 1", inboxBody);
			Assert.DoesNotContain("== CompanyId", inboxBody);

			// No request-controlled or defaulted company may appear.
			Assert.DoesNotContain("CompanyId = 1", source);
			Assert.DoesNotContain("companyId = 1", source);
			Assert.DoesNotContain("Request.Query", source);
			Assert.DoesNotContain("Request.Form", source);
			Assert.DoesNotContain("Session.Get", source);
		}

		// Comment lines are stripped before the structural assertions run. The controller's comments NAME
		// the tables it no longer queries, in order to record what was removed and why — scanning raw text
		// would report that history as a live violation, and deleting the explanation to satisfy a grep is
		// the wrong trade.
		private static string ApprovalsControllerCode()
		{
			const char lineFeed = (char)10;   // written as a code point: an escape sequence here does not survive the toolchain
			var lines = ApprovalsControllerSource().Split(lineFeed)
				.Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
			return string.Join(lineFeed.ToString(), lines);
		}

		// The text of one method, from its signature to the next method at the same indentation. Crude but
		// sufficient, and far better than asserting over a whole file that legitimately contains a legacy
		// constant on paths this phase does not own.
		private static string MethodBody(string code, string signature)
		{
			var start = code.IndexOf(signature, StringComparison.Ordinal);
			Assert.True(start >= 0, $"method not found: {signature}");
			var next = code.IndexOf(((char)10) + "		public ", start + signature.Length, StringComparison.Ordinal);
			return next < 0 ? code[start..] : code[start..next];
		}

		// Same comment-stripping rule as ApprovalsControllerCode, for any source file.
		private static string SourceCode(params string[] parts)
		{
			const char lineFeed = (char)10;
			for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
			{
				var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
				if (!File.Exists(candidate)) continue;
				var lines = File.ReadAllText(candidate).Split(lineFeed)
					.Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
				return string.Join(lineFeed.ToString(), lines);
			}

			Assert.Fail($"{string.Join("/", parts)} could not be located from the test assembly directory. " +
						"The structural guard cannot run, and a sweep over an empty string would pass vacuously.");
			return string.Empty;
		}

		private static string ApprovalsControllerSource()
		{
			for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
			{
				var candidate = Path.Combine(directory.FullName, "CrossBuy", "Controllers", "ApprovalsController.cs");
				if (File.Exists(candidate)) return File.ReadAllText(candidate);
			}

			Assert.Fail("CrossBuy/Controllers/ApprovalsController.cs could not be located from the test " +
						"assembly directory. The ownership guards cannot run, and a sweep over an empty " +
						"string would pass vacuously.");
			return string.Empty;
		}

		// ---------------------------------------------------------------------------------------------
		// Controller harness
		// ---------------------------------------------------------------------------------------------

		private static ApprovalsController Controller(PlatformTestHost host, int employeeId, BusinessContext? context)
		{
			var accessor = context == null
				? StubContextAccessor.Unresolved()
				: new StubContextAccessor(context);

			// The controller now consumes the reusable cross-silo inbox instead of querying the three
			// silos itself. The inbox is built from the REAL module readers, so these tests still drive
			// every company boundary end to end — that is what makes them the migration's proof that the
			// rendered page is unchanged.
			var leave = new LeaveWorkflowService(host.Db, new InboxNoopNotifications(),
				new InboxStubLeaveDashboard(), Microsoft.Extensions.Logging.Abstractions.NullLogger<LeaveWorkflowService>.Instance);
			var inbox = new CrossBuy.BL.Approvals.ApprovalInboxService(
				leave, new EmployeeRequestService(host.Db, new InboxNoopNotifications(), leave), Service(host.Db));

			var controller = new ApprovalsController(
				new StubEmployees(employeeId), host.Db, new StubLocalizer(), inbox, accessor);

			var http = new DefaultHttpContext
			{
				User = new ClaimsPrincipal(new ClaimsIdentity(
					new[] { new Claim(ClaimTypes.NameIdentifier, "u" + employeeId) }, "test")),
			};

			controller.ControllerContext = new ControllerContext { HttpContext = http };
			controller.Url = new StubUrlHelper(controller.ControllerContext);
			controller.TempData = new TempDataDictionary(http, new StubTempDataProvider());
			return controller;
		}

		private sealed class StubEmployees : IEmployeeService
		{
			private readonly int _employeeId;
			public StubEmployees(int employeeId) => _employeeId = employeeId;

			public Task<CrossBuy.ViewModel.EmployeeViewModel> GetEmployeeByUserIdAsync(string userId)
				=> Task.FromResult(new CrossBuy.ViewModel.EmployeeViewModel { ID = _employeeId });

			// Not part of the inbox path. Throwing beats returning a plausible value: if the controller ever
			// starts calling one of these, the test should say so rather than quietly agreeing.
			public Task<CrossBuy.ViewModel.EmployeeViewModel> GetByIdAsync(int id) => throw new NotSupportedException();
			public Task<List<CrossBuy.ViewModel.EmployeeListItemDto>> GetAllAsync() => throw new NotSupportedException();
			public Task<CrossBuy.ViewModel.EmployeeViewModel> SaveEmployeeAsync(
				CrossBuy.ViewModel.EmployeeViewModel model, IFormFile profileImage, string webRootPath)
				=> throw new NotSupportedException();
			public Task<CrossBuy.ViewModel.EmployeeViewModel> SaveEmployeeImageAsync(
				int employeeId, IFormFile profileImage, string webRootPath) => throw new NotSupportedException();
		}

		private sealed class StubLocalizer : IStringLocalizer<CrossBuy.SharedResources>
		{
			public LocalizedString this[string name] => new(name, name, resourceNotFound: false);
			public LocalizedString this[string name, params object[] arguments]
				=> new(name, string.Format(CultureInfo.InvariantCulture, name, arguments), resourceNotFound: false);
			public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
				=> Array.Empty<LocalizedString>();
		}

		private sealed class StubUrlHelper : IUrlHelper
		{
			public StubUrlHelper(ActionContext actionContext) => ActionContext = actionContext;
			public ActionContext ActionContext { get; }
			public string? Action(UrlActionContext actionContext)
				=> "/" + actionContext.Controller + "/" + actionContext.Action;
			public string? Content(string? contentPath) => contentPath;
			public bool IsLocalUrl(string? url) => true;
			public string? Link(string? routeName, object? values) => "/";
			public string? RouteUrl(UrlRouteContext routeContext) => "/";
		}

		private sealed class InboxNoopNotifications : INotificationService
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

		private sealed class InboxStubLeaveDashboard : ILeaveDashboardService
		{
			public Task<PeopleDashboardDto> BuildAsync(int employeeId) => throw new NotImplementedException();
			public Task<int> RemainingForTypeAsync(int employeeId, int leaveTypeId) => Task.FromResult(30);
			public Task<int> RemainingForTypeInYearAsync(int employeeId, int leaveTypeId, int year) => Task.FromResult(30);
			public Task<bool[]?> WorkDayFlagsAsync(int employeeId) => Task.FromResult<bool[]?>(null);
			public Task<int> WorkingDaysAsync(int employeeId, DateTime start, DateTime end)
				=> Task.FromResult(Math.Max(1, (end.Date - start.Date).Days + 1));
		}

		private sealed class StubTempDataProvider : ITempDataProvider
		{
			public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
			public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
		}
	}
}
