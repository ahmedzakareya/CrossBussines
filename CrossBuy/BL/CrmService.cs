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
		Task<(List<Lead> rows, int total)> SearchLeadsAsync(int companyId, string? q, string? status, int page, int pageSize);
		Task<Lead?> GetLeadAsync(int companyId, int id);
		Task<(bool ok, string? error)> SaveLeadAsync(int companyId, Lead dto, string? userId);
		Task<(bool ok, string? error, int accountId)> ConvertLeadToAccountAsync(int companyId, int leadId, string? userId);

		// CRM 3-2: accounts (360 party) + contacts
		Task<(List<AccountRow> rows, int total)> SearchAccountsAsync(int companyId, string? q, int page, int pageSize);
		Task<CrmAccount?> GetAccountAsync(int companyId, int id);
		Task<(bool ok, string? error, int id)> SaveAccountAsync(int companyId, CrmAccount dto, string? userId);
		Task<List<(int id, string name)>> GetAccountsForPickAsync(int companyId, string? term);
		Task<(bool ok, string? error)> SaveContactAsync(int companyId, CrmContact dto);
		Task<bool> DeleteContactAsync(int companyId, int id);

		Task<(List<OppRow> rows, int total)> SearchOpportunitiesAsync(int companyId, string? q, string? stage, int page, int pageSize);
		Task<List<PipelineStage>> PipelineSummaryAsync(int companyId);
		Task<Opportunity?> GetOpportunityAsync(int companyId, int id);
		Task<(bool ok, string? error)> SaveOpportunityAsync(int companyId, Opportunity dto, string? userId);
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
		public CrmService(CrossDbContext context, ICrmCustomerLink link, INotificationService notify, ICrmAccessService access, ISellingService selling) { _context = context; _link = link; _notify = notify; _access = access; _selling = selling; }

		private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

		private static int Clamp(int page, ref int pageSize) { if (page < 1) page = 1; if (pageSize < 1) pageSize = 25; else if (pageSize > 100000) pageSize = 100000; return page; }

		// CRM 3-1 data scope: owner-ids the current user may see (null = unrestricted)
		private async Task<List<int>?> ScopeAsync() => (await _access.VisibleOwnerIdsAsync())?.ToList();
		private int? Me() => _access.CurrentEmployeeId();

		// ---------------- Leads ----------------
		public async Task<(List<Lead> rows, int total)> SearchLeadsAsync(int companyId, string? q, string? status, int page, int pageSize)
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
			var total = await query.CountAsync();
			page = Clamp(page, ref pageSize);
			var rows = await query.OrderByDescending(l => l.ID).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			return (rows, total);
		}

		public Task<Lead?> GetLeadAsync(int companyId, int id) =>
			_context.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.CompanyID == companyId && l.ID == id);

		public async Task<(bool ok, string? error)> SaveLeadAsync(int companyId, Lead dto, string? userId)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "اسم العميل المحتمل مطلوب");
			Lead e;
			if (dto.ID > 0)
			{
				e = await _context.Leads.FirstOrDefaultAsync(l => l.CompanyID == companyId && l.ID == dto.ID) ?? throw new InvalidOperationException("غير موجود");
			}
			else
			{
				// CRM 3-7 routing: a brand-new unowned lead is auto-assigned to a SalesRep when routing is on
				var routed = dto.OwnerEmployeeId ?? await RouteLeadOwnerAsync(companyId) ?? Me();
				e = new Lead { CompanyID = companyId, CreatedAt = DateTime.UtcNow, CreatedBy = userId, OwnerEmployeeId = routed };
				_context.Leads.Add(e);
			}
			e.Name = dto.Name.Trim(); e.NameEn = dto.NameEn; e.Company = dto.Company; e.Phone = dto.Phone; e.Email = dto.Email; e.Source = dto.Source;
			e.Segment = dto.Segment; e.EstimatedValue = dto.EstimatedValue; e.Status = string.IsNullOrWhiteSpace(dto.Status) ? "New" : dto.Status; e.Notes = dto.Notes;
			e.CampaignId = dto.CampaignId;
			if (dto.OwnerEmployeeId.HasValue) e.OwnerEmployeeId = dto.OwnerEmployeeId;
			e.Score = await ComputeLeadScoreAsync(companyId, e);   // CRM 3-7: refresh score from active rules
			await _context.SaveChangesAsync();
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
			if (lead == null) return (false, "العميل المحتمل غير موجود", 0);
			if (lead.AccountId.HasValue) return (false, "تم تحويله لحساب بالفعل", lead.AccountId.Value);
			var acc = new CrmAccount
			{
				CompanyID = companyId, Name = string.IsNullOrWhiteSpace(lead.Company) ? lead.Name : lead.Company!, Phone = lead.Phone, Email = lead.Email,
				Segment = lead.Segment, Source = lead.Source, OwnerEmployeeId = lead.OwnerEmployeeId ?? Me(), IsActive = true, CreatedBy = userId, CreatedAt = DateTime.UtcNow
			};
			_context.CrmAccounts.Add(acc);
			await _context.SaveChangesAsync();
			// primary contact from the lead's person name (when the lead carried a company)
			if (!string.IsNullOrWhiteSpace(lead.Name))
				_context.CrmContacts.Add(new CrmContact { CompanyID = companyId, AccountId = acc.ID, Name = lead.Name, Phone = lead.Phone, Email = lead.Email, IsPrimary = true, OwnerEmployeeId = acc.OwnerEmployeeId, CreatedAt = DateTime.UtcNow });
			lead.AccountId = acc.ID; lead.Status = "Converted";
			await _context.SaveChangesAsync();
			return (true, null, acc.ID);
		}

		// CRM 3-2: ensure a CrmAccount has a linked financial Customer (create + enrich via the seam). Returns customerId.
		private async Task<int?> EnsureAccountCustomerAsync(int companyId, int accountId)
		{
			var acc = await _context.CrmAccounts.FirstOrDefaultAsync(a => a.CompanyID == companyId && a.ID == accountId);
			if (acc == null) return null;
			if (acc.CustomerId.HasValue) return acc.CustomerId.Value;
			var custId = await _link.CreateCustomerAsync(companyId, acc.Name, acc.NameEn, null, null);
			var primary = await _context.CrmContacts.AsNoTracking().Where(c => c.AccountId == accountId).OrderByDescending(c => c.IsPrimary).Select(c => c.Name).FirstOrDefaultAsync();
			await _link.EnrichAsync(custId, acc.Phone, acc.Email, acc.Segment, primary ?? acc.Name);
			acc.CustomerId = custId;
			await _context.SaveChangesAsync();
			return custId;
		}

		// CRM 3-2: on Won, link the account to a financial customer and stamp the opportunity's CustomerId for O2C.
		private async Task LinkWonOpportunityAsync(int companyId, Opportunity o)
		{
			try
			{
				if (o.AccountId.HasValue)
				{
					var custId = await EnsureAccountCustomerAsync(companyId, o.AccountId.Value);
					if (custId.HasValue && o.CustomerId != custId)
					{
						var live = await _context.Opportunities.FirstOrDefaultAsync(x => x.ID == o.ID);
						if (live != null) { live.CustomerId = custId; await _context.SaveChangesAsync(); }
					}
				}
			}
			catch { /* linking failure must not block the stage change */ }
		}

		// ---------------- Opportunities ----------------
		public async Task<(List<OppRow> rows, int total)> SearchOpportunitiesAsync(int companyId, string? q, string? stage, int page, int pageSize)
		{
			var scope = await ScopeAsync();
			var oppSrc = _context.Opportunities.AsNoTracking().Where(o => o.CompanyID == companyId);
			if (scope != null) oppSrc = oppSrc.Where(o => o.OwnerEmployeeId == null || scope.Contains(o.OwnerEmployeeId.Value));
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
			if (o == null) return (false, "الفرصة غير موجودة");
			var pid = o.PipelineId ?? await GetDefaultPipelineIdAsync(companyId);
			var st = await ResolveStageAsync(companyId, pid, stage, null);
			bool becameWon;
			if (st != null)
			{
				becameWon = st.IsWon && o.StageId != st.ID;
				o.PipelineId = pid; o.StageId = st.ID; o.Stage = st.Name; o.Probability = st.Probability;
			}
			else   // no configured pipeline → legacy stage-name behavior
			{
				if (string.IsNullOrWhiteSpace(stage)) return (false, "مرحلة غير صحيحة");
				becameWon = stage == "Won" && o.Stage != "Won";
				o.Stage = stage; o.Probability = stage == "Won" ? 100 : (stage == "Lost" ? 0 : o.Probability);
			}
			await _context.SaveChangesAsync();
			if (becameWon) { await LinkWonOpportunityAsync(companyId, o); await NotifyOpportunityWonAsync(companyId, o); }
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
		private async Task<CrmPipelineStage?> ResolveStageAsync(int companyId, int pipelineId, string? stageName, int? stageId)
		{
			if (pipelineId <= 0) return null;
			var q = _context.CrmPipelineStages.AsNoTracking().Where(s => s.CompanyID == companyId && s.PipelineId == pipelineId);
			if (stageId.HasValue && stageId.Value > 0) return await q.FirstOrDefaultAsync(s => s.ID == stageId.Value);
			if (!string.IsNullOrWhiteSpace(stageName)) { var byName = await q.FirstOrDefaultAsync(s => s.Name == stageName); if (byName != null) return byName; }
			return await q.OrderBy(s => s.Sort).FirstOrDefaultAsync();
		}
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
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "اسم خط الأنابيب مطلوب", 0);
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
			if (opp == null) return (false, "الفرصة غير موجودة");
			_context.OpportunityProducts.RemoveRange(_context.OpportunityProducts.Where(p => p.OpportunityId == oppId));
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
			if (opp == null) return (false, "الفرصة غير موجودة", null);
			if (opp.QuotationId.HasValue) return (false, "تم إنشاء عرض سعر لهذه الفرصة بالفعل", opp.QuotationId);
			if (opp.AccountId == null) return (false, "اربط الفرصة بحساب أولًا", null);
			var products = await _context.OpportunityProducts.AsNoTracking().Where(p => p.OpportunityId == oppId).ToListAsync();
			if (products.Count == 0) return (false, "أضف بنودًا للفرصة قبل التحويل لعرض سعر", null);
			var custId = await EnsureAccountCustomerAsync(companyId, opp.AccountId.Value);
			if (custId == null) return (false, "تعذّر ربط الحساب بعميل مالي", null);

			var lines = products.Select(p => new SoLineInput
			{
				ItemId = p.ItemId, ItemDescription = p.ItemDescription, Qty = p.Qty, UnitPrice = p.UnitPrice,
				DiscountAmount = R2(p.Qty * p.UnitPrice * p.DiscountPercent / 100m), TaxRate = 0
			}).ToList();

			var (ok, err, q) = await _selling.CreateQuotationAsync(companyId, custId.Value, null, DateTime.Today, null, $"من فرصة CRM: {opp.Title}", lines, userId);
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
					$"تم ربح الفرصة «{o.Title}» بقيمة {o.Amount:N2}", $"Opportunity \"{o.Title}\" won ({o.Amount:N2})",
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

		public Task<Opportunity?> GetOpportunityAsync(int companyId, int id) =>
			_context.Opportunities.AsNoTracking().FirstOrDefaultAsync(o => o.CompanyID == companyId && o.ID == id);

		public async Task<(bool ok, string? error)> SaveOpportunityAsync(int companyId, Opportunity dto, string? userId)
		{
			if (string.IsNullOrWhiteSpace(dto.Title)) return (false, "عنوان الفرصة مطلوب");
			Opportunity e;
			if (dto.ID > 0) { e = await _context.Opportunities.FirstOrDefaultAsync(o => o.CompanyID == companyId && o.ID == dto.ID) ?? throw new InvalidOperationException("غير موجود"); }
			else { e = new Opportunity { CompanyID = companyId, CreatedAt = DateTime.UtcNow, CreatedBy = userId, OwnerEmployeeId = dto.OwnerEmployeeId ?? Me() }; _context.Opportunities.Add(e); }
			var prevStageId = e.StageId; var prevStage = e.Stage;
			var pid = dto.PipelineId ?? e.PipelineId ?? await GetDefaultPipelineIdAsync(companyId);
			var st = await ResolveStageAsync(companyId, pid, dto.Stage, dto.StageId);
			e.Title = dto.Title.Trim(); e.TitleEn = string.IsNullOrWhiteSpace(dto.TitleEn) ? null : dto.TitleEn.Trim(); e.AccountId = dto.AccountId; e.CustomerId = dto.CustomerId; e.LeadId = dto.LeadId;
			e.PipelineId = pid > 0 ? pid : (int?)null; e.WinLossReason = dto.WinLossReason;
			bool becameWon;
			if (st != null) { e.StageId = st.ID; e.Stage = st.Name; e.Probability = st.Probability; becameWon = st.IsWon && prevStageId != st.ID; }
			else { e.Stage = string.IsNullOrWhiteSpace(dto.Stage) ? "Prospecting" : dto.Stage; becameWon = e.Stage == "Won" && prevStage != "Won"; }
			e.Amount = dto.Amount; e.ExpectedCloseDate = dto.ExpectedCloseDate; e.Notes = dto.Notes; e.CampaignId = dto.CampaignId;
			if (dto.OwnerEmployeeId.HasValue) e.OwnerEmployeeId = dto.OwnerEmployeeId;
			await _context.SaveChangesAsync();
			if (becameWon) { await LinkWonOpportunityAsync(companyId, e); await NotifyOpportunityWonAsync(companyId, e); }
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

		public Task<Campaign?> GetCampaignAsync(int companyId, int id) =>
			_context.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyID == companyId && c.ID == id);

		public async Task<(bool ok, string? error)> SaveCampaignAsync(int companyId, Campaign dto, string? userId)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "اسم الحملة مطلوب");
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
			int added = 0;
			foreach (var (etype, eid, name) in members)
			{
				if (string.IsNullOrWhiteSpace(etype) || eid <= 0) continue;
				if (!have.Add(etype + ":" + eid)) continue;   // skip duplicates
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
					Members = _context.CrmListMembers.Count(m => m.ListId == l.ID) });
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
			l.Members = await _context.CrmListMembers.AsNoTracking().Where(m => m.CompanyID == companyId && m.ListId == id).OrderByDescending(m => m.ID).ToListAsync();
			return l;
		}

		public async Task<(bool ok, string? error, int id)> SaveListAsync(int companyId, CrmMarketingList dto, string? userId)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "اسم القائمة مطلوب", 0);
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
			int added = 0;
			foreach (var (etype, eid, name) in members)
			{
				if (string.IsNullOrWhiteSpace(etype) || eid <= 0) continue;
				if (!have.Add(etype + ":" + eid)) continue;
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
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "اسم السياسة مطلوب");
			if (dto.FirstResponseMins <= 0 || dto.ResolutionMins <= 0) return (false, "المدد يجب أن تكون أكبر من صفر");
			CrmSlaPolicy e;
			if (dto.ID > 0) { e = await _context.CrmSlaPolicies.FirstOrDefaultAsync(p => p.CompanyID == companyId && p.ID == dto.ID) ?? throw new InvalidOperationException("غير موجودة"); }
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

		public Task<CrmTicket?> GetTicketAsync(int companyId, int id) =>
			_context.CrmTickets.AsNoTracking().FirstOrDefaultAsync(t => t.CompanyID == companyId && t.ID == id);

		public async Task<(bool ok, string? error, int id)> SaveTicketAsync(int companyId, CrmTicket dto, string? userId)
		{
			if (string.IsNullOrWhiteSpace(dto.Subject)) return (false, "عنوان التذكرة مطلوب", 0);
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
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "اسم القاعدة مطلوب");
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
			if (string.IsNullOrWhiteSpace(dto.Subject)) return (false, "موضوع النشاط مطلوب");
			Activity e;
			if (dto.ID > 0) { e = await _context.Activities.FirstOrDefaultAsync(a => a.CompanyID == companyId && a.ID == dto.ID) ?? throw new InvalidOperationException("غير موجود"); }
			else { e = new Activity { CompanyID = companyId, CreatedAt = DateTime.UtcNow, CreatedBy = userId, OwnerEmployeeId = dto.OwnerEmployeeId ?? Me() }; _context.Activities.Add(e); }
			e.Type = string.IsNullOrWhiteSpace(dto.Type) ? "Task" : dto.Type; e.Subject = dto.Subject.Trim(); e.SubjectEn = string.IsNullOrWhiteSpace(dto.SubjectEn) ? null : dto.SubjectEn.Trim();
			e.DueDate = dto.DueDate; e.Done = dto.Done; e.LeadId = dto.LeadId; e.OpportunityId = dto.OpportunityId; e.CustomerId = dto.CustomerId; e.Notes = dto.Notes;
			e.EntityType = string.IsNullOrWhiteSpace(dto.EntityType) ? null : dto.EntityType.Trim();
			e.EntityId = dto.EntityId;
			if (e.ReminderAt != dto.ReminderAt) { e.ReminderAt = dto.ReminderAt; e.Reminded = false; }   // re-arm on change
			if (dto.OwnerEmployeeId.HasValue) e.OwnerEmployeeId = dto.OwnerEmployeeId;
			await _context.SaveChangesAsync();
			return (true, null);
		}

		public async Task<bool> ToggleActivityAsync(int companyId, int id)
		{
			var a = await _context.Activities.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == id);
			if (a == null) return false;
			a.Done = !a.Done; await _context.SaveChangesAsync(); return true;
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
		public async Task<(List<AccountRow> rows, int total)> SearchAccountsAsync(int companyId, string? q, int page, int pageSize)
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
			var total = await query.CountAsync();
			page = Clamp(page, ref pageSize);
			var rows = await query.OrderByDescending(a => a.ID).Skip((page - 1) * pageSize).Take(pageSize)
				.Select(a => new AccountRow { Id = a.ID, Name = a.Name, NameEn = a.NameEn, Industry = a.Industry, IndustryEn = a.IndustryEn, Segment = a.Segment, Phone = a.Phone, CustomerId = a.CustomerId, IsActive = a.IsActive,
					Contacts = _context.CrmContacts.Count(c => c.AccountId == a.ID), Opps = _context.Opportunities.Count(o => o.AccountId == a.ID) }).ToListAsync();
			return (rows, total);
		}

		public async Task<CrmAccount?> GetAccountAsync(int companyId, int id)
		{
			var a = await _context.CrmAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == id);
			if (a == null) return null;
			a.Contacts = await _context.CrmContacts.AsNoTracking().Where(c => c.AccountId == id).OrderByDescending(c => c.IsPrimary).ThenBy(c => c.Name).ToListAsync();
			return a;
		}

		public async Task<(bool ok, string? error, int id)> SaveAccountAsync(int companyId, CrmAccount dto, string? userId)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "اسم الحساب مطلوب", 0);
			CrmAccount e;
			if (dto.ID > 0) { e = await _context.CrmAccounts.FirstOrDefaultAsync(a => a.CompanyID == companyId && a.ID == dto.ID) ?? throw new InvalidOperationException("غير موجود"); }
			else { e = new CrmAccount { CompanyID = companyId, CreatedAt = DateTime.UtcNow, CreatedBy = userId, OwnerEmployeeId = dto.OwnerEmployeeId ?? Me() }; _context.CrmAccounts.Add(e); }
			e.Name = dto.Name.Trim(); e.NameEn = dto.NameEn; e.Industry = dto.Industry; e.IndustryEn = dto.IndustryEn; e.Phone = dto.Phone; e.Email = dto.Email; e.Website = dto.Website;
			e.Address = dto.Address; e.Source = dto.Source; e.Segment = dto.Segment; e.IsActive = dto.IsActive; e.Notes = dto.Notes;
			if (dto.OwnerEmployeeId.HasValue) e.OwnerEmployeeId = dto.OwnerEmployeeId;
			await _context.SaveChangesAsync();
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
			if (dto.AccountId <= 0) return (false, "الحساب مطلوب");
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "اسم جهة الاتصال مطلوب");
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
