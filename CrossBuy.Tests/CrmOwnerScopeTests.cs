using CrossBuy.BL;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Crm;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// A CRM record hidden from your list must not be readable by guessing its id.
	//
	// THE DEFECT. Eleven list reads in CrmService narrow to
	//
	//     OwnerEmployeeId == null || scope.Contains(OwnerEmployeeId)
	//
	// and the single-record reads sitting beside them applied only the company predicate. So a sales
	// rep whose Accounts list showed three rows could read the other company's — sorry, the other
	// REP's — account by typing its id into /Crm/AccountEditor?id=. Four editor screens take an id
	// straight off the URL: AccountEditor, TicketEditor, ListEditor and OpportunityProducts.
	//
	// This is in-company IDOR: company isolation was never the thing failing here, owner scoping was.
	// Both matter, and a test suite that only proved the company boundary would have called this clean.
	//
	// WHAT MUST NOT REGRESS while fixing it — each of these is a real authority the module already
	// grants, and narrowing them would be a different bug wearing a security badge:
	//   * a SalesManager sees their whole team subtree;
	//   * Marketing, CrmViewer and an unconfigured company are UNRESTRICTED (scope == null);
	//   * an UNOWNED record stays visible to everyone, exactly as every list predicate has it.
	//
	// And the refusal must look like absence. "Forbidden" on a real id and "not found" on a made-up one
	// tells a rep which ids exist in their company — the same existence oracle the cross-company work
	// closed, one boundary further in.
	// =================================================================================================
	public class CrmOwnerScopeTests
	{
		private const int Company = 1;

		private const int Rep = 31;        // sees only their own records
		private const int OtherRep = 32;   // a colleague in the same company
		private const int Manager = 33;    // sees the team

		private static Employee Person(int id) => new()
		{
			ID = id, EmpCompanyID = Company,
			FullName = "م" + id, FullNameEn = "Person " + id,
			FirstName = "P", LastName = "T", Address = "-", PhoneNumber = "-",
			Email = "e" + id + "@example.invalid", ProfileImage = "-", Gender = "-", MaritalStatus = "-",
			UserId = "u" + id,
		};

		private static ICrmService Service(CrossDbContext db, ICrmAccessService access)
			=> new CrmService(db, null!, null!, access, null!);

		// Owner scope is injected directly rather than derived from seeded role rows: these tests are
		// about what CrmService DOES with a scope, not about how CrmAccessService computes one. That
		// separation also means they keep their meaning through TAB-1's rewrite of the access service.
		private sealed class ScopedAccess : ICrmAccessService
		{
			private readonly HashSet<int>? _visible;
			private readonly int _me;
			public ScopedAccess(int me, HashSet<int>? visible) { _me = me; _visible = visible; }
			public int? CurrentEmployeeId() => _me;
			public Task<List<string>> MyRolesAsync() => Task.FromResult(new List<string>());
			public Task<bool> CanAsync(string action) => Task.FromResult(true);
			public Task<HashSet<int>?> VisibleOwnerIdsAsync() => Task.FromResult(_visible);
			public Task<HashSet<int>> TeamOwnerIdsAsync(int managerEmployeeId) => Task.FromResult(new HashSet<int>());
			public Task<string> RoleLabelAsync(bool isAr) => Task.FromResult("test");
		}

		private static ICrmAccessService OwnRecordsOnly(int me) => new ScopedAccess(me, new HashSet<int> { me });
		private static ICrmAccessService Team(int me, params int[] team) => new ScopedAccess(me, new HashSet<int>(team) { me });
		private static ICrmAccessService Unrestricted(int me) => new ScopedAccess(me, null);

		private static void SeedPeople(PlatformTestHost host)
		{
			host.Seed.Employee.AddRange(Person(Rep), Person(OtherRep), Person(Manager));
			host.Seed.SaveChanges();
		}

		private static T Seed<T>(PlatformTestHost host, T row) where T : class
		{
			host.Seed.Add(row);
			host.Seed.SaveChanges();
			return row;
		}

		private static Lead ALead(int? owner) => new()
		{ CompanyID = Company, Name = "Lead", Status = "New", OwnerEmployeeId = owner };

		private static CrmAccount AnAccount(int? owner) => new()
		{ CompanyID = Company, Name = "Account", IsActive = true, OwnerEmployeeId = owner };

		private static Opportunity AnOpportunity(int? owner) => new()
		{ CompanyID = Company, Title = "Deal", Stage = "Prospecting", Amount = 100m, OwnerEmployeeId = owner };

		private static CrmTicket ATicket(int? owner) => new()
		{ CompanyID = Company, Subject = "Case", Priority = "Normal", Status = "New", OwnerEmployeeId = owner };

		private static Campaign ACampaign(int? owner) => new()
		{ CompanyID = Company, Name = "Campaign", Status = "Active", OwnerEmployeeId = owner };

		private static CrmMarketingList AList(int? owner) => new()
		{ CompanyID = Company, Name = "Segment", IsActive = true, OwnerEmployeeId = owner };

		// =============================================================================================
		// THE IDOR, one entity at a time.
		// =============================================================================================

		[Fact]
		public async Task A_rep_cannot_read_a_colleagues_lead_by_id()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var theirs = Seed(host, ALead(OtherRep));

			var svc = Service(host.Request(Company).Db, OwnRecordsOnly(Rep));

			Assert.Null(await svc.GetLeadAsync(Company, theirs.ID));
		}

		[Fact]
		public async Task A_rep_cannot_read_a_colleagues_account_by_id()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var theirs = Seed(host, AnAccount(OtherRep));

			var svc = Service(host.Request(Company).Db, OwnRecordsOnly(Rep));

			Assert.Null(await svc.GetAccountAsync(Company, theirs.ID));
		}

		[Fact]
		public async Task A_rep_cannot_read_a_colleagues_opportunity_by_id()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var theirs = Seed(host, AnOpportunity(OtherRep));

			var svc = Service(host.Request(Company).Db, OwnRecordsOnly(Rep));

			Assert.Null(await svc.GetOpportunityAsync(Company, theirs.ID));
		}

		[Fact]
		public async Task A_rep_cannot_read_a_colleagues_ticket_by_id()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var theirs = Seed(host, ATicket(OtherRep));

			var svc = Service(host.Request(Company).Db, OwnRecordsOnly(Rep));

			Assert.Null(await svc.GetTicketAsync(Company, theirs.ID));
		}

		[Fact]
		public async Task A_rep_cannot_read_a_colleagues_campaign_by_id()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var theirs = Seed(host, ACampaign(OtherRep));

			var svc = Service(host.Request(Company).Db, OwnRecordsOnly(Rep));

			Assert.Null(await svc.GetCampaignAsync(Company, theirs.ID));
		}

		[Fact]
		public async Task A_rep_cannot_read_a_colleagues_marketing_list_by_id()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var theirs = Seed(host, AList(OtherRep));

			var svc = Service(host.Request(Company).Db, OwnRecordsOnly(Rep));

			Assert.Null(await svc.GetListAsync(Company, theirs.ID));
		}

		// =============================================================================================
		// THE PROPERTY, stated directly: list and by-id must agree.
		// =============================================================================================

		[Fact]
		public async Task What_the_list_hides_the_id_lookup_also_hides()
		{
			// The assertion is the AGREEMENT of the two reads, not the emptiness of either. A future
			// change that narrowed the list without narrowing the lookup — or the reverse — breaks here.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var mine = Seed(host, AnAccount(Rep));
			var theirs = Seed(host, AnAccount(OtherRep));

			var svc = Service(host.Request(Company).Db, OwnRecordsOnly(Rep));

			var (rows, _) = await svc.SearchAccountsAsync(Company, null, 1, 50);
			var listed = rows.Select(r => r.Id).ToHashSet();

			Assert.Equal(new[] { mine.ID }, listed.ToArray());
			Assert.NotNull(await svc.GetAccountAsync(Company, mine.ID));
			Assert.Null(await svc.GetAccountAsync(Company, theirs.ID));

			foreach (var id in new[] { mine.ID, theirs.ID })
				Assert.Equal(listed.Contains(id), await svc.GetAccountAsync(Company, id) != null);
		}

		[Fact]
		public async Task A_hidden_record_is_reported_as_absent_not_as_forbidden()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var theirs = Seed(host, AnAccount(OtherRep));

			var svc = Service(host.Request(Company).Db, OwnRecordsOnly(Rep));

			var hidden = await svc.GetAccountAsync(Company, theirs.ID);
			var absent = await svc.GetAccountAsync(Company, 999_999);

			Assert.Null(hidden);
			Assert.Null(absent);
		}

		[Fact]
		public async Task A_hidden_account_does_not_leak_its_contacts()
		{
			// GetAccountAsync loads the account's people into Contacts. The owner check runs BEFORE that
			// read, so a hidden account costs no contact query and returns no names.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var theirs = Seed(host, AnAccount(OtherRep));
			Seed(host, new CrmContact
			{
				CompanyID = Company, AccountId = theirs.ID, Name = "Their decision maker",
				Email = "dm@example.invalid", IsPrimary = true,
			});

			var svc = Service(host.Request(Company).Db, OwnRecordsOnly(Rep));

			Assert.Null(await svc.GetAccountAsync(Company, theirs.ID));
		}

		// =============================================================================================
		// WHAT MUST STILL WORK. Each of these would fail if the fix were a blanket "own records only".
		// =============================================================================================

		[Fact]
		public async Task A_rep_still_reads_their_own_record()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var mine = Seed(host, AnAccount(Rep));

			var svc = Service(host.Request(Company).Db, OwnRecordsOnly(Rep));

			Assert.NotNull(await svc.GetAccountAsync(Company, mine.ID));
		}

		[Fact]
		public async Task A_manager_still_reads_a_team_members_record()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var theirs = Seed(host, AnAccount(Rep));

			var svc = Service(host.Request(Company).Db, Team(Manager, Rep, OtherRep));

			Assert.NotNull(await svc.GetAccountAsync(Company, theirs.ID));
		}

		[Fact]
		public async Task A_manager_does_not_read_a_record_owned_outside_their_team()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var outsider = Seed(host, AnAccount(OtherRep));

			var svc = Service(host.Request(Company).Db, Team(Manager, Rep));

			Assert.Null(await svc.GetAccountAsync(Company, outsider.ID));
		}

		[Fact]
		public async Task An_unrestricted_caller_reads_everything_in_the_company()
		{
			// scope == null: Marketing, CrmViewer, and the company that has configured no CRM role yet.
			// Narrowing this would change an authorization policy under the cover of a security fix.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var a = Seed(host, AnAccount(Rep));
			var b = Seed(host, AnAccount(OtherRep));

			var svc = Service(host.Request(Company).Db, Unrestricted(Manager));

			Assert.NotNull(await svc.GetAccountAsync(Company, a.ID));
			Assert.NotNull(await svc.GetAccountAsync(Company, b.ID));
		}

		[Fact]
		public async Task An_unowned_record_stays_readable_by_everyone()
		{
			// Every list predicate is `OwnerEmployeeId == null || scope.Contains(owner)`. Dropping the
			// null branch here would hide legacy rows that predate the owner column from the very people
			// who need to claim them — visible in the list, unopenable from it.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var orphan = Seed(host, AnAccount(null));

			var svc = Service(host.Request(Company).Db, OwnRecordsOnly(Rep));

			Assert.NotNull(await svc.GetAccountAsync(Company, orphan.ID));
		}

		[Fact]
		public async Task An_unowned_lead_and_opportunity_stay_readable_too()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var lead = Seed(host, ALead(null));
			var opp = Seed(host, AnOpportunity(null));

			var svc = Service(host.Request(Company).Db, OwnRecordsOnly(Rep));

			Assert.NotNull(await svc.GetLeadAsync(Company, lead.ID));
			Assert.NotNull(await svc.GetOpportunityAsync(Company, opp.ID));
		}

		// =============================================================================================
		// Config tables are NOT owned records. Pipelines and SLA policies carry no OwnerEmployeeId and
		// are already gated by CrmPerm("manage"); owner-scoping them would be meaningless, so this
		// records the decision rather than leaving the omission looking like an oversight.
		// =============================================================================================

		[Fact]
		public void Pipeline_and_sla_policy_carry_no_owner_and_are_manage_gated()
		{
			Assert.Null(typeof(CrmPipeline).GetProperty("OwnerEmployeeId"));
			Assert.Null(typeof(CrmSlaPolicy).GetProperty("OwnerEmployeeId"));

			var controller = SourceText("CrossBuy", "Controllers", "CrmController.cs");
			Assert.Contains("[HttpGet][CrossBuy.Models.CrmPerm(\"manage\")]\r\n\t\tpublic async Task<IActionResult> Pipelines()", controller.Replace("\n", "\r\n").Replace("\r\r", "\r"), StringComparison.Ordinal);
		}

		[Fact]
		public void Every_owner_bearing_single_read_consults_the_owner_scope()
		{
			// Source-pinned so a NEW single-record read cannot quietly ship without the check. The six
			// are the owner-bearing ones; the config reads above are excluded by the test beside this.
			var code = SourceText("CrossBuy", "BL", "CrmService.cs");

			Assert.Contains("private async Task<bool> OwnerVisibleAsync(int? ownerEmployeeId)", code, StringComparison.Ordinal);
			Assert.Equal(6, Occurrences(code, "OwnerVisibleAsync(") - 1);   // -1 for the declaration
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
