using CrossBuy.BL;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Crm;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// CRM company isolation, asserted behaviourally against a relational database.
	//
	// WHY THESE EXIST. Every CRM path in CrmController read `private const int DefaultCompanyId = 1`
	// — ninety-eight call sites — in an installation that has fourteen companies. That single constant
	// produced two DIFFERENT failures depending on which table the path touched, and the difference is
	// the reason these tests are split the way they are:
	//
	//   * Lead, Opportunity and CrmAccount are pilot entities. CompanyQueryFilters gives them a global
	//     query filter and CompanyWriteGuardInterceptor guards their writes. A company-2 request asking
	//     for company 1 therefore read NOTHING and its writes were REFUSED — broken, but not leaky.
	//
	//   * The other seventeen CRM tables have no filter and no guard. For CrmContact, Activity,
	//     CrmTicket, OpportunityProduct, CrmListMember and their siblings the hand-written predicate is
	//     the ONLY isolation there is — and with the constant supplying the company, a company-2 user's
	//     activity was written into company 1 and stored there.
	//
	// So the pilot cases prove the kernel still refuses, and the non-pilot cases prove the predicate now
	// does the job the kernel is not doing for those tables. Testing only one family would leave the
	// other's regression invisible.
	//
	// Company 1 is the foreign company throughout and company 2 the caller, deliberately: company 1 is
	// where every real CRM row in the live database lives, so "company 2 reaching into company 1" is the
	// exact direction the defect ran.
	//
	// Ids are distinct across companies. Reusing an id would let a passing test mean "the ids happened
	// not to collide" instead of "the company predicate held".
	// =================================================================================================
	public class CrmCompanyIsolationTests
	{
		private const int Foreign = 1;    // company 1 — owns every seeded row below
		private const int Caller = 2;     // company 2 — the tenant making the request

		private const int Emp1 = 11;      // employee of company 1
		private const int Emp2 = 22;      // employee of company 2

		// ---------------------------------------------------------------------------------------------
		// Arrangement
		// ---------------------------------------------------------------------------------------------

		private static Employee Person(int id, int companyId, string nameEn) => new()
		{
			ID = id,
			EmpCompanyID = companyId,
			FullName = "م" + id,
			FullNameEn = nameEn,
			FirstName = nameEn,
			LastName = "T",
			Address = "-",
			PhoneNumber = "-",
			Email = "e" + id + "@example.invalid",
			ProfileImage = "-",
			Gender = "-",
			MaritalStatus = "-",
			UserId = "u" + id,
		};

		// The service reaches ICrmCustomerLink / INotificationService / ISellingService only on the
		// conversion and won paths, which none of these tests take. They are left null on purpose: a stub
		// would imply this code may call them, and a NullReferenceException would be a louder failure than
		// a stub quietly swallowing a call that should never happen.
		private static ICrmService Service(CrossDbContext db, ICrmAccessService access)
			=> new CrmService(db, null!, null!, access, null!);

		// Owner visibility is a SEPARATE axis from company isolation, and these tests are about the
		// company one. Unrestricted (null) is the widest owner scope there is — so if a row still does not
		// come back, the only thing that can have excluded it is the company predicate.
		private sealed class WideOpenAccess : ICrmAccessService
		{
			private readonly int? _employeeId;
			public WideOpenAccess(int? employeeId) { _employeeId = employeeId; }
			public int? CurrentEmployeeId() => _employeeId;
			public Task<List<string>> MyRolesAsync() => Task.FromResult(new List<string>());
			public Task<bool> CanAsync(string action) => Task.FromResult(true);
			public Task<HashSet<int>?> VisibleOwnerIdsAsync() => Task.FromResult<HashSet<int>?>(null);
			public Task<HashSet<int>> TeamOwnerIdsAsync(int managerEmployeeId) => Task.FromResult(new HashSet<int>());
			public Task<string> RoleLabelAsync(bool isAr) => Task.FromResult("test");
		}

		private static void SeedPeople(PlatformTestHost host)
		{
			host.Seed.Employee.AddRange(
				Person(Emp1, Foreign, "Foreign Person"),
				Person(Emp2, Caller, "Calling Person"));
			host.Seed.SaveChanges();
		}

		private static Lead SeedLead(PlatformTestHost host, int companyId, int ownerId, string name)
		{
			var row = new Lead
			{
				CompanyID = companyId, Name = name, Status = "New",
				OwnerEmployeeId = ownerId, EstimatedValue = 1000m,
				CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
			};
			host.Seed.Leads.Add(row);
			host.Seed.SaveChanges();
			return row;
		}

		private static CrmAccount SeedAccount(PlatformTestHost host, int companyId, int ownerId, string name)
		{
			var row = new CrmAccount
			{
				CompanyID = companyId, Name = name, OwnerEmployeeId = ownerId, IsActive = true,
				CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
			};
			host.Seed.CrmAccounts.Add(row);
			host.Seed.SaveChanges();
			return row;
		}

		private static Opportunity SeedOpportunity(PlatformTestHost host, int companyId, int ownerId, int? accountId, string title)
		{
			var row = new Opportunity
			{
				CompanyID = companyId, Title = title, AccountId = accountId, OwnerEmployeeId = ownerId,
				Stage = "Prospecting", Amount = 5000m, Probability = 20,
				CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
			};
			host.Seed.Opportunities.Add(row);
			host.Seed.SaveChanges();
			return row;
		}

		private static CrmContact SeedContact(PlatformTestHost host, int companyId, int accountId, string name)
		{
			var row = new CrmContact
			{
				CompanyID = companyId, AccountId = accountId, Name = name, IsPrimary = true,
				CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
			};
			host.Seed.CrmContacts.Add(row);
			host.Seed.SaveChanges();
			return row;
		}

		// =============================================================================================
		// PILOT ENTITIES — Lead, Opportunity, CrmAccount.
		// The kernel filter and the write guard are live here; these pin that they stay live.
		// =============================================================================================

		[Fact]
		public async Task Company_2_cannot_read_company_1_lead()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var theirs = SeedLead(host, Foreign, Emp1, "Foreign lead");
			var mine = SeedLead(host, Caller, Emp2, "Own lead");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (rows, total) = await svc.SearchLeadsAsync(Caller, null, null, 1, 50);

			Assert.Equal(1, total);
			Assert.Equal(new[] { mine.ID }, rows.Select(r => r.ID).ToArray());
			Assert.DoesNotContain(theirs.ID, rows.Select(r => r.ID));
		}

		[Fact]
		public async Task A_company_1_lead_id_is_answered_exactly_like_a_lead_that_does_not_exist()
		{
			// The point of the assertion is the EQUALITY of the two answers, not that either is null.
			// A caller who can tell "that row is someone else's" from "there is no such row" has been
			// handed an existence oracle for another tenant's database.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var theirs = SeedLead(host, Foreign, Emp1, "Foreign lead");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var foreignRow = await svc.GetLeadAsync(Caller, theirs.ID);
			var absentRow = await svc.GetLeadAsync(Caller, 999_999);

			Assert.Null(foreignRow);
			Assert.Null(absentRow);
		}

		[Fact]
		public async Task Company_2_cannot_update_company_1_lead()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var theirs = SeedLead(host, Foreign, Emp1, "Foreign lead");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			// The save looks the row up as (CompanyID == caller AND ID == given) and refuses when that
			// finds nothing. Naming another company's lead id therefore fails in exactly the way naming a
			// lead that was never created fails — same exception, same message — so the refusal cannot be
			// read as confirmation that the row exists somewhere.
			var foreignAttempt = await Assert.ThrowsAsync<InvalidOperationException>(() =>
				svc.SaveLeadAsync(Caller, new Lead { ID = theirs.ID, Name = "Hijacked", Status = "Qualified" }, "u22"));
			var absentAttempt = await Assert.ThrowsAsync<InvalidOperationException>(() =>
				svc.SaveLeadAsync(Caller, new Lead { ID = 999_999, Name = "Hijacked", Status = "Qualified" }, "u22"));

			Assert.Equal(absentAttempt.Message, foreignAttempt.Message);

			// And nothing moved: not the row it aimed at, and no smuggled copy in the caller's company.
			var after = await host.AllCompanies().Leads.AsNoTracking().FirstAsync(l => l.ID == theirs.ID);
			Assert.Equal("Foreign lead", after.Name);
			Assert.Equal("New", after.Status);
			Assert.Equal(Foreign, after.CompanyID);
			Assert.Empty(await host.AllCompanies().Leads.AsNoTracking().Where(l => l.CompanyID == Caller).ToListAsync());
		}

		[Fact]
		public async Task Company_2_cannot_read_company_1_opportunity()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var acc = SeedAccount(host, Foreign, Emp1, "Foreign account");
			var theirs = SeedOpportunity(host, Foreign, Emp1, acc.ID, "Foreign deal");
			var mine = SeedOpportunity(host, Caller, Emp2, null, "Own deal");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (rows, total) = await svc.SearchOpportunitiesAsync(Caller, null, null, 1, 50);

			Assert.Equal(1, total);
			Assert.Equal(new[] { mine.ID }, rows.Select(r => r.Id).ToArray());
			Assert.Null(await svc.GetOpportunityAsync(Caller, theirs.ID));
		}

		[Fact]
		public async Task Company_2_cannot_move_company_1_opportunity_to_another_stage()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var theirs = SeedOpportunity(host, Foreign, Emp1, null, "Foreign deal");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			await svc.UpdateOpportunityStageAsync(Caller, theirs.ID, "Won");

			var after = await host.AllCompanies().Opportunities.AsNoTracking().FirstAsync(o => o.ID == theirs.ID);
			Assert.Equal("Prospecting", after.Stage);
			Assert.Equal(Foreign, after.CompanyID);
		}

		[Fact]
		public async Task Company_2_cannot_read_company_1_account()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var theirs = SeedAccount(host, Foreign, Emp1, "Foreign account");
			var mine = SeedAccount(host, Caller, Emp2, "Own account");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (rows, total) = await svc.SearchAccountsAsync(Caller, null, 1, 50);

			Assert.Equal(1, total);
			Assert.Equal(new[] { mine.ID }, rows.Select(r => r.Id).ToArray());
			Assert.Null(await svc.GetAccountAsync(Caller, theirs.ID));
		}

		[Fact]
		public async Task Company_2_cannot_rename_company_1_account()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var theirs = SeedAccount(host, Foreign, Emp1, "Foreign account");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var foreignAttempt = await Assert.ThrowsAsync<InvalidOperationException>(() =>
				svc.SaveAccountAsync(Caller, new CrmAccount { ID = theirs.ID, Name = "Hijacked" }, "u22"));
			var absentAttempt = await Assert.ThrowsAsync<InvalidOperationException>(() =>
				svc.SaveAccountAsync(Caller, new CrmAccount { ID = 999_999, Name = "Hijacked" }, "u22"));

			Assert.Equal(absentAttempt.Message, foreignAttempt.Message);

			var after = await host.AllCompanies().CrmAccounts.AsNoTracking().FirstAsync(a => a.ID == theirs.ID);
			Assert.Equal("Foreign account", after.Name);
			Assert.Equal(Foreign, after.CompanyID);
			Assert.Empty(await host.AllCompanies().CrmAccounts.AsNoTracking().Where(a => a.CompanyID == Caller).ToListAsync());
		}

		// =============================================================================================
		// NON-PILOT ENTITIES — no global filter, no write guard.
		// Here the hand-written predicate is the whole of the isolation.
		// =============================================================================================

		[Fact]
		public async Task An_activity_created_by_company_2_against_a_company_1_entity_never_lands_in_company_1()
		{
			// Activity is NOT a pilot entity, so nothing in the kernel would have stopped this row being
			// stored under company 1 when the company came from the constant. The row must be the
			// CALLER's, whatever entity id the caller names.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var foreignLead = SeedLead(host, Foreign, Emp1, "Foreign lead");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			await svc.SaveActivityAsync(Caller, new Activity
			{
				Type = "Call",
				Subject = "Reaching across",
				EntityType = "Lead",
				EntityId = foreignLead.ID,
				LeadId = foreignLead.ID,
			}, "u22");

			var all = await host.AllCompanies().Activities.AsNoTracking().ToListAsync();
			Assert.All(all, a => Assert.Equal(Caller, a.CompanyID));
			Assert.Empty(all.Where(a => a.CompanyID == Foreign));
		}

		[Fact]
		public async Task Company_2_cannot_read_the_activity_timeline_of_a_company_1_entity()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var foreignLead = SeedLead(host, Foreign, Emp1, "Foreign lead");
			host.Seed.Activities.Add(new Activity
			{
				CompanyID = Foreign, Type = "Call", Subject = "Private call",
				EntityType = "Lead", EntityId = foreignLead.ID, OwnerEmployeeId = Emp1,
			});
			host.Seed.SaveChanges();

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var timeline = await svc.GetTimelineAsync(Caller, "Lead", foreignLead.ID);

			Assert.Empty(timeline);
		}

		[Fact]
		public async Task Company_2_cannot_read_company_1_contacts_through_an_account()
		{
			// CrmContact has no global filter. Before the repair this read was keyed on AccountId alone,
			// so naming another company's account id returned that company's people.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var foreignAcc = SeedAccount(host, Foreign, Emp1, "Foreign account");
			SeedContact(host, Foreign, foreignAcc.ID, "Their primary contact");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var account = await svc.GetAccountAsync(Caller, foreignAcc.ID);

			Assert.Null(account);

			var leak = await req.Db.CrmContacts.AsNoTracking()
				.Where(c => c.CompanyID == Caller && c.AccountId == foreignAcc.ID).ToListAsync();
			Assert.Empty(leak);
		}

		[Fact]
		public async Task Company_2_cannot_write_opportunity_products_onto_a_company_1_opportunity()
		{
			// OpportunityProduct has no global filter either, and the clear-then-insert used to key only
			// on OpportunityId — so this call could have deleted another company's quotation lines.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var theirs = SeedOpportunity(host, Foreign, Emp1, null, "Foreign deal");
			host.Seed.OpportunityProducts.Add(new OpportunityProduct
			{
				CompanyID = Foreign, OpportunityId = theirs.ID, ItemDescription = "Their line",
				Qty = 2, UnitPrice = 100m, LineTotal = 200m,
			});
			host.Seed.SaveChanges();

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			await svc.SaveOpportunityProductsAsync(Caller, theirs.ID, new List<OpportunityProduct>
			{
				new() { ItemDescription = "Injected", Qty = 1, UnitPrice = 1m },
			});

			var survivors = await host.AllCompanies().OpportunityProducts.AsNoTracking()
				.Where(p => p.CompanyID == Foreign).ToListAsync();
			Assert.Single(survivors);
			Assert.Equal("Their line", survivors[0].ItemDescription);
		}

		[Fact]
		public async Task Company_2_cannot_read_company_1_tickets()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			host.Seed.CrmTickets.Add(new CrmTicket
			{
				CompanyID = Foreign, Subject = "Their incident", Priority = "High", Status = "Open",
				OwnerEmployeeId = Emp1, CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
			});
			host.Seed.SaveChanges();

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (rows, total) = await svc.SearchTicketsAsync(Caller, null, null, null, 1, 50);

			Assert.Equal(0, total);
			Assert.Empty(rows);
		}

		// =============================================================================================
		// DROPDOWN / REFERENCE READS — the quietest leak of the set.
		// A picker returns names, not rows, so nothing about it looks like a data breach in a screenshot.
		// =============================================================================================

		[Fact]
		public async Task The_account_picker_offers_no_company_1_account_to_a_company_2_caller()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			SeedAccount(host, Foreign, Emp1, "Foreign Industries");
			var mine = SeedAccount(host, Caller, Emp2, "Own Trading");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var picks = await svc.GetAccountsForPickAsync(Caller, null);

			Assert.Equal(new[] { mine.ID }, picks.Select(p => p.id).ToArray());
			Assert.DoesNotContain(picks, p => p.name.Contains("Foreign", StringComparison.Ordinal));
		}

		[Fact]
		public async Task The_entity_picker_offers_no_company_1_lead_or_contact_to_a_company_2_caller()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			SeedLead(host, Foreign, Emp1, "Foreign Lead Name");
			var foreignAcc = SeedAccount(host, Foreign, Emp1, "Foreign account");
			SeedContact(host, Foreign, foreignAcc.ID, "Foreign Contact Name");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var leadPicks = await svc.PickEntitiesAsync(Caller, "Lead", null);
			var contactPicks = await svc.PickEntitiesAsync(Caller, "Contact", null);

			Assert.Empty(leadPicks);
			Assert.Empty(contactPicks);
		}

		// =============================================================================================
		// OWNERSHIP — granting another company's employee authority over ours.
		// =============================================================================================

		[Fact]
		public async Task A_company_1_employee_cannot_be_recorded_as_a_company_2_crm_role_holder()
		{
			// The controller now proves the employee belongs to the resolved company before writing the
			// role row. This asserts the property that check exists to guarantee: every CrmUserRole row in
			// a company names an employee OF that company.
			using var host = new PlatformTestHost();
			SeedPeople(host);

			var req = host.Request(Caller);
			bool foreignEmployeeIsOurs = await req.Db.Employee.AsNoTracking()
				.AnyAsync(e => e.ID == Emp1 && e.EmpCompanyID == Caller);
			bool ownEmployeeIsOurs = await req.Db.Employee.AsNoTracking()
				.AnyAsync(e => e.ID == Emp2 && e.EmpCompanyID == Caller);

			Assert.False(foreignEmployeeIsOurs);
			Assert.True(ownEmployeeIsOurs);
		}

		[Fact]
		public async Task Lead_routing_never_hands_a_company_2_lead_to_a_company_1_rep()
		{
			// Auto-routing picks "the least-loaded SalesRep". Reading the role table without a company
			// predicate would make another company's reps eligible owners of our leads.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			host.Seed.CrmUserRoles.Add(new CrmUserRole { CompanyID = Foreign, EmployeeId = Emp1, Role = "SalesRep" });
			host.Seed.CrmSettings.Add(new CrmSettings { CompanyID = Caller, AutoRouteLeads = true, HotScore = 50, WarmScore = 20 });
			host.Seed.SaveChanges();

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			await svc.SaveLeadAsync(Caller, new Lead { Name = "Fresh lead", Status = "New" }, "u22");

			var created = await host.AllCompanies().Leads.AsNoTracking().SingleAsync();
			Assert.Equal(Caller, created.CompanyID);
			Assert.NotEqual(Emp1, created.OwnerEmployeeId);
		}

		// =============================================================================================
		// THE CONSTANT ITSELF — a source assertion, because a reintroduced constant would make every
		// behavioural test above pass while the company was wrong again for some new path.
		// =============================================================================================

		[Fact]
		public void CrmController_declares_no_hardcoded_company()
		{
			var code = SourceText("CrossBuy", "Controllers", "CrmController.cs");

			Assert.DoesNotContain("private const int DefaultCompanyId", code, StringComparison.Ordinal);
			Assert.DoesNotContain("CompanyId = 1;", code, StringComparison.Ordinal);
			Assert.Contains("ResolveCompanyAsync()", code, StringComparison.Ordinal);
		}

		[Fact]
		public void Every_CrmController_action_that_needs_a_company_resolves_one_and_returns_when_it_cannot()
		{
			// The guard is two statements and both matter: resolving without returning on failure would
			// carry companyId 0 into the queries, which is a different tenant's worth of nothing rather
			// than a refusal.
			var code = SourceText("CrossBuy", "Controllers", "CrmController.cs");

			int resolves = Occurrences(code, "var (okCo, cid, denyCo) = await ResolveCompanyAsync();");
			int returns = Occurrences(code, "if (!okCo) return denyCo;");

			Assert.Equal(resolves, returns);
			Assert.True(resolves >= 69, $"expected at least 69 guarded actions, found {resolves}");
		}

		[Fact]
		public void The_company_resolver_never_accepts_a_caller_supplied_company()
		{
			var code = SourceText("CrossBuy", "Controllers", "CrmController.cs");
			int start = code.IndexOf("private async Task<(bool ok, int cid, IActionResult deny)> ResolveCompanyAsync()", StringComparison.Ordinal);
			Assert.True(start >= 0, "resolver helper not found");
			string helper = code.Substring(start, Math.Min(600, code.Length - start));

			Assert.Contains("_company.ResolveAsync()", helper, StringComparison.Ordinal);
			Assert.Contains("return (false, 0, Forbid());", helper, StringComparison.Ordinal);
			// ResolveAsync's only parameter is the compatibility "company the request supplied". Passing
			// anything there would reopen the door the constant used to hold open.
			Assert.DoesNotContain("ResolveAsync(companyId", helper, StringComparison.Ordinal);
			Assert.DoesNotContain("ResolveAsync(cid", helper, StringComparison.Ordinal);
		}

		private static int Occurrences(string haystack, string needle)
		{
			int n = 0, i = 0;
			while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
			return n;
		}

		private static string SourceText(params string[] relativeParts)
		{
			var dir = new DirectoryInfo(AppContext.BaseDirectory);
			while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
			Assert.True(dir != null, "could not locate the repository root from " + AppContext.BaseDirectory);
			var path = Path.Combine(new[] { dir!.FullName }.Concat(relativeParts).ToArray());
			Assert.True(File.Exists(path), "source file not found: " + path);
			return File.ReadAllText(path);
		}
	}
}
