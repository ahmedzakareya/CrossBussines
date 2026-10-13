using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Orchestration;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Crm;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// CRM BUSINESS EVENT PRODUCERS + THE CRM TASK RULE.
	//
	// WHAT THESE PROVE THAT A BUILD CANNOT. A producer compiles whether or not it is inside the
	// transaction that commits the fact, whether or not it names the row's own company, and whether or
	// not it fires twice for one thing that happened. Every one of those is a defect that only shows up
	// as a wrong row in a permanent audit log, months later, in somebody else's report.
	//
	// THE ONE RULE THAT SHAPES THE WHOLE FILE: an event is a claim that something COMMITTED. So each
	// test that asserts an event exists also asserts the CRM row is there, and each test that asserts a
	// refusal also asserts the event count is zero — because a refused mutation with an event is worse
	// than a refused mutation, and a rolled-back write with a surviving event is worse still.
	//
	// Company 41 and company 77 throughout, per the increment's own requirement, and read back through
	// an UNFILTERED context so a passing assertion cannot be the global query filter agreeing with
	// itself.
	// =================================================================================================
	public class CrmEventProducerTests
	{
		private const int Co41 = 41;
		private const int Co77 = 77;
		private const int Emp41 = 4101;
		private const int Emp77 = 7701;

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

		private sealed class RecordingLink : ICrmCustomerLink
		{
			private readonly bool _throws;
			public RecordingLink(bool throws = false) { _throws = throws; }
			public int Creates { get; private set; }
			public int NextId { get; set; } = 9001;
			public Task<int> CreateCustomerAsync(int companyId, string name, string? nameEn, string? taxNo, decimal? creditLimit)
			{
				Creates++;
				if (_throws) throw new InvalidOperationException("AR refused the customer");
				return Task.FromResult(NextId);
			}
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

		/// A recorder that fails, to prove the CRM fact goes down with it (§8's rollback property).
		private sealed class ThrowingEvents : IBusinessEventService
		{
			public int Calls { get; private set; }
			public Task<long> RecordAsync(BusinessEventRecord record, CancellationToken cancellationToken = default)
			{
				Calls++;
				throw new BusinessEventContractException("the recorder refused this event");
			}
			public BusinessEventEnvelope BuildEnvelope(BusinessEvent stored) => throw new NotSupportedException();
		}

		private static ICrmService Service(PlatformTestHost host, CrossDbContext db, int companyId,
			ICrmCustomerLink? link = null, IBusinessEventService? events = null, int? employeeId = null,
			BusinessContext? context = null)
			=> new CrmService(db, link ?? new RecordingLink(), new SilentNotifications(),
				new WideOpenAccess(employeeId ?? Emp41), null!,
				events ?? host.Events(db, context ?? PlatformTestHost.DefaultContext(companyId, employeeId ?? Emp41)));

		private static readonly DateTime Seeded = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

		private static void SeedPeople(PlatformTestHost host)
		{
			host.Seed.Employee.AddRange(
				Person(Emp41, Co41, "Company 41 person"),
				Person(Emp77, Co77, "Company 77 person"));
			host.Seed.SaveChanges();
		}

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

		/// EVERY event in the database, read with no company filter at all. A filtered read would agree
		/// with a producer that stamped the wrong company, which is the exact defect these tests exist
		/// to catch.
		private static async Task<List<BusinessEvent>> AllEventsAsync(PlatformTestHost host)
			=> await host.AllCompanies().BusinessEvents.AsNoTracking().OrderBy(e => e.EventId).ToListAsync();

		private static async Task<List<string>> EventTypesAsync(PlatformTestHost host)
			=> (await AllEventsAsync(host)).Select(e => e.EventType).ToList();

		// =============================================================================================
		// A. LEAD
		// =============================================================================================

		[Fact]
		public async Task Creating_a_lead_records_exactly_one_Created_event_against_the_lead()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (ok, err) = await svc.SaveLeadAsync(Co41, new Lead { Name = "Sara", Status = "New", EstimatedValue = 500m }, "u41");

			Assert.True(ok, err);
			var lead = await host.AllCompanies().Leads.AsNoTracking().SingleAsync();
			var ev = Assert.Single(await AllEventsAsync(host));
			Assert.Equal(LeadEvents.Created, ev.EventType);
			Assert.Equal(EntityRegistry.Lead, ev.EntityType);
			Assert.Equal(lead.ID, ev.EntityId);
			Assert.Equal(Co41, ev.CompanyID);
			Assert.Equal(LeadEventPayload.Version, ev.PayloadVersion);
			Assert.Contains("Sara", ev.Payload);
		}

		[Fact]
		public async Task Editing_a_lead_records_Updated_carrying_the_status_it_moved_from()
		{
			// The point of PreviousStatus: after the save, the status the lead LEFT exists nowhere in
			// the database. A consumer that wants "which leads were qualified this week" can only get
			// it from the event.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var lead = SeedLead(host, Co41, Emp41, "Sara", "Contacted");
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (ok, err) = await svc.SaveLeadAsync(Co41, new Lead { ID = lead.ID, Name = "Sara", Status = "Qualified" }, "u41");

			Assert.True(ok, err);
			var ev = Assert.Single(await AllEventsAsync(host));
			Assert.Equal(LeadEvents.Updated, ev.EventType);
			Assert.Contains("\"previousStatus\":\"Contacted\"", ev.Payload);
			Assert.Contains("\"status\":\"Qualified\"", ev.Payload);
			Assert.Contains("Status", ev.Payload);       // named in changedFields
			Assert.Null(ev.DedupKey);                     // an edit is repeatable
		}

		[Fact]
		public async Task A_refused_lead_save_records_no_event()
		{
			// A foreign campaign id: CRM Wave 1 refuses it. The refusal must cost nothing at all —
			// neither a lead nor an event, because an event is a claim that something committed.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var foreignCampaign = new Campaign { CompanyID = Co77, Name = "Theirs", Status = "Active", CreatedAt = Seeded };
			host.Seed.Campaigns.Add(foreignCampaign); host.Seed.SaveChanges();

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (ok, _) = await svc.SaveLeadAsync(Co41, new Lead { Name = "Sara", Status = "New", CampaignId = foreignCampaign.ID }, "u41");

			Assert.False(ok);
			Assert.Empty(await AllEventsAsync(host));
			Assert.Empty(await host.AllCompanies().Leads.AsNoTracking().ToListAsync());
		}

		[Fact]
		public async Task When_the_recorder_refuses_the_lead_is_not_written_either()
		{
			// THE ROLLBACK PROPERTY, stated as the increment states it: recorder failure -> transaction
			// fails -> CRM fact rolls back -> event 0 -> task 0. This is why the producer sits inside
			// the transaction instead of after the commit.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var recorder = new ThrowingEvents();
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41, events: recorder);

			await Assert.ThrowsAsync<BusinessEventContractException>(() =>
				svc.SaveLeadAsync(Co41, new Lead { Name = "Sara", Status = "New" }, "u41"));

			Assert.Equal(1, recorder.Calls);
			Assert.Empty(await host.AllCompanies().Leads.AsNoTracking().ToListAsync());
			Assert.Empty(await AllEventsAsync(host));
		}

		[Fact]
		public async Task Converting_a_lead_records_the_account_and_the_conversion_in_one_transaction()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var lead = SeedLead(host, Co41, Emp41, "Sara", "Qualified");
			lead.Company = "Acme"; host.Seed.SaveChanges();

			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (ok, err, accountId) = await svc.ConvertLeadToAccountAsync(Co41, lead.ID, "u41");

			Assert.True(ok, err);
			var events = await AllEventsAsync(host);
			Assert.Equal(new[] { CrmAccountEvents.Created, LeadEvents.Converted }, events.Select(e => e.EventType).ToArray());

			var account = events[0];
			Assert.Equal(accountId, account.EntityId);
			Assert.Contains($"\"convertedFromLeadId\":{lead.ID}", account.Payload);

			var converted = events[1];
			Assert.Equal(lead.ID, converted.EntityId);
			Assert.Contains($"\"crmAccountId\":{accountId}", converted.Payload);
			Assert.Contains("\"previousStatus\":\"Qualified\"", converted.Payload);
			Assert.Equal($"Lead.Converted:{lead.ID}", converted.DedupKey);
		}

		[Fact]
		public async Task A_second_conversion_attempt_records_nothing()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var lead = SeedLead(host, Co41, Emp41, "Sara", "Qualified");
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			await svc.ConvertLeadToAccountAsync(Co41, lead.ID, "u41");
			int after = (await AllEventsAsync(host)).Count;
			var second = await svc.ConvertLeadToAccountAsync(Co41, lead.ID, "u41");

			Assert.False(second.ok);
			Assert.Equal(after, (await AllEventsAsync(host)).Count);
		}

		// =============================================================================================
		// B. OPPORTUNITY — one event per save, named by what happened
		// =============================================================================================

		[Fact]
		public async Task Creating_an_opportunity_records_Created_and_not_a_stage_transition()
		{
			// A new deal's opening stage is where it STARTS. Announcing it as a transition would put a
			// move in the audit log that nobody made.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var pipe = SeedPipeline(host, Co41);
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (ok, err) = await svc.SaveOpportunityAsync(Co41, new Opportunity
			{ Title = "New deal", Stage = "Prospecting", PipelineId = pipe.pipe.ID, Amount = 900m }, "u41");

			Assert.True(ok, err);
			var ev = Assert.Single(await AllEventsAsync(host));
			Assert.Equal(OpportunityEvents.Created, ev.EventType);
			// The KEY is always serialized; what matters is that it carries nothing. There was no
			// previous stage, so claiming one would be inventing a move nobody made.
			Assert.Contains("\"previousStageName\":null", ev.Payload);
			Assert.Contains("\"previousStageId\":null", ev.Payload);
		}

		[Fact]
		public async Task Moving_a_deal_records_StageChanged_carrying_both_ends_of_the_move()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var pipe = SeedPipeline(host, Co41);
			var opp = SeedOpportunity(host, Co41, Emp41, null, "Live deal", "Prospecting", pipe.prospect.ID, pipe.pipe.ID);
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (ok, err) = await svc.UpdateOpportunityStageAsync(Co41, opp.ID, "Negotiation");

			Assert.True(ok, err);
			var ev = Assert.Single(await AllEventsAsync(host));
			Assert.Equal(OpportunityEvents.StageChanged, ev.EventType);
			Assert.Contains("\"stageName\":\"Negotiation\"", ev.Payload);
			Assert.Contains("\"previousStageName\":\"Prospecting\"", ev.Payload);
			Assert.Contains($"\"previousStageId\":{pipe.prospect.ID}", ev.Payload);

			// NOT keyed: Prospecting -> Negotiation -> Prospecting -> Negotiation is four real facts,
			// and a key built from the stage pair would silence the fourth.
			Assert.Null(ev.DedupKey);
		}

		[Fact]
		public async Task Editing_a_deal_without_moving_it_records_Updated_rather_than_a_transition()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var pipe = SeedPipeline(host, Co41);
			var opp = SeedOpportunity(host, Co41, Emp41, null, "Live deal", "Negotiation", pipe.negotiate.ID, pipe.pipe.ID);
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (ok, err) = await svc.SaveOpportunityAsync(Co41, new Opportunity
			{ ID = opp.ID, Title = "Live deal", Stage = "Negotiation", StageId = pipe.negotiate.ID, PipelineId = pipe.pipe.ID, Amount = 8000m }, "u41");

			Assert.True(ok, err);
			var ev = Assert.Single(await AllEventsAsync(host));
			Assert.Equal(OpportunityEvents.Updated, ev.EventType);
			Assert.Contains("Amount", ev.Payload);
		}

		[Fact]
		public async Task A_stage_move_that_also_edits_fields_records_ONE_event_and_it_is_the_transition()
		{
			// One committed save is one fact. Emitting Updated AND StageChanged would leave every
			// consumer to decide which of two rows about the same save to believe.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var pipe = SeedPipeline(host, Co41);
			var opp = SeedOpportunity(host, Co41, Emp41, null, "Live deal", "Prospecting", pipe.prospect.ID, pipe.pipe.ID);
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			await svc.SaveOpportunityAsync(Co41, new Opportunity
			{ ID = opp.ID, Title = "Renamed deal", Stage = "Negotiation", StageId = pipe.negotiate.ID, PipelineId = pipe.pipe.ID, Amount = 12000m }, "u41");

			var ev = Assert.Single(await AllEventsAsync(host));
			Assert.Equal(OpportunityEvents.StageChanged, ev.EventType);
		}

		[Fact]
		public async Task Winning_a_deal_records_Won_after_the_customer_link_has_succeeded()
		{
			// The ORDER is the assertion. Won says the deal is won AND billable, so the customer must
			// already be stamped when the event is written — otherwise a consumer reading the event
			// would find a deal it cannot invoice.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var account = SeedAccount(host, Co41, Emp41, "Acme");
			var opp = SeedOpportunity(host, Co41, Emp41, account.ID, "Live deal");
			var link = new RecordingLink { NextId = 9123 };
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41, link: link);

			var (ok, err) = await svc.UpdateOpportunityStageAsync(Co41, opp.ID, "Won");

			Assert.True(ok, err);
			var ev = Assert.Single(await AllEventsAsync(host));
			Assert.Equal(OpportunityEvents.Won, ev.EventType);
			Assert.Contains("\"isWon\":true", ev.Payload);
			Assert.Contains("\"customerId\":9123", ev.Payload);
			Assert.Equal($"Opportunity.Won:{opp.ID}", ev.DedupKey);
		}

		[Fact]
		public async Task A_failed_customer_link_leaves_no_Won_event_and_no_transition()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var account = SeedAccount(host, Co41, Emp41, "Acme");
			var opp = SeedOpportunity(host, Co41, Emp41, account.ID, "Live deal");
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41, link: new RecordingLink(throws: true));

			await Assert.ThrowsAsync<InvalidOperationException>(() => svc.UpdateOpportunityStageAsync(Co41, opp.ID, "Won"));

			Assert.Empty(await AllEventsAsync(host));
			Assert.Equal("Prospecting", (await host.AllCompanies().Opportunities.AsNoTracking().FirstAsync(o => o.ID == opp.ID)).Stage);
		}

		[Fact]
		public async Task Losing_a_deal_records_Lost_with_its_reason()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var pipe = SeedPipeline(host, Co41);
			var opp = SeedOpportunity(host, Co41, Emp41, null, "Live deal", "Negotiation", pipe.negotiate.ID, pipe.pipe.ID);
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (ok, err) = await svc.SaveOpportunityAsync(Co41, new Opportunity
			{
				ID = opp.ID, Title = "Live deal", Stage = "Lost", StageId = pipe.lost.ID, PipelineId = pipe.pipe.ID,
				WinLossReason = "Price",
			}, "u41");

			Assert.True(ok, err);
			var ev = Assert.Single(await AllEventsAsync(host));
			Assert.Equal(OpportunityEvents.Lost, ev.EventType);
			Assert.Contains("\"isLost\":true", ev.Payload);
			Assert.Contains("Price", ev.Payload);
		}

		[Fact]
		public async Task Re_saving_a_won_deal_to_correct_its_amount_does_not_record_a_second_Won()
		{
			// §14 exactly: a correction is not a second win. The terminal rule allows the edit, and the
			// producer must not read it as a transition.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var pipe = SeedPipeline(host, Co41);
			var account = SeedAccount(host, Co41, Emp41, "Acme");
			var opp = SeedOpportunity(host, Co41, Emp41, account.ID, "Live deal", "Prospecting", pipe.prospect.ID, pipe.pipe.ID);
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			await svc.UpdateOpportunityStageAsync(Co41, opp.ID, "Won");
			await svc.SaveOpportunityAsync(Co41, new Opportunity
			{
				ID = opp.ID, Title = "Live deal", AccountId = account.ID, Stage = "Won", StageId = pipe.won.ID,
				PipelineId = pipe.pipe.ID, Amount = 7777m, WinLossReason = "Best price",
			}, "u41");

			var types = await EventTypesAsync(host);
			Assert.Equal(new[] { OpportunityEvents.Won, OpportunityEvents.Updated }, types.ToArray());
			Assert.Single(types, t => t == OpportunityEvents.Won);
		}

		[Fact]
		public async Task A_refused_stage_move_records_no_event()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var pipe = SeedPipeline(host, Co41);
			var opp = SeedOpportunity(host, Co41, Emp41, null, "Live deal", "Negotiation", pipe.negotiate.ID, pipe.pipe.ID);
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (ok, _) = await svc.UpdateOpportunityStageAsync(Co41, opp.ID, "Proposal");   // not in this pipeline

			Assert.False(ok);
			Assert.Empty(await AllEventsAsync(host));
		}

		[Fact]
		public async Task Reopening_a_closed_deal_is_refused_and_records_no_event()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var pipe = SeedPipeline(host, Co41);
			var opp = SeedOpportunity(host, Co41, Emp41, null, "Closed deal", "Won", pipe.won.ID, pipe.pipe.ID);
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (ok, _) = await svc.UpdateOpportunityStageAsync(Co41, opp.ID, "Negotiation");

			Assert.False(ok);
			Assert.Empty(await AllEventsAsync(host));
		}

		// =============================================================================================
		// C. ACCOUNT
		// =============================================================================================

		[Fact]
		public async Task Saving_an_account_records_Created_then_Updated()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var created = await svc.SaveAccountAsync(Co41, new CrmAccount { Name = "Acme", IsActive = true }, "u41");
			Assert.True(created.ok, created.error);
			await svc.SaveAccountAsync(Co41, new CrmAccount { ID = created.id, Name = "Acme Holdings", IsActive = true }, "u41");

			var events = await AllEventsAsync(host);
			Assert.Equal(new[] { CrmAccountEvents.Created, CrmAccountEvents.Updated }, events.Select(e => e.EventType).ToArray());
			Assert.Equal($"CrmAccount.Created:{created.id}", events[0].DedupKey);
			Assert.Null(events[1].DedupKey);
			Assert.Contains("Name", events[1].Payload);
		}

		// =============================================================================================
		// D. COMPANY — 41 and 77, read unfiltered
		// =============================================================================================

		[Fact]
		public async Task An_event_carries_the_company_of_the_row_it_describes()
		{
			using var host = new PlatformTestHost(Co77);
			SeedPeople(host);
			var req = host.Request(Co77);
			var svc = Service(host, req.Db, Co77, employeeId: Emp77);

			await svc.SaveLeadAsync(Co77, new Lead { Name = "Company 77 lead", Status = "New" }, "u77");

			var ev = Assert.Single(await AllEventsAsync(host));
			Assert.Equal(Co77, ev.CompanyID);
			Assert.DoesNotContain(await AllEventsAsync(host), e => e.CompanyID == Co41);
		}

		[Fact]
		public async Task Company_41_cannot_mutate_a_company_77_lead_and_therefore_writes_no_event()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var theirs = SeedLead(host, Co77, Emp77, "Company 77 lead");
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var refused = await svc.SaveLeadAsync(Co41, new Lead { ID = theirs.ID, Name = "Hijacked", Status = "Qualified" }, "u41");
            Assert.False(refused.ok);

			Assert.Empty(await AllEventsAsync(host));
			Assert.Equal("New", (await host.AllCompanies().Leads.AsNoTracking().FirstAsync(l => l.ID == theirs.ID)).Status);
		}

		[Fact]
		public async Task A_system_context_writing_for_one_company_records_that_company_and_not_its_own()
		{
			// THE TRAP THIS TEST EXISTS FOR. CompanyIdOverride is honoured ONLY when the context is a
			// system one — so for a request, "override" and "no override" are indistinguishable and a
			// missing override would go unnoticed forever. Under a SYSTEM context they diverge: the
			// ambient company is 41 and the row belongs to 77, and the event must say 77.
			using var host = new PlatformTestHost(Co77);
			SeedPeople(host);
			var lead = SeedLead(host, Co77, Emp77, "Company 77 lead");

			var req = host.Request(Co77);
			var systemContext = new BusinessContext
			{
				CompanyId = Co41,                        // deliberately the WRONG company
				EmployeeId = null,
				UserId = "system",
				Roles = Array.Empty<string>(),
				Source = BusinessContextSource.System,
				CorrelationId = Guid.NewGuid(),
			};
			var svc = Service(host, req.Db, Co77, employeeId: Emp77, context: systemContext);

			var (ok, err) = await svc.SaveLeadAsync(Co77, new Lead { ID = lead.ID, Name = "Company 77 lead", Status = "Qualified" }, "system");

			Assert.True(ok, err);
			var ev = Assert.Single(await AllEventsAsync(host));
			Assert.Equal(Co77, ev.CompanyID);
		}

		// =============================================================================================
		// E. THE FOLLOW-UP: from a CRM activity to managed work
		// =============================================================================================

		[Fact]
		public async Task A_task_activity_with_an_owner_and_a_due_date_announces_a_follow_up()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var lead = SeedLead(host, Co41, Emp41, "Sara");
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			var (ok, err) = await svc.SaveActivityAsync(Co41, new Activity
			{
				Type = "Task", Subject = "Call Sara back", LeadId = lead.ID,
				OwnerEmployeeId = Emp41, DueDate = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
			}, "u41");

			Assert.True(ok, err);
			var ev = Assert.Single(await AllEventsAsync(host));
			Assert.Equal(LeadEvents.FollowUpScheduled, ev.EventType);
			Assert.Equal(EntityRegistry.Lead, ev.EntityType);
			Assert.Equal(lead.ID, ev.EntityId);
			Assert.Contains($"\"ownerEmployeeId\":{Emp41}", ev.Payload);
			Assert.Contains("2026-09-20", ev.Payload);
		}

		[Theory]
		[InlineData("Call")]      // an interaction, not an obligation
		[InlineData("Meeting")]
		[InlineData("Email")]
		public async Task An_interaction_is_not_work_and_announces_nothing(string type)
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var lead = SeedLead(host, Co41, Emp41, "Sara");
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			await svc.SaveActivityAsync(Co41, new Activity
			{
				Type = type, Subject = "Spoke to Sara", LeadId = lead.ID,
				OwnerEmployeeId = Emp41, DueDate = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
			}, "u41");

			Assert.Empty(await AllEventsAsync(host));
			Assert.Single(await host.AllCompanies().Activities.AsNoTracking().ToListAsync());   // the CRM record still exists
		}

		[Fact]
		public async Task A_task_activity_with_no_due_date_announces_nothing_because_there_is_no_deadline_to_carry()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var lead = SeedLead(host, Co41, Emp41, "Sara");
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			await svc.SaveActivityAsync(Co41, new Activity
			{ Type = "Task", Subject = "Someday", LeadId = lead.ID, OwnerEmployeeId = Emp41 }, "u41");

			Assert.Empty(await AllEventsAsync(host));
		}

		[Fact]
		public async Task Re_saving_an_open_follow_up_does_not_announce_a_second_one()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var lead = SeedLead(host, Co41, Emp41, "Sara");
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			await svc.SaveActivityAsync(Co41, new Activity
			{
				Type = "Task", Subject = "Call Sara back", LeadId = lead.ID,
				OwnerEmployeeId = Emp41, DueDate = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
			}, "u41");
			var activity = await host.AllCompanies().Activities.AsNoTracking().SingleAsync();

			await svc.SaveActivityAsync(Co41, new Activity
			{
				ID = activity.ID, Type = "Task", Subject = "Call Sara back (edited)", LeadId = lead.ID,
				OwnerEmployeeId = Emp41, DueDate = new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc),
			}, "u41");

			Assert.Single(await AllEventsAsync(host));
		}

		[Fact]
		public async Task Ticking_off_a_follow_up_announces_that_the_work_finished()
		{
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var opp = SeedOpportunity(host, Co41, Emp41, null, "Live deal");
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			await svc.SaveActivityAsync(Co41, new Activity
			{
				Type = "Task", Subject = "Send the proposal", OpportunityId = opp.ID,
				OwnerEmployeeId = Emp41, DueDate = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
			}, "u41");
			var activity = await host.AllCompanies().Activities.AsNoTracking().SingleAsync();

			Assert.True(await svc.ToggleActivityAsync(Co41, activity.ID));

			var types = await EventTypesAsync(host);
			Assert.Equal(new[] { OpportunityEvents.FollowUpScheduled, OpportunityEvents.FollowUpCompleted }, types.ToArray());
		}

		[Fact]
		public async Task An_activity_against_an_account_announces_nothing_because_follow_ups_are_modeled_on_leads_and_deals()
		{
			// The typed columns are the model's own statement about which records own follow-ups:
			// Activity has LeadId and OpportunityId and nothing else. An account activity reaches the
			// record only through the generic pair, so no obligation is claimed for it.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var account = SeedAccount(host, Co41, Emp41, "Acme");
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			await svc.SaveActivityAsync(Co41, new Activity
			{
				Type = "Task", Subject = "Review the account", EntityType = "Account", EntityId = account.ID,
				OwnerEmployeeId = Emp41, DueDate = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
			}, "u41");

			Assert.Empty(await AllEventsAsync(host));
		}

		// =============================================================================================
		// F. THE TASK RULE — decisions only; the service does the writing
		// =============================================================================================

		private static BusinessEventEnvelope Envelope(string entityCode, int entityId, string eventType,
			int companyId, object payload)
		{
			var json = System.Text.Json.JsonSerializer.Serialize(payload,
				new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
			return new BusinessEventEnvelope
			{
				EventUid = Guid.NewGuid(),
				EventType = eventType,
				Entity = new BusinessEventEntity { Code = entityCode, Id = entityId },
				Actor = new BusinessEventActor { EmployeeId = null },
				Context = new BusinessEventContext { CompanyId = companyId },
				OccurredAt = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc),
				PayloadVersion = CrmFollowUpEventPayload.Version,
				Visibility = BusinessEventVisibility.Internal,
				Payload = System.Text.Json.Nodes.JsonNode.Parse(json),
			};
		}

		private static object FollowUpPayload(int activityId, int? owner, DateTime? due, string name = "Sara") => new
		{
			activityId,
			subject = "Call Sara back",
			activityType = "Task",
			ownerEmployeeId = owner,
			dueDate = due,
			entityCode = EntityRegistry.Lead,
			entityId = 5,
			entityName = name,
		};

		[Fact]
		public void A_scheduled_follow_up_becomes_work_assigned_to_the_CRM_owner_with_the_CRM_deadline()
		{
			var rule = new CrmFollowUpRule();
			var due = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

			var decision = rule.Decide(Envelope(EntityRegistry.Lead, 5, LeadEvents.FollowUpScheduled, Co41,
				FollowUpPayload(activityId: 77, owner: Emp41, due: due)));

			Assert.Equal(TaskProjectionAction.Create, decision.Action);
			Assert.Equal(Emp41, decision.Intent!.AssigneeEmployeeId);
			Assert.Equal(due, decision.Intent.SourceDueDate);
			Assert.Equal(EntityRegistry.Lead, decision.Intent.EntityType);
			Assert.Equal(5, decision.Intent.EntityId);
			Assert.Equal(CrmFollowUpRule.KeyFor(EntityRegistry.Lead, 5, 77), decision.Intent.RuleKey);
			Assert.Equal(DueDatePolicy.FromSource, rule.DefaultDueDatePolicy);
		}

		[Fact]
		public void The_rule_key_is_the_ACTIVITY_so_two_follow_ups_on_one_lead_are_two_pieces_of_work()
		{
			// Keying on the lead would make the second follow-up disappear into the first one's ledger
			// row — worse than a duplicate, because it looks like success.
			var rule = new CrmFollowUpRule();
			var due = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

			var first = rule.Decide(Envelope(EntityRegistry.Lead, 5, LeadEvents.FollowUpScheduled, Co41, FollowUpPayload(77, Emp41, due)));
			var second = rule.Decide(Envelope(EntityRegistry.Lead, 5, LeadEvents.FollowUpScheduled, Co41, FollowUpPayload(78, Emp41, due)));

			Assert.NotEqual(first.Intent!.RuleKey, second.Intent!.RuleKey);
		}

		[Fact]
		public void A_completed_follow_up_closes_only_the_work_opened_for_that_same_activity()
		{
			var rule = new CrmFollowUpRule();
			var decision = rule.Decide(Envelope(EntityRegistry.Lead, 5, LeadEvents.FollowUpCompleted, Co41,
				FollowUpPayload(77, Emp41, null)));

			Assert.Equal(TaskProjectionAction.Complete, decision.Action);
			Assert.Equal(CrmFollowUpRule.KeyFor(EntityRegistry.Lead, 5, 77), decision.CompleteRuleKeyPrefix);
		}

		[Fact]
		public void A_follow_up_that_names_nobody_becomes_unassigned_rather_than_guessing_a_person()
		{
			var rule = new CrmFollowUpRule();
			var decision = rule.Decide(Envelope(EntityRegistry.Lead, 5, LeadEvents.FollowUpScheduled, Co41,
				FollowUpPayload(77, owner: null, due: new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc))));

			Assert.Equal(TaskProjectionAction.Create, decision.Action);
			Assert.Equal(0, decision.Intent!.AssigneeEmployeeId);
		}

		[Fact]
		public void A_follow_up_with_no_deadline_is_ignored_rather_than_given_an_invented_one()
		{
			var rule = new CrmFollowUpRule();
			var decision = rule.Decide(Envelope(EntityRegistry.Lead, 5, LeadEvents.FollowUpScheduled, Co41,
				FollowUpPayload(77, Emp41, due: null)));

			Assert.Equal(TaskProjectionAction.Ignore, decision.Action);
		}

		[Theory]
		[InlineData("Lead.Created")]
		[InlineData("Lead.Updated")]
		[InlineData("Lead.Converted")]
		public void Every_lead_fact_that_is_history_rather_than_an_obligation_creates_no_work(string eventType)
		{
			var rule = new CrmFollowUpRule();
			var decision = rule.Decide(Envelope(EntityRegistry.Lead, 5, eventType, Co41, new { leadName = "Sara" }));
			Assert.Equal(TaskProjectionAction.Ignore, decision.Action);
		}

		[Theory]
		[InlineData("Opportunity.Created")]
		[InlineData("Opportunity.Updated")]
		[InlineData("Opportunity.StageChanged")]
		[InlineData("Opportunity.Won")]
		[InlineData("Opportunity.Lost")]
		public void No_opportunity_lifecycle_fact_creates_or_closes_work(string eventType)
		{
			// Won is the one worth stating out loud: it does NOT mark anything Done. Nothing in CRM
			// says a won deal completes a person's follow-up, and closing an unrelated task to make a
			// mapping look complete would put a false fact in the work history. Lost likewise —
			// TASK-CANCELLED-PRODUCT-DECISION stays open rather than being papered over with Done.
			var rule = new CrmFollowUpRule();
			var decision = rule.Decide(Envelope(EntityRegistry.Opportunity, 9, eventType, Co41, new { opportunityTitle = "Deal" }));
			Assert.Equal(TaskProjectionAction.Ignore, decision.Action);
		}

		[Fact]
		public void A_follow_up_on_an_entity_this_rule_does_not_serve_is_ignored()
		{
			var rule = new CrmFollowUpRule();
			var decision = rule.Decide(Envelope(EntityRegistry.CrmAccount, 3, "CrmAccount.FollowUpScheduled", Co41,
				FollowUpPayload(77, Emp41, new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc))));
			Assert.Equal(TaskProjectionAction.Ignore, decision.Action);
		}

		[Fact]
		public void A_payload_missing_the_activity_id_is_ignored_rather_than_throwing()
		{
			// A rule that throws fails its dispatch row forever. Ignoring is the answer that lets an
			// older or malformed payload pass through without poisoning the outbox.
			var rule = new CrmFollowUpRule();
			var decision = rule.Decide(Envelope(EntityRegistry.Lead, 5, LeadEvents.FollowUpScheduled, Co41,
				new { subject = "no identity here" }));
			Assert.Equal(TaskProjectionAction.Ignore, decision.Action);
		}

		// =============================================================================================
		// G. CONTRACT GUARDS
		// =============================================================================================

		[Fact]
		public void Every_CRM_event_type_is_canonical_and_prefixed_with_a_registered_entity()
		{
			foreach (var (code, type) in new[]
					 {
						 (EntityRegistry.Lead, LeadEvents.Created),
						 (EntityRegistry.Lead, LeadEvents.Updated),
						 (EntityRegistry.Lead, LeadEvents.Converted),
						 (EntityRegistry.Lead, LeadEvents.FollowUpScheduled),
						 (EntityRegistry.Lead, LeadEvents.FollowUpCompleted),
						 (EntityRegistry.Opportunity, OpportunityEvents.Created),
						 (EntityRegistry.Opportunity, OpportunityEvents.Updated),
						 (EntityRegistry.Opportunity, OpportunityEvents.StageChanged),
						 (EntityRegistry.Opportunity, OpportunityEvents.Won),
						 (EntityRegistry.Opportunity, OpportunityEvents.Lost),
						 (EntityRegistry.Opportunity, OpportunityEvents.FollowUpScheduled),
						 (EntityRegistry.Opportunity, OpportunityEvents.FollowUpCompleted),
						 (EntityRegistry.CrmAccount, CrmAccountEvents.Created),
						 (EntityRegistry.CrmAccount, CrmAccountEvents.Updated),
					 })
			{
				Assert.True(BusinessEventTypes.TryValidate(type, code, out var error), $"{type}: {error}");
			}
		}

		[Fact]
		public void CrmService_cannot_be_constructed_without_a_recorder()
		{
			// THE DI CONTRACT IS THE GUARANTEE, and this is what that sentence means in code. There is no
			// overload, no default and no fallback that yields a CrmService with no IBusinessEventService,
			// so a caller cannot accidentally build a silent one. Asserted on the type rather than on a
			// call site, because a call site proves only that one caller got it right.
			var ctors = typeof(CrmService).GetConstructors();
			Assert.All(ctors, c =>
			{
				var p = c.GetParameters().Single(x => x.ParameterType == typeof(IBusinessEventService));
				Assert.False(p.IsOptional, "the recorder is optional again - a CrmService can go silent.");
				Assert.False(p.HasDefaultValue);
			});

			// And nothing may quietly stand in for one.
			var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
			while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
			Assert.True(dir != null, "could not locate the repository root from " + AppContext.BaseDirectory);
			var raw = System.IO.File.ReadAllText(
				System.IO.Path.Combine(dir!.FullName, "CrossBuy", "BL", "CrmService.cs"));

			// COMMENTS ARE STRIPPED FIRST, and finding out why was worth the detour: the first version of
			// this guard failed on the comment in CrmService that EXPLAINS there is no
			// NullBusinessEventService. A source guard that reads prose is not reading the code, and it
			// would have gone on failing - or worse, passing - for reasons unrelated to the contract.
			var code = string.Join(Environment.NewLine, raw.Split('\n')
				.Select(line => { int c = line.IndexOf("//", StringComparison.Ordinal); return c < 0 ? line : line[..c]; }));

			Assert.DoesNotContain("_events?.", code, StringComparison.Ordinal);
			Assert.DoesNotContain("NullBusinessEventService", code, StringComparison.Ordinal);
			Assert.DoesNotContain("GetService<IBusinessEventService>", code, StringComparison.Ordinal);

			// And the guard itself is proved to be looking at real code, not at an empty string.
			Assert.Contains("_events.RecordAsync(record)", code, StringComparison.Ordinal);
		}

		[Fact]
		public void The_timeline_tripwire_has_fired_and_is_now_the_standing_invariant()
		{
			// THIS TEST USED TO ASSERT THE OPPOSITE, and that is the point rather than an embarrassment.
			//
			// While the presenter did not know CRM, it pinned the REASON the registry flag stayed false:
			// SupportsTimeline is not one switch, and flipping it alone would have dispatched every CRM
			// event to a strict consumer that could not render it. It said, in as many words, that when
			// the presenter learned these events this test would be the tripwire announcing it.
			//
			// It has fired. TimelineEventPresenter now carries all fourteen CRM arms and the registry
			// carries the flag, landed together. So the assertion turns over rather than being deleted:
			// the same two facts are still checked, now in the direction the code actually has.
			foreach (var type in new[] { LeadEvents.Created, OpportunityEvents.Won, CrmAccountEvents.Created })
			{
				bool presented = TimelineEventPresenter.TryPresent(type, 1, "{}", out var view, out var error);
				Assert.True(presented, $"{type}: {error}");
				Assert.False(string.IsNullOrWhiteSpace(view!.TitleAr));
				Assert.False(string.IsNullOrWhiteSpace(view.TitleEn));
			}

			using var host = new PlatformTestHost();
			var registry = new EntityRegistry(host.Db);
			foreach (var code in new[] { EntityRegistry.Lead, EntityRegistry.Opportunity, EntityRegistry.CrmAccount })
				Assert.True(registry.GetDefinition(code).SupportsTimeline);
		}

		[Fact]
		public async Task The_real_container_supplies_the_recorder_so_CRM_cannot_go_quiet_unnoticed()
		{
			// THE COMPROMISE THIS GUARDED IS GONE, AND THE GUARD STAYS.
			//
			// IBusinessEventService was an OPTIONAL constructor parameter for exactly one increment,
			// because CRM Wave 1 suites construct CrmService directly and the governance then in force
			// forbade touching them. A hand-built CrmService therefore recorded nothing, and every
			// behavioural test would still have passed if the DI registration had disappeared.
			//
			// The parameter is now REQUIRED - the type system closes that hole at every call site, and
			// the three fixtures that omitted it supply a real recorder. This test is still worth its
			// place: the constructor contract proves a recorder EXISTS, and only a container proves the
			// application actually supplies one. Those are different failures.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var req = host.Request(Co41);

			var services = new ServiceCollection();
			services.AddSingleton(req.Db);
			services.AddSingleton<IEntityRegistry>(host.Registry(req.Db));
			services.AddSingleton<IBusinessContextAccessor>(
				new StubContextAccessor(PlatformTestHost.DefaultContext(Co41, Emp41)));
			services.AddSingleton<Microsoft.Extensions.Logging.ILogger<BusinessEventService>>(
				Microsoft.Extensions.Logging.Abstractions.NullLogger<BusinessEventService>.Instance);
			services.AddScoped<IBusinessEventService, BusinessEventService>();
			services.AddSingleton<ICrmCustomerLink>(new RecordingLink());
			services.AddSingleton<INotificationService>(new SilentNotifications());
			services.AddSingleton<ICrmAccessService>(new WideOpenAccess(Emp41));
			services.AddSingleton<ISellingService>(_ => null!);
			services.AddScoped<ICrmService, CrmService>();

			using var provider = services.BuildServiceProvider();
			var svc = provider.GetRequiredService<ICrmService>();

			var (ok, err) = await svc.SaveLeadAsync(Co41, new Lead { Name = "From the container", Status = "New" }, "u41");

			Assert.True(ok, err);
			var ev = Assert.Single(await AllEventsAsync(host));
			Assert.Equal(LeadEvents.Created, ev.EventType);
		}

		// =============================================================================================
		// H. END TO END — the event actually becomes work, through the platform's own service
		//
		// The rule tests above prove what the rule DECIDES. These prove what the platform DOES with the
		// decision: one task, assigned to the CRM owner, due on the CRM date, and idempotent under the
		// redelivery that an at-least-once outbox guarantees will happen.
		// =============================================================================================

		private static BusinessEventEnvelope FollowUpEnvelope(
			string entityCode, int entityId, string action, int companyId, int activityId,
			int? owner, DateTime? due, Guid? uid = null)
			=> new()
			{
				EventUid = uid ?? Guid.NewGuid(),
				EventType = entityCode + "." + action,
				Entity = new BusinessEventEntity { Code = entityCode, Id = entityId },
				Actor = new BusinessEventActor { EmployeeId = null },
				Context = new BusinessEventContext { CompanyId = companyId },
				PayloadVersion = CrmFollowUpEventPayload.Version,
				Visibility = BusinessEventVisibility.Internal,
				OccurredAt = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc),
				Payload = new System.Text.Json.Nodes.JsonObject
				{
					["activityId"] = activityId,
					["subject"] = "Call Sara back",
					["activityType"] = "Task",
					["ownerEmployeeId"] = owner,
					["dueDate"] = due,
					["entityCode"] = entityCode,
					["entityId"] = entityId,
					["entityName"] = "Sara",
				},
			};

		[Fact]
		public async Task One_follow_up_event_becomes_one_task_carrying_the_CRM_owner_and_the_CRM_deadline()
		{
			using var fx = new OrchestrationFixture(companyId: Co41, rules: new ITaskOrchestrationRule[] { new CrmFollowUpRule() });
			var owner = fx.AddEmployee(Co41, "crm-owner");
			var due = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

			var result = await fx.Orchestration.ProjectAsync(
				FollowUpEnvelope(EntityRegistry.Lead, 5, CrmFollowUpRule.ScheduledAction, Co41, 77, owner.ID, due));

			Assert.Equal(TaskProjectionAction.Create, result.Action);
			var task = Assert.Single(fx.TasksFor(Co41));
			Assert.Equal(owner.ID, task.AssigneeEmployeeId);
			Assert.Equal(due.Date, task.DueDate!.Value.Date);      // the CRM date, not now + N
			Assert.Equal(EntityRegistry.Lead, task.EntityType);
			Assert.Equal(5, task.EntityId);
			Assert.Equal(Co41, task.CompanyId);
		}

		[Fact]
		public async Task Redelivering_the_same_follow_up_event_does_not_open_a_second_task()
		{
			// The outbox is at-least-once, so this is not a hypothetical: a retry, a second worker and a
			// replayed dispatch row all arrive as the same event twice.
			using var fx = new OrchestrationFixture(companyId: Co41, rules: new ITaskOrchestrationRule[] { new CrmFollowUpRule() });
			var owner = fx.AddEmployee(Co41, "crm-owner");
			var due = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
			var uid = Guid.NewGuid();

			await fx.Orchestration.ProjectAsync(FollowUpEnvelope(EntityRegistry.Lead, 5, CrmFollowUpRule.ScheduledAction, Co41, 77, owner.ID, due, uid));
			var again = await fx.Orchestration.ProjectAsync(FollowUpEnvelope(EntityRegistry.Lead, 5, CrmFollowUpRule.ScheduledAction, Co41, 77, owner.ID, due, uid));

			Assert.True(again.AlreadyProjected);
			Assert.Single(fx.TasksFor(Co41));
			Assert.Single(fx.Ledger(Co41));
		}

		[Fact]
		public async Task Two_follow_ups_on_the_same_lead_are_two_tasks()
		{
			using var fx = new OrchestrationFixture(companyId: Co41, rules: new ITaskOrchestrationRule[] { new CrmFollowUpRule() });
			var owner = fx.AddEmployee(Co41, "crm-owner");
			var due = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

			await fx.Orchestration.ProjectAsync(FollowUpEnvelope(EntityRegistry.Lead, 5, CrmFollowUpRule.ScheduledAction, Co41, 77, owner.ID, due));
			await fx.Orchestration.ProjectAsync(FollowUpEnvelope(EntityRegistry.Lead, 5, CrmFollowUpRule.ScheduledAction, Co41, 78, owner.ID, due));

			Assert.Equal(2, fx.TasksFor(Co41).Count);
		}

		[Fact]
		public async Task Completing_a_follow_up_closes_the_task_that_follow_up_opened_and_no_other()
		{
			using var fx = new OrchestrationFixture(companyId: Co41, rules: new ITaskOrchestrationRule[] { new CrmFollowUpRule() });
			var owner = fx.AddEmployee(Co41, "crm-owner");
			var due = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

			await fx.Orchestration.ProjectAsync(FollowUpEnvelope(EntityRegistry.Lead, 5, CrmFollowUpRule.ScheduledAction, Co41, 77, owner.ID, due));
			await fx.Orchestration.ProjectAsync(FollowUpEnvelope(EntityRegistry.Lead, 5, CrmFollowUpRule.ScheduledAction, Co41, 78, owner.ID, due));

			await fx.Orchestration.ProjectAsync(FollowUpEnvelope(EntityRegistry.Lead, 5, CrmFollowUpRule.CompletedAction, Co41, 77, owner.ID, due));

			var tasks = fx.TasksFor(Co41).OrderBy(t => t.ID).ToList();
			Assert.Equal(2, tasks.Count);
			Assert.Equal(TaskStatuses.Done, tasks[0].Status);
			Assert.Equal(TaskStatuses.New, tasks[1].Status);   // the sibling follow-up is untouched
		}

		[Fact]
		public async Task An_owner_from_another_company_is_never_routed_to_and_the_task_is_created_unassigned()
		{
			// Defence in depth, and the layer that matters most: even if a payload arrived naming a
			// foreign employee, the orchestration service checks the Employee row itself and refuses.
			using var fx = new OrchestrationFixture(companyId: Co41, rules: new ITaskOrchestrationRule[] { new CrmFollowUpRule() });
			var foreigner = fx.AddEmployee(Co77, "other-tenant");
			var due = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

			await fx.Orchestration.ProjectAsync(
				FollowUpEnvelope(EntityRegistry.Lead, 5, CrmFollowUpRule.ScheduledAction, Co41, 77, foreigner.ID, due));

			var task = Assert.Single(fx.TasksFor(Co41));
			Assert.Equal(0, task.AssigneeEmployeeId);
			Assert.Equal(Co41, task.CompanyId);
			Assert.Empty(fx.TasksFor(Co77));
		}

		[Theory]
		[InlineData("Lead.Created")]
		[InlineData("Lead.Converted")]
		[InlineData("Opportunity.Won")]
		[InlineData("Opportunity.StageChanged")]
		public async Task A_CRM_fact_that_is_history_produces_no_task_at_all(string eventType)
		{
			using var fx = new OrchestrationFixture(companyId: Co41, rules: new ITaskOrchestrationRule[] { new CrmFollowUpRule() });

			var dot = eventType.IndexOf('.');
			var envelope = FollowUpEnvelope(eventType[..dot], 5, eventType[(dot + 1)..], Co41, 77, null, null);
			var result = await fx.Orchestration.ProjectAsync(envelope);

			Assert.Equal(TaskProjectionAction.Ignore, result.Action);
			Assert.Empty(fx.TasksFor(Co41));
		}


		[Fact]
		public async Task Two_follow_ups_on_the_same_lead_are_two_events_because_the_key_is_the_activity()
		{
			// THE DEDUP KEY IS THE ACTIVITY, and this is what proves it. Keyed on the LEAD instead, the
			// second follow-up would be silently deduplicated by the recorder — the event would return
			// the first one's id, no row would be written, and every caller would be told it worked.
			// A salesperson would simply never be given the second piece of work.
			using var host = new PlatformTestHost(Co41);
			SeedPeople(host);
			var lead = SeedLead(host, Co41, Emp41, "Sara");
			var req = host.Request(Co41);
			var svc = Service(host, req.Db, Co41);

			await svc.SaveActivityAsync(Co41, new Activity
			{
				Type = "Task", Subject = "Call Sara back", LeadId = lead.ID,
				OwnerEmployeeId = Emp41, DueDate = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
			}, "u41");
			await svc.SaveActivityAsync(Co41, new Activity
			{
				Type = "Task", Subject = "Send the quote", LeadId = lead.ID,
				OwnerEmployeeId = Emp41, DueDate = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc),
			}, "u41");

			var events = await AllEventsAsync(host);
			Assert.Equal(2, events.Count);
			Assert.All(events, e => Assert.Equal(LeadEvents.FollowUpScheduled, e.EventType));
			Assert.Equal(2, events.Select(e => e.DedupKey).Distinct().Count());
		}

		[Fact]
		public void A_lifecycle_event_is_ignored_even_when_it_arrives_carrying_a_follow_up_shaped_payload()
		{
			// The rule matches on the ACTION, and this pins that the action is what decides. Without it,
			// a rule that let other actions through would still look correct in every test above —
			// because those events happen to carry no activity id, so the later guard hides the defect.
			// Here the payload is a perfectly valid follow-up and only the action is wrong.
			var rule = new CrmFollowUpRule();
			var due = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

			foreach (var eventType in new[] { LeadEvents.Created, LeadEvents.Updated, LeadEvents.Converted })
			{
				var decision = rule.Decide(Envelope(EntityRegistry.Lead, 5, eventType, Co41,
					FollowUpPayload(activityId: 77, owner: Emp41, due: due)));
				Assert.Equal(TaskProjectionAction.Ignore, decision.Action);
			}

			foreach (var eventType in new[] { OpportunityEvents.Won, OpportunityEvents.Lost, OpportunityEvents.StageChanged })
			{
				var decision = rule.Decide(Envelope(EntityRegistry.Opportunity, 9, eventType, Co41,
					FollowUpPayload(activityId: 77, owner: Emp41, due: due)));
				Assert.Equal(TaskProjectionAction.Ignore, decision.Action);
			}
		}

	}
}
