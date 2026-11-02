using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Crm;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class OppRow
	{
		public int Id { get; set; }
		public string Title { get; set; } = "";
		public string? TitleEn { get; set; }
		public int? AccountId { get; set; }
		public int? CustomerId { get; set; }
		public string? CustomerName { get; set; }      // displays the account (party) name
		public int? CampaignId { get; set; }
		public string Stage { get; set; } = "";
		public decimal Amount { get; set; }
		public int Probability { get; set; }
		public DateTime? ExpectedCloseDate { get; set; }
	}
	public class PipelineStage { public string Stage { get; set; } = ""; public int Count { get; set; } public decimal Amount { get; set; } }
	public class CampaignRow
	{
		public int Id { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string? Channel { get; set; }
		public string Status { get; set; } = "";
		public DateTime? StartDate { get; set; }
		public DateTime? EndDate { get; set; }
		public decimal Budget { get; set; }
		public string? Notes { get; set; }
		public int Leads { get; set; }
		public int Opps { get; set; }
		public int Won { get; set; }
		public decimal WonValue { get; set; }
		public decimal? RoiPct => Budget > 0 ? Math.Round((WonValue - Budget) / Budget * 100, 1) : (decimal?)null;
	}

	// CRM 3-5: full ROI + funnel for one campaign.
	public class CampaignDetail
	{
		public Campaign Campaign { get; set; } = new();
		public int Members { get; set; }
		public int Responded { get; set; }       // members whose status reached Responded/Converted
		public int Converted { get; set; }       // members marked Converted
		public int Leads { get; set; }           // leads attributed to the campaign
		public int Opps { get; set; }
		public int Won { get; set; }
		public decimal WonValue { get; set; }    // revenue from won opps
		public Dictionary<string, int> ByStatus { get; set; } = new();
		public decimal Cost => Campaign.Budget;
		public decimal? RoiPct => Cost > 0 ? Math.Round((WonValue - Cost) / Cost * 100, 1) : (decimal?)null;
		public decimal? ResponseRatePct => Members > 0 ? Math.Round((decimal)Responded / Members * 100, 1) : (decimal?)null;
		public decimal? CostPerWon => Won > 0 ? Math.Round(Cost / Won, 2) : (decimal?)null;
	}
	public class MemberRow
	{
		public int Id { get; set; }
		public string EntityType { get; set; } = "";
		public int EntityId { get; set; }
		public string? MemberName { get; set; }
		public string Status { get; set; } = "";
		public DateTime? RespondedAt { get; set; }
	}
	public class ListRow { public int Id { get; set; } public string Name { get; set; } = ""; public string? NameEn { get; set; } public string? Description { get; set; } public string? DescriptionEn { get; set; } public bool IsActive { get; set; } public int Members { get; set; } }

	// CRM 3-6: a ticket row with SLA breach flags computed at read time.
	public class TicketRow
	{
		public int Id { get; set; }
		public string Subject { get; set; } = "";
		public string? SubjectEn { get; set; }
		public int? AccountId { get; set; }
		public string? AccountName { get; set; }
		public string Priority { get; set; } = "";
		public string Status { get; set; } = "";
		public int? OwnerEmployeeId { get; set; }
		public DateTime? CreatedAt { get; set; }
		public DateTime? FirstResponseDueAt { get; set; }
		public DateTime? ResolutionDueAt { get; set; }
		public DateTime? FirstRespondedAt { get; set; }
		public DateTime? ResolvedAt { get; set; }
		public DateTime? ClosedAt { get; set; }
		public bool IsClosed => Status == "Resolved" || Status == "Closed";
		// breached = past due and the target wasn't met yet (and the ticket is still open)
		public bool FrBreached => FirstResponseDueAt.HasValue && FirstRespondedAt == null && !IsClosed && DateTime.UtcNow > FirstResponseDueAt.Value;
		public bool ResBreached => ResolutionDueAt.HasValue && ResolvedAt == null && !IsClosed && DateTime.UtcNow > ResolutionDueAt.Value;
	}

	// CRM 3-8: per-rep performance line in the 360 report.
	public class OwnerPerf
	{
		public int EmployeeId { get; set; }
		public string Name { get; set; } = "";
		public int Leads { get; set; }
		public int OpenOpps { get; set; }
		public decimal OpenValue { get; set; }
		public decimal WonValue { get; set; }
	}

	// CRM 3-8: the consolidated 360° CRM report.
	public class CrmReport
	{
		public Dictionary<string, int> LeadsByStatus { get; set; } = new();
		public int LeadsHot { get; set; }
		public int LeadsWarm { get; set; }
		public int LeadsCold { get; set; }
		public List<PipelineStage> Pipeline { get; set; } = new();
		public decimal PipelineWeighted { get; set; }
		public int OppOpen { get; set; }
		public int OppWon { get; set; }
		public int OppLost { get; set; }
		public decimal WonValue { get; set; }
		public decimal? WinRatePct { get; set; }
		public int ActOpen { get; set; }
		public int ActDone { get; set; }
		public int ActOverdue { get; set; }
		public int TicketsOpen { get; set; }
		public int TicketsBreached { get; set; }
		public int CampaignCount { get; set; }
		public decimal CampaignBudget { get; set; }
		public decimal CampaignWon { get; set; }
		public decimal? CampaignRoiPct { get; set; }
		public List<OwnerPerf> Owners { get; set; } = new();
	}

	// CRM 3-7: one forecast bucket (a month) — weighted pipeline vs won.
	public class ForecastRow
	{
		public string Month { get; set; } = "";        // yyyy-MM
		public int OpenCount { get; set; }
		public decimal OpenAmount { get; set; }
		public decimal WeightedAmount { get; set; }     // Σ amount × probability/100 (open opps)
		public decimal WonAmount { get; set; }
	}

	public class AccountRow
	{
		public int Id { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string? Industry { get; set; }
		public string? IndustryEn { get; set; }
		public string? Segment { get; set; }
		public string? Phone { get; set; }
		public int? CustomerId { get; set; }
		public bool IsActive { get; set; }
		public int Contacts { get; set; }
		public int Opps { get; set; }
	}

	public interface ICrmService
	{
		Task<(List<Lead> rows, int total)> SearchLeadsAsync(int companyId, string? q, string? status, int page, int pageSize,
			int[]? tagIds = null, bool tagMatchAll = false, bool unassignedOnly = false, bool overdueFollowUpOnly = false);
		Task<Lead?> GetLeadAsync(int companyId, int id);
		/// <param name="tagIds">
		/// The tag set this save OWNS, or null when the caller manages no tags. Null and empty are
		/// different answers on purpose: an emptied multi-select posts no key at all, so without the
		/// distinction "cleared every tag" would be indistinguishable from "did not touch tags", and
		/// clearing the last chip would silently keep them. The controller turns the form's sentinel
		/// into an empty array; here, empty means REMOVE THEM ALL.
		/// </param>
		Task<(bool ok, string? error)> SaveLeadAsync(int companyId, Lead dto, string? userId, int[]? tagIds = null);
		Task<(bool ok, string? error, int accountId)> ConvertLeadToAccountAsync(int companyId, int leadId, string? userId);

		// CRM 3-2: accounts (360 party) + contacts
		Task<(List<AccountRow> rows, int total)> SearchAccountsAsync(int companyId, string? q, int page, int pageSize,
			int[]? tagIds = null, bool tagMatchAll = false, bool unassignedOnly = false);
		Task<CrmAccount?> GetAccountAsync(int companyId, int id);
		/// <param name="tagIds">
		/// The tag set this save OWNS, or null when the caller manages no tags. Null and empty are
		/// different answers on purpose: an emptied multi-select posts no key at all, so without the
		/// distinction "cleared every tag" would be indistinguishable from "did not touch tags", and
		/// clearing the last chip would silently keep them. The controller turns the form's sentinel
		/// into an empty array; here, empty means REMOVE THEM ALL.
		/// </param>
		Task<(bool ok, string? error, int id)> SaveAccountAsync(int companyId, CrmAccount dto, string? userId, int[]? tagIds = null);
		Task<List<(int id, string name)>> GetAccountsForPickAsync(int companyId, string? term);
		Task<(bool ok, string? error)> SaveContactAsync(int companyId, CrmContact dto);
		Task<bool> DeleteContactAsync(int companyId, int id);

		Task<(List<OppRow> rows, int total)> SearchOpportunitiesAsync(int companyId, string? q, string? stage, int page, int pageSize,
			int[]? tagIds = null, bool tagMatchAll = false, bool unassignedOnly = false, bool overdueFollowUpOnly = false);
		Task<List<PipelineStage>> PipelineSummaryAsync(int companyId);
		Task<Opportunity?> GetOpportunityAsync(int companyId, int id);
		/// <param name="tagIds">
		/// The tag set this save OWNS, or null when the caller manages no tags. Null and empty are
		/// different answers on purpose: an emptied multi-select posts no key at all, so without the
		/// distinction "cleared every tag" would be indistinguishable from "did not touch tags", and
		/// clearing the last chip would silently keep them. The controller turns the form's sentinel
		/// into an empty array; here, empty means REMOVE THEM ALL.
		/// </param>
		Task<(bool ok, string? error)> SaveOpportunityAsync(int companyId, Opportunity dto, string? userId, int[]? tagIds = null);
		Task<List<OppRow>> PipelineBoardAsync(int companyId);
		Task<(bool ok, string? error)> UpdateOpportunityStageAsync(int companyId, int id, string stage);

		// CRM 3-3: configurable pipelines + stages
		Task<int> GetDefaultPipelineIdAsync(int companyId);
		Task<List<CrmPipelineStage>> GetStagesAsync(int companyId, int pipelineId);
		Task<List<CrmPipeline>> GetPipelinesAsync(int companyId);
		Task<CrmPipeline?> GetPipelineWithStagesAsync(int companyId, int id);
		Task<(bool ok, string? error, int id)> SavePipelineAsync(int companyId, CrmPipeline dto, List<CrmPipelineStage> stages);

		// CRM 3-3b: opportunity products + O2C
		Task<List<OpportunityProduct>> GetOpportunityProductsAsync(int companyId, int oppId);
		Task<(bool ok, string? error)> SaveOpportunityProductsAsync(int companyId, int oppId, List<OpportunityProduct> lines);
		Task<(bool ok, string? error, int? quotationId)> ConvertOpportunityToQuotationAsync(int companyId, int oppId, string? userId);

		Task<(List<CampaignRow> rows, int total)> SearchCampaignsAsync(int companyId, string? q, string? status, int page, int pageSize);
		Task<List<(int id, string name)>> GetCampaignsForPickAsync(int companyId);
		Task<Campaign?> GetCampaignAsync(int companyId, int id);
		Task<(bool ok, string? error)> SaveCampaignAsync(int companyId, Campaign dto, string? userId);
		// CRM 3-5: campaign members (audience + response funnel) + ROI detail.
		Task<CampaignDetail?> GetCampaignDetailAsync(int companyId, int campaignId);
		Task<List<MemberRow>> GetCampaignMembersAsync(int companyId, int campaignId);
		Task<int> AddCampaignMembersAsync(int companyId, int campaignId, IEnumerable<(string entityType, int entityId, string? name)> members);
		Task<bool> UpdateMemberStatusAsync(int companyId, int memberId, string status);
		Task<bool> RemoveCampaignMemberAsync(int companyId, int memberId);
		Task<int> AddListToCampaignAsync(int companyId, int campaignId, int listId);
		// CRM 3-5: reusable marketing lists.
		Task<(List<ListRow> rows, int total)> SearchListsAsync(int companyId, string? q, int page, int pageSize);
		Task<List<(int id, string name)>> GetListsForPickAsync(int companyId);
		Task<CrmMarketingList?> GetListAsync(int companyId, int id);
		Task<(bool ok, string? error, int id)> SaveListAsync(int companyId, CrmMarketingList dto, string? userId);
		Task<int> AddListMembersAsync(int companyId, int listId, IEnumerable<(string entityType, int entityId, string? name)> members);
		Task<bool> RemoveListMemberAsync(int companyId, int memberId);
		Task<List<(int id, string name)>> PickEntitiesAsync(int companyId, string entityType, string? term);
		// CRM 3-6: SLA policies + service tickets.
		Task<List<CrmSlaPolicy>> GetSlaPoliciesAsync(int companyId);
		Task<CrmSlaPolicy?> GetSlaPolicyAsync(int companyId, int id);
		Task<(bool ok, string? error)> SaveSlaPolicyAsync(int companyId, CrmSlaPolicy dto);
		Task<(List<TicketRow> rows, int total)> SearchTicketsAsync(int companyId, string? q, string? status, string? priority, int page, int pageSize);
		Task<CrmTicket?> GetTicketAsync(int companyId, int id);
		Task<(bool ok, string? error, int id)> SaveTicketAsync(int companyId, CrmTicket dto, string? userId);
		Task<bool> UpdateTicketStatusAsync(int companyId, int id, string status);
		Task<(int open, int breachedFr, int breachedRes)> TicketStatsAsync(int companyId);
		// CRM 3-7: scoring rules + CRM settings + recompute + forecast.
		Task<List<CrmScoringRule>> GetScoringRulesAsync(int companyId);
		Task<(bool ok, string? error)> SaveScoringRuleAsync(int companyId, CrmScoringRule dto);
		Task<CrmSettings> GetCrmSettingsAsync(int companyId);
		Task<(bool ok, string? error)> SaveCrmSettingsAsync(int companyId, CrmSettings dto);
		Task<int> RecomputeAllLeadScoresAsync(int companyId);
		Task<List<ForecastRow>> GetForecastAsync(int companyId);
		// CRM 3-8: consolidated 360° report.
		Task<CrmReport> GetCrmReportAsync(int companyId);

		Task<(List<Activity> rows, int total)> SearchActivitiesAsync(int companyId, string? q, bool? done, int page, int pageSize);
		Task<(bool ok, string? error)> SaveActivityAsync(int companyId, Activity dto, string? userId);
		Task<bool> ToggleActivityAsync(int companyId, int id);
		// CRM 3-4: timeline of any entity + due-reminder dispatch (used by CrmReminderHostedService).
		Task<List<Activity>> GetTimelineAsync(int companyId, string entityType, int entityId);
		Task<List<Activity>> GetDueRemindersAsync(int companyId, DateTime nowUtc);
		Task MarkRemindedAsync(int companyId, IEnumerable<int> ids);
	}

	public class CrmService : ICrmService
	{
		private readonly CrossDbContext _context;
		private readonly ICrmCustomerLink _link;
		private readonly INotificationService _notify;
		private readonly ICrmAccessService _access;
		private readonly ISellingService _selling;

		// =============================================================================================
		// THE EVENT RECORDER, AND THE ONE TEST SEAM IN THIS FILE.
		//
		// Every other producer in this codebase takes IBusinessEventService as a REQUIRED constructor
		// parameter, and that is the better shape. It is optional here for a governance reason rather
		// than a design one: CRM Wave 1's suites construct CrmService directly, and SHF-65 forbids this
		// increment from touching them — their assertions are the proof that CRM behaviour did not
		// move. A required sixth parameter would force every one of those call sites to change.
		//
		// THE RISK IS REAL AND IT IS GUARDED, NOT HOPED AWAY: if the DI registration ever stopped
		// supplying IBusinessEventService, CRM would go quiet and no behavioural test would notice.
		// So CrmEventProducerTests resolves ICrmService FROM THE REAL CONTAINER and proves an event
		// lands. Nothing in production constructs this service by hand.
		//
		// Note what this is NOT: it is not a swallowed failure. When a recorder is present its
		// exceptions propagate and roll the CRM transaction back, which is the whole point of
		// ADR-001. The only thing a null recorder skips is the recording itself.
		// =============================================================================================
		private readonly Platform.IBusinessEventService _events;

		// =============================================================================================
		// THE TAG AUTHORITY, AND WHY IT IS COMPOSED RATHER THAN INJECTED.
		//
		// This is the PLATFORM TagService — the same class Tasks uses, over the same tables, reading the
		// same company vocabulary. It is composed here for the reason TaskService composes it: a
		// required constructor parameter would force every hand-built CrmService in the CRM Wave 1
		// suites to change, and those suites are the evidence that this increment moved no CRM
		// behaviour. Composition keeps the field non-null on every path, so tag work can never be
		// silently skipped the way an optional collaborator can be.
		//
		// It is NOT a CRM tag service. Nothing here writes TagAssignments, defines a tag, or holds a
		// second vocabulary: every tag operation below goes through this one interface.
		private readonly Platform.ITagService _tags;

		/// The project's clock, wired for ONE concern: deciding whether a follow-up is overdue. Every
		/// other date in this file is untouched.
		private readonly TimeProvider _clock;

		// IBusinessEventService IS REQUIRED, and the DI contract is the guarantee.
		//
		// It was optional for exactly one increment, and only because two hand-built test suites could
		// not be edited under the governance in force at the time. That compromise had a real cost: a
		// CrmService built without a recorder wrote CRM rows and published nothing, silently, and no
		// test could tell the difference. There is deliberately NO NullBusinessEventService, no service
		// locator lookup and no TryGetService fallback here - each of those would restore the silence
		// under a different name. A caller that cannot supply a recorder cannot construct this service.
		public CrmService(CrossDbContext context, ICrmCustomerLink link, INotificationService notify,
			ICrmAccessService access, ISellingService selling, Platform.IBusinessEventService events,
			TimeProvider? clock = null)
		{
			_context = context; _link = link; _notify = notify; _access = access; _selling = selling;
			_events = events ?? throw new ArgumentNullException(nameof(events));
			_tags = new Platform.TagService(context);
			_clock = clock ?? TimeProvider.System;
		}

		// No null-conditional. Every authoritative CRM write path reaches a real recorder, or the object
		// could not have been constructed.
		private Task RecordAsync(Models.Platform.BusinessEventRecord record) => _events.RecordAsync(record);

		private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

		private static int Clamp(int page, ref int pageSize) { if (page < 1) page = 1; if (pageSize < 1) pageSize = 25; else if (pageSize > 100000) pageSize = 100000; return page; }

		// CRM 3-1 data scope: owner-ids the current user may see (null = unrestricted)
		private async Task<List<int>?> ScopeAsync() => (await _access.VisibleOwnerIdsAsync())?.ToList();

		// The LIST rule, applied to one row.
		//
		// Every scoped list here narrows to `OwnerEmployeeId == null || scope.Contains(owner)`. The
		// single-record reads beside them checked only the company, so a rep who could not SEE a record in
		// their own list could still READ it by putting its id in the query string — four editor screens
		// take an id straight off the URL. Hiding a row from a list while serving it by id is not a
		// narrower kind of access, it is the same access behind a worse door.
		//
		// Three behaviours are preserved deliberately, because they are the list's behaviours:
		//   * scope == null is UNRESTRICTED — manager-with-team, marketing, viewer, and the unconfigured
		//     bootstrap case. Manager and system authority keep working exactly as policy already says.
		//   * an UNOWNED row (null owner) stays visible to everyone, as it is in every list predicate.
		//   * a hidden row is reported as ABSENT, never as forbidden, so the id cannot be probed for
		//     existence — the same reason a foreign company's id is answered like a missing one.
		private async Task<bool> OwnerVisibleAsync(int? ownerEmployeeId)
		{
			var scope = await ScopeAsync();
			if (scope == null) return true;
			if (ownerEmployeeId is not int owner) return true;
			return scope.Contains(owner);
		}
		private int? Me() => _access.CurrentEmployeeId();

		// =============================================================================================
		// WAVE 1 — REFERENCE INTEGRITY ACROSS COMPANIES.
		//
		// Every CRM save takes its foreign keys straight off a browser POST: SaveLead carries a
		// campaignId, SaveOpportunity an accountId and a pipelineId, SaveActivity a leadId /
		// opportunityId / customerId, SaveContact an accountId. The ROW is stamped with the resolved
		// company, so CompanyWriteGuardInterceptor and the global query filter are both satisfied —
		// and neither of them looks at where the id POINTS. Company 41 could file its opportunity
		// under company 77's account, and every later join would then read one tenant's pipeline
		// through another tenant's party.
		//
		// The id is verified in the CALLER's company or the save is REFUSED. Refusing is the only
		// honest answer: silently dropping the reference would store a record the user believes is
		// linked, and silently keeping it is the leak itself.
		//
		// The message never says whose company the id belongs to — that would confirm the row exists
		// somewhere, which is the same probe the owner-scope rule above is careful not to answer.
		// =============================================================================================
		private const string ForeignReference = "مرجع غير موجود في هذه الشركة";
		// A record hidden by owner scope must be answered EXACTLY as a record that does not exist —
		// same exception, same message. Any difference is an existence oracle over a colleague's book.
		private const string HiddenRecord = "غير موجود";

		private static async Task<bool> RefOkAsync(int? id, Func<int, Task<bool>> existsInCompany)
			=> !id.HasValue || id.Value <= 0 || await existsInCompany(id.Value);

		private Task<bool> LeadInCompanyAsync(int companyId, int id) =>
			_context.Leads.AsNoTracking().AnyAsync(l => l.CompanyID == companyId && l.ID == id);
		private Task<bool> AccountInCompanyAsync(int companyId, int id) =>
			_context.CrmAccounts.AsNoTracking().AnyAsync(a => a.CompanyID == companyId && a.ID == id);
		private Task<bool> OpportunityInCompanyAsync(int companyId, int id) =>
			_context.Opportunities.AsNoTracking().AnyAsync(o => o.CompanyID == companyId && o.ID == id);
		private Task<bool> CampaignInCompanyAsync(int companyId, int id) =>
			_context.Campaigns.AsNoTracking().AnyAsync(c => c.CompanyID == companyId && c.ID == id);
		private Task<bool> CustomerInCompanyAsync(int companyId, int id) =>
			_context.Customers.AsNoTracking().AnyAsync(c => c.CompanyID == companyId && c.ID == id);
		private Task<bool> PipelineInCompanyAsync(int companyId, int id) =>
			_context.CrmPipelines.AsNoTracking().AnyAsync(p => p.CompanyID == companyId && p.ID == id);

		// The polymorphic pair an Activity or a campaign member carries. An unknown type is not an
		// error — Customer and Contact timelines are legitimate — but a KNOWN type with a foreign id is.
		private async Task<bool> EntityRefOkAsync(int companyId, string? entityType, int? entityId)
		{
			if (string.IsNullOrWhiteSpace(entityType) || entityId is not int id || id <= 0) return true;
			return entityType.Trim() switch
			{
				"Lead" => await LeadInCompanyAsync(companyId, id),
				"Opportunity" => await OpportunityInCompanyAsync(companyId, id),
				"Account" => await AccountInCompanyAsync(companyId, id),
				"Customer" => await CustomerInCompanyAsync(companyId, id),
				"Contact" => await _context.CrmContacts.AsNoTracking().AnyAsync(c => c.CompanyID == companyId && c.ID == id),
				_ => true,
			};
		}

		// =============================================================================================
		// BUSINESS EVENT PRODUCERS.
		//
		// Each one is recorded INSIDE the transaction that writes the CRM row and BEFORE it commits,
		// which is not a style choice: IBusinessEventService refuses to record without an ambient
		// transaction precisely so that a fact and its event share one fate. Three write paths had no
		// transaction of their own (lead, account and activity saves were a single SaveChanges), so
		// they open one now — the transaction the producer requires, and nothing else about them
		// changed.
		//
		// COMPANY. Every record carries CompanyIdOverride from the ROW, never from the ambient
		// context. The kernel honours the override only for system callers, so for a request the two
		// agree by construction and for a worker the entity's own company wins — which is the case
		// that matters, because a background caller's ambient company is whatever company it is
		// iterating, not necessarily the one that owns the row it just wrote.
		//
		// VISIBILITY is Internal for every CRM event: these are ordinary commercial facts that anyone
		// who may view the record may read. Amounts appear on opportunity events, which is the one
		// judgement call here — a deal's value is visible on the CRM screens to everybody who can
		// open the deal, so the event is not more revealing than the record it describes.
		// =============================================================================================

		/// The outcome of a REFUSED transition: nothing moved, so nothing is announced.
		private static readonly StageOutcome NoStageChange = new();

		private static Models.Platform.OpportunityEventPayload PayloadFor(
			Opportunity e, StageOutcome outcome, string[]? changed) => new()
			{
				OpportunityTitle = e.Title,
				CrmAccountId = e.AccountId,
				LeadId = e.LeadId,
				OwnerEmployeeId = e.OwnerEmployeeId,
				PipelineId = e.PipelineId,
				StageId = e.StageId,
				StageName = e.Stage,
				PreviousStageId = outcome.Moved ? outcome.PreviousStageId : null,
				PreviousStageName = outcome.Moved ? outcome.PreviousStageName : null,
				Probability = e.Probability,
				Amount = e.Amount,
				ExpectedCloseDate = e.ExpectedCloseDate,
				IsWon = outcome.BecameWon ? true : null,
				IsLost = outcome.BecameLost ? true : null,
				WinLossReason = outcome.BecameWon || outcome.BecameLost ? e.WinLossReason : null,
				CustomerId = outcome.BecameWon ? e.CustomerId : null,
				ChangedFields = changed,
			};

		/// The stage transition, named by its outcome. Called from BOTH stage writers, so the board
		/// and the editor cannot disagree about what a move is called — the same reason ApplyStageAsync
		/// exists at all.
		private Task RecordStageEventAsync(Opportunity e, StageOutcome outcome)
		{
			string type = outcome.BecameWon ? Platform.OpportunityEvents.Won
				: outcome.BecameLost ? Platform.OpportunityEvents.Lost
				: Platform.OpportunityEvents.StageChanged;

			// WON AND LOST ARE KEYED; A PLAIN MOVE IS NOT. Terminal states are reached once — the
			// stage authority refuses to leave them — so a key on the deal is exactly right and makes
			// a redelivered transition harmless. An ordinary move is legitimately repeatable, and
			// Prospecting -> Negotiation -> Prospecting -> Negotiation is four real facts: any key
			// built from the stage pair would silence the fourth.
			string? key = outcome.BecameWon ? $"Opportunity.Won:{e.ID}"
				: outcome.BecameLost ? $"Opportunity.Lost:{e.ID}"
				: null;

			return RecordAsync(new Models.Platform.BusinessEventRecord
			{
				EntityCode = Platform.EntityRegistry.Opportunity,
				EntityId = e.ID,
				EventType = type,
				PayloadVersion = Models.Platform.OpportunityEventPayload.Version,
				Visibility = Models.Platform.BusinessEventVisibility.Internal,
				DedupKey = key,
				CompanyIdOverride = e.CompanyID,
				Payload = PayloadFor(e, outcome, changed: null),
			});
		}

		// =============================================================================================
		// THE FOLLOW-UP: the one CRM fact that is WORK rather than history.
		//
		// A CRM Activity of type "Task", with an owner and a due date, is the only place this model
		// says THIS PERSON must do THIS by THIS DATE. Everything else CRM records is something that
		// already happened. So that fact — and only that fact — is announced as a follow-up event, and
		// the orchestration rule turns it into managed work.
		//
		// A call or a meeting is NOT converted: those are interactions, and CRM Activity remains their
		// home. That boundary is CRM-ACTIVITY-VS-PLATFORM-TASK-AUTHORITY, and this increment narrows
		// it rather than resolving it.
		//
		// WHICH RECORD THE EVENT IS ABOUT. The typed columns are the model's own statement about which
		// entities own follow-ups: Activity has LeadId and OpportunityId and nothing else. An activity
		// against an account, a customer or a contact reaches those only through the generic
		// EntityType/EntityId pair, and produces no follow-up event here.
		// =============================================================================================
		private const string WorkActivityType = "Task";

		private static (string? code, int? id) FollowUpTarget(Activity a)
		{
			if (a.OpportunityId is int oid && oid > 0) return (Platform.EntityRegistry.Opportunity, oid);
			if (a.LeadId is int lid && lid > 0) return (Platform.EntityRegistry.Lead, lid);
			// The polymorphic pair, but only for the two entities the typed columns already name.
			if (a.EntityId is int eid && eid > 0)
			{
				if (string.Equals(a.EntityType, "Opportunity", StringComparison.Ordinal)) return (Platform.EntityRegistry.Opportunity, eid);
				if (string.Equals(a.EntityType, "Lead", StringComparison.Ordinal)) return (Platform.EntityRegistry.Lead, eid);
			}
			return (null, null);
		}

		/// Assigned work with a deadline, still open. All four conditions matter: without an owner
		/// there is nobody to assign, without a due date the task would need an invented one, a
		/// non-"Task" activity is an interaction, and a done activity is not outstanding work.
		private static bool IsOutstandingWork(Activity a)
			=> string.Equals(a.Type, WorkActivityType, StringComparison.Ordinal)
			   && a.DueDate.HasValue
			   && a.OwnerEmployeeId is > 0
			   && !a.Done;

		private async Task RecordFollowUpAsync(Activity a, bool completed)
		{
			var (code, id) = FollowUpTarget(a);
			if (code == null || id == null) return;

			string name = code == Platform.EntityRegistry.Opportunity
				? await _context.Opportunities.AsNoTracking().Where(o => o.CompanyID == a.CompanyID && o.ID == id).Select(o => o.Title).FirstOrDefaultAsync() ?? ""
				: await _context.Leads.AsNoTracking().Where(l => l.CompanyID == a.CompanyID && l.ID == id).Select(l => l.Name).FirstOrDefaultAsync() ?? "";

			string action = completed ? "FollowUpCompleted" : "FollowUpScheduled";
			await RecordAsync(new Models.Platform.BusinessEventRecord
			{
				EntityCode = code,
				EntityId = id.Value,
				EventType = Platform.BusinessEventTypes.Build(code, action),
				PayloadVersion = Models.Platform.CrmFollowUpEventPayload.Version,
				Visibility = Models.Platform.BusinessEventVisibility.Internal,
				// Keyed on the ACTIVITY, not the record it is about: a lead may carry many follow-ups,
				// and keying on the lead would make the second one disappear.
				DedupKey = $"{code}.{action}:{a.ID}",
				CompanyIdOverride = a.CompanyID,
				Payload = new Models.Platform.CrmFollowUpEventPayload
				{
					ActivityId = a.ID,
					Subject = a.Subject,
					SubjectEn = a.SubjectEn,
					ActivityType = a.Type,
					OwnerEmployeeId = a.OwnerEmployeeId,
					DueDate = a.DueDate,
					EntityCode = code,
					EntityId = id,
					EntityName = name,
				},
			});
		}

		// =============================================================================================
		// TAGS AND THE TWO OPERATIONAL FILTERS.
		//
		// THE ORDER IS THE SECURITY. Every list below applies its predicates in one order, and the
		// order is not a style note: company scope, then the CRM owner scope, then the screen's own
		// filters, and only THEN the tag / unassigned / overdue predicates. A tag filter may only
		// narrow a set the caller was already allowed to see. Matching tags FIRST and intersecting
		// afterwards is exactly how a tag would become a way to discover a colleague's lead.
		//
		// THE COUNT AND THE LIST SHARE ONE QUERY, so a filtered page can never be counted against an
		// unfiltered total. That is not a habit here - it is a defect this codebase has already had.
		// =============================================================================================

		/// Tag matching, expressed through the tag AUTHORITY instead of its tables.
		///
		/// WHY THIS COSTS AN EXTRA ROUND TRIP, stated rather than hidden: ITagService exposes no
		/// "which entities carry these tags" query, and reading the TagAssignments DbSet directly is
		/// forbidden - one vocabulary, one authority. So the ALREADY-SCOPED, already-filtered ids are
		/// projected (ints, not rows), handed to the authority in ONE batch call, and the surviving
		/// ids narrow the query the database then counts and pages.
		///
		/// The scoped set being the INPUT is what makes this safe: an id the caller may not see never
		/// enters the batch, so no tag can bring it back. Recorded as
		/// CRM-TAG-FILTER-ID-MATERIALIZATION-DEBT - the fix is an entity-id query on ITagService, and
		/// that interface belongs to whoever owns it, not to this increment.
		private async Task<List<int>?> TagMatchedIdsAsync(
			int companyId, string entityCode, IQueryable<int> scopedIds, int[]? tagIds, bool matchAll)
		{
			var wanted = (tagIds ?? Array.Empty<int>()).Where(id => id > 0).Distinct().ToArray();
			if (wanted.Length == 0) return null;   // no tags chosen is NO restriction, not an empty result

			var ids = await scopedIds.ToListAsync();
			if (ids.Count == 0) return new List<int>();

			var byEntity = await _tags.GetTagsForEntitiesAsync(companyId, entityCode, ids);
			var keep = new List<int>();
			foreach (var pair in byEntity)
			{
				var have = pair.Value.Select(t => t.ID).ToHashSet();
				// ANY: at least one chosen tag. ALL: every one of them. There is no third mode.
				bool hit = matchAll ? wanted.All(have.Contains) : wanted.Any(have.Contains);
				if (hit) keep.Add(pair.Key);
			}
			return keep;
		}

		// Overdue follow-up is evaluated as an EXISTS the database runs - never a query per row, and
		// never an activity list pulled into memory.
		//
		// The definition is the work contract the events increment established: an activity of type
		// "Task", not done, with a due date STRICTLY in the past, in this company, related to this
		// record. Strictly, because a follow-up due at this instant is due rather than late - the same
		// comparison TaskStatuses.IsOverdue and the CRM dashboard already make.
		private const string WorkActivityTypeName = "Task";

		private DateTime NowUtc() => _clock.GetUtcNow().UtcDateTime;

		/// Tag replacement, inside the caller's transaction and through the platform authority.
		///
		/// It runs BEFORE the commit for the same reason the Business Event does: a refused tag - one
		/// belonging to another company, or one that does not exist - must take the whole save down
		/// with it. A lead that saved while its tags did not is a record the user believes is
		/// classified and that no filter will ever find.
		///
		/// null means THIS CALLER MANAGES NO TAGS, so the existing set is left alone. Empty means the
		/// form owned the tag set and the user cleared it.
		private async Task<(bool ok, string? error)> ApplyTagsAsync(int companyId, string entityCode, int entityId, int[]? tagIds)
		{
			if (tagIds == null) return (true, null);
			return await _tags.SetEntityTagsAsync(companyId, entityCode, entityId, tagIds, Me());
		}


		private static string[]? Changed(params (string field, bool moved)[] candidates)
		{
			var list = candidates.Where(c => c.moved).Select(c => c.field).ToArray();
			return list.Length == 0 ? null : list;
		}

		// =============================================================================================
		// WAVE 1 — ONE STAGE AUTHORITY.
		//
		// Two methods moved an opportunity through its pipeline: UpdateOpportunityStageAsync (the
		// Kanban board) and SaveOpportunityAsync (the editor). Each computed "did this become Won?"
		// from its own snapshot, and each accepted whatever stage arrived. When the requested stage did
		// not resolve, ResolveStageAsync answered with the FIRST stage of the pipeline — so a typo
		// moved a negotiation back to prospecting and reported success.
		//
		// Both paths now go through ApplyStageAsync. It is the only place that decides a stage, a
		// probability, and whether the Won side effects run.
		// =============================================================================================
		private const string InvalidStage = "مرحلة غير صحيحة";
		private const string TerminalOpportunity = "لا يمكن تغيير مرحلة فرصة مغلقة";

		// A stage the pipeline does not contain is REFUSED, not rounded to the nearest one. An empty
		// request still means "start at the beginning", which is what a new opportunity needs.
		private async Task<(CrmPipelineStage? stage, bool unknown)> ResolveStageStrictAsync(
			int companyId, int pipelineId, string? stageName, int? stageId)
		{
			if (pipelineId <= 0) return (null, false);
			var q = _context.CrmPipelineStages.AsNoTracking().Where(s => s.CompanyID == companyId && s.PipelineId == pipelineId);
			if (stageId.HasValue && stageId.Value > 0)
			{
				var byId = await q.FirstOrDefaultAsync(s => s.ID == stageId.Value);
				return (byId, byId == null);
			}
			if (!string.IsNullOrWhiteSpace(stageName))
			{
				var byName = await q.FirstOrDefaultAsync(s => s.Name == stageName);
				return (byName, byName == null);
			}
			return (await q.OrderBy(s => s.Sort).FirstOrDefaultAsync(), false);
		}

		// Won and Lost are where a deal STOPS. The pipeline says which stages those are; without a
		// configured pipeline the legacy vocabulary does.
		private async Task<bool> IsTerminalAsync(int companyId, Opportunity o)
		{
			if (o.StageId is int sid && sid > 0)
			{
				var st = await _context.CrmPipelineStages.AsNoTracking()
					.FirstOrDefaultAsync(s => s.CompanyID == companyId && s.ID == sid);
				if (st != null) return st.IsWon || st.IsLost;
			}
			return o.Stage == "Won" || o.Stage == "Lost";
		}

		// The single transition. Returns becameWon so the caller runs the Won side effects EXACTLY
		// once, inside the same transaction that made the stage true.
		/// WHAT A TRANSITION ACTUALLY WAS, so the producer beside it can name the fact instead of
		/// guessing at it. Nothing here decides anything the previous version did not decide: Moved,
		/// PreviousStageId and PreviousStageName are values this method already computed or held, and
		/// BecameLost is the mirror of a BecameWon that was already here. The stage rules themselves
		/// are byte-for-byte the rules CRM Wave 1 qualified.
		private sealed class StageOutcome
		{
			public bool Moved { get; init; }
			public bool BecameWon { get; init; }
			public bool BecameLost { get; init; }
			public int? PreviousStageId { get; init; }
			public string? PreviousStageName { get; init; }
		}

		private async Task<(bool ok, string? error, StageOutcome outcome)> ApplyStageAsync(
			int companyId, Opportunity e, string? stageName, int? stageId, int? requestedPipelineId)
		{
			if (requestedPipelineId is int rp && rp > 0 && !await PipelineInCompanyAsync(companyId, rp))
				return (false, ForeignReference, NoStageChange);

			var pid = requestedPipelineId ?? e.PipelineId ?? await GetDefaultPipelineIdAsync(companyId);
			var (st, unknown) = await ResolveStageStrictAsync(companyId, pid, stageName, stageId);
			if (unknown) return (false, InvalidStage, NoStageChange);

			// Legacy vocabulary is a closed set too. Accepting free text here is how "Wonn" became a
			// stage nobody could filter by and no board column showed.
			if (st == null && !string.IsNullOrWhiteSpace(stageName) && !OppStages.Contains(stageName))
				return (false, InvalidStage, NoStageChange);

			bool sameStage = st != null ? e.StageId == st.ID : string.Equals(e.Stage, stageName, StringComparison.Ordinal);

			// A closed deal does not quietly reopen. There is no reopen rule in this product, so the
			// refusal is the rule; re-saving the SAME terminal stage is not a transition and stays
			// allowed, otherwise editing a won deal's notes would be impossible.
			if (!sameStage && await IsTerminalAsync(companyId, e))
				return (false, TerminalOpportunity, NoStageChange);

			var previousStageId = e.StageId;
			var previousStageName = e.Stage;

			if (st != null)
			{
				var became = st.IsWon && e.StageId != st.ID;
				var becameLost = st.IsLost && e.StageId != st.ID;
				e.PipelineId = pid; e.StageId = st.ID; e.Stage = st.Name; e.Probability = st.Probability;
				return (true, null, new StageOutcome
				{
					Moved = !sameStage,
					BecameWon = became,
					BecameLost = becameLost,
					PreviousStageId = previousStageId,
					PreviousStageName = previousStageName,
				});
			}

			var target = string.IsNullOrWhiteSpace(stageName) ? (string.IsNullOrWhiteSpace(e.Stage) ? "Prospecting" : e.Stage) : stageName!;
			var becameWonLegacy = target == "Won" && e.Stage != "Won";
			var becameLostLegacy = target == "Lost" && e.Stage != "Lost";
			e.Stage = target;
			e.Probability = target == "Won" ? 100 : (target == "Lost" ? 0 : e.Probability);
			return (true, null, new StageOutcome
			{
				Moved = !sameStage,
				BecameWon = becameWonLegacy,
				BecameLost = becameLostLegacy,
				PreviousStageId = previousStageId,
				PreviousStageName = previousStageName,
			});
		}

		// ---------------- Leads ----------------
		public async Task<(List<Lead> rows, int total)> SearchLeadsAsync(int companyId, string? q, string? status, int page, int pageSize,
			int[]? tagIds = null, bool tagMatchAll = false, bool unassignedOnly = false, bool overdueFollowUpOnly = false)
		{
			var query = _context.Leads.AsNoTracking().Where(l => l.CompanyID == companyId);
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<Lead>(terms, s => l => l.Name.Contains(s) || (l.Company != null && l.Company.Contains(s))
					|| (l.Phone != null && l.Phone.Contains(s)) || (l.Email != null && l.Email.Contains(s)) || (l.Source != null && l.Source.Contains(s)));
				if (pred != null) query = query.Where(pred);
			}
			if (!string.IsNullOrWhiteSpace(status)) query = query.Where(l => l.Status == status);
			var scope = await ScopeAsync();
			if (scope != null) query = query.Where(l => l.OwnerEmployeeId == null || scope.Contains(l.OwnerEmployeeId.Value));

			// AFTER the company and owner predicates, never before them.
			if (unassignedOnly) query = query.Where(l => l.OwnerEmployeeId == null);
			if (overdueFollowUpOnly)
			{
				var now = NowUtc();
				query = query.Where(l => _context.Activities.Any(a =>
					a.CompanyID == companyId && a.LeadId == l.ID &&
					a.Type == WorkActivityTypeName && !a.Done && a.DueDate != null && a.DueDate < now));
			}
			var tagged = await TagMatchedIdsAsync(companyId, Platform.EntityRegistry.Lead, query.Select(l => l.ID), tagIds, tagMatchAll);
			if (tagged != null) query = query.Where(l => tagged.Contains(l.ID));

			var total = await query.CountAsync();
			page = Clamp(page, ref pageSize);
			var rows = await query.OrderByDescending(l => l.ID).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			return (rows, total);
		}

		public async Task<Lead?> GetLeadAsync(int companyId, int id)
		{
			var e = await _context.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.CompanyID == companyId && l.ID == id);
			return e != null && await OwnerVisibleAsync(e.OwnerEmployeeId) ? e : null;
		}

		public async Task<(bool ok, string? error)> SaveLeadAsync(int companyId, Lead dto, string? userId, int[]? tagIds = null)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "Lead name is required");
			if (!await RefOkAsync(dto.CampaignId, x => CampaignInCompanyAsync(companyId, x))) return (false, ForeignReference);
			Lead e;
			if (dto.ID > 0)
			{
				var existing = await _context.Leads.FirstOrDefaultAsync(l => l.CompanyID == companyId && l.ID == dto.ID);
                if (existing == null) return (false, "Lead not found");
                e = existing;
				// The list hides a colleague's lead and the id lookup hides it too. A WRITE was the one
				// door left open: a rep could not see the row but could still overwrite it by id.
				if (!await OwnerVisibleAsync(e.OwnerEmployeeId)) return (false, "Lead not found");
			}
			else
			{
				// CRM 3-7 routing: a brand-new unowned lead is auto-assigned to a SalesRep when routing is on
				var routed = dto.OwnerEmployeeId ?? await RouteLeadOwnerAsync(companyId) ?? Me();
				e = new Lead { CompanyID = companyId, CreatedAt = DateTime.UtcNow, CreatedBy = userId, OwnerEmployeeId = routed };
				_context.Leads.Add(e);
			}
			bool isNew = dto.ID <= 0;

			// The state the event will describe as PREVIOUS, captured before the assignment overwrites
			// it. After the save it exists nowhere else — which is the whole reason a payload carries it.
			var beforeStatus = isNew ? null : e.Status;
			var beforeName = e.Name; var beforeOwner = e.OwnerEmployeeId; var beforeValue = e.EstimatedValue;
			var beforeSource = e.Source; var beforeSegment = e.Segment; var beforeCampaign = e.CampaignId;
			var beforePhone = e.Phone; var beforeEmail = e.Email; var beforeCompany = e.Company;

			e.Name = dto.Name.Trim(); e.NameEn = dto.NameEn; e.Company = dto.Company; e.Phone = dto.Phone; e.Email = dto.Email; e.Source = dto.Source;
			e.Segment = dto.Segment; e.EstimatedValue = dto.EstimatedValue; e.Status = string.IsNullOrWhiteSpace(dto.Status) ? "New" : dto.Status; e.Notes = dto.Notes;
			e.CampaignId = dto.CampaignId;
			if (dto.OwnerEmployeeId.HasValue) e.OwnerEmployeeId = dto.OwnerEmployeeId;
			e.Score = await ComputeLeadScoreAsync(companyId, e);   // CRM 3-7: refresh score from active rules

			// The transaction exists for the event. This path was a bare SaveChanges, and a producer
			// without an ambient transaction is refused by the kernel — correctly, because an event
			// that outlives a rolled-back write is the failure the kernel exists to prevent.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			await _context.SaveChangesAsync();

			await RecordAsync(new Models.Platform.BusinessEventRecord
			{
				EntityCode = Platform.EntityRegistry.Lead,
				EntityId = e.ID,
				EventType = isNew ? Platform.LeadEvents.Created : Platform.LeadEvents.Updated,
				PayloadVersion = Models.Platform.LeadEventPayload.Version,
				Visibility = Models.Platform.BusinessEventVisibility.Internal,
				// Created happens once per lead and is keyed. Updated has no key at all: an edit is a
				// repeatable fact, and a key would silence every edit after the first.
				DedupKey = isNew ? $"Lead.Created:{e.ID}" : null,
				CompanyIdOverride = e.CompanyID,
				Payload = new Models.Platform.LeadEventPayload
				{
					LeadName = e.Name,
					CompanyName = e.Company,
					OwnerEmployeeId = e.OwnerEmployeeId,
					Status = e.Status,
					PreviousStatus = beforeStatus,
					Source = e.Source,
					EstimatedValue = e.EstimatedValue,
					CampaignId = e.CampaignId,
					ChangedFields = isNew ? null : Changed(
						("Name", !string.Equals(beforeName, e.Name, StringComparison.Ordinal)),
						("Status", !string.Equals(beforeStatus, e.Status, StringComparison.Ordinal)),
						("OwnerEmployeeId", beforeOwner != e.OwnerEmployeeId),
						("EstimatedValue", beforeValue != e.EstimatedValue),
						("Source", !string.Equals(beforeSource, e.Source, StringComparison.Ordinal)),
						("Segment", !string.Equals(beforeSegment, e.Segment, StringComparison.Ordinal)),
						("CampaignId", beforeCampaign != e.CampaignId),
						("Phone", !string.Equals(beforePhone, e.Phone, StringComparison.Ordinal)),
						("Email", !string.Equals(beforeEmail, e.Email, StringComparison.Ordinal)),
						("Company", !string.Equals(beforeCompany, e.Company, StringComparison.Ordinal))),
				},
			});

			var (leadTagOk, leadTagErr) = await ApplyTagsAsync(companyId, Platform.EntityRegistry.Lead, e.ID, tagIds);
			if (!leadTagOk) { await tx.RollbackAsync(); return (false, leadTagErr); }

			await tx.CommitAsync();
			return (true, null);
		}

		// CRM 3-7: round-robin-ish routing — least-loaded active SalesRep (only when AutoRouteLeads is on).
		private async Task<int?> RouteLeadOwnerAsync(int companyId)
		{
			var st = await _context.CrmSettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyID == companyId);
			if (st == null || !st.AutoRouteLeads) return null;
			var reps = await _context.CrmUserRoles.AsNoTracking()
				.Where(r => r.CompanyID == companyId && r.Role == "SalesRep").Select(r => r.EmployeeId).Distinct().ToListAsync();
			if (reps.Count == 0) return null;
			// assign to the rep currently owning the fewest leads
			var loads = await _context.Leads.AsNoTracking().Where(l => l.CompanyID == companyId && l.OwnerEmployeeId != null)
				.GroupBy(l => l.OwnerEmployeeId!.Value).Select(g => new { Emp = g.Key, Cnt = g.Count() }).ToListAsync();
			var map = loads.ToDictionary(x => x.Emp, x => x.Cnt);
			return reps.OrderBy(r => map.TryGetValue(r, out var c) ? c : 0).ThenBy(r => r).First();
		}

		// CRM 3-7: sum points of active scoring rules whose Field matches the lead by Operator.
		private async Task<int> ComputeLeadScoreAsync(int companyId, Lead lead)
		{
			var rules = await _context.CrmScoringRules.AsNoTracking().Where(r => r.CompanyID == companyId && r.IsActive).ToListAsync();
			return ScoreLead(rules, lead);
		}

		private static int ScoreLead(List<CrmScoringRule> rules, Lead lead)
		{
			int score = 0;
			foreach (var r in rules)
			{
				var fieldVal = (r.Field ?? "").ToLowerInvariant() switch {
					"source" => lead.Source, "segment" => lead.Segment, "status" => lead.Status,
					"estimatedvalue" => lead.EstimatedValue.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => null };
				var rv = r.Value ?? "";
				bool hit = (r.Operator ?? "eq").ToLowerInvariant() switch {
					"contains" => !string.IsNullOrEmpty(fieldVal) && fieldVal.Contains(rv, StringComparison.OrdinalIgnoreCase),
					"gte" => decimal.TryParse(fieldVal, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var fv)
							 && decimal.TryParse(rv, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var tv) && fv >= tv,
					_ => string.Equals(fieldVal ?? "", rv, StringComparison.OrdinalIgnoreCase) };
				if (hit) score += r.Points;
			}
			return score;
		}

		// CRM 3-2: qualify a lead into a CrmAccount (+ primary Contact). Does NOT create a financial Customer —
		// that happens only when an opportunity on the account is Won (keeps the customer tree clean).
		public async Task<(bool ok, string? error, int accountId)> ConvertLeadToAccountAsync(int companyId, int leadId, string? userId)
		{
			var lead = await _context.Leads.FirstOrDefaultAsync(l => l.CompanyID == companyId && l.ID == leadId);
			if (lead == null) return (false, "Lead not found", 0);
			if (!await OwnerVisibleAsync(lead.OwnerEmployeeId)) return (false, "Lead not found", 0);
			if (lead.AccountId.HasValue) return (false, "It has already been converted to an account", lead.AccountId.Value);
			// Converted WITHOUT an account is the shape a half-finished conversion used to leave behind.
			// It is refused rather than converted again, because the second run would create the party
			// twice and the first account would be the one nothing points at.
			if (lead.Status == "Converted") return (false, "It has already been converted", 0);

			// ONE transaction. This writes three rows — an account, its primary contact, and the lead's
			// own converted state — and they are one business fact. Before, they were two SaveChanges
			// calls with nothing between them: a failure on the second left a real account and a real
			// contact behind while the lead still read as un-converted, so the next click created the
			// party all over again.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			var acc = new CrmAccount
			{
				CompanyID = companyId, Name = string.IsNullOrWhiteSpace(lead.Company) ? lead.Name : lead.Company!, Phone = lead.Phone, Email = lead.Email,
				Segment = lead.Segment, Source = lead.Source, OwnerEmployeeId = lead.OwnerEmployeeId ?? Me(), IsActive = true, CreatedBy = userId, CreatedAt = DateTime.UtcNow
			};
			_context.CrmAccounts.Add(acc);
			await _context.SaveChangesAsync();
			// primary contact from the lead's person name (when the lead carried a company)
			CrmContact? primary = null;
			if (!string.IsNullOrWhiteSpace(lead.Name))
			{
				primary = new CrmContact { CompanyID = companyId, AccountId = acc.ID, Name = lead.Name, Phone = lead.Phone, Email = lead.Email, IsPrimary = true, OwnerEmployeeId = acc.OwnerEmployeeId, CreatedAt = DateTime.UtcNow };
				_context.CrmContacts.Add(primary);
			}
			var previousStatus = lead.Status;
			lead.AccountId = acc.ID; lead.Status = "Converted";
			await _context.SaveChangesAsync();

			// TWO FACTS, TWO EVENTS, ONE TRANSACTION. A party came into existence and a lead stopped
			// being a lead. A consumer that cares about accounts must not have to know that some of
			// them are born inside a conversion, and a consumer that cares about the funnel must not
			// have to infer the conversion from an account it did not ask about.
			await RecordAsync(new Models.Platform.BusinessEventRecord
			{
				EntityCode = Platform.EntityRegistry.CrmAccount,
				EntityId = acc.ID,
				EventType = Platform.CrmAccountEvents.Created,
				PayloadVersion = Models.Platform.CrmAccountEventPayload.Version,
				Visibility = Models.Platform.BusinessEventVisibility.Internal,
				DedupKey = $"CrmAccount.Created:{acc.ID}",
				CompanyIdOverride = acc.CompanyID,
				Payload = new Models.Platform.CrmAccountEventPayload
				{
					AccountName = acc.Name,
					OwnerEmployeeId = acc.OwnerEmployeeId,
					Segment = acc.Segment,
					ConvertedFromLeadId = lead.ID,
				},
			});

			await RecordAsync(new Models.Platform.BusinessEventRecord
			{
				EntityCode = Platform.EntityRegistry.Lead,
				EntityId = lead.ID,
				EventType = Platform.LeadEvents.Converted,
				PayloadVersion = Models.Platform.LeadEventPayload.Version,
				Visibility = Models.Platform.BusinessEventVisibility.Internal,
				// A lead converts once — the method refuses a second attempt — so the key is the lead.
				DedupKey = $"Lead.Converted:{lead.ID}",
				CompanyIdOverride = lead.CompanyID,
				Payload = new Models.Platform.LeadEventPayload
				{
					LeadName = lead.Name,
					CompanyName = lead.Company,
					OwnerEmployeeId = lead.OwnerEmployeeId,
					Status = lead.Status,
					PreviousStatus = previousStatus,
					Source = lead.Source,
					EstimatedValue = lead.EstimatedValue,
					CampaignId = lead.CampaignId,
					CrmAccountId = acc.ID,
					PrimaryContactId = primary?.ID,
				},
			});

			await tx.CommitAsync();
			return (true, null, acc.ID);
		}

		// CRM 3-2: ensure a CrmAccount has a linked financial Customer (create + enrich via the seam). Returns customerId.
		private async Task<int?> EnsureAccountCustomerAsync(int companyId, int accountId)
		{
			var acc = await _context.CrmAccounts.FirstOrDefaultAsync(a => a.CompanyID == companyId && a.ID == accountId);
			if (acc == null) return null;
			if (acc.CustomerId.HasValue) return acc.CustomerId.Value;
			var custId = await _link.CreateCustomerAsync(companyId, acc.Name, acc.NameEn, null, null);
			var primary = await _context.CrmContacts.AsNoTracking().Where(c => c.CompanyID == companyId && c.AccountId == accountId).OrderByDescending(c => c.IsPrimary).Select(c => c.Name).FirstOrDefaultAsync();
			await _link.EnrichAsync(custId, acc.Phone, acc.Email, acc.Segment, primary ?? acc.Name);
			acc.CustomerId = custId;
			await _context.SaveChangesAsync();
			return custId;
		}

		// CRM 3-2: on Won, link the account to a financial customer and stamp the opportunity's CustomerId for O2C.
		//
		// WAVE 1 — THIS NO LONGER SWALLOWS. The catch used to say "linking failure must not block the
		// stage change", and the result was a deal marked Won whose CustomerId was never stamped: the
		// O2C path then refused to raise a quotation from it and nobody was told why. The link is not
		// decoration — it is the fact that makes a won deal billable — so it now runs inside the same
		// transaction as the transition and its failure refuses the transition.
		//
		// It is also the only writer here that reaches OUTSIDE CRM (ICrmCustomerLink creates the
		// financial customer), which is exactly the kind of write that must not be half-done.
		private async Task<(bool ok, string? error)> LinkWonOpportunityAsync(int companyId, Opportunity o)
		{
			if (!o.AccountId.HasValue) return (true, null);
			var custId = await EnsureAccountCustomerAsync(companyId, o.AccountId.Value);
			if (custId == null) return (false, "تعذّر ربط الحساب بعميل مالي");
			if (o.CustomerId != custId)
			{
				var live = await _context.Opportunities.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == o.ID);
				if (live != null) { live.CustomerId = custId; await _context.SaveChangesAsync(); }
			}
			return (true, null);
		}

		// ---------------- Opportunities ----------------
		public async Task<(List<OppRow> rows, int total)> SearchOpportunitiesAsync(int companyId, string? q, string? stage, int page, int pageSize,
			int[]? tagIds = null, bool tagMatchAll = false, bool unassignedOnly = false, bool overdueFollowUpOnly = false)
		{
			var scope = await ScopeAsync();
			var oppSrc = _context.Opportunities.AsNoTracking().Where(o => o.CompanyID == companyId);
			if (scope != null) oppSrc = oppSrc.Where(o => o.OwnerEmployeeId == null || scope.Contains(o.OwnerEmployeeId.Value));

			// AFTER the company and owner predicates, and before the projection so the database still
			// counts and pages the narrowed set.
			if (unassignedOnly) oppSrc = oppSrc.Where(o => o.OwnerEmployeeId == null);
			if (overdueFollowUpOnly)
			{
				var now = NowUtc();
				oppSrc = oppSrc.Where(o => _context.Activities.Any(a =>
					a.CompanyID == companyId && a.OpportunityId == o.ID &&
					a.Type == WorkActivityTypeName && !a.Done && a.DueDate != null && a.DueDate < now));
			}
			var tagged = await TagMatchedIdsAsync(companyId, Platform.EntityRegistry.Opportunity, oppSrc.Select(o => o.ID), tagIds, tagMatchAll);
			if (tagged != null) oppSrc = oppSrc.Where(o => tagged.Contains(o.ID));

			var isEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
			var q0 = from o in oppSrc
					 join a in _context.CrmAccounts.AsNoTracking() on o.AccountId equals (int?)a.ID into aj
					 from a in aj.DefaultIfEmpty()
					 select new OppRow { Id = o.ID, Title = o.Title, TitleEn = o.TitleEn, AccountId = o.AccountId, CustomerId = o.CustomerId, CustomerName = a != null ? (isEn ? (a.NameEn ?? a.Name) : a.Name) : null, CampaignId = o.CampaignId, Stage = o.Stage, Amount = o.Amount, Probability = o.Probability, ExpectedCloseDate = o.ExpectedCloseDate };
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<OppRow>(terms, s => r => r.Title.Contains(s) || (r.CustomerName != null && r.CustomerName.Contains(s)));
				if (pred != null) q0 = q0.Where(pred);
			}
			if (!string.IsNullOrWhiteSpace(stage)) q0 = q0.Where(r => r.Stage == stage);
			var total = await q0.CountAsync();
			page = Clamp(page, ref pageSize);
			var rows = await q0.OrderByDescending(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			return (rows, total);
		}

		private static readonly string[] OppStages = { "Prospecting", "Qualification", "Proposal", "Negotiation", "Won", "Lost" };

		// all opportunities (flattened with customer name) for the Kanban board, newest first
		public async Task<List<OppRow>> PipelineBoardAsync(int companyId)
		{
			var scope = await ScopeAsync();
			var oppSrc = _context.Opportunities.AsNoTracking().Where(o => o.CompanyID == companyId);
			if (scope != null) oppSrc = oppSrc.Where(o => o.OwnerEmployeeId == null || scope.Contains(o.OwnerEmployeeId.Value));
			var isEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
			return await (from o in oppSrc
				   join a in _context.CrmAccounts.AsNoTracking() on o.AccountId equals (int?)a.ID into aj
				   from a in aj.DefaultIfEmpty()
				   orderby o.ID descending
				   select new OppRow { Id = o.ID, Title = o.Title, TitleEn = o.TitleEn, AccountId = o.AccountId, CustomerId = o.CustomerId, CustomerName = a != null ? (isEn ? (a.NameEn ?? a.Name) : a.Name) : null, CampaignId = o.CampaignId, Stage = o.Stage, Amount = o.Amount, Probability = o.Probability, ExpectedCloseDate = o.ExpectedCloseDate }).ToListAsync();
		}

		public async Task<(bool ok, string? error)> UpdateOpportunityStageAsync(int companyId, int id, string stage)
		{
			var o = await _context.Opportunities.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == id);
			if (o == null) return (false, "Opportunity not found");
			if (!await OwnerVisibleAsync(o.OwnerEmployeeId)) return (false, "Opportunity not found");
			if (string.IsNullOrWhiteSpace(stage)) return (false, InvalidStage);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			var (ok, err, outcome) = await ApplyStageAsync(companyId, o, stage, null, null);
			if (!ok) { await tx.RollbackAsync(); return (false, err); }
			await _context.SaveChangesAsync();
			if (outcome.BecameWon)
			{
				var (linked, linkErr) = await LinkWonOpportunityAsync(companyId, o);
				if (!linked) { await tx.RollbackAsync(); return (false, linkErr); }
			}

			// AFTER the link, not before it. The Won event says the deal is won AND billable — the
			// customer is stamped by the time this row is written — so a link that fails takes the
			// transition, the event and any work down with it rather than leaving a Won event behind
			// pointing at a deal that never linked.
			await RecordStageEventAsync(o, outcome);

			await tx.CommitAsync();
			// AFTER the commit, and deliberately: a notification is not the business fact, and a mail
			// failure must not undo a won deal.
			if (outcome.BecameWon) await NotifyOpportunityWonAsync(companyId, o);
			return (true, null);
		}

		// CRM 3-3: pipeline + stage helpers
		public async Task<int> GetDefaultPipelineIdAsync(int companyId)
		{
			var def = await _context.CrmPipelines.AsNoTracking().Where(p => p.CompanyID == companyId && p.IsActive)
				.OrderByDescending(p => p.IsDefault).ThenBy(p => p.ID).Select(p => (int?)p.ID).FirstOrDefaultAsync();
			return def ?? 0;
		}
		public Task<List<CrmPipelineStage>> GetStagesAsync(int companyId, int pipelineId) =>
			_context.CrmPipelineStages.AsNoTracking().Where(s => s.CompanyID == companyId && s.PipelineId == pipelineId).OrderBy(s => s.Sort).ToListAsync();
		// ResolveStageAsync was removed with its last caller. It ended by returning the pipeline's FIRST
		// stage for a request it could not resolve, which is the silent rounding described in §3.3 of
		// docs/crm/CRM-STRENGTHENING-WAVE1.md — leaving it in the file would leave that behaviour one
		// call away. ResolveStageStrictAsync is the replacement and refuses instead.
		public Task<List<CrmPipeline>> GetPipelinesAsync(int companyId) =>
			_context.CrmPipelines.AsNoTracking().Where(p => p.CompanyID == companyId).OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync();
		public async Task<CrmPipeline?> GetPipelineWithStagesAsync(int companyId, int id)
		{
			var p = await _context.CrmPipelines.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == id);
			if (p == null) return null;
			p.Stages = await GetStagesAsync(companyId, id);
			return p;
		}
		public async Task<(bool ok, string? error, int id)> SavePipelineAsync(int companyId, CrmPipeline dto, List<CrmPipelineStage> stages)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "Pipeline name is required", 0);
			CrmPipeline e;
			if (dto.ID > 0) { e = await _context.CrmPipelines.FirstOrDefaultAsync(p => p.CompanyID == companyId && p.ID == dto.ID) ?? throw new InvalidOperationException("غير موجود"); _context.CrmPipelineStages.RemoveRange(_context.CrmPipelineStages.Where(s => s.PipelineId == e.ID)); }
			else { e = new CrmPipeline { CompanyID = companyId, CreatedAt = DateTime.UtcNow }; _context.CrmPipelines.Add(e); }
			e.Name = dto.Name.Trim(); e.NameEn = dto.NameEn; e.IsActive = dto.IsActive;
			if (dto.IsDefault) { foreach (var other in _context.CrmPipelines.Where(p => p.CompanyID == companyId && p.ID != e.ID && p.IsDefault)) other.IsDefault = false; e.IsDefault = true; }
			await _context.SaveChangesAsync();
			int sort = 0;
			foreach (var s in stages.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
				_context.CrmPipelineStages.Add(new CrmPipelineStage { CompanyID = companyId, PipelineId = e.ID, Name = s.Name.Trim(), NameEn = s.NameEn, Sort = sort++, Probability = s.Probability < 0 ? 0 : (s.Probability > 100 ? 100 : s.Probability), IsWon = s.IsWon, IsLost = s.IsLost, CreatedAt = DateTime.UtcNow });
			await _context.SaveChangesAsync();
			return (true, null, e.ID);
		}

		// ---------------- Opportunity products + O2C (3-3b) ----------------
		public Task<List<OpportunityProduct>> GetOpportunityProductsAsync(int companyId, int oppId) =>
			_context.OpportunityProducts.AsNoTracking().Where(p => p.CompanyID == companyId && p.OpportunityId == oppId).OrderBy(p => p.ID).ToListAsync();

		public async Task<(bool ok, string? error)> SaveOpportunityProductsAsync(int companyId, int oppId, List<OpportunityProduct> lines)
		{
			var opp = await _context.Opportunities.FirstOrDefaultAsync(o => o.CompanyID == companyId && o.ID == oppId);
			if (opp == null) return (false, "Opportunity not found");
			_context.OpportunityProducts.RemoveRange(_context.OpportunityProducts.Where(p => p.CompanyID == companyId && p.OpportunityId == oppId));
			decimal total = 0;
			foreach (var l in (lines ?? new()).Where(l => l.ItemId != null || !string.IsNullOrWhiteSpace(l.ItemDescription)))
			{
				var disc = l.DiscountPercent < 0 ? 0 : (l.DiscountPercent > 100 ? 100 : l.DiscountPercent);
				var lt = R2(l.Qty * l.UnitPrice * (1 - disc / 100m));
				total += lt;
				_context.OpportunityProducts.Add(new OpportunityProduct { CompanyID = companyId, OpportunityId = oppId, ItemId = l.ItemId, ItemDescription = l.ItemDescription, Qty = l.Qty, UnitPrice = l.UnitPrice, DiscountPercent = disc, LineTotal = lt });
			}
			opp.Amount = R2(total);   // opportunity value follows its product lines
			await _context.SaveChangesAsync();
			return (true, null);
		}

		// O2C: turn an opportunity (+ products) into a Quotation. Ensures the account links to a financial customer first.
		public async Task<(bool ok, string? error, int? quotationId)> ConvertOpportunityToQuotationAsync(int companyId, int oppId, string? userId)
		{
			var opp = await _context.Opportunities.FirstOrDefaultAsync(o => o.CompanyID == companyId && o.ID == oppId);
			if (opp == null) return (false, "Opportunity not found", null);
			if (opp.QuotationId.HasValue) return (false, "A quotation has already been created for this opportunity", opp.QuotationId);
			if (opp.AccountId == null) return (false, "Link the opportunity to an account first", null);
			var products = await _context.OpportunityProducts.AsNoTracking().Where(p => p.CompanyID == companyId && p.OpportunityId == oppId).ToListAsync();
			if (products.Count == 0) return (false, "Add lines to the opportunity before converting it to a quotation", null);
			var custId = await EnsureAccountCustomerAsync(companyId, opp.AccountId.Value);
			if (custId == null) return (false, "Could not link the account to a financial customer", null);

			var lines = products.Select(p => new SoLineInput
			{
				ItemId = p.ItemId, ItemDescription = p.ItemDescription, Qty = p.Qty, UnitPrice = p.UnitPrice,
				DiscountAmount = R2(p.Qty * p.UnitPrice * p.DiscountPercent / 100m), TaxRate = 0
			}).ToList();

			var (ok, err, q) = await _selling.CreateQuotationAsync(companyId, custId.Value, null, DateTime.Today, null, $"From CRM opportunity: {opp.Title}", lines, userId);
			if (!ok) return (false, err, null);
			opp.QuotationId = q!.ID;
			await _context.SaveChangesAsync();
			return (true, null, q.ID);
		}

		// celebratory + downstream alert when an opportunity is closed-won (fire-and-forget)
		private async Task NotifyOpportunityWonAsync(int companyId, Opportunity o)
		{
			try
			{
				await _notify.NotifyRoleAsync(companyId, "acc", new[] { "ChiefAccountant", "Accountant" },
					"فرصة بيعية رابحة 🎉", "Opportunity won 🎉",
					$"Opportunity «{o.Title}» was won, worth {o.Amount:N2}", $"Opportunity \"{o.Title}\" won ({o.Amount:N2})",
					"opportunity_won", o.ID);
			}
			catch { /* notifications never block the business flow */ }
		}

		public async Task<List<PipelineStage>> PipelineSummaryAsync(int companyId)
		{
			var scope = await ScopeAsync();
			var src = _context.Opportunities.AsNoTracking().Where(o => o.CompanyID == companyId);
			if (scope != null) src = src.Where(o => o.OwnerEmployeeId == null || scope.Contains(o.OwnerEmployeeId.Value));
			return await src.GroupBy(o => o.Stage).Select(g => new PipelineStage { Stage = g.Key, Count = g.Count(), Amount = g.Sum(x => x.Amount) }).ToListAsync();
		}

		public async Task<Opportunity?> GetOpportunityAsync(int companyId, int id)
		{
			var e = await _context.Opportunities.AsNoTracking().FirstOrDefaultAsync(o => o.CompanyID == companyId && o.ID == id);
			return e != null && await OwnerVisibleAsync(e.OwnerEmployeeId) ? e : null;
		}

		public async Task<(bool ok, string? error)> SaveOpportunityAsync(int companyId, Opportunity dto, string? userId, int[]? tagIds = null)
		{
			if (string.IsNullOrWhiteSpace(dto.Title)) return (false, "Opportunity title is required");
			// Every id on this form arrived from the browser. None of them is trusted.
			if (!await RefOkAsync(dto.AccountId, x => AccountInCompanyAsync(companyId, x))) return (false, ForeignReference);
			if (!await RefOkAsync(dto.LeadId, x => LeadInCompanyAsync(companyId, x))) return (false, ForeignReference);
			if (!await RefOkAsync(dto.CampaignId, x => CampaignInCompanyAsync(companyId, x))) return (false, ForeignReference);
			if (!await RefOkAsync(dto.CustomerId, x => CustomerInCompanyAsync(companyId, x))) return (false, ForeignReference);

			Opportunity e;
			if (dto.ID > 0)
			{
				var existing = await _context.Opportunities.FirstOrDefaultAsync(o => o.CompanyID == companyId && o.ID == dto.ID);
                if (existing == null) return (false, "Opportunity not found");
                e = existing;
				if (!await OwnerVisibleAsync(e.OwnerEmployeeId)) return (false, "Opportunity not found");
			}
			else { e = new Opportunity { CompanyID = companyId, CreatedAt = DateTime.UtcNow, CreatedBy = userId, OwnerEmployeeId = dto.OwnerEmployeeId ?? Me() }; _context.Opportunities.Add(e); }
			bool isNew = dto.ID <= 0;
			var beforeTitle = e.Title; var beforeAmount = e.Amount; var beforeAccount = e.AccountId;
			var beforeOwner = e.OwnerEmployeeId; var beforeClose = e.ExpectedCloseDate; var beforeReason = e.WinLossReason;

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			// The stage is decided FIRST and by the one authority, so a refused transition costs no
			// write at all — the editor cannot smuggle a closed deal back into the pipeline by saving
			// a title alongside it.
			var (stageOk, stageErr, outcome) = await ApplyStageAsync(companyId, e, dto.Stage, dto.StageId, dto.PipelineId);
			if (!stageOk) { await tx.RollbackAsync(); return (false, stageErr); }

			e.Title = dto.Title.Trim(); e.TitleEn = string.IsNullOrWhiteSpace(dto.TitleEn) ? null : dto.TitleEn.Trim(); e.AccountId = dto.AccountId; e.CustomerId = dto.CustomerId; e.LeadId = dto.LeadId;
			e.WinLossReason = dto.WinLossReason;
			e.Amount = dto.Amount; e.ExpectedCloseDate = dto.ExpectedCloseDate; e.Notes = dto.Notes; e.CampaignId = dto.CampaignId;
			if (dto.OwnerEmployeeId.HasValue) e.OwnerEmployeeId = dto.OwnerEmployeeId;
			await _context.SaveChangesAsync();
			if (outcome.BecameWon)
			{
				var (linked, linkErr) = await LinkWonOpportunityAsync(companyId, e);
				if (!linked) { await tx.RollbackAsync(); return (false, linkErr); }
			}

			// ONE EVENT FOR ONE SAVE, and which one depends on what actually happened.
			//
			// A NEW deal announces itself as Created — its opening stage is where it starts, not a
			// transition it made. It announces Won or Lost as well only in the unusual case where it
			// was created straight into a terminal stage, because that IS a win, and because the
			// customer link above has already run for it.
			//
			// An EDIT announces the transition when the stage moved, and Updated when it did not.
			// Never both: a stage move that also renamed the deal is one committed save, and a
			// consumer should not have to decide which of two rows about it to believe.
			if (isNew)
			{
				await RecordAsync(new Models.Platform.BusinessEventRecord
				{
					EntityCode = Platform.EntityRegistry.Opportunity,
					EntityId = e.ID,
					EventType = Platform.OpportunityEvents.Created,
					PayloadVersion = Models.Platform.OpportunityEventPayload.Version,
					Visibility = Models.Platform.BusinessEventVisibility.Internal,
					DedupKey = $"Opportunity.Created:{e.ID}",
					CompanyIdOverride = e.CompanyID,
					// NoStageChange, not the outcome: a new deal's opening stage is where it starts, so
					// the payload carries no "previous stage" — there was no previous. The outcome still
					// matters below, where a deal created straight into a terminal stage says so.
					Payload = PayloadFor(e, NoStageChange, changed: null),
				});
				if (outcome.BecameWon || outcome.BecameLost) await RecordStageEventAsync(e, outcome);
			}
			else if (outcome.Moved)
			{
				await RecordStageEventAsync(e, outcome);
			}
			else
			{
				await RecordAsync(new Models.Platform.BusinessEventRecord
				{
					EntityCode = Platform.EntityRegistry.Opportunity,
					EntityId = e.ID,
					EventType = Platform.OpportunityEvents.Updated,
					PayloadVersion = Models.Platform.OpportunityEventPayload.Version,
					Visibility = Models.Platform.BusinessEventVisibility.Internal,
					DedupKey = null,   // an edit is repeatable; a key would silence every later one
					CompanyIdOverride = e.CompanyID,
					Payload = PayloadFor(e, outcome, Changed(
						("Title", !string.Equals(beforeTitle, e.Title, StringComparison.Ordinal)),
						("Amount", beforeAmount != e.Amount),
						("AccountId", beforeAccount != e.AccountId),
						("OwnerEmployeeId", beforeOwner != e.OwnerEmployeeId),
						("ExpectedCloseDate", beforeClose != e.ExpectedCloseDate),
						("WinLossReason", !string.Equals(beforeReason, e.WinLossReason, StringComparison.Ordinal)))),
				});
			}

			var (oppTagOk, oppTagErr) = await ApplyTagsAsync(companyId, Platform.EntityRegistry.Opportunity, e.ID, tagIds);
			if (!oppTagOk) { await tx.RollbackAsync(); return (false, oppTagErr); }

			await tx.CommitAsync();
			if (outcome.BecameWon) await NotifyOpportunityWonAsync(companyId, e);
			return (true, null);
		}

		// ---------------- Campaigns ----------------
		public async Task<(List<CampaignRow> rows, int total)> SearchCampaignsAsync(int companyId, string? q, string? status, int page, int pageSize)
		{
			var query = _context.Campaigns.AsNoTracking().Where(c => c.CompanyID == companyId)
				.Select(c => new CampaignRow
				{
					Id = c.ID, Name = c.Name, NameEn = c.NameEn, Channel = c.Channel, Status = c.Status,
					StartDate = c.StartDate, EndDate = c.EndDate, Budget = c.Budget, Notes = c.Notes,
					Leads = _context.Leads.Count(l => l.CampaignId == c.ID),
					Opps = _context.Opportunities.Count(o => o.CampaignId == c.ID),
					Won = _context.Opportunities.Count(o => o.CampaignId == c.ID && o.Stage == "Won"),
					WonValue = _context.Opportunities.Where(o => o.CampaignId == c.ID && o.Stage == "Won").Sum(o => (decimal?)o.Amount) ?? 0m
				});
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<CampaignRow>(terms, s => r => r.Name.Contains(s) || (r.NameEn != null && r.NameEn.Contains(s)) || (r.Channel != null && r.Channel.Contains(s)));
				if (pred != null) query = query.Where(pred);
			}
			if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
			var total = await query.CountAsync();
			page = Clamp(page, ref pageSize);
			var rows = await query.OrderByDescending(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			return (rows, total);
		}

		public async Task<List<(int id, string name)>> GetCampaignsForPickAsync(int companyId)
		{
			var rows = await _context.Campaigns.AsNoTracking().Where(c => c.CompanyID == companyId && c.Status != "Cancelled")
				.OrderByDescending(c => c.ID).Select(c => new { c.ID, c.Name }).ToListAsync();
			return rows.Select(r => (r.ID, r.Name)).ToList();
		}

		public async Task<Campaign?> GetCampaignAsync(int companyId, int id)
		{
			var e = await _context.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyID == companyId && c.ID == id);
			return e != null && await OwnerVisibleAsync(e.OwnerEmployeeId) ? e : null;
		}

		public async Task<(bool ok, string? error)> SaveCampaignAsync(int companyId, Campaign dto, string? userId)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "Campaign name is required");
			Campaign e;
			if (dto.ID > 0) { e = await _context.Campaigns.FirstOrDefaultAsync(c => c.CompanyID == companyId && c.ID == dto.ID) ?? throw new InvalidOperationException("غير موجودة"); }
			else { e = new Campaign { CompanyID = companyId, CreatedAt = DateTime.UtcNow, CreatedBy = userId, OwnerEmployeeId = dto.OwnerEmployeeId ?? Me() }; _context.Campaigns.Add(e); }
			e.Name = dto.Name.Trim(); e.NameEn = dto.NameEn; e.Channel = dto.Channel;
			e.Status = string.IsNullOrWhiteSpace(dto.Status) ? "Planned" : dto.Status;
			e.StartDate = dto.StartDate; e.EndDate = dto.EndDate; e.Budget = dto.Budget < 0 ? 0 : dto.Budget; e.Notes = dto.Notes;
			await _context.SaveChangesAsync();
			return (true, null);
		}

		// ---------------- Campaign members + ROI (3-5) ----------------
		private static readonly string[] _respondedStatuses = { "Responded", "Converted" };

		public async Task<CampaignDetail?> GetCampaignDetailAsync(int companyId, int campaignId)
		{
			var c = await _context.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == campaignId);
			if (c == null) return null;
			var members = await _context.CampaignMembers.AsNoTracking().Where(m => m.CompanyID == companyId && m.CampaignId == campaignId).ToListAsync();
			var byStatus = members.GroupBy(m => m.Status).ToDictionary(g => g.Key, g => g.Count());
			var wonValue = await _context.Opportunities.Where(o => o.CampaignId == campaignId && o.Stage == "Won").SumAsync(o => (decimal?)o.Amount) ?? 0m;
			return new CampaignDetail
			{
				Campaign = c,
				Members = members.Count,
				Responded = members.Count(m => _respondedStatuses.Contains(m.Status)),
				Converted = members.Count(m => m.Status == "Converted"),
				Leads = await _context.Leads.CountAsync(l => l.CampaignId == campaignId),
				Opps = await _context.Opportunities.CountAsync(o => o.CampaignId == campaignId),
				Won = await _context.Opportunities.CountAsync(o => o.CampaignId == campaignId && o.Stage == "Won"),
				WonValue = wonValue,
				ByStatus = byStatus
			};
		}

		public async Task<List<MemberRow>> GetCampaignMembersAsync(int companyId, int campaignId) =>
			await _context.CampaignMembers.AsNoTracking().Where(m => m.CompanyID == companyId && m.CampaignId == campaignId)
				.OrderByDescending(m => m.ID)
				.Select(m => new MemberRow { Id = m.ID, EntityType = m.EntityType, EntityId = m.EntityId, MemberName = m.MemberName, Status = m.Status, RespondedAt = m.RespondedAt })
				.ToListAsync();

		public async Task<int> AddCampaignMembersAsync(int companyId, int campaignId, IEnumerable<(string entityType, int entityId, string? name)> members)
		{
			var existing = await _context.CampaignMembers.Where(m => m.CompanyID == companyId && m.CampaignId == campaignId)
				.Select(m => new { m.EntityType, m.EntityId }).ToListAsync();
			var have = existing.Select(e => e.EntityType + ":" + e.EntityId).ToHashSet();
			// The container first: enrolling into another tenant's campaign would attribute this
			// company's audience to that company's marketing.
			if (!await CampaignInCompanyAsync(companyId, campaignId)) return 0;
			int added = 0;
			foreach (var (etype, eid, name) in members)
			{
				if (string.IsNullOrWhiteSpace(etype) || eid <= 0) continue;
				if (!have.Add(etype + ":" + eid)) continue;   // skip duplicates
				// A member id arrives from the picker, and the picker is company-scoped — but the POST is
				// not the picker. A foreign member is SKIPPED rather than fatal, matching the duplicate
				// rule beside it, and the returned count still tells the caller what was really added.
				if (!await EntityRefOkAsync(companyId, etype, eid)) continue;
				_context.CampaignMembers.Add(new CampaignMember { CompanyID = companyId, CampaignId = campaignId, EntityType = etype, EntityId = eid, MemberName = name, Status = "Targeted", CreatedAt = DateTime.UtcNow });
				added++;
			}
			if (added > 0) await _context.SaveChangesAsync();
			return added;
		}

		public async Task<bool> UpdateMemberStatusAsync(int companyId, int memberId, string status)
		{
			var m = await _context.CampaignMembers.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == memberId);
			if (m == null) return false;
			m.Status = string.IsNullOrWhiteSpace(status) ? "Targeted" : status;
			m.RespondedAt = _respondedStatuses.Contains(m.Status) ? (m.RespondedAt ?? DateTime.UtcNow) : null;
			await _context.SaveChangesAsync();
			return true;
		}

		public async Task<bool> RemoveCampaignMemberAsync(int companyId, int memberId)
		{
			var m = await _context.CampaignMembers.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == memberId);
			if (m == null) return false;
			_context.CampaignMembers.Remove(m); await _context.SaveChangesAsync(); return true;
		}

		public async Task<int> AddListToCampaignAsync(int companyId, int campaignId, int listId)
		{
			var members = await _context.CrmListMembers.AsNoTracking().Where(m => m.CompanyID == companyId && m.ListId == listId)
				.Select(m => new { m.EntityType, m.EntityId, m.MemberName }).ToListAsync();
			return await AddCampaignMembersAsync(companyId, campaignId, members.Select(m => (m.EntityType, m.EntityId, m.MemberName)));
		}

		// ---------------- Marketing lists (3-5) ----------------
		public async Task<(List<ListRow> rows, int total)> SearchListsAsync(int companyId, string? q, int page, int pageSize)
		{
			var query = _context.CrmMarketingLists.AsNoTracking().Where(l => l.CompanyID == companyId)
				.Select(l => new ListRow { Id = l.ID, Name = l.Name, NameEn = l.NameEn, Description = l.Description, DescriptionEn = l.DescriptionEn, IsActive = l.IsActive,
					Members = _context.CrmListMembers.Count(m => m.CompanyID == companyId && m.ListId == l.ID) });
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<ListRow>(terms, s => r => r.Name.Contains(s) || (r.NameEn != null && r.NameEn.Contains(s)) || (r.Description != null && r.Description.Contains(s)));
				if (pred != null) query = query.Where(pred);
			}
			var total = await query.CountAsync();
			page = Clamp(page, ref pageSize);
			var rows = await query.OrderByDescending(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			return (rows, total);
		}

		public async Task<List<(int id, string name)>> GetListsForPickAsync(int companyId)
		{
			var rows = await _context.CrmMarketingLists.AsNoTracking().Where(l => l.CompanyID == companyId && l.IsActive)
				.OrderByDescending(l => l.ID).Select(l => new { l.ID, l.Name }).ToListAsync();
			return rows.Select(r => (r.ID, r.Name)).ToList();
		}

		public async Task<CrmMarketingList?> GetListAsync(int companyId, int id)
		{
			var l = await _context.CrmMarketingLists.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == id);
			if (l == null) return null;
			if (!await OwnerVisibleAsync(l.OwnerEmployeeId)) return null;
			l.Members = await _context.CrmListMembers.AsNoTracking().Where(m => m.CompanyID == companyId && m.ListId == id).OrderByDescending(m => m.ID).ToListAsync();
			return l;
		}

		public async Task<(bool ok, string? error, int id)> SaveListAsync(int companyId, CrmMarketingList dto, string? userId)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "List name is required", 0);
			CrmMarketingList e;
			if (dto.ID > 0) { e = await _context.CrmMarketingLists.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == dto.ID) ?? throw new InvalidOperationException("غير موجودة"); }
			else { e = new CrmMarketingList { CompanyID = companyId, CreatedAt = DateTime.UtcNow, CreatedBy = userId, OwnerEmployeeId = Me() }; _context.CrmMarketingLists.Add(e); }
			e.Name = dto.Name.Trim(); e.NameEn = dto.NameEn; e.Description = dto.Description; e.DescriptionEn = dto.DescriptionEn; e.IsActive = dto.IsActive;
			await _context.SaveChangesAsync();
			return (true, null, e.ID);
		}

		public async Task<int> AddListMembersAsync(int companyId, int listId, IEnumerable<(string entityType, int entityId, string? name)> members)
		{
			var have = (await _context.CrmListMembers.Where(m => m.CompanyID == companyId && m.ListId == listId)
				.Select(m => new { m.EntityType, m.EntityId }).ToListAsync()).Select(e => e.EntityType + ":" + e.EntityId).ToHashSet();
			if (!await _context.CrmMarketingLists.AsNoTracking().AnyAsync(l => l.CompanyID == companyId && l.ID == listId)) return 0;
			int added = 0;
			foreach (var (etype, eid, name) in members)
			{
				if (string.IsNullOrWhiteSpace(etype) || eid <= 0) continue;
				if (!have.Add(etype + ":" + eid)) continue;
				if (!await EntityRefOkAsync(companyId, etype, eid)) continue;   // same rule as the campaign audience
				_context.CrmListMembers.Add(new CrmListMember { CompanyID = companyId, ListId = listId, EntityType = etype, EntityId = eid, MemberName = name, CreatedAt = DateTime.UtcNow });
				added++;
			}
			if (added > 0) await _context.SaveChangesAsync();
			return added;
		}

		public async Task<bool> RemoveListMemberAsync(int companyId, int memberId)
		{
			var m = await _context.CrmListMembers.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == memberId);
			if (m == null) return false;
			_context.CrmListMembers.Remove(m); await _context.SaveChangesAsync(); return true;
		}

		// Generic select2 source for member pickers — Lead | Account | Contact.
		public async Task<List<(int id, string name)>> PickEntitiesAsync(int companyId, string entityType, string? term)
		{
			var t = (term ?? "").Trim();
			switch (entityType)
			{
				case "Account":
					return await GetAccountsForPickAsync(companyId, term);
				case "Lead":
				{
					var q = _context.Leads.AsNoTracking().Where(l => l.CompanyID == companyId);
					if (t.Length > 0) q = q.Where(l => l.Name.Contains(t) || (l.Company != null && l.Company.Contains(t)));
					var rows = await q.OrderByDescending(l => l.ID).Take(20).Select(l => new { l.ID, l.Name, l.Company }).ToListAsync();
					return rows.Select(r => (r.ID, string.IsNullOrWhiteSpace(r.Company) ? r.Name : $"{r.Name} — {r.Company}")).ToList();
				}
				case "Contact":
				{
					var q = _context.CrmContacts.AsNoTracking().Where(c => c.CompanyID == companyId);
					if (t.Length > 0) q = q.Where(c => c.Name.Contains(t) || (c.Email != null && c.Email.Contains(t)));
					var rows = await q.OrderByDescending(c => c.ID).Take(20).Select(c => new { c.ID, c.Name, c.Email }).ToListAsync();
					return rows.Select(r => (r.ID, string.IsNullOrWhiteSpace(r.Email) ? r.Name : $"{r.Name} <{r.Email}>")).ToList();
				}
				default: return new();
			}
		}

		// ---------------- SLA policies + tickets (3-6) ----------------
		public async Task<List<CrmSlaPolicy>> GetSlaPoliciesAsync(int companyId) =>
			await _context.CrmSlaPolicies.AsNoTracking().Where(p => p.CompanyID == companyId).OrderBy(p => p.ID).ToListAsync();

		public Task<CrmSlaPolicy?> GetSlaPolicyAsync(int companyId, int id) =>
			_context.CrmSlaPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.CompanyID == companyId && p.ID == id);

		public async Task<(bool ok, string? error)> SaveSlaPolicyAsync(int companyId, CrmSlaPolicy dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "Policy name is required");
			if (dto.FirstResponseMins <= 0 || dto.ResolutionMins <= 0) return (false, "The durations must be greater than zero");
			CrmSlaPolicy e;
			if (dto.ID > 0) { e = await _context.CrmSlaPolicies.FirstOrDefaultAsync(p => p.CompanyID == companyId && p.ID == dto.ID) ?? throw new InvalidOperationException("Not found"); }
			else { e = new CrmSlaPolicy { CompanyID = companyId, CreatedAt = DateTime.UtcNow }; _context.CrmSlaPolicies.Add(e); }
			e.Name = dto.Name.Trim(); e.Priority = string.IsNullOrWhiteSpace(dto.Priority) ? "Normal" : dto.Priority;
			e.FirstResponseMins = dto.FirstResponseMins; e.ResolutionMins = dto.ResolutionMins; e.IsActive = dto.IsActive;
			await _context.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(List<TicketRow> rows, int total)> SearchTicketsAsync(int companyId, string? q, string? status, string? priority, int page, int pageSize)
		{
			var isEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
			var query = from t in _context.CrmTickets.AsNoTracking().Where(t => t.CompanyID == companyId)
						join a in _context.CrmAccounts.AsNoTracking() on t.AccountId equals (int?)a.ID into ga
						from a in ga.DefaultIfEmpty()
						select new TicketRow {
							Id = t.ID, Subject = t.Subject, SubjectEn = t.SubjectEn, AccountId = t.AccountId, AccountName = a != null ? (isEn ? (a.NameEn ?? a.Name) : a.Name) : null,
							Priority = t.Priority, Status = t.Status, OwnerEmployeeId = t.OwnerEmployeeId, CreatedAt = t.CreatedAt,
							FirstResponseDueAt = t.FirstResponseDueAt, ResolutionDueAt = t.ResolutionDueAt,
							FirstRespondedAt = t.FirstRespondedAt, ResolvedAt = t.ResolvedAt, ClosedAt = t.ClosedAt };
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<TicketRow>(terms, s => r => r.Subject.Contains(s) || (r.AccountName != null && r.AccountName.Contains(s)));
				if (pred != null) query = query.Where(pred);
			}
			if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
			if (!string.IsNullOrWhiteSpace(priority)) query = query.Where(r => r.Priority == priority);
			var scope = await ScopeAsync();
			if (scope != null) query = query.Where(r => r.OwnerEmployeeId == null || scope.Contains(r.OwnerEmployeeId.Value));
			var total = await query.CountAsync();
			page = Clamp(page, ref pageSize);
			var rows = await query.OrderByDescending(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			return (rows, total);
		}

		public async Task<CrmTicket?> GetTicketAsync(int companyId, int id)
		{
			var e = await _context.CrmTickets.AsNoTracking().FirstOrDefaultAsync(t => t.CompanyID == companyId && t.ID == id);
			return e != null && await OwnerVisibleAsync(e.OwnerEmployeeId) ? e : null;
		}

		public async Task<(bool ok, string? error, int id)> SaveTicketAsync(int companyId, CrmTicket dto, string? userId)
		{
			if (string.IsNullOrWhiteSpace(dto.Subject)) return (false, "Ticket title is required", 0);
			var priority = string.IsNullOrWhiteSpace(dto.Priority) ? "Normal" : dto.Priority;
			CrmTicket e;
			if (dto.ID > 0)
			{
				e = await _context.CrmTickets.FirstOrDefaultAsync(t => t.CompanyID == companyId && t.ID == dto.ID) ?? throw new InvalidOperationException("غير موجودة");
				var priorityChanged = e.Priority != priority;
				e.Subject = dto.Subject.Trim(); e.SubjectEn = string.IsNullOrWhiteSpace(dto.SubjectEn) ? null : dto.SubjectEn.Trim(); e.Description = dto.Description; e.DescriptionEn = string.IsNullOrWhiteSpace(dto.DescriptionEn) ? null : dto.DescriptionEn.Trim(); e.AccountId = dto.AccountId; e.ContactId = dto.ContactId;
				e.CustomerId = dto.CustomerId; e.Category = dto.Category; e.CategoryEn = string.IsNullOrWhiteSpace(dto.CategoryEn) ? null : dto.CategoryEn.Trim(); e.Priority = priority;
				if (dto.OwnerEmployeeId.HasValue) e.OwnerEmployeeId = dto.OwnerEmployeeId;
				if (priorityChanged) await StampSlaAsync(companyId, e);
			}
			else
			{
				e = new CrmTicket { CompanyID = companyId, CreatedAt = DateTime.UtcNow, CreatedBy = userId, Status = "New", Priority = priority,
					Subject = dto.Subject.Trim(), SubjectEn = string.IsNullOrWhiteSpace(dto.SubjectEn) ? null : dto.SubjectEn.Trim(), Description = dto.Description, DescriptionEn = string.IsNullOrWhiteSpace(dto.DescriptionEn) ? null : dto.DescriptionEn.Trim(), AccountId = dto.AccountId, ContactId = dto.ContactId,
					CustomerId = dto.CustomerId, Category = dto.Category, CategoryEn = string.IsNullOrWhiteSpace(dto.CategoryEn) ? null : dto.CategoryEn.Trim(), OwnerEmployeeId = dto.OwnerEmployeeId ?? Me() };
				_context.CrmTickets.Add(e);
				await StampSlaAsync(companyId, e);
			}
			await _context.SaveChangesAsync();
			return (true, null, e.ID);
		}

		// stamp SLA policy + due dates from the active policy matching the ticket priority
		private async Task StampSlaAsync(int companyId, CrmTicket t)
		{
			var pol = await _context.CrmSlaPolicies.AsNoTracking()
				.FirstOrDefaultAsync(p => p.CompanyID == companyId && p.IsActive && p.Priority == t.Priority);
			var baseTime = t.CreatedAt ?? DateTime.UtcNow;
			if (pol != null)
			{
				t.SlaPolicyId = pol.ID;
				t.FirstResponseDueAt = baseTime.AddMinutes(pol.FirstResponseMins);
				t.ResolutionDueAt = baseTime.AddMinutes(pol.ResolutionMins);
			}
			else { t.SlaPolicyId = null; t.FirstResponseDueAt = null; t.ResolutionDueAt = null; }
		}

		public async Task<bool> UpdateTicketStatusAsync(int companyId, int id, string status)
		{
			var t = await _context.CrmTickets.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == id);
			if (t == null) return false;
			t.Status = string.IsNullOrWhiteSpace(status) ? t.Status : status;
			var now = DateTime.UtcNow;
			if (t.Status != "New" && t.FirstRespondedAt == null) t.FirstRespondedAt = now;
			if (t.Status == "Resolved" && t.ResolvedAt == null) t.ResolvedAt = now;
			if (t.Status == "Closed") { if (t.ResolvedAt == null) t.ResolvedAt = now; t.ClosedAt = now; }
			if (t.Status != "Resolved" && t.Status != "Closed") { t.ResolvedAt = null; t.ClosedAt = null; }
			await _context.SaveChangesAsync();
			return true;
		}

		public async Task<(int open, int breachedFr, int breachedRes)> TicketStatsAsync(int companyId)
		{
			var now = DateTime.UtcNow;
			var open = await _context.CrmTickets.CountAsync(t => t.CompanyID == companyId && t.Status != "Resolved" && t.Status != "Closed");
			var fr = await _context.CrmTickets.CountAsync(t => t.CompanyID == companyId && t.Status != "Resolved" && t.Status != "Closed"
				&& t.FirstRespondedAt == null && t.FirstResponseDueAt != null && t.FirstResponseDueAt < now);
			var res = await _context.CrmTickets.CountAsync(t => t.CompanyID == companyId && t.Status != "Resolved" && t.Status != "Closed"
				&& t.ResolvedAt == null && t.ResolutionDueAt != null && t.ResolutionDueAt < now);
			return (open, fr, res);
		}

		// ---------------- Scoring + settings + forecast (3-7) ----------------
		public async Task<List<CrmScoringRule>> GetScoringRulesAsync(int companyId) =>
			await _context.CrmScoringRules.AsNoTracking().Where(r => r.CompanyID == companyId).OrderBy(r => r.ID).ToListAsync();

		public async Task<(bool ok, string? error)> SaveScoringRuleAsync(int companyId, CrmScoringRule dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "Rule name is required");
			CrmScoringRule e;
			if (dto.ID > 0) { e = await _context.CrmScoringRules.FirstOrDefaultAsync(r => r.CompanyID == companyId && r.ID == dto.ID) ?? throw new InvalidOperationException("غير موجودة"); }
			else { e = new CrmScoringRule { CompanyID = companyId, CreatedAt = DateTime.UtcNow }; _context.CrmScoringRules.Add(e); }
			e.Name = dto.Name.Trim(); e.NameEn = string.IsNullOrWhiteSpace(dto.NameEn) ? null : dto.NameEn.Trim(); e.Field = string.IsNullOrWhiteSpace(dto.Field) ? "Source" : dto.Field;
			e.Operator = string.IsNullOrWhiteSpace(dto.Operator) ? "eq" : dto.Operator; e.Value = dto.Value; e.Points = dto.Points; e.IsActive = dto.IsActive;
			await _context.SaveChangesAsync();
			return (true, null);
		}

		public async Task<CrmSettings> GetCrmSettingsAsync(int companyId)
		{
			var s = await _context.CrmSettings.FirstOrDefaultAsync(x => x.CompanyID == companyId);
			if (s == null) { s = new CrmSettings { CompanyID = companyId, CreatedAt = DateTime.UtcNow }; _context.CrmSettings.Add(s); await _context.SaveChangesAsync(); }
			return s;
		}

		public async Task<(bool ok, string? error)> SaveCrmSettingsAsync(int companyId, CrmSettings dto)
		{
			var s = await GetCrmSettingsAsync(companyId);
			s.AutoRouteLeads = dto.AutoRouteLeads;
			s.HotScore = dto.HotScore < 0 ? 0 : dto.HotScore;
			s.WarmScore = dto.WarmScore < 0 ? 0 : dto.WarmScore;
			await _context.SaveChangesAsync();
			return (true, null);
		}

		public async Task<int> RecomputeAllLeadScoresAsync(int companyId)
		{
			var rules = await _context.CrmScoringRules.AsNoTracking().Where(r => r.CompanyID == companyId && r.IsActive).ToListAsync();
			var leads = await _context.Leads.Where(l => l.CompanyID == companyId).ToListAsync();
			foreach (var l in leads) l.Score = ScoreLead(rules, l);
			await _context.SaveChangesAsync();
			return leads.Count;
		}

		public async Task<List<ForecastRow>> GetForecastAsync(int companyId)
		{
			var q = _context.Opportunities.AsNoTracking().Where(o => o.CompanyID == companyId && o.ExpectedCloseDate != null && o.Stage != "Lost");
			var scope = await ScopeAsync();
			if (scope != null) q = q.Where(o => o.OwnerEmployeeId == null || scope.Contains(o.OwnerEmployeeId.Value));
			var list = await q.Select(o => new { o.ExpectedCloseDate, o.Amount, o.Probability, o.Stage }).ToListAsync();
			return list.GroupBy(o => o.ExpectedCloseDate!.Value.ToString("yyyy-MM"))
				.Select(g => new ForecastRow {
					Month = g.Key,
					OpenCount = g.Count(x => x.Stage != "Won"),
					OpenAmount = g.Where(x => x.Stage != "Won").Sum(x => x.Amount),
					WeightedAmount = g.Where(x => x.Stage != "Won").Sum(x => x.Amount * x.Probability / 100m),
					WonAmount = g.Where(x => x.Stage == "Won").Sum(x => x.Amount),
				}).OrderBy(r => r.Month).ToList();
		}

		// CRM 3-8: consolidated 360° report — funnel, pipeline, win/loss, activities, tickets, campaigns, reps.
		public async Task<CrmReport> GetCrmReportAsync(int companyId)
		{
			var scope = await ScopeAsync();
			var rep = new CrmReport();

			// leads (+ scoring bands)
			var lq = _context.Leads.AsNoTracking().Where(l => l.CompanyID == companyId);
			if (scope != null) lq = lq.Where(l => l.OwnerEmployeeId == null || scope.Contains(l.OwnerEmployeeId.Value));
			var leads = await lq.Select(l => new { l.Status, l.Score, l.OwnerEmployeeId }).ToListAsync();
			var st = await GetCrmSettingsAsync(companyId);
			rep.LeadsByStatus = leads.GroupBy(l => l.Status ?? "New").ToDictionary(g => g.Key, g => g.Count());
			rep.LeadsHot = leads.Count(l => l.Score >= st.HotScore);
			rep.LeadsWarm = leads.Count(l => l.Score < st.HotScore && l.Score >= st.WarmScore);
			rep.LeadsCold = leads.Count(l => l.Score < st.WarmScore);

			// opportunities (pipeline / win-loss)
			var oq = _context.Opportunities.AsNoTracking().Where(o => o.CompanyID == companyId);
			if (scope != null) oq = oq.Where(o => o.OwnerEmployeeId == null || scope.Contains(o.OwnerEmployeeId.Value));
			var opps = await oq.Select(o => new { o.Stage, o.Amount, o.Probability, o.OwnerEmployeeId, o.CampaignId }).ToListAsync();
			var open = opps.Where(o => o.Stage != "Won" && o.Stage != "Lost").ToList();
			rep.Pipeline = open.GroupBy(o => o.Stage).Select(g => new PipelineStage { Stage = g.Key, Count = g.Count(), Amount = g.Sum(x => x.Amount) }).OrderByDescending(p => p.Amount).ToList();
			rep.PipelineWeighted = open.Sum(o => o.Amount * o.Probability / 100m);
			rep.OppOpen = open.Count;
			rep.OppWon = opps.Count(o => o.Stage == "Won");
			rep.OppLost = opps.Count(o => o.Stage == "Lost");
			rep.WonValue = opps.Where(o => o.Stage == "Won").Sum(o => o.Amount);
			var decided = rep.OppWon + rep.OppLost;
			rep.WinRatePct = decided > 0 ? Math.Round((decimal)rep.OppWon / decided * 100, 1) : (decimal?)null;

			// activities
			var aq = _context.Activities.AsNoTracking().Where(a => a.CompanyID == companyId);
			if (scope != null) aq = aq.Where(a => a.OwnerEmployeeId == null || scope.Contains(a.OwnerEmployeeId.Value));
			var acts = await aq.Select(a => new { a.Done, a.DueDate }).ToListAsync();
			var today = DateTime.UtcNow.Date;
			rep.ActOpen = acts.Count(a => !a.Done);
			rep.ActDone = acts.Count(a => a.Done);
			rep.ActOverdue = acts.Count(a => !a.Done && a.DueDate.HasValue && a.DueDate.Value.Date < today);

			// tickets
			var (tOpen, tFr, tRes) = await TicketStatsAsync(companyId);
			rep.TicketsOpen = tOpen; rep.TicketsBreached = tFr + tRes;

			// campaigns (ROI overall)
			var camps = await _context.Campaigns.AsNoTracking().Where(c => c.CompanyID == companyId).Select(c => new { c.Budget }).ToListAsync();
			rep.CampaignCount = camps.Count;
			rep.CampaignBudget = camps.Sum(c => c.Budget);
			rep.CampaignWon = opps.Where(o => o.Stage == "Won" && o.CampaignId != null).Sum(o => o.Amount);
			rep.CampaignRoiPct = rep.CampaignBudget > 0 ? Math.Round((rep.CampaignWon - rep.CampaignBudget) / rep.CampaignBudget * 100, 1) : (decimal?)null;

			// per-rep performance (top 10 by won value)
			var ownerIds = leads.Where(l => l.OwnerEmployeeId != null).Select(l => l.OwnerEmployeeId!.Value)
				.Concat(opps.Where(o => o.OwnerEmployeeId != null).Select(o => o.OwnerEmployeeId!.Value)).Distinct().ToList();
			var repIsEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
			var names = await _context.Employee.AsNoTracking().Where(e => ownerIds.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync();
			var nameMap = names.ToDictionary(n => n.ID, n => (repIsEn && !string.IsNullOrWhiteSpace(n.FullNameEn) ? n.FullNameEn! : n.FullName) ?? ("#" + n.ID));
			rep.Owners = ownerIds.Select(id => new OwnerPerf {
				EmployeeId = id, Name = nameMap.TryGetValue(id, out var nm) ? nm : ("#" + id),
				Leads = leads.Count(l => l.OwnerEmployeeId == id),
				OpenOpps = open.Count(o => o.OwnerEmployeeId == id),
				OpenValue = open.Where(o => o.OwnerEmployeeId == id).Sum(o => o.Amount),
				WonValue = opps.Where(o => o.Stage == "Won" && o.OwnerEmployeeId == id).Sum(o => o.Amount),
			}).OrderByDescending(o => o.WonValue).ThenByDescending(o => o.OpenValue).Take(10).ToList();

			return rep;
		}

		// ---------------- Activities ----------------
		public async Task<(List<Activity> rows, int total)> SearchActivitiesAsync(int companyId, string? q, bool? done, int page, int pageSize)
		{
			var query = _context.Activities.AsNoTracking().Where(a => a.CompanyID == companyId);
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<Activity>(terms, s => a => a.Subject.Contains(s) || a.Type.Contains(s) || (a.Notes != null && a.Notes.Contains(s)));
				if (pred != null) query = query.Where(pred);
			}
			if (done.HasValue) query = query.Where(a => a.Done == done.Value);
			var scope = await ScopeAsync();
			if (scope != null) query = query.Where(a => a.OwnerEmployeeId == null || scope.Contains(a.OwnerEmployeeId.Value));

			var total = await query.CountAsync();
			page = Clamp(page, ref pageSize);
			var rows = await query.OrderBy(a => a.Done).ThenBy(a => a.DueDate ?? DateTime.MaxValue).ThenByDescending(a => a.ID).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			return (rows, total);
		}

		public async Task<(bool ok, string? error)> SaveActivityAsync(int companyId, Activity dto, string? userId)
		{
			if (string.IsNullOrWhiteSpace(dto.Subject)) return (false, "Activity subject is required");
			// An activity is the CRM interaction record, so its links are the history: pointing one at
			// another tenant's lead would file this company's call under that company's story.
			if (!await RefOkAsync(dto.LeadId, x => LeadInCompanyAsync(companyId, x))) return (false, ForeignReference);
			if (!await RefOkAsync(dto.OpportunityId, x => OpportunityInCompanyAsync(companyId, x))) return (false, ForeignReference);
			if (!await RefOkAsync(dto.CustomerId, x => CustomerInCompanyAsync(companyId, x))) return (false, ForeignReference);
			if (!await EntityRefOkAsync(companyId, dto.EntityType, dto.EntityId)) return (false, ForeignReference);
			Activity e;
			if (dto.ID > 0)
			{
				e = await _context.Activities.FirstOrDefaultAsync(a => a.CompanyID == companyId && a.ID == dto.ID) ?? throw new InvalidOperationException("Not found");
				if (!await OwnerVisibleAsync(e.OwnerEmployeeId)) throw new InvalidOperationException(HiddenRecord);
			}
			else { e = new Activity { CompanyID = companyId, CreatedAt = DateTime.UtcNow, CreatedBy = userId, OwnerEmployeeId = dto.OwnerEmployeeId ?? Me() }; _context.Activities.Add(e); }

			// Read BEFORE the assignment: whether this activity was already outstanding work decides
			// which of the two follow-up facts — if either — this save represents.
			bool wasWork = dto.ID > 0 && IsOutstandingWork(e);

			e.Type = string.IsNullOrWhiteSpace(dto.Type) ? "Task" : dto.Type; e.Subject = dto.Subject.Trim(); e.SubjectEn = string.IsNullOrWhiteSpace(dto.SubjectEn) ? null : dto.SubjectEn.Trim();
			e.DueDate = dto.DueDate; e.Done = dto.Done; e.LeadId = dto.LeadId; e.OpportunityId = dto.OpportunityId; e.CustomerId = dto.CustomerId; e.Notes = dto.Notes;
			e.EntityType = string.IsNullOrWhiteSpace(dto.EntityType) ? null : dto.EntityType.Trim();
			e.EntityId = dto.EntityId;
			if (e.ReminderAt != dto.ReminderAt) { e.ReminderAt = dto.ReminderAt; e.Reminded = false; }   // re-arm on change
			if (dto.OwnerEmployeeId.HasValue) e.OwnerEmployeeId = dto.OwnerEmployeeId;

			// WORK BEGINS OR WORK ENDS, and only a change between the two is announced. An activity
			// that was already outstanding work and still is has not started anything; re-saving it
			// must not open a second follow-up.
			bool isWork = IsOutstandingWork(e);
			bool becameWork = !wasWork && isWork;
			bool finished = wasWork && e.Done;

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			await _context.SaveChangesAsync();
			if (becameWork) await RecordFollowUpAsync(e, completed: false);
			else if (finished) await RecordFollowUpAsync(e, completed: true);
			await tx.CommitAsync();
			return (true, null);
		}

		public async Task<bool> ToggleActivityAsync(int companyId, int id)
		{
			var a = await _context.Activities.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == id);
			if (a == null) return false;
			if (!await OwnerVisibleAsync(a.OwnerEmployeeId)) return false;   // hidden reads as absent, and absent cannot be toggled

			bool wasWork = IsOutstandingWork(a);
			a.Done = !a.Done;

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			await _context.SaveChangesAsync();

			// TICKING IT OFF ENDS THE WORK. Un-ticking it does NOT reopen the work, and that is a
			// recorded gap rather than an omission: the projection vocabulary has Create, Complete and
			// SyncProgress and no Reopen, so announcing a re-opening would produce an event no
			// consumer could act on. CRM-FOLLOWUP-REOPEN-NOT-PROJECTED.
			if (wasWork && a.Done) await RecordFollowUpAsync(a, completed: true);
			await tx.CommitAsync();
			return true;
		}

		// CRM 3-4: every activity attached to one entity, newest first, scope-filtered.
		public async Task<List<Activity>> GetTimelineAsync(int companyId, string entityType, int entityId)
		{
			var query = _context.Activities.AsNoTracking()
				.Where(a => a.CompanyID == companyId && a.EntityType == entityType && a.EntityId == entityId);
			var scope = await ScopeAsync();
			if (scope != null) query = query.Where(a => a.OwnerEmployeeId == null || scope.Contains(a.OwnerEmployeeId.Value));
			return await query.OrderByDescending(a => a.CreatedAt ?? DateTime.MinValue).ThenByDescending(a => a.ID).ToListAsync();
		}

		// Due, un-fired reminders on still-open activities (no scope filter — runs in the background for all owners).
		public async Task<List<Activity>> GetDueRemindersAsync(int companyId, DateTime nowUtc) =>
			await _context.Activities.AsNoTracking()
				.Where(a => a.CompanyID == companyId && !a.Reminded && !a.Done && a.ReminderAt != null && a.ReminderAt <= nowUtc)
				.OrderBy(a => a.ReminderAt).Take(200).ToListAsync();

		public async Task MarkRemindedAsync(int companyId, IEnumerable<int> ids)
		{
			var list = ids.Distinct().ToList();
			if (list.Count == 0) return;
			var rows = await _context.Activities.Where(a => a.CompanyID == companyId && list.Contains(a.ID)).ToListAsync();
			foreach (var r in rows) r.Reminded = true;
			await _context.SaveChangesAsync();
		}

		// ---------------- Accounts + Contacts (3-2) ----------------
		public async Task<(List<AccountRow> rows, int total)> SearchAccountsAsync(int companyId, string? q, int page, int pageSize,
			int[]? tagIds = null, bool tagMatchAll = false, bool unassignedOnly = false)
		{
			var query = _context.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == companyId);
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<CrmAccount>(terms, s => a => a.Name.Contains(s) || (a.NameEn != null && a.NameEn.Contains(s))
					|| (a.Phone != null && a.Phone.Contains(s)) || (a.Industry != null && a.Industry.Contains(s)) || (a.Segment != null && a.Segment.Contains(s)));
				if (pred != null) query = query.Where(pred);
			}
			var scope = await ScopeAsync();
			if (scope != null) query = query.Where(a => a.OwnerEmployeeId == null || scope.Contains(a.OwnerEmployeeId.Value));
			// Accounts carry the same authoritative owner field, so Unassigned means the same thing
			// here. Overdue follow-up does NOT reach this list: Activity has typed columns for a lead
			// and an opportunity and none for an account, so an account "follow-up" would be a
			// relationship the model does not state.
			if (unassignedOnly) query = query.Where(a => a.OwnerEmployeeId == null);
			var tagged = await TagMatchedIdsAsync(companyId, Platform.EntityRegistry.CrmAccount, query.Select(a => a.ID), tagIds, tagMatchAll);
			if (tagged != null) query = query.Where(a => tagged.Contains(a.ID));

			var total = await query.CountAsync();
			page = Clamp(page, ref pageSize);
			var rows = await query.OrderByDescending(a => a.ID).Skip((page - 1) * pageSize).Take(pageSize)
				.Select(a => new AccountRow { Id = a.ID, Name = a.Name, NameEn = a.NameEn, Industry = a.Industry, IndustryEn = a.IndustryEn, Segment = a.Segment, Phone = a.Phone, CustomerId = a.CustomerId, IsActive = a.IsActive,
					Contacts = _context.CrmContacts.Count(c => c.CompanyID == companyId && c.AccountId == a.ID), Opps = _context.Opportunities.Count(o => o.CompanyID == companyId && o.AccountId == a.ID) }).ToListAsync();
			return (rows, total);
		}

		public async Task<CrmAccount?> GetAccountAsync(int companyId, int id)
		{
			var a = await _context.CrmAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == id);
			if (a == null) return null;
			// Checked before the contacts are loaded: a hidden account must not cause its people to be read.
			if (!await OwnerVisibleAsync(a.OwnerEmployeeId)) return null;
			a.Contacts = await _context.CrmContacts.AsNoTracking().Where(c => c.CompanyID == companyId && c.AccountId == id).OrderByDescending(c => c.IsPrimary).ThenBy(c => c.Name).ToListAsync();
			return a;
		}

		public async Task<(bool ok, string? error, int id)> SaveAccountAsync(int companyId, CrmAccount dto, string? userId, int[]? tagIds = null)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "Account name is required", 0);
			CrmAccount e;
			if (dto.ID > 0)
			{
				var existing = await _context.CrmAccounts.FirstOrDefaultAsync(a => a.CompanyID == companyId && a.ID == dto.ID);
                if (existing == null) return (false, "Not found", 0);
                e = existing;
				if (!await OwnerVisibleAsync(e.OwnerEmployeeId)) return (false, "Not found", 0);
			}
			else { e = new CrmAccount { CompanyID = companyId, CreatedAt = DateTime.UtcNow, CreatedBy = userId, OwnerEmployeeId = dto.OwnerEmployeeId ?? Me() }; _context.CrmAccounts.Add(e); }
			bool isNew = dto.ID <= 0;
			var beforeName = e.Name; var beforeOwner = e.OwnerEmployeeId; var beforeIndustry = e.Industry;
			var beforeSegment = e.Segment; var beforeActive = e.IsActive;

			e.Name = dto.Name.Trim(); e.NameEn = dto.NameEn; e.Industry = dto.Industry; e.IndustryEn = dto.IndustryEn; e.Phone = dto.Phone; e.Email = dto.Email; e.Website = dto.Website;
			e.Address = dto.Address; e.Source = dto.Source; e.Segment = dto.Segment; e.IsActive = dto.IsActive; e.Notes = dto.Notes;
			if (dto.OwnerEmployeeId.HasValue) e.OwnerEmployeeId = dto.OwnerEmployeeId;

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			await _context.SaveChangesAsync();

			await RecordAsync(new Models.Platform.BusinessEventRecord
			{
				EntityCode = Platform.EntityRegistry.CrmAccount,
				EntityId = e.ID,
				EventType = isNew ? Platform.CrmAccountEvents.Created : Platform.CrmAccountEvents.Updated,
				PayloadVersion = Models.Platform.CrmAccountEventPayload.Version,
				Visibility = Models.Platform.BusinessEventVisibility.Internal,
				DedupKey = isNew ? $"CrmAccount.Created:{e.ID}" : null,
				CompanyIdOverride = e.CompanyID,
				Payload = new Models.Platform.CrmAccountEventPayload
				{
					AccountName = e.Name,
					OwnerEmployeeId = e.OwnerEmployeeId,
					Industry = e.Industry,
					Segment = e.Segment,
					// Reported, never created here: an account is linked to a financial customer on
					// Won, and an account write must not open an AR account as a side effect.
					CustomerId = e.CustomerId,
					ChangedFields = isNew ? null : Changed(
						("Name", !string.Equals(beforeName, e.Name, StringComparison.Ordinal)),
						("OwnerEmployeeId", beforeOwner != e.OwnerEmployeeId),
						("Industry", !string.Equals(beforeIndustry, e.Industry, StringComparison.Ordinal)),
						("Segment", !string.Equals(beforeSegment, e.Segment, StringComparison.Ordinal)),
						("IsActive", beforeActive != e.IsActive)),
				},
			});

			var (accTagOk, accTagErr) = await ApplyTagsAsync(companyId, Platform.EntityRegistry.CrmAccount, e.ID, tagIds);
			if (!accTagOk) { await tx.RollbackAsync(); return (false, accTagErr, 0); }

			await tx.CommitAsync();
			return (true, null, e.ID);
		}

		public async Task<List<(int id, string name)>> GetAccountsForPickAsync(int companyId, string? term)
		{
			var t = (term ?? "").Trim();
			var query = _context.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.IsActive);
			if (t.Length > 0) query = query.Where(a => a.Name.Contains(t) || (a.NameEn != null && a.NameEn.Contains(t)) || (a.Phone != null && a.Phone.Contains(t)));
			var scope = await ScopeAsync();
			if (scope != null) query = query.Where(a => a.OwnerEmployeeId == null || scope.Contains(a.OwnerEmployeeId.Value));
			var rows = await query.OrderBy(a => a.Name).Take(20).Select(a => new { a.ID, a.Name }).ToListAsync();
			return rows.Select(r => (r.ID, r.Name)).ToList();
		}

		public async Task<(bool ok, string? error)> SaveContactAsync(int companyId, CrmContact dto)
		{
			if (dto.AccountId <= 0) return (false, "Account is required");
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "Contact name is required");
			// The contact row would carry the CALLER's company while hanging off another tenant's
			// account — a person filed under a party this company cannot even read.
			if (!await AccountInCompanyAsync(companyId, dto.AccountId)) return (false, ForeignReference);
			CrmContact e;
			if (dto.ID > 0) { e = await _context.CrmContacts.FirstOrDefaultAsync(c => c.CompanyID == companyId && c.ID == dto.ID) ?? throw new InvalidOperationException("غير موجود"); }
			else { e = new CrmContact { CompanyID = companyId, AccountId = dto.AccountId, CreatedAt = DateTime.UtcNow, OwnerEmployeeId = Me() }; _context.CrmContacts.Add(e); }
			e.Name = dto.Name.Trim(); e.NameEn = dto.NameEn; e.Title = dto.Title; e.TitleEn = dto.TitleEn; e.Phone = dto.Phone; e.Email = dto.Email; e.IsPrimary = dto.IsPrimary; e.Notes = dto.Notes;
			await _context.SaveChangesAsync();
			return (true, null);
		}

		public async Task<bool> DeleteContactAsync(int companyId, int id)
		{
			var c = await _context.CrmContacts.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == id);
			if (c == null) return false;
			_context.CrmContacts.Remove(c); await _context.SaveChangesAsync(); return true;
		}
	}
}
