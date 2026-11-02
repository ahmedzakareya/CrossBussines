using Microsoft.Extensions.Logging.Abstractions;
using CrossBuy.BL.Platform;
using CrossBuy.BL;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Crm;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// CRM STRENGTHENING WAVE 1 — lifecycle authority, reference integrity and owner scope on WRITES.
	//
	// WHY A SECOND CRM SUITE. CrmCompanyIsolationTests already proves a CRM row lands in the caller's
	// company. That is a different question from the one here: not "whose row is this" but "what is
	// this row allowed to POINT AT, and who may change it".
	//
	// The distinction is the whole defect. Every CRM save takes its foreign keys off a browser POST —
	// SaveOpportunity carries accountId and pipelineId, SaveActivity carries leadId — and the row is
	// stamped with the RESOLVED company, so CompanyWriteGuardInterceptor and the global query filter
	// are both satisfied. Neither of them looks at where the id points. A company-41 opportunity could
	// therefore be filed under company 77's account and every later join would read one tenant's
	// pipeline through another tenant's party.
	//
	// Company 1 is the foreign company and company 2 the caller, matching the sibling suite, so a
	// reader can move between them without re-learning the arrangement.
	// =================================================================================================
	public class CrmLifecycleAuthorityTests
	{
		private const int Foreign = 1;
		private const int Caller = 2;
		private const int Emp1 = 11;      // employee of company 1
		private const int Emp2 = 22;      // employee of company 2 — "me"
		private const int Emp3 = 33;      // a COLLEAGUE in company 2, whose records I may not see

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

		/// Unrestricted owner scope: the widest there is, so anything a test still refuses was refused
		/// by the rule under test rather than by owner visibility.
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

		/// A sales rep: sees only the owner ids listed. This is the shape CrmAccessService produces for
		/// the SalesRep role, and the shape the READ paths already honour.
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

		/// Records what the Won path did, so "exactly once" can be asserted rather than assumed.
		private sealed class RecordingLink : ICrmCustomerLink
		{
			private readonly bool _throws;
			public RecordingLink(bool throws = false) { _throws = throws; }
			public int Creates { get; private set; }
			public int NextId { get; set; } = 5001;
			public Task<int> CreateCustomerAsync(int companyId, string name, string? nameEn, string? taxNo, decimal? creditLimit)
			{
				Creates++;
				if (_throws) throw new InvalidOperationException("AR refused the customer");
				return Task.FromResult(NextId);
			}
			public Task EnrichAsync(int customerId, string? phone, string? email, string? segment, string? contactPerson)
				=> Task.CompletedTask;
		}

		private sealed class RecordingNotifications : INotificationService
		{
			public int RoleBroadcasts { get; private set; }
			public Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn, string? bodyAr, string? bodyEn,
				string type, int? refId = null, string? url = null, int? companyId = null, int? actorEmployeeId = null,
				string? priority = null, string? category = null, string? dedupKey = null, DateTime? expiresAt = null,
				string? icon = null, string? entityType = null, int? entityId = null) => Task.CompletedTask;
			public Task<int> NotifyRoleAsync(int companyId, string scope, string[] roles, string? titleAr, string? titleEn,
				string? bodyAr, string? bodyEn, string type, int? refId = null, int? exceptEmployeeId = null)
			{
				RoleBroadcasts++;
				return Task.FromResult(1);
			}
		}

		private static ICrmService Service(CrossDbContext db, ICrmAccessService access,
			ICrmCustomerLink? link = null, INotificationService? notify = null)
			=> new CrmService(db, link!, notify!, access, null!, Recorder(db));
		// COMPELLED BY THE MANDATORY RECORDER, and nothing else in this file moves.
		//
		// CrmService now REQUIRES IBusinessEventService, so this fixture must supply one. It supplies a
		// REAL BusinessEventService rather than a silent stub: a dummy that discards facts would restore
		// exactly the blindness making the dependency mandatory was meant to remove. The company comes
		// from the context the caller already scoped, so the event lands in the same tenant as the row -
		// BusinessEvent is a pilot entity and the write guard would refuse it otherwise.
		private static IBusinessEventService Recorder(CrossDbContext db)
			=> new BusinessEventService(
				db, new EntityRegistry(db),
				new StubContextAccessor(PlatformTestHost.DefaultContext(db.CompanyScope.CompanyId ?? 1)),
				NullLogger<BusinessEventService>.Instance);

		private static void SeedPeople(PlatformTestHost host)
		{
			host.Seed.Employee.AddRange(
				Person(Emp1, Foreign, "Foreign Person"),
				Person(Emp2, Caller, "Calling Person"),
				Person(Emp3, Caller, "Colleague"));
			host.Seed.SaveChanges();
		}

		private static readonly DateTime Seeded = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

		private static Lead SeedLead(PlatformTestHost host, int companyId, int? ownerId, string name, string status = "New")
		{
			var row = new Lead { CompanyID = companyId, Name = name, Status = status, OwnerEmployeeId = ownerId, EstimatedValue = 1000m, CreatedAt = Seeded };
			host.Seed.Leads.Add(row); host.Seed.SaveChanges(); return row;
		}

		private static CrmAccount SeedAccount(PlatformTestHost host, int companyId, int? ownerId, string name)
		{
			var row = new CrmAccount { CompanyID = companyId, Name = name, OwnerEmployeeId = ownerId, IsActive = true, CreatedAt = Seeded };
			host.Seed.CrmAccounts.Add(row); host.Seed.SaveChanges(); return row;
		}

		private static Opportunity SeedOpportunity(PlatformTestHost host, int companyId, int? ownerId, int? accountId,
			string title, string stage = "Prospecting", int? stageId = null, int? pipelineId = null)
		{
			var row = new Opportunity
			{
				CompanyID = companyId, Title = title, AccountId = accountId, OwnerEmployeeId = ownerId,
				Stage = stage, StageId = stageId, PipelineId = pipelineId, Amount = 5000m, Probability = 20, CreatedAt = Seeded,
			};
			host.Seed.Opportunities.Add(row); host.Seed.SaveChanges(); return row;
		}

		private static Campaign SeedCampaign(PlatformTestHost host, int companyId, string name)
		{
			var row = new Campaign { CompanyID = companyId, Name = name, Status = "Active", CreatedAt = Seeded };
			host.Seed.Campaigns.Add(row); host.Seed.SaveChanges(); return row;
		}

		private static Activity SeedActivity(PlatformTestHost host, int companyId, int? ownerId, string subject)
		{
			var row = new Activity { CompanyID = companyId, Type = "Call", Subject = subject, OwnerEmployeeId = ownerId, Done = false, CreatedAt = Seeded };
			host.Seed.Activities.Add(row); host.Seed.SaveChanges(); return row;
		}

		/// A four-stage pipeline whose last two stages are terminal, which is the shape the product ships.
		private static (CrmPipeline pipe, CrmPipelineStage prospect, CrmPipelineStage negotiate, CrmPipelineStage won, CrmPipelineStage lost)
			SeedPipeline(PlatformTestHost host, int companyId)
		{
			var p = new CrmPipeline { CompanyID = companyId, Name = "Default", IsDefault = true, IsActive = true, CreatedAt = Seeded };
			host.Seed.CrmPipelines.Add(p); host.Seed.SaveChanges();
			var stages = new[]
			{
				new CrmPipelineStage { CompanyID = companyId, PipelineId = p.ID, Name = "Prospecting", Sort = 0, Probability = 10, CreatedAt = Seeded },
				new CrmPipelineStage { CompanyID = companyId, PipelineId = p.ID, Name = "Negotiation", Sort = 1, Probability = 75, CreatedAt = Seeded },
				new CrmPipelineStage { CompanyID = companyId, PipelineId = p.ID, Name = "Won", Sort = 2, Probability = 100, IsWon = true, CreatedAt = Seeded },
				new CrmPipelineStage { CompanyID = companyId, PipelineId = p.ID, Name = "Lost", Sort = 3, Probability = 0, IsLost = true, CreatedAt = Seeded },
			};
			host.Seed.CrmPipelineStages.AddRange(stages); host.Seed.SaveChanges();
			return (p, stages[0], stages[1], stages[2], stages[3]);
		}

		// =============================================================================================
		// A. CROSS-COMPANY REFERENCE INTEGRITY
		//
		// Each of these posts a well-formed request whose only fault is that an id belongs to somebody
		// else. The assertion is always in two parts: the save was REFUSED, and nothing was written —
		// because a refusal that still persisted the row would be a worse defect than the one it fixed.
		// =============================================================================================

		[Fact]
		public async Task An_opportunity_cannot_be_filed_under_another_companys_account()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var foreignAccount = SeedAccount(host, Foreign, Emp1, "Foreign account");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, err) = await svc.SaveOpportunityAsync(Caller, new Opportunity
			{
				Title = "Reaching across", AccountId = foreignAccount.ID, Stage = "Prospecting", Amount = 100m,
			}, "u22");

			Assert.False(ok);
			Assert.NotNull(err);
			Assert.Empty(await host.AllCompanies().Opportunities.AsNoTracking().ToListAsync());
		}

		[Fact]
		public async Task An_opportunity_cannot_be_attributed_to_another_companys_campaign()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var foreignCampaign = SeedCampaign(host, Foreign, "Foreign campaign");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, _) = await svc.SaveOpportunityAsync(Caller, new Opportunity
			{
				Title = "Attributed elsewhere", CampaignId = foreignCampaign.ID, Stage = "Prospecting",
			}, "u22");

			Assert.False(ok);
			Assert.Empty(await host.AllCompanies().Opportunities.AsNoTracking().ToListAsync());
		}

		[Fact]
		public async Task An_opportunity_cannot_be_traced_back_to_another_companys_lead()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var foreignLead = SeedLead(host, Foreign, Emp1, "Foreign lead");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, _) = await svc.SaveOpportunityAsync(Caller, new Opportunity
			{
				Title = "Wrong origin", LeadId = foreignLead.ID, Stage = "Prospecting",
			}, "u22");

			Assert.False(ok);
			Assert.Empty(await host.AllCompanies().Opportunities.AsNoTracking().ToListAsync());
		}

		[Fact]
		public async Task An_opportunity_cannot_be_stamped_with_another_companys_pipeline()
		{
			// The pipeline id is the subtlest of the four, because the STAGE lookup is company-scoped and
			// therefore finds nothing — so the old code fell through to the legacy branch and stored the
			// foreign pipeline id anyway, on a row whose stages could never be read.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var foreignPipe = SeedPipeline(host, Foreign);

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, _) = await svc.SaveOpportunityAsync(Caller, new Opportunity
			{
				Title = "Foreign pipeline", PipelineId = foreignPipe.pipe.ID, Stage = "Prospecting",
			}, "u22");

			Assert.False(ok);
			Assert.Empty(await host.AllCompanies().Opportunities.AsNoTracking().ToListAsync());
		}

		[Fact]
		public async Task A_foreign_pipeline_is_refused_even_when_no_stage_is_named()
		{
			// The pipeline check has to stand on its own. When a stage name IS given, the stage lookup is
			// company-scoped, finds nothing and refuses first — so the pipeline guard is masked and could
			// be deleted without any test noticing. With the stage left empty the request means "put this
			// deal at the start of THAT pipeline", the stage lookup has nothing to refuse, and only the
			// pipeline's own company can answer.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var foreignPipe = SeedPipeline(host, Foreign);

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, _) = await svc.SaveOpportunityAsync(Caller, new Opportunity
			{
				Title = "Foreign pipeline, no stage", PipelineId = foreignPipe.pipe.ID, Stage = "",
			}, "u22");

			Assert.False(ok);
			Assert.Empty(await host.AllCompanies().Opportunities.AsNoTracking().ToListAsync());
		}

		[Fact]
		public async Task A_lead_cannot_be_attributed_to_another_companys_campaign()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var foreignCampaign = SeedCampaign(host, Foreign, "Foreign campaign");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, _) = await svc.SaveLeadAsync(Caller, new Lead
			{
				Name = "Attributed elsewhere", Status = "New", CampaignId = foreignCampaign.ID,
			}, "u22");

			Assert.False(ok);
			Assert.Empty(await host.AllCompanies().Leads.AsNoTracking().Where(l => l.CompanyID == Caller).ToListAsync());
		}

		[Fact]
		public async Task An_activity_cannot_be_filed_against_another_companys_lead()
		{
			// The sibling suite asserts such a row lands in the CALLER's company, and it did. What it
			// could not see is that the row still POINTED at company 1's lead — so company 1's timeline
			// gained an entry written by a tenant that cannot read it.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var foreignLead = SeedLead(host, Foreign, Emp1, "Foreign lead");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, _) = await svc.SaveActivityAsync(Caller, new Activity
			{
				Type = "Call", Subject = "Reaching across", EntityType = "Lead", EntityId = foreignLead.ID, LeadId = foreignLead.ID,
			}, "u22");

			Assert.False(ok);
			Assert.Empty(await host.AllCompanies().Activities.AsNoTracking().ToListAsync());
		}

		[Fact]
		public async Task An_activity_cannot_be_filed_against_another_companys_opportunity()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var foreignOpp = SeedOpportunity(host, Foreign, Emp1, null, "Foreign deal");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, _) = await svc.SaveActivityAsync(Caller, new Activity
			{
				Type = "Meeting", Subject = "Reaching across", OpportunityId = foreignOpp.ID,
			}, "u22");

			Assert.False(ok);
			Assert.Empty(await host.AllCompanies().Activities.AsNoTracking().ToListAsync());
		}

		[Fact]
		public async Task An_activity_cannot_name_another_companys_account_through_the_polymorphic_pair_alone()
		{
			// The pair is a SEPARATE door from LeadId and OpportunityId, and the test above closes only
			// those: it sets both, so the typed check alone would still refuse it and the polymorphic
			// check could rot untested. Here EntityType/EntityId are the only reference on the row —
			// which is exactly how an Account or Contact timeline entry is written.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var foreignAccount = SeedAccount(host, Foreign, Emp1, "Foreign account");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, _) = await svc.SaveActivityAsync(Caller, new Activity
			{
				Type = "Meeting", Subject = "Reaching across", EntityType = "Account", EntityId = foreignAccount.ID,
			}, "u22");

			Assert.False(ok);
			Assert.Empty(await host.AllCompanies().Activities.AsNoTracking().ToListAsync());
		}

		[Fact]
		public async Task A_contact_cannot_be_attached_to_another_companys_account()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var foreignAccount = SeedAccount(host, Foreign, Emp1, "Foreign account");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, _) = await svc.SaveContactAsync(Caller, new CrmContact
			{
				AccountId = foreignAccount.ID, Name = "Their person", Phone = "1",
			});

			Assert.False(ok);
			Assert.Empty(await host.AllCompanies().CrmContacts.AsNoTracking().ToListAsync());
		}

		[Fact]
		public async Task A_marketing_audience_cannot_be_built_from_another_companys_records()
		{
			// The same class of defect in the marketing corner: the member picker is company-scoped, but
			// the POST that enrols a member is not the picker. A foreign member is skipped rather than
			// fatal — matching the duplicate rule beside it — and the returned count is what tells the
			// caller the truth.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var campaign = SeedCampaign(host, Caller, "My campaign");
			var mine = SeedLead(host, Caller, Emp2, "My lead");
			var theirs = SeedLead(host, Foreign, Emp1, "Foreign lead");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var added = await svc.AddCampaignMembersAsync(Caller, campaign.ID, new[]
			{
				("Lead", mine.ID, (string?)"My lead"),
				("Lead", theirs.ID, (string?)"Foreign lead"),
			});

			Assert.Equal(1, added);
			var rows = await host.AllCompanies().CampaignMembers.AsNoTracking().ToListAsync();
			Assert.Equal(new[] { mine.ID }, rows.Select(r => r.EntityId).ToArray());
		}

		[Fact]
		public async Task Members_cannot_be_enrolled_into_another_companys_campaign()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var foreignCampaign = SeedCampaign(host, Foreign, "Foreign campaign");
			var mine = SeedLead(host, Caller, Emp2, "My lead");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var added = await svc.AddCampaignMembersAsync(Caller, foreignCampaign.ID, new[] { ("Lead", mine.ID, (string?)"My lead") });

			Assert.Equal(0, added);
			Assert.Empty(await host.AllCompanies().CampaignMembers.AsNoTracking().ToListAsync());
		}

		[Fact]
		public async Task The_reference_guard_does_not_refuse_a_legitimate_link_in_the_callers_own_company()
		{
			// The counterpart every guard needs: a rule that refuses everything would pass all eight tests
			// above and break the product.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var myAccount = SeedAccount(host, Caller, Emp2, "My account");
			var myCampaign = SeedCampaign(host, Caller, "My campaign");
			var myLead = SeedLead(host, Caller, Emp2, "My lead");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, err) = await svc.SaveOpportunityAsync(Caller, new Opportunity
			{
				Title = "Perfectly ordinary deal", AccountId = myAccount.ID, CampaignId = myCampaign.ID,
				LeadId = myLead.ID, Stage = "Prospecting", Amount = 900m,
			}, "u22");

			Assert.True(ok, err);
			var saved = await host.AllCompanies().Opportunities.AsNoTracking().SingleAsync();
			Assert.Equal(myAccount.ID, saved.AccountId);
			Assert.Equal(myCampaign.ID, saved.CampaignId);
			Assert.Equal(myLead.ID, saved.LeadId);
			Assert.Equal(Caller, saved.CompanyID);
		}

		// =============================================================================================
		// B. OWNER SCOPE ON THE WRITE PATH
		//
		// The read paths already refuse a colleague's record by id. The write paths did not ask at all,
		// so the same rep could not SEE the row and could still overwrite it.
		//
		// A hidden record must answer EXACTLY as an absent one — the tests below assert the equality of
		// the two answers, not merely that each fails, because a caller who can tell them apart has an
		// existence oracle over a colleague's book.
		// =============================================================================================

		[Fact]
		public async Task A_rep_cannot_overwrite_a_colleagues_lead_and_cannot_tell_it_from_one_that_does_not_exist()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var colleagues = SeedLead(host, Caller, Emp3, "Colleague's lead");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new RepAccess(Emp2));

			var hidden = await svc.SaveLeadAsync(Caller, new Lead { ID = colleagues.ID, Name = "Hijacked", Status = "Qualified" }, "u22");
            Assert.False(hidden.ok);
			var absent = await svc.SaveLeadAsync(Caller, new Lead { ID = 999_999, Name = "Hijacked", Status = "Qualified" }, "u22");
            Assert.False(absent.ok);

			Assert.Equal(absent.error, hidden.error);

			var after = await host.AllCompanies().Leads.AsNoTracking().FirstAsync(l => l.ID == colleagues.ID);
			Assert.Equal("Colleague's lead", after.Name);
			Assert.Equal("New", after.Status);
		}

		[Fact]
		public async Task A_rep_cannot_convert_a_colleagues_lead()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var colleagues = SeedLead(host, Caller, Emp3, "Colleague's lead");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new RepAccess(Emp2));

			var (hiddenOk, hiddenErr, _) = await svc.ConvertLeadToAccountAsync(Caller, colleagues.ID, "u22");
			var (absentOk, absentErr, _) = await svc.ConvertLeadToAccountAsync(Caller, 999_999, "u22");

			Assert.False(hiddenOk);
			Assert.False(absentOk);
			Assert.Equal(absentErr, hiddenErr);
			Assert.Empty(await host.AllCompanies().CrmAccounts.AsNoTracking().ToListAsync());
			Assert.Equal("New", (await host.AllCompanies().Leads.AsNoTracking().FirstAsync(l => l.ID == colleagues.ID)).Status);
		}

		[Fact]
		public async Task A_rep_cannot_move_a_colleagues_opportunity_through_the_pipeline()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var colleagues = SeedOpportunity(host, Caller, Emp3, null, "Colleague's deal");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new RepAccess(Emp2));

			var (hiddenOk, hiddenErr) = await svc.UpdateOpportunityStageAsync(Caller, colleagues.ID, "Negotiation");
			var (absentOk, absentErr) = await svc.UpdateOpportunityStageAsync(Caller, 999_999, "Negotiation");

			Assert.False(hiddenOk);
			Assert.False(absentOk);
			Assert.Equal(absentErr, hiddenErr);
			Assert.Equal("Prospecting", (await host.AllCompanies().Opportunities.AsNoTracking().FirstAsync(o => o.ID == colleagues.ID)).Stage);
		}

		[Fact]
		public async Task A_rep_cannot_tick_off_a_colleagues_activity()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var colleagues = SeedActivity(host, Caller, Emp3, "Colleague's call");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new RepAccess(Emp2));

			Assert.False(await svc.ToggleActivityAsync(Caller, colleagues.ID));
			Assert.False(await svc.ToggleActivityAsync(Caller, 999_999));
			Assert.False((await host.AllCompanies().Activities.AsNoTracking().FirstAsync(a => a.ID == colleagues.ID)).Done);
		}

		[Fact]
		public async Task A_rep_still_writes_their_own_record_and_an_unowned_one()
		{
			// The two behaviours the list predicate already has, preserved on the write side: my own row
			// is mine, and a row nobody owns stays everybody's. Without this the guard would be a lock on
			// the whole module rather than a rule about colleagues.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var mine = SeedLead(host, Caller, Emp2, "My lead");
			var unowned = SeedLead(host, Caller, null, "Nobody's lead");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new RepAccess(Emp2));

			var (mineOk, mineErr) = await svc.SaveLeadAsync(Caller, new Lead { ID = mine.ID, Name = "My lead v2", Status = "Contacted" }, "u22");
			var (freeOk, freeErr) = await svc.SaveLeadAsync(Caller, new Lead { ID = unowned.ID, Name = "Claimed", Status = "Contacted" }, "u22");

			Assert.True(mineOk, mineErr);
			Assert.True(freeOk, freeErr);
			Assert.Equal("My lead v2", (await host.AllCompanies().Leads.AsNoTracking().FirstAsync(l => l.ID == mine.ID)).Name);
			Assert.Equal("Claimed", (await host.AllCompanies().Leads.AsNoTracking().FirstAsync(l => l.ID == unowned.ID)).Name);
		}

		[Fact]
		public async Task An_unrestricted_caller_still_writes_every_record_in_the_company()
		{
			// Manager, marketing, viewer-with-manage and the unconfigured bootstrap case all arrive as
			// scope == null. The write rule must not narrow what policy already allows.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var colleagues = SeedLead(host, Caller, Emp3, "Colleague's lead");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, err) = await svc.SaveLeadAsync(Caller, new Lead { ID = colleagues.ID, Name = "Edited by manager", Status = "Contacted" }, "u22");

			Assert.True(ok, err);
			Assert.Equal("Edited by manager", (await host.AllCompanies().Leads.AsNoTracking().FirstAsync(l => l.ID == colleagues.ID)).Name);
		}

		// =============================================================================================
		// C. STAGE AUTHORITY
		// =============================================================================================

		[Fact]
		public async Task A_stage_the_pipeline_does_not_contain_is_refused_instead_of_rounded_to_the_first_one()
		{
			// THE defect this section exists for. The resolver ended with "return the first stage", so an
			// unrecognised stage dragged a negotiation back to prospecting and reported success. The
			// assertion is therefore not merely that it failed — it is that the deal DID NOT MOVE.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var pipe = SeedPipeline(host, Caller);
			var opp = SeedOpportunity(host, Caller, Emp2, null, "Live deal", "Negotiation", pipe.negotiate.ID, pipe.pipe.ID);

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, err) = await svc.UpdateOpportunityStageAsync(Caller, opp.ID, "Negotiationn");

			Assert.False(ok);
			Assert.NotNull(err);
			var after = await host.AllCompanies().Opportunities.AsNoTracking().FirstAsync(o => o.ID == opp.ID);
			Assert.Equal("Negotiation", after.Stage);
			Assert.Equal(pipe.negotiate.ID, after.StageId);
			Assert.Equal(20, after.Probability);
		}

		[Fact]
		public async Task An_unrecognised_legacy_stage_name_is_refused_too()
		{
			// No pipeline configured: the legacy vocabulary is still a closed set. Free text here is how a
			// stage nobody can filter by and no board column shows gets into the table.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var opp = SeedOpportunity(host, Caller, Emp2, null, "Legacy deal");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, _) = await svc.UpdateOpportunityStageAsync(Caller, opp.ID, "Almost-there");

			Assert.False(ok);
			Assert.Equal("Prospecting", (await host.AllCompanies().Opportunities.AsNoTracking().FirstAsync(o => o.ID == opp.ID)).Stage);
		}

		[Fact]
		public async Task A_stage_this_pipeline_lacks_is_refused_even_when_the_legacy_vocabulary_knows_the_name()
		{
			// The sharpest version of the rounding defect. "Proposal" IS a legitimate legacy stage name,
			// so the closed-vocabulary check waves it through — and only the pipeline lookup can tell
			// that THIS pipeline has no such stage. Without that check the opportunity ends up with
			// Stage = "Proposal" while StageId still points at Negotiation: a row whose two stage columns
			// disagree, which no board and no report can render honestly.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var pipe = SeedPipeline(host, Caller);   // Prospecting, Negotiation, Won, Lost — no Proposal
			var opp = SeedOpportunity(host, Caller, Emp2, null, "Live deal", "Negotiation", pipe.negotiate.ID, pipe.pipe.ID);

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, _) = await svc.UpdateOpportunityStageAsync(Caller, opp.ID, "Proposal");

			Assert.False(ok);
			var after = await host.AllCompanies().Opportunities.AsNoTracking().FirstAsync(o => o.ID == opp.ID);
			Assert.Equal("Negotiation", after.Stage);
			Assert.Equal(pipe.negotiate.ID, after.StageId);
		}

		[Fact]
		public async Task A_valid_stage_move_carries_the_stages_own_probability()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var pipe = SeedPipeline(host, Caller);
			var opp = SeedOpportunity(host, Caller, Emp2, null, "Live deal", "Prospecting", pipe.prospect.ID, pipe.pipe.ID);

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, err) = await svc.UpdateOpportunityStageAsync(Caller, opp.ID, "Negotiation");

			Assert.True(ok, err);
			var after = await host.AllCompanies().Opportunities.AsNoTracking().FirstAsync(o => o.ID == opp.ID);
			Assert.Equal("Negotiation", after.Stage);
			Assert.Equal(pipe.negotiate.ID, after.StageId);
			Assert.Equal(75, after.Probability);
		}

		[Fact]
		public async Task The_editor_and_the_board_reach_the_same_stage_through_the_same_authority()
		{
			// Two entry points, one transition. Before, each computed "became Won" from its own snapshot
			// and validated differently — which is how the board could refuse what the editor accepted.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var pipe = SeedPipeline(host, Caller);
			var viaBoard = SeedOpportunity(host, Caller, Emp2, null, "Board deal", "Prospecting", pipe.prospect.ID, pipe.pipe.ID);
			var viaEditor = SeedOpportunity(host, Caller, Emp2, null, "Editor deal", "Prospecting", pipe.prospect.ID, pipe.pipe.ID);

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			await svc.UpdateOpportunityStageAsync(Caller, viaBoard.ID, "Negotiation");
			await svc.SaveOpportunityAsync(Caller, new Opportunity { ID = viaEditor.ID, Title = "Editor deal", Stage = "Negotiation", PipelineId = pipe.pipe.ID }, "u22");

			var board = await host.AllCompanies().Opportunities.AsNoTracking().FirstAsync(o => o.ID == viaBoard.ID);
			var editor = await host.AllCompanies().Opportunities.AsNoTracking().FirstAsync(o => o.ID == viaEditor.ID);
			Assert.Equal(board.Stage, editor.Stage);
			Assert.Equal(board.StageId, editor.StageId);
			Assert.Equal(board.Probability, editor.Probability);
		}

		// =============================================================================================
		// D. TERMINAL STATE
		// =============================================================================================

		[Fact]
		public async Task A_won_deal_does_not_quietly_reopen()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var pipe = SeedPipeline(host, Caller);
			var opp = SeedOpportunity(host, Caller, Emp2, null, "Closed deal", "Won", pipe.won.ID, pipe.pipe.ID);

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2), new RecordingLink(), new RecordingNotifications());

			var (ok, err) = await svc.UpdateOpportunityStageAsync(Caller, opp.ID, "Prospecting");

			Assert.False(ok);
			Assert.NotNull(err);
			Assert.Equal("Won", (await host.AllCompanies().Opportunities.AsNoTracking().FirstAsync(o => o.ID == opp.ID)).Stage);
		}

		[Fact]
		public async Task A_lost_deal_does_not_quietly_reopen_either()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var pipe = SeedPipeline(host, Caller);
			var opp = SeedOpportunity(host, Caller, Emp2, null, "Dead deal", "Lost", pipe.lost.ID, pipe.pipe.ID);

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2), new RecordingLink(), new RecordingNotifications());

			var (ok, _) = await svc.UpdateOpportunityStageAsync(Caller, opp.ID, "Negotiation");

			Assert.False(ok);
			Assert.Equal("Lost", (await host.AllCompanies().Opportunities.AsNoTracking().FirstAsync(o => o.ID == opp.ID)).Stage);
		}

		[Fact]
		public async Task The_legacy_vocabulary_is_terminal_too_when_no_pipeline_is_configured()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var opp = SeedOpportunity(host, Caller, Emp2, null, "Legacy won", "Won");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2), new RecordingLink(), new RecordingNotifications());

			var (ok, _) = await svc.UpdateOpportunityStageAsync(Caller, opp.ID, "Negotiation");

			Assert.False(ok);
			Assert.Equal("Won", (await host.AllCompanies().Opportunities.AsNoTracking().FirstAsync(o => o.ID == opp.ID)).Stage);
		}

		[Fact]
		public async Task A_closed_deal_can_still_be_edited_as_long_as_its_stage_does_not_move()
		{
			// The rule is about TRANSITIONS, not about freezing the record. Someone must still be able to
			// correct the amount or write down why it was lost.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var pipe = SeedPipeline(host, Caller);
			var opp = SeedOpportunity(host, Caller, Emp2, null, "Closed deal", "Won", pipe.won.ID, pipe.pipe.ID);

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2), new RecordingLink(), new RecordingNotifications());

			var (ok, err) = await svc.SaveOpportunityAsync(Caller, new Opportunity
			{
				ID = opp.ID, Title = "Closed deal", Stage = "Won", StageId = pipe.won.ID, PipelineId = pipe.pipe.ID,
				Amount = 7777m, WinLossReason = "Best price",
			}, "u22");

			Assert.True(ok, err);
			var after = await host.AllCompanies().Opportunities.AsNoTracking().FirstAsync(o => o.ID == opp.ID);
			Assert.Equal(7777m, after.Amount);
			Assert.Equal("Best price", after.WinLossReason);
			Assert.Equal("Won", after.Stage);
		}

		[Fact]
		public async Task Re_saving_a_won_deal_does_not_win_it_a_second_time()
		{
			// The side effects are what makes this matter: a second Won created the financial customer
			// again and broadcast another celebration to every accountant.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var pipe = SeedPipeline(host, Caller);
			var account = SeedAccount(host, Caller, Emp2, "My account");
			var opp = SeedOpportunity(host, Caller, Emp2, account.ID, "Live deal", "Prospecting", pipe.prospect.ID, pipe.pipe.ID);

			var req = host.Request(Caller);
			var link = new RecordingLink();
			var notify = new RecordingNotifications();
			var svc = Service(req.Db, new WideOpenAccess(Emp2), link, notify);

			var first = await svc.UpdateOpportunityStageAsync(Caller, opp.ID, "Won");
			var again = await svc.SaveOpportunityAsync(Caller, new Opportunity
			{
				ID = opp.ID, Title = "Live deal", AccountId = account.ID, Stage = "Won", StageId = pipe.won.ID, PipelineId = pipe.pipe.ID,
			}, "u22");

			Assert.True(first.ok, first.error);
			Assert.True(again.ok, again.error);
			Assert.Equal(1, link.Creates);
			Assert.Equal(1, notify.RoleBroadcasts);
		}

		// =============================================================================================
		// E. WON → FINANCIAL CUSTOMER, AND THE ROLLBACK
		// =============================================================================================

		[Fact]
		public async Task Winning_a_deal_stamps_the_customer_the_billing_path_needs()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var account = SeedAccount(host, Caller, Emp2, "My account");
			var opp = SeedOpportunity(host, Caller, Emp2, account.ID, "Live deal");

			var req = host.Request(Caller);
			var link = new RecordingLink { NextId = 6123 };
			var svc = Service(req.Db, new WideOpenAccess(Emp2), link, new RecordingNotifications());

			var (ok, err) = await svc.UpdateOpportunityStageAsync(Caller, opp.ID, "Won");

			Assert.True(ok, err);
			var after = await host.AllCompanies().Opportunities.AsNoTracking().FirstAsync(o => o.ID == opp.ID);
			Assert.Equal("Won", after.Stage);
			Assert.Equal(6123, after.CustomerId);
			Assert.Equal(6123, (await host.AllCompanies().CrmAccounts.AsNoTracking().FirstAsync(a => a.ID == account.ID)).CustomerId);
		}

		[Fact]
		public async Task When_the_customer_link_fails_the_deal_does_not_become_won_at_all()
		{
			// THE ROLLBACK PROOF. This used to be a swallowed catch: the deal went Won, the CustomerId was
			// never stamped, and the O2C path then refused to raise a quotation from a deal the pipeline
			// reported as closed-won. Half of that fact is worse than neither half.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var account = SeedAccount(host, Caller, Emp2, "My account");
			var opp = SeedOpportunity(host, Caller, Emp2, account.ID, "Live deal");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2), new RecordingLink(throws: true), new RecordingNotifications());

			await Assert.ThrowsAsync<InvalidOperationException>(() => svc.UpdateOpportunityStageAsync(Caller, opp.ID, "Won"));

			var after = await host.AllCompanies().Opportunities.AsNoTracking().FirstAsync(o => o.ID == opp.ID);
			Assert.Equal("Prospecting", after.Stage);
			Assert.Null(after.CustomerId);
			Assert.Null((await host.AllCompanies().CrmAccounts.AsNoTracking().FirstAsync(a => a.ID == account.ID)).CustomerId);
		}

		[Fact]
		public async Task A_deal_whose_account_has_vanished_cannot_be_won()
		{
			// The OTHER way the link fails, and the one a throwing stub cannot reach: the account the
			// opportunity points at is no longer there, so the linker returns "no customer" rather than
			// raising. A deal cannot be closed-won against a party that does not exist — and the old
			// swallow made exactly this outcome silent.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var opp = SeedOpportunity(host, Caller, Emp2, accountId: 4242, title: "Points at nothing");

			var req = host.Request(Caller);
			var link = new RecordingLink();
			var svc = Service(req.Db, new WideOpenAccess(Emp2), link, new RecordingNotifications());

			var (ok, err) = await svc.UpdateOpportunityStageAsync(Caller, opp.ID, "Won");

			Assert.False(ok);
			Assert.NotNull(err);
			Assert.Equal(0, link.Creates);
			Assert.Equal("Prospecting", (await host.AllCompanies().Opportunities.AsNoTracking().FirstAsync(o => o.ID == opp.ID)).Stage);
		}

		[Fact]
		public async Task A_deal_with_no_account_still_wins_because_there_is_nothing_to_link()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var opp = SeedOpportunity(host, Caller, Emp2, null, "Unlinked deal");

			var req = host.Request(Caller);
			var link = new RecordingLink();
			var svc = Service(req.Db, new WideOpenAccess(Emp2), link, new RecordingNotifications());

			var (ok, err) = await svc.UpdateOpportunityStageAsync(Caller, opp.ID, "Won");

			Assert.True(ok, err);
			Assert.Equal(0, link.Creates);
			Assert.Equal("Won", (await host.AllCompanies().Opportunities.AsNoTracking().FirstAsync(o => o.ID == opp.ID)).Stage);
		}

		// =============================================================================================
		// F. LEAD → ACCOUNT CONVERSION
		// =============================================================================================

		[Fact]
		public async Task Conversion_creates_the_account_its_primary_contact_and_the_converted_lead_together()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var lead = SeedLead(host, Caller, Emp2, "Sara", "Qualified");
			lead.Company = "Acme"; lead.Phone = "555"; lead.Email = "s@example.invalid";
			host.Seed.SaveChanges();

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, err, accountId) = await svc.ConvertLeadToAccountAsync(Caller, lead.ID, "u22");

			Assert.True(ok, err);
			var account = await host.AllCompanies().CrmAccounts.AsNoTracking().SingleAsync();
			Assert.Equal(accountId, account.ID);
			Assert.Equal("Acme", account.Name);          // the COMPANY becomes the party
			Assert.Equal(Emp2, account.OwnerEmployeeId); // the owner follows the lead

			var contact = await host.AllCompanies().CrmContacts.AsNoTracking().SingleAsync();
			Assert.Equal("Sara", contact.Name);           // the PERSON becomes the contact
			Assert.True(contact.IsPrimary);
			Assert.Equal(account.ID, contact.AccountId);

			var after = await host.AllCompanies().Leads.AsNoTracking().FirstAsync(l => l.ID == lead.ID);
			Assert.Equal("Converted", after.Status);
			Assert.Equal(account.ID, after.AccountId);
		}

		[Fact]
		public async Task Converting_the_same_lead_twice_does_not_create_a_second_party()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var lead = SeedLead(host, Caller, Emp2, "Sara", "Qualified");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var first = await svc.ConvertLeadToAccountAsync(Caller, lead.ID, "u22");
			var second = await svc.ConvertLeadToAccountAsync(Caller, lead.ID, "u22");

			Assert.True(first.ok, first.error);
			Assert.False(second.ok);
			Assert.Equal(first.accountId, second.accountId);   // it points at the party that already exists
			Assert.Single(await host.AllCompanies().CrmAccounts.AsNoTracking().ToListAsync());
			Assert.Single(await host.AllCompanies().CrmContacts.AsNoTracking().ToListAsync());
		}

		[Fact]
		public async Task A_lead_already_marked_converted_is_not_converted_again()
		{
			// Converted WITHOUT an account is exactly the shape the old two-SaveChanges conversion left
			// behind when the second call failed. Converting it again would create the party a second
			// time and leave the first one orphaned.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var lead = SeedLead(host, Caller, Emp2, "Sara", "Converted");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, err, _) = await svc.ConvertLeadToAccountAsync(Caller, lead.ID, "u22");

			Assert.False(ok);
			Assert.NotNull(err);
			Assert.Empty(await host.AllCompanies().CrmAccounts.AsNoTracking().ToListAsync());
		}

		[Fact]
		public async Task Conversion_does_not_create_a_financial_customer()
		{
			// The product contract: a party becomes a financial customer when a deal is WON, not when a
			// lead is qualified. A conversion that quietly opened an AR account would put marketing
			// prospects into the customer master.
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var lead = SeedLead(host, Caller, Emp2, "Sara", "Qualified");

			var req = host.Request(Caller);
			var link = new RecordingLink();
			var svc = Service(req.Db, new WideOpenAccess(Emp2), link, new RecordingNotifications());

			var (ok, err, _) = await svc.ConvertLeadToAccountAsync(Caller, lead.ID, "u22");

			Assert.True(ok, err);
			Assert.Equal(0, link.Creates);
			Assert.Null((await host.AllCompanies().CrmAccounts.AsNoTracking().SingleAsync()).CustomerId);
		}

		[Fact]
		public void Conversion_holds_its_three_writes_in_one_transaction()
		{
			// PINNED AT THE SOURCE, and the reason is worth stating rather than hiding: no behavioural
			// test in this harness can kill the removal of this transaction. Conversion's three writes
			// only diverge when the SECOND save fails, and SQLite-in-memory enforces none of the
			// constraints (max length, FK, unique) that would make it fail — so on every path a test can
			// drive, "two saves" and "one transaction" produce identical rows.
			//
			// The defect is real in production, where those constraints exist: a failure on the second
			// save left a real account and a real contact behind while the lead still read as
			// un-converted, and the next click created the party again. Since the consequence cannot be
			// observed here, the PRESENCE of the transaction is asserted instead — deliberately the
			// weaker instrument, chosen because the alternative is no instrument at all.
			var code = SourceText("CrossBuy", "BL", "CrmService.cs");
			var body = MethodBody(code, "public async Task<(bool ok, string? error, int accountId)> ConvertLeadToAccountAsync(");

			Assert.Contains("ScopedTx.BeginOrJoinAsync(_context)", body, StringComparison.Ordinal);
			Assert.Contains("await tx.CommitAsync();", body, StringComparison.Ordinal);

			// And the commit is the LAST thing: a commit before the lead's converted state is written
			// would be the same defect wearing a transaction.
			int commit = body.IndexOf("await tx.CommitAsync();", StringComparison.Ordinal);
			int convertedState = body.IndexOf("lead.Status = \"Converted\";", StringComparison.Ordinal);
			Assert.True(convertedState > 0 && convertedState < commit,
				"the lead's converted state must be written INSIDE the transaction it belongs to");
		}

		/// The text from a method's signature to the start of the next member.
		private static string MethodBody(string code, string signature)
		{
			int start = code.IndexOf(signature, StringComparison.Ordinal);
			Assert.True(start > 0, "method not found, or renamed without updating this test: " + signature);
			int next = code.IndexOf("\n\t\tpublic ", start + signature.Length, StringComparison.Ordinal);
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

		[Fact]
		public async Task Company_2_cannot_convert_company_1_lead()
		{
			using var host = new PlatformTestHost();
			SeedPeople(host);
			var theirs = SeedLead(host, Foreign, Emp1, "Foreign lead", "Qualified");

			var req = host.Request(Caller);
			var svc = Service(req.Db, new WideOpenAccess(Emp2));

			var (ok, _, _) = await svc.ConvertLeadToAccountAsync(Caller, theirs.ID, "u22");

			Assert.False(ok);
			Assert.Empty(await host.AllCompanies().CrmAccounts.AsNoTracking().ToListAsync());
			Assert.Equal("Qualified", (await host.AllCompanies().Leads.AsNoTracking().FirstAsync(l => l.ID == theirs.ID)).Status);
		}
	}
}
