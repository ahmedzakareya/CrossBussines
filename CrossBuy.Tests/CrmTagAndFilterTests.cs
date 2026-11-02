using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Crm;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// CRM TAGS + THE TWO OPERATIONAL FILTERS.
	//
	// WHAT MAKES THIS SUITE DIFFERENT FROM A CRUD SUITE. Three of the things it proves are absences,
	// and an absence is what no screenshot and no manual pass ever checks:
	//
	//   * a tag must never WIDEN what a caller can see. The tag filter runs after the company and
	//     owner predicates, so a colleague's lead cannot be found by tagging it — and the test for
	//     that arranges the record to be found if the order were wrong.
	//   * a company's vocabulary must not bridge tenants. Company 41 and company 77 may both have
	//     "Priority" and they are different tags; a query in 41 must see only 41's record.
	//   * a filtered page must not be counted against an unfiltered total. That is a defect this
	//     codebase has already shipped once, so every filter here asserts the total as well as the rows.
	//
	// Companies 41 and 77 throughout, and every assertion reads back through an UNFILTERED context so a
	// pass cannot be the global query filter agreeing with the code under test.
	// =================================================================================================
	public class CrmTagAndFilterTests
	{
		private const int Co41 = 41;
		private const int Co77 = 77;
		private const int Emp41 = 4101;
		private const int Emp77 = 7701;
		private const int Colleague41 = 4102;   // same company, another owner

		private static readonly DateTime Seeded = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

		/// The instant every overdue test is evaluated at. Fixed, because "is this late?" must not
		/// depend on when the suite happens to run.
		private static readonly DateTime Now = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

		private static TimeProvider ClockAt(DateTime utc) => new FixedClock(utc);

		private sealed class FixedClock : TimeProvider
		{
			private readonly DateTimeOffset _now;
			public FixedClock(DateTime utc) { _now = new DateTimeOffset(utc, TimeSpan.Zero); }
			public override DateTimeOffset GetUtcNow() => _now;
		}

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

		/// Unrestricted owner scope — the widest there is, so anything still refused was refused by the
		/// rule under test rather than by owner visibility.
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

		/// A sales rep: sees only their own records, exactly as CrmAccessService reports for that role.
		private sealed class RepAccess : ICrmAccessService
		{
			private readonly int _me;
			public RepAccess(int me) { _me = me; }
			public int? CurrentEmployeeId() => _me;
			public Task<List<string>> MyRolesAsync() => Task.FromResult(new List<string> { "SalesRep" });
			public Task<bool> CanAsync(string action) => Task.FromResult(true);
			public Task<HashSet<int>?> VisibleOwnerIdsAsync() => Task.FromResult<HashSet<int>?>(new HashSet<int> { _me });
			public Task<HashSet<int>> TeamOwnerIdsAsync(int managerEmployeeId) => Task.FromResult(new HashSet<int> { _me });
			public Task<string> RoleLabelAsync(bool isAr) => Task.FromResult("rep");
		}

		private sealed class StubLink : ICrmCustomerLink
		{
			public Task<int> CreateCustomerAsync(int companyId, string name, string? nameEn, string? taxNo, decimal? creditLimit)
				=> Task.FromResult(9001);
			public Task EnrichAsync(int customerId, string? phone, string? email, string? segment, string? contactPerson)
				=> Task.CompletedTask;
		}

		private sealed class SilentNotifications : INotificationService
		{
			public Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn, string? bodyAr, string? bodyEn,
				string type, int? refId = null, string? url = null, int? companyId = null, int? actorEmployeeId = null,
				string? priority = null, string? category = null, string? dedupKey = null, DateTime? expiresAt = null,
				string? icon = null, string? entityType = null, int? entityId = null) => Task.CompletedTask;
			public Task<int> NotifyRoleAsync(int companyId, string scope, string[] roles, string? titleAr, string? titleEn,
				string? bodyAr, string? bodyEn, string type, int? refId = null, int? exceptEmployeeId = null)
				=> Task.FromResult(0);
		}

		private static ICrmService Service(PlatformTestHost host, CrossDbContext db, int companyId,
			ICrmAccessService? access = null, int? employeeId = null, TimeProvider? clock = null)
			=> new CrmService(db, new StubLink(), new SilentNotifications(),
				access ?? new WideOpenAccess(employeeId ?? Emp41), null!,
				host.Events(db, PlatformTestHost.DefaultContext(companyId, employeeId ?? Emp41)),
				clock ?? ClockAt(Now));

		private static ITagService Tags(CrossDbContext db) => new TagService(db);

		private static void SeedPeople(PlatformTestHost host)
		{
			host.Seed.Employee.AddRange(
				Person(Emp41, Co41, "Company 41 person"),
				Person(Colleague41, Co41, "Company 41 colleague"),
				Person(Emp77, Co77, "Company 77 person"));
			host.Seed.SaveChanges();
		}

		/// A tag definition, written the way TagService writes one so the normalized key is right.
		private static Tag SeedTag(PlatformTestHost host, int companyId, string name)
		{
			var row = new Tag
			{
				CompanyID = companyId,
				Name = name,
				NormalizedName = TagService.Normalize(name),
				IsActive = true,
				CreatedAt = Seeded,
			};
			host.Seed.Tags.Add(row); host.Seed.SaveChanges(); return row;
		}

		private static Lead SeedLead(PlatformTestHost host, int companyId, int? ownerId, string name)
		{
			var row = new Lead { CompanyID = companyId, Name = name, Status = "New", OwnerEmployeeId = ownerId, EstimatedValue = 1000m, CreatedAt = Seeded };
			host.Seed.Leads.Add(row); host.Seed.SaveChanges(); return row;
		}

		private static Opportunity SeedOpportunity(PlatformTestHost host, int companyId, int? ownerId, string title)
		{
			var row = new Opportunity { CompanyID = companyId, Title = title, OwnerEmployeeId = ownerId, Stage = "Prospecting", Amount = 5000m, Probability = 20, CreatedAt = Seeded };
			host.Seed.Opportunities.Add(row); host.Seed.SaveChanges(); return row;
		}

		private static CrmAccount SeedAccount(PlatformTestHost host, int companyId, int? ownerId, string name)
		{
			var row = new CrmAccount { CompanyID = companyId, Name = name, OwnerEmployeeId = ownerId, IsActive = true, CreatedAt = Seeded };
			host.Seed.CrmAccounts.Add(row); host.Seed.SaveChanges(); return row;
		}

		/// A CRM follow-up: the one activity shape that is assigned work with a deadline.
		private static Activity SeedFollowUp(PlatformTestHost host, int companyId, int? leadId, int? oppId,
			DateTime? due, bool done = false, string type = "Task", int? owner = Emp41)
		{
			var row = new Activity
			{
				CompanyID = companyId, Type = type, Subject = "follow up", LeadId = leadId, OpportunityId = oppId,
				DueDate = due, Done = done, OwnerEmployeeId = owner, CreatedAt = Seeded,
			};
			host.Seed.Activities.Add(row); host.Seed.SaveChanges(); return row;
		}

		private static async Task AssignAsync(CrossDbContext db, int companyId, string entityCode, int entityId, params int[] tagIds)
		{
			var (ok, err) = await Tags(db).SetEntityTagsAsync(companyId, entityCode, entityId, tagIds, Emp41);
			Assert.True(ok, err);
		}

		private static async Task<List<int>> TagIdsOnAsync(PlatformTestHost host, int companyId, string entityCode, int entityId)
			=> (await Tags(host.AllCompanies()).GetEntityTagsAsync(companyId, entityCode, entityId)).Select(t => t.ID).OrderBy(i => i).ToList();

		private static async Task<List<TagAssignment>> AllAssignmentsAsync(PlatformTestHost host)
			=> await host.AllCompanies().TagAssignments.AsNoTracking().ToListAsync();

		// =============================================================================================
		// A. TAGS ON A SAVE — one transaction, replacement semantics, clear-all
		// =============================================================================================

		[Fact]
		public async Task Saving_a_lead_with_tags_stores_them_through_the_platform_authority()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var red = SeedTag(host, Co41, "Red");
			var blue = SeedTag(host, Co41, "Blue");

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (ok, err) = await svc.SaveLeadAsync(Co41, new Lead { Name = "Sara", Status = "New" }, "u41", new[] { red.ID, blue.ID });

			Assert.True(ok, err);
			var lead = await host.AllCompanies().Leads.AsNoTracking().SingleAsync();
			Assert.Equal(new[] { red.ID, blue.ID }.OrderBy(i => i), await TagIdsOnAsync(host, Co41, EntityRegistry.Lead, lead.ID));
			Assert.All(await AllAssignmentsAsync(host), a => Assert.Equal(Co41, a.CompanyID));
		}

		[Fact]
		public async Task Saving_an_opportunity_and_an_account_with_tags_does_the_same()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var red = SeedTag(host, Co41, "Red");

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var opp = await svc.SaveOpportunityAsync(Co41, new Opportunity { Title = "Deal", Stage = "Prospecting" }, "u41", new[] { red.ID });
			var acc = await svc.SaveAccountAsync(Co41, new CrmAccount { Name = "Acme", IsActive = true }, "u41", new[] { red.ID });

			Assert.True(opp.ok, opp.error);
			Assert.True(acc.ok, acc.error);
			var oppId = (await host.AllCompanies().Opportunities.AsNoTracking().SingleAsync()).ID;
			Assert.Equal(new[] { red.ID }, await TagIdsOnAsync(host, Co41, EntityRegistry.Opportunity, oppId));
			Assert.Equal(new[] { red.ID }, await TagIdsOnAsync(host, Co41, EntityRegistry.CrmAccount, acc.id));
		}

		[Fact]
		public async Task Editing_replaces_the_tag_set_rather_than_adding_to_it()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var red = SeedTag(host, Co41, "Red");
			var blue = SeedTag(host, Co41, "Blue");
			var lead = SeedLead(host, Co41, Emp41, "Sara");

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);
			await AssignAsync(req.Db, Co41, EntityRegistry.Lead, lead.ID, red.ID);

			await svc.SaveLeadAsync(Co41, new Lead { ID = lead.ID, Name = "Sara", Status = "New" }, "u41", new[] { blue.ID });

			Assert.Equal(new[] { blue.ID }, await TagIdsOnAsync(host, Co41, EntityRegistry.Lead, lead.ID));
		}

		[Fact]
		public async Task An_empty_set_clears_every_tag_and_null_leaves_them_alone()
		{
			// The distinction the sentinel exists for. Without it these two would be the same call, and
			// removing the last chip would silently keep the tags.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var red = SeedTag(host, Co41, "Red");
			var lead = SeedLead(host, Co41, Emp41, "Sara");

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);
			await AssignAsync(req.Db, Co41, EntityRegistry.Lead, lead.ID, red.ID);

			await svc.SaveLeadAsync(Co41, new Lead { ID = lead.ID, Name = "Sara", Status = "New" }, "u41", tagIds: null);
			Assert.Equal(new[] { red.ID }, await TagIdsOnAsync(host, Co41, EntityRegistry.Lead, lead.ID));

			await svc.SaveLeadAsync(Co41, new Lead { ID = lead.ID, Name = "Sara", Status = "New" }, "u41", Array.Empty<int>());
			Assert.Empty(await TagIdsOnAsync(host, Co41, EntityRegistry.Lead, lead.ID));
		}

		[Fact]
		public async Task A_duplicated_tag_id_produces_one_assignment_not_two()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var red = SeedTag(host, Co41, "Red");
			var blue = SeedTag(host, Co41, "Blue");

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			await svc.SaveLeadAsync(Co41, new Lead { Name = "Sara", Status = "New" }, "u41",
				new[] { red.ID, red.ID, blue.ID, blue.ID });

			Assert.Equal(2, (await AllAssignmentsAsync(host)).Count);
		}

		[Fact]
		public async Task A_foreign_tag_takes_the_whole_save_down_with_it()
		{
			// THE ATOMICITY PROOF. The tag replacement runs inside the save's own transaction, so a tag
			// that cannot be used does not leave a lead behind that the user believes is classified.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var theirs = SeedTag(host, Co77, "Priority");

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (ok, err) = await svc.SaveLeadAsync(Co41, new Lead { Name = "Sara", Status = "New" }, "u41", new[] { theirs.ID });

			Assert.False(ok);
			Assert.NotNull(err);
			Assert.Empty(await host.AllCompanies().Leads.AsNoTracking().Where(l => l.CompanyID == Co41).ToListAsync());
			Assert.Empty(await AllAssignmentsAsync(host));
			// and the event the save would have written went with it
			Assert.Empty(await host.AllCompanies().BusinessEvents.AsNoTracking().ToListAsync());
		}

		[Fact]
		public async Task An_edit_refused_for_a_foreign_tag_leaves_the_record_exactly_as_it_was()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var theirs = SeedTag(host, Co77, "Priority");
			var mine = SeedTag(host, Co41, "Red");
			var lead = SeedLead(host, Co41, Emp41, "Sara");

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);
			await AssignAsync(req.Db, Co41, EntityRegistry.Lead, lead.ID, mine.ID);

			var (ok, _) = await svc.SaveLeadAsync(Co41, new Lead { ID = lead.ID, Name = "Renamed", Status = "Qualified" }, "u41",
				new[] { theirs.ID });

			Assert.False(ok);
			var after = await host.AllCompanies().Leads.AsNoTracking().FirstAsync(l => l.ID == lead.ID);
			Assert.Equal("Sara", after.Name);
			Assert.Equal("New", after.Status);
			Assert.Equal(new[] { mine.ID }, await TagIdsOnAsync(host, Co41, EntityRegistry.Lead, lead.ID));
		}

		[Fact]
		public async Task A_company_cannot_tag_another_companys_record()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var mine = SeedTag(host, Co41, "Red");
			var theirLead = SeedLead(host, Co77, Emp77, "Their lead");

			var req = host.Request(Co41);
			var (ok, err) = await Tags(req.Db).SetEntityTagsAsync(Co41, EntityRegistry.Lead, theirLead.ID, new[] { mine.ID }, Emp41);

			Assert.False(ok);
			Assert.NotNull(err);
			Assert.Empty(await AllAssignmentsAsync(host));
		}

		// =============================================================================================
		// B. TAG FILTERING — ANY, ALL, and the count that must agree with the page
		// =============================================================================================

		[Fact]
		public async Task ANY_returns_every_record_carrying_at_least_one_chosen_tag()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var red = SeedTag(host, Co41, "Red");
			var blue = SeedTag(host, Co41, "Blue");
			var a = SeedLead(host, Co41, Emp41, "A");
			var b = SeedLead(host, Co41, Emp41, "B");
			var c = SeedLead(host, Co41, Emp41, "C");
			SeedLead(host, Co41, Emp41, "D");   // untagged

			var req = host.Request(Co41);
			await AssignAsync(req.Db, Co41, EntityRegistry.Lead, a.ID, red.ID);
			await AssignAsync(req.Db, Co41, EntityRegistry.Lead, b.ID, blue.ID);
			await AssignAsync(req.Db, Co41, EntityRegistry.Lead, c.ID, red.ID, blue.ID);

			var svc = Service(host, req.Db, Co41);
			var (rows, total) = await svc.SearchLeadsAsync(Co41, null, null, 1, 50, new[] { red.ID, blue.ID }, tagMatchAll: false);

			Assert.Equal(new[] { a.ID, b.ID, c.ID }.OrderBy(i => i), rows.Select(r => r.ID).OrderBy(i => i));
			Assert.Equal(3, total);
		}

		[Fact]
		public async Task ALL_returns_only_the_record_carrying_every_chosen_tag()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var red = SeedTag(host, Co41, "Red");
			var blue = SeedTag(host, Co41, "Blue");
			var a = SeedLead(host, Co41, Emp41, "A");
			var b = SeedLead(host, Co41, Emp41, "B");
			var c = SeedLead(host, Co41, Emp41, "C");

			var req = host.Request(Co41);
			await AssignAsync(req.Db, Co41, EntityRegistry.Lead, a.ID, red.ID);
			await AssignAsync(req.Db, Co41, EntityRegistry.Lead, b.ID, blue.ID);
			await AssignAsync(req.Db, Co41, EntityRegistry.Lead, c.ID, red.ID, blue.ID);

			var svc = Service(host, req.Db, Co41);
			var (rows, total) = await svc.SearchLeadsAsync(Co41, null, null, 1, 50, new[] { red.ID, blue.ID }, tagMatchAll: true);

			Assert.Equal(new[] { c.ID }, rows.Select(r => r.ID).ToArray());
			Assert.Equal(1, total);
		}

		[Fact]
		public async Task Choosing_no_tags_is_no_restriction_rather_than_an_empty_result()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			SeedLead(host, Co41, Emp41, "A");
			SeedLead(host, Co41, Emp41, "B");

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (rows, total) = await svc.SearchLeadsAsync(Co41, null, null, 1, 50, Array.Empty<int>(), tagMatchAll: false);

			Assert.Equal(2, rows.Count);
			Assert.Equal(2, total);
		}

		[Fact]
		public async Task The_total_counts_the_filtered_set_and_paging_walks_it_without_overlap()
		{
			// The paging defect this codebase has already had: a filtered page against an unfiltered
			// total shows seven rows and calls them page 1 of 3.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var red = SeedTag(host, Co41, "Red");
			var req = host.Request(Co41);

			var tagged = new List<int>();
			for (int i = 0; i < 7; i++)
			{
				var lead = SeedLead(host, Co41, Emp41, "tagged " + i);
				await AssignAsync(req.Db, Co41, EntityRegistry.Lead, lead.ID, red.ID);
				tagged.Add(lead.ID);
			}
			for (int i = 0; i < 5; i++) SeedLead(host, Co41, Emp41, "plain " + i);

			var svc = Service(host, req.Db, Co41);
			var page1 = await svc.SearchLeadsAsync(Co41, null, null, 1, 5, new[] { red.ID });
			var page2 = await svc.SearchLeadsAsync(Co41, null, null, 2, 5, new[] { red.ID });

			Assert.Equal(7, page1.total);
			Assert.Equal(7, page2.total);
			Assert.Equal(5, page1.rows.Count);
			Assert.Equal(2, page2.rows.Count);
			Assert.Empty(page1.rows.Select(r => r.ID).Intersect(page2.rows.Select(r => r.ID)));
			Assert.Equal(tagged.OrderBy(i => i), page1.rows.Concat(page2.rows).Select(r => r.ID).OrderBy(i => i));
		}

		[Fact]
		public async Task Opportunities_and_accounts_filter_by_tag_too()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var red = SeedTag(host, Co41, "Red");
			var opp = SeedOpportunity(host, Co41, Emp41, "Tagged deal");
			SeedOpportunity(host, Co41, Emp41, "Plain deal");
			var acc = SeedAccount(host, Co41, Emp41, "Tagged account");
			SeedAccount(host, Co41, Emp41, "Plain account");

			var req = host.Request(Co41);
			await AssignAsync(req.Db, Co41, EntityRegistry.Opportunity, opp.ID, red.ID);
			await AssignAsync(req.Db, Co41, EntityRegistry.CrmAccount, acc.ID, red.ID);

			var svc = Service(host, req.Db, Co41);
			var opps = await svc.SearchOpportunitiesAsync(Co41, null, null, 1, 50, new[] { red.ID });
			var accounts = await svc.SearchAccountsAsync(Co41, null, 1, 50, new[] { red.ID });

			Assert.Equal(new[] { opp.ID }, opps.rows.Select(r => r.Id).ToArray());
			Assert.Equal(1, opps.total);
			Assert.Equal(new[] { acc.ID }, accounts.rows.Select(r => r.Id).ToArray());
			Assert.Equal(1, accounts.total);
		}

		// =============================================================================================
		// C. THE TWO ISOLATIONS — a tag may only ever narrow
		// =============================================================================================

		[Fact]
		public async Task A_tag_does_not_reveal_a_colleagues_lead()
		{
			// THE ONE THAT MATTERS MOST. Both leads carry the same tag; one is hidden by owner scope.
			// If the tag predicate ran before the scope predicate — or matched first and intersected
			// afterwards — the hidden lead would come back. It is arranged to be found if the order
			// were wrong.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var red = SeedTag(host, Co41, "Red");
			var mine = SeedLead(host, Co41, Emp41, "Mine");
			var colleagues = SeedLead(host, Co41, Colleague41, "Colleague's");

			var req = host.Request(Co41);
			await AssignAsync(req.Db, Co41, EntityRegistry.Lead, mine.ID, red.ID);
			await AssignAsync(req.Db, Co41, EntityRegistry.Lead, colleagues.ID, red.ID);

			var svc = Service(host, req.Db, Co41, access: new RepAccess(Emp41));
			var (rows, total) = await svc.SearchLeadsAsync(Co41, null, null, 1, 50, new[] { red.ID });

			Assert.Equal(new[] { mine.ID }, rows.Select(r => r.ID).ToArray());
			Assert.Equal(1, total);
		}

		[Fact]
		public async Task A_tag_of_the_same_name_in_another_company_reaches_nothing()
		{
			// Each tenant may have "Priority" and they are different tags. The normalized name must
			// never be a bridge between companies.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var mine = SeedTag(host, Co41, "Priority");
			var theirs = SeedTag(host, Co77, "Priority");
			var myLead = SeedLead(host, Co41, Emp41, "Mine");
			var theirLead = SeedLead(host, Co77, Emp77, "Theirs");

			var req41 = host.Request(Co41);
			await AssignAsync(req41.Db, Co41, EntityRegistry.Lead, myLead.ID, mine.ID);
			var req77 = host.Request(Co77);
			await AssignAsync(req77.Db, Co77, EntityRegistry.Lead, theirLead.ID, theirs.ID);

			var svc = Service(host, req41.Db, Co41);
			var (rows, total) = await svc.SearchLeadsAsync(Co41, null, null, 1, 50, new[] { mine.ID });

			Assert.Equal(new[] { myLead.ID }, rows.Select(r => r.ID).ToArray());
			Assert.Equal(1, total);

			// And the other company's tag id, used from here, matches nothing at all.
			var crossed = await svc.SearchLeadsAsync(Co41, null, null, 1, 50, new[] { theirs.ID });
			Assert.Empty(crossed.rows);
			Assert.Equal(0, crossed.total);
		}

		// =============================================================================================
		// D. UNASSIGNED
		// =============================================================================================

		[Fact]
		public async Task Unassigned_returns_only_records_with_no_owner()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var free = SeedLead(host, Co41, null, "Nobody's");
			SeedLead(host, Co41, Emp41, "Mine");
			SeedLead(host, Co77, null, "Another company's unowned");

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (rows, total) = await svc.SearchLeadsAsync(Co41, null, null, 1, 50, unassignedOnly: true);

			Assert.Equal(new[] { free.ID }, rows.Select(r => r.ID).ToArray());
			Assert.Equal(1, total);
		}

		[Fact]
		public async Task Unassigned_applies_to_opportunities_and_accounts_as_well()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var freeOpp = SeedOpportunity(host, Co41, null, "Unowned deal");
			SeedOpportunity(host, Co41, Emp41, "Owned deal");
			var freeAcc = SeedAccount(host, Co41, null, "Unowned account");
			SeedAccount(host, Co41, Emp41, "Owned account");

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var opps = await svc.SearchOpportunitiesAsync(Co41, null, null, 1, 50, unassignedOnly: true);
			var accounts = await svc.SearchAccountsAsync(Co41, null, 1, 50, unassignedOnly: true);

			Assert.Equal(new[] { freeOpp.ID }, opps.rows.Select(r => r.Id).ToArray());
			Assert.Equal(1, opps.total);
			Assert.Equal(new[] { freeAcc.ID }, accounts.rows.Select(r => r.Id).ToArray());
			Assert.Equal(1, accounts.total);
		}

		[Fact]
		public async Task Unassigned_does_not_widen_what_owner_scope_hides()
		{
			// An unowned record stays visible to everyone — that is the list's existing rule — but a
			// colleague's OWNED record must not appear merely because the filter is about ownership.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var free = SeedLead(host, Co41, null, "Nobody's");
			SeedLead(host, Co41, Colleague41, "Colleague's");

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41, access: new RepAccess(Emp41));

			var (rows, total) = await svc.SearchLeadsAsync(Co41, null, null, 1, 50, unassignedOnly: true);

			Assert.Equal(new[] { free.ID }, rows.Select(r => r.ID).ToArray());
			Assert.Equal(1, total);
		}

		// =============================================================================================
		// E. OVERDUE FOLLOW-UP — at a controlled instant
		// =============================================================================================

		[Fact]
		public async Task An_open_task_activity_past_its_due_date_makes_its_lead_overdue()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var late = SeedLead(host, Co41, Emp41, "Late");
			SeedLead(host, Co41, Emp41, "Fine");
			SeedFollowUp(host, Co41, leadId: late.ID, oppId: null, due: Now.AddDays(-1));

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (rows, total) = await svc.SearchLeadsAsync(Co41, null, null, 1, 50, overdueFollowUpOnly: true);

			Assert.Equal(new[] { late.ID }, rows.Select(r => r.ID).ToArray());
			Assert.Equal(1, total);
		}

		[Fact]
		public async Task A_follow_up_due_at_this_exact_instant_is_due_rather_than_late()
		{
			// The boundary, pinned. The comparison is STRICTLY less than, matching
			// TaskStatuses.IsOverdue and the CRM dashboard — not a choice made here.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var lead = SeedLead(host, Co41, Emp41, "On the line");
			SeedFollowUp(host, Co41, leadId: lead.ID, oppId: null, due: Now);

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (rows, total) = await svc.SearchLeadsAsync(Co41, null, null, 1, 50, overdueFollowUpOnly: true);

			Assert.Empty(rows);
			Assert.Equal(0, total);

			// One tick later the same row IS late, which is what proves the clock is being consulted.
			var laterSvc = Service(host, req.Db, Co41, clock: ClockAt(Now.AddTicks(1)));
			var later = await laterSvc.SearchLeadsAsync(Co41, null, null, 1, 50, overdueFollowUpOnly: true);
			Assert.Equal(new[] { lead.ID }, later.rows.Select(r => r.ID).ToArray());
		}

		[Theory]
		[InlineData("Call")]
		[InlineData("Meeting")]
		[InlineData("Email")]
		public async Task An_interaction_is_never_an_overdue_follow_up(string type)
		{
			// The Activity boundary, enforced by the filter as well as by the producer: a call that was
			// logged with a date is history, not work somebody owes.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var lead = SeedLead(host, Co41, Emp41, "Has an old call");
			SeedFollowUp(host, Co41, leadId: lead.ID, oppId: null, due: Now.AddDays(-5), type: type);

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (rows, total) = await svc.SearchLeadsAsync(Co41, null, null, 1, 50, overdueFollowUpOnly: true);

			Assert.Empty(rows);
			Assert.Equal(0, total);
		}

		[Fact]
		public async Task A_completed_or_undated_follow_up_is_not_overdue()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var done = SeedLead(host, Co41, Emp41, "Done");
			var undated = SeedLead(host, Co41, Emp41, "Undated");
			var future = SeedLead(host, Co41, Emp41, "Future");
			SeedFollowUp(host, Co41, done.ID, null, due: Now.AddDays(-3), done: true);
			SeedFollowUp(host, Co41, undated.ID, null, due: null);
			SeedFollowUp(host, Co41, future.ID, null, due: Now.AddDays(3));

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (rows, total) = await svc.SearchLeadsAsync(Co41, null, null, 1, 50, overdueFollowUpOnly: true);

			Assert.Empty(rows);
			Assert.Equal(0, total);
		}

		[Fact]
		public async Task A_follow_up_belonging_to_another_company_or_another_record_does_not_count()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var mine = SeedLead(host, Co41, Emp41, "Mine");
			var other = SeedLead(host, Co41, Emp41, "Other");
			// a late follow-up, but against a DIFFERENT lead
			SeedFollowUp(host, Co41, other.ID, null, due: Now.AddDays(-2));
			// a late follow-up in another company that names MY lead's id
			SeedFollowUp(host, Co77, mine.ID, null, due: Now.AddDays(-2));

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (rows, total) = await svc.SearchLeadsAsync(Co41, null, null, 1, 50, overdueFollowUpOnly: true);

			Assert.Equal(new[] { other.ID }, rows.Select(r => r.ID).ToArray());
			Assert.Equal(1, total);
		}

		[Fact]
		public async Task Opportunities_carry_the_overdue_filter_through_their_own_relation()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var late = SeedOpportunity(host, Co41, Emp41, "Late deal");
			SeedOpportunity(host, Co41, Emp41, "Fine deal");
			SeedFollowUp(host, Co41, leadId: null, oppId: late.ID, due: Now.AddDays(-1));

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (rows, total) = await svc.SearchOpportunitiesAsync(Co41, null, null, 1, 50, overdueFollowUpOnly: true);

			Assert.Equal(new[] { late.ID }, rows.Select(r => r.Id).ToArray());
			Assert.Equal(1, total);
		}

		[Fact]
		public async Task The_filters_compose_and_still_only_narrow()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var red = SeedTag(host, Co41, "Red");
			var both = SeedLead(host, Co41, null, "Unowned, tagged, late");
			var tagOnly = SeedLead(host, Co41, null, "Unowned, tagged, on time");
			var lateOnly = SeedLead(host, Co41, null, "Unowned, untagged, late");
			var ownedLateTagged = SeedLead(host, Co41, Emp41, "Owned, tagged, late");

			var req = host.Request(Co41);
			foreach (var id in new[] { both.ID, tagOnly.ID, ownedLateTagged.ID })
				await AssignAsync(req.Db, Co41, EntityRegistry.Lead, id, red.ID);
			SeedFollowUp(host, Co41, both.ID, null, due: Now.AddDays(-1));
			SeedFollowUp(host, Co41, lateOnly.ID, null, due: Now.AddDays(-1));
			SeedFollowUp(host, Co41, ownedLateTagged.ID, null, due: Now.AddDays(-1));

			var svc = Service(host, req.Db, Co41);
			var (rows, total) = await svc.SearchLeadsAsync(Co41, null, null, 1, 50,
				new[] { red.ID }, tagMatchAll: false, unassignedOnly: true, overdueFollowUpOnly: true);

			Assert.Equal(new[] { both.ID }, rows.Select(r => r.ID).ToArray());
			Assert.Equal(1, total);
		}

		// =============================================================================================
		// F. NON-REGRESSION — the events and the work contract are untouched by tags
		// =============================================================================================

		[Fact]
		public async Task Saving_a_lead_with_tags_still_writes_exactly_one_lead_event()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var red = SeedTag(host, Co41, "Red");

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			await svc.SaveLeadAsync(Co41, new Lead { Name = "Sara", Status = "New" }, "u41", new[] { red.ID });

			var ev = Assert.Single(await host.AllCompanies().BusinessEvents.AsNoTracking().ToListAsync());
			Assert.Equal(LeadEvents.Created, ev.EventType);
		}

		[Fact]
		public async Task A_stage_move_carrying_tags_still_announces_only_the_transition()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var red = SeedTag(host, Co41, "Red");
			var opp = SeedOpportunity(host, Co41, Emp41, "Deal");

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			await svc.SaveOpportunityAsync(Co41, new Opportunity { ID = opp.ID, Title = "Deal", Stage = "Negotiation" }, "u41", new[] { red.ID });

			var ev = Assert.Single(await host.AllCompanies().BusinessEvents.AsNoTracking().ToListAsync());
			Assert.Equal(OpportunityEvents.StageChanged, ev.EventType);
		}

		[Fact]
		public async Task Changing_only_the_tags_writes_no_tag_event_of_its_own()
		{
			// A save is a save: it announces the fact the CRM contract already had. Tags do not get an
			// event family of their own in this increment, and re-saving does not invent one.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var red = SeedTag(host, Co41, "Red");
			var lead = SeedLead(host, Co41, Emp41, "Sara");

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			await svc.SaveLeadAsync(Co41, new Lead { ID = lead.ID, Name = "Sara", Status = "New" }, "u41", new[] { red.ID });

			var events = await host.AllCompanies().BusinessEvents.AsNoTracking().ToListAsync();
			var ev = Assert.Single(events);
			Assert.Equal(LeadEvents.Updated, ev.EventType);        // the existing save contract
			Assert.DoesNotContain("Tag", ev.EventType, StringComparison.Ordinal);
		}

		[Fact]
		public async Task A_follow_up_activity_still_produces_exactly_one_follow_up_event()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var lead = SeedLead(host, Co41, Emp41, "Sara");

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			await svc.SaveActivityAsync(Co41, new Activity
			{
				Type = "Task", Subject = "Call Sara", LeadId = lead.ID,
				OwnerEmployeeId = Emp41, DueDate = Now.AddDays(2),
			}, "u41");

			var ev = Assert.Single(await host.AllCompanies().BusinessEvents.AsNoTracking().ToListAsync());
			Assert.Equal(LeadEvents.FollowUpScheduled, ev.EventType);
		}

		// =============================================================================================
		// G. THE SENTINEL, PINNED AT THE SOURCE
		// =============================================================================================

		[Fact]
		public void The_controller_reads_the_form_sentinel_and_turns_an_absent_list_into_an_empty_one()
		{
			// WHY THIS IS A SOURCE TEST AND NOT A BEHAVIOURAL ONE. Every test above calls the service,
			// where null and empty already mean different things - so none of them can see the ONE line
			// that decides which of the two a browser actually sent. And that line is where the
			// historical clear-all bug lives: an emptied multi-select posts no key at all, so a
			// controller that inferred "managed" from a NON-EMPTY id list would read "the user removed
			// the last chip" as "the user did not touch tags", and the tags would silently survive.
			//
			// The mutation campaign is what makes this guard worth having: replacing the sentinel check
			// with an emptiness check leaves every behavioural test green.
			var code = SourceText("CrossBuy", "Controllers", "CrmController.cs");
			var body = MethodBody(code, "private int[]? TagsFromForm(int[]? tagIds)");

			// The FORM says whether it owned the tag set - not the shape of what arrived.
			Assert.Contains("Request.Form.ContainsKey(\"TagsManaged\")", body, StringComparison.Ordinal);
			// A form that owned them and sent none means CLEAR, so null becomes an empty array.
			Assert.Contains("return tagIds ?? Array.Empty<int>();", body, StringComparison.Ordinal);
			// A form that did not own them means LEAVE ALONE, which is null all the way down.
			Assert.Contains("if (!managed) return null;", body, StringComparison.Ordinal);

			// And the three saves ask the form rather than passing the raw binding through.
			Assert.Equal(3, Occurrences(code, "TagsFromForm(tagIds)"));
		}

		[Fact]
		public void Every_CRM_form_that_offers_tags_also_sends_the_sentinel()
		{
			// The other half of the same contract: a picker without the sentinel can add tags but can
			// never clear them, and the failure is silent.
			foreach (var view in new[] { "Leads.cshtml", "Opportunities.cshtml", "AccountEditor.cshtml" })
			{
				var markup = SourceText("CrossBuy", "Views", "Crm", view);
				Assert.Contains("name=\"TagsManaged\"", markup, StringComparison.Ordinal);
				Assert.Contains("name=\"tagIds\"", markup, StringComparison.Ordinal);
			}
		}

		[Fact]
		public void The_edit_surfaces_load_the_tags_a_record_already_carries()
		{
			// An edit that opened with an EMPTY picker would silently clear every tag on the next save,
			// because the form owns the set. No server-side test can see that: the loss happens in the
			// browser before anything is posted. Both halves are asserted - the row handing its ids
			// over, and the picker selecting them.
			foreach (var view in new[] { "Leads.cshtml", "Opportunities.cshtml" })
			{
				var markup = SourceText("CrossBuy", "Views", "Crm", view);
				Assert.Contains("cbSetTags(r.getAttribute('data-tags'))", markup, StringComparison.Ordinal);
				Assert.Contains("cbSetTags('')", markup, StringComparison.Ordinal);   // and a new record starts clean
			}
			foreach (var partial in new[] { "_LeadRows.cshtml", "_OppRows.cshtml" })
				Assert.Contains("data-tags=", SourceText("CrossBuy", "Views", "Crm", partial), StringComparison.Ordinal);

			// The account editor is a page, so it selects server-side instead.
			var editor = SourceText("CrossBuy", "Views", "Crm", "AccountEditor.cshtml");
			Assert.Contains("myTagIds.Contains(tg.ID)", editor, StringComparison.Ordinal);
		}

		[Fact]
		public void The_row_partials_load_their_tags_in_ONE_batch_rather_than_per_row()
		{
			// N+1 is the defect a list partial invites: a tag read inside the foreach would be one query
			// per row, and it would look perfectly correct on a page of three.
			foreach (var partial in new[] { "_LeadRows.cshtml", "_OppRows.cshtml", "_AccountRows.cshtml" })
			{
				var markup = SourceText("CrossBuy", "Views", "Crm", partial);
				Assert.Contains("ViewBag.RowTags", markup, StringComparison.Ordinal);
				Assert.DoesNotContain("GetEntityTagsAsync", markup, StringComparison.Ordinal);
			}

			var code = SourceText("CrossBuy", "Controllers", "CrmController.cs");
			var loader = MethodBody(code, "private async Task LoadRowTagsAsync(");
			Assert.Contains("GetTagsForEntitiesAsync", loader, StringComparison.Ordinal);
			Assert.Equal(3, Occurrences(code, "await LoadRowTagsAsync("));   // one per list action
		}

		private static int Occurrences(string haystack, string needle)
		{
			int n = 0, i = 0;
			while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
			return n;
		}

		private static string MethodBody(string code, string signature)
		{
			int start = code.IndexOf(signature, StringComparison.Ordinal);
			Assert.True(start > 0, "method not found, or renamed without updating this test: " + signature);
			int next = code.IndexOf("\n\t\tprivate ", start + signature.Length, StringComparison.Ordinal);
			int alt = code.IndexOf("\n\t\t[Http", start + signature.Length, StringComparison.Ordinal);
			if (alt >= 0 && (next < 0 || alt < next)) next = alt;
			return next < 0 ? code[start..] : code[start..next];
		}

		private static string SourceText(params string[] relativeParts)
		{
			var dir = new DirectoryInfo(AppContext.BaseDirectory);
			while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
			Assert.True(dir != null, "could not locate the repository root from " + AppContext.BaseDirectory);
			var path = Path.Combine(new[] { dir!.FullName }.Concat(relativeParts).ToArray());
			Assert.True(File.Exists(path), "source file not found: " + path);
			var text = File.ReadAllText(path);
			Assert.NotEqual(0, text.Length);
			return text;
		}

	}
}
