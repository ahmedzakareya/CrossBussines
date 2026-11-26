using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Crm;
using Microsoft.EntityFrameworkCore;
using A = CrossBuy.BL.CrmIdentity.CrmAssistRules;
using Risk = CrossBuy.BL.Platform.Ai.CrmOpportunityRiskRules;
using S = CrossBuy.BL.CrmIdentity.CrmSequenceRules;

namespace CrossBuy.BL.CrmIdentity
{
	// CRM C9 — the assistant, built from rules that say why (CrmAssistRules) and facts already in the record.
	//
	// LEAD SCORE: the company's own rule score (Leads.Score) is the FIT; behaviour is added on top from what the lead did
	// — a booked meeting (C6), a message on chat or WhatsApp (C4), a website form (C2), recorded consent (C1), work done
	// on it — and silence takes points away. Every point is stored with its reason, so the list can say WHY.
	// DEAL: the next best actions come from the deal itself (C3: stage requirements, the discount, the committee), the
	// inbox (a customer waiting for an answer) and the pipeline risk rules the AI layer already owns (read, not copied).
	// The SUMMARY is the facts of the deal; the DRAFT REPLY is a template filled with them — ready at once, never waiting
	// on a model. MY DAY puts a rep's work for today in one ordered list.
	// No model is called from a request: nothing here can make a screen wait.

	public sealed record LeadInsightView(int Score, string Band, List<A.Reason> Reasons);
	public sealed record SummaryFact(string Code, string? Value, string Tone);   // Tone: ok | warn | bad | info
	public sealed record Draft(string Purpose, string ToName, string Body);
	public sealed record DealAssist(List<SummaryFact> Summary, List<A.Action> Actions, List<Draft> Drafts, IReadOnlyList<Risk.Insight> Risks);
	public sealed record DayItem(string Kind, int Weight, string Title, string? Sub, string Url, DateTime? Due, bool Late, string? Badge);
	public sealed record MyDayView(PersonRef? Me, List<DayItem> Items, List<(int id, string name, string? company, LeadInsightView ins)> HotLeads,
		int TasksToday, int Overdue, int WaitingCustomers, int MeetingsToday, decimal? QuotaMonth, decimal WonMonth);

	public interface ICrmAssistService
	{
		Task<int> RefreshLeadsAsync(int cid, IReadOnlyCollection<int>? onlyLeads = null);
		Task<Dictionary<int, LeadInsightView>> LeadInsightsAsync(int cid, IReadOnlyCollection<int> leadIds);
		Task<DealAssist> DealAsync(int cid, DealFile file, IReadOnlyList<string> unmetLabels);
		Task<Dictionary<int, (int urgency, A.Action? top, Risk.Severity? risk)>> PipelineHintsAsync(int cid, IReadOnlyCollection<int> oppIds);
		Task<MyDayView> MyDayAsync(int cid, int? me, HashSet<int>? visible);
	}

	/// THE ONE HOOK for rewording a draft with a model, left empty on purpose (owner governance first: an approved
	/// external processor and the durable egress audit — see the AI layer's AiExternalProvider/switchboard). The tone is an
	/// ENUM mapped to fixed system prompts, never text a user typed. It is called only from the CRM worker, never from a
	/// request; the template draft is always shown at once. Null means "keep the template".
	public enum DraftTone { Formal, Friendly }
	public interface ICrmDraftRewriter { Task<string?> RewordAsync(int companyId, string draft, DraftTone tone, string language, CancellationToken ct); }
	public sealed class NoDraftRewriter : ICrmDraftRewriter { public Task<string?> RewordAsync(int companyId, string draft, DraftTone tone, string language, CancellationToken ct) => Task.FromResult<string?>(null); }

	public sealed class CrmAssistService : ICrmAssistService
	{
		private readonly CrossDbContext _db;
		public CrmAssistService(CrossDbContext db) { _db = db; }
		static bool IsAr => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
		static readonly JsonSerializerOptions Js = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

		// ------------------------------------------------------------------ leads
		public async Task<int> RefreshLeadsAsync(int cid, IReadOnlyCollection<int>? onlyLeads = null)
		{
			var lq = _db.Leads.AsNoTracking().Where(l => l.CompanyID == cid && l.Status != "Lost");
			if (onlyLeads != null) lq = lq.Where(l => onlyLeads.Contains(l.ID));
			var leads = await lq.Select(l => new { l.ID, l.Score, l.Status, l.CreatedAt }).ToListAsync();
			if (leads.Count == 0) return 0;
			var ids = leads.Select(l => l.ID).ToList();
			var booked = (await _db.Set<CrmBooking>().AsNoTracking().Where(b => b.CompanyId == cid && b.Status == "Booked" && b.LeadId != null && ids.Contains(b.LeadId.Value)).Select(b => b.LeadId!.Value).Distinct().ToListAsync()).ToHashSet();
			var inbound = await (from m in _db.Set<CrmConversationMessage>().AsNoTracking()
								 join c in _db.Set<CrmConversation>().AsNoTracking() on m.ConversationId equals c.ID
								 where c.CompanyId == cid && c.LeadId != null && ids.Contains(c.LeadId.Value) && m.Direction == "In"
								 group m by c.LeadId into g select new { Lead = g.Key!.Value, Last = g.Max(x => x.At) }).ToDictionaryAsync(x => x.Lead, x => x.Last);
			var forms = await _db.Set<CrmWebSubmission>().AsNoTracking().Where(w => w.CompanyId == cid && w.LeadId != null && ids.Contains(w.LeadId.Value))
				.GroupBy(w => w.LeadId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
			var optIn = (await _db.Set<CrmConsent>().AsNoTracking().Where(c => c.CompanyId == cid && c.SubjectType == "Lead" && ids.Contains(c.SubjectId) && c.State == "OptIn").Select(c => c.SubjectId).Distinct().ToListAsync()).ToHashSet();
			var acts = await _db.Activities.AsNoTracking().Where(a => a.CompanyID == cid && a.LeadId != null && ids.Contains(a.LeadId.Value))
				.GroupBy(a => a.LeadId!.Value).Select(g => new { g.Key, Done = g.Count(a => a.Done), Last = g.Max(a => a.CreatedAt) }).ToDictionaryAsync(x => x.Key);
			var existing = await _db.Set<CrmLeadInsight>().Where(x => x.CompanyId == cid && ids.Contains(x.LeadId)).ToDictionaryAsync(x => x.LeadId);
			// the SAME hot/warm thresholds the company set for its rule score, so the two badges speak one language
			var bands = await _db.CrmSettings.AsNoTracking().Where(x => x.CompanyID == cid).Select(x => new { x.HotScore, x.WarmScore }).FirstOrDefaultAsync();
			var now = DateTime.UtcNow;
			foreach (var l in leads)
			{
				var act = acts.GetValueOrDefault(l.ID);
				var s = A.ScoreLead(new A.LeadSignals(l.Score, booked.Contains(l.ID), inbound.TryGetValue(l.ID, out var li) ? li : null, forms.GetValueOrDefault(l.ID),
					optIn.Contains(l.ID), act?.Done ?? 0, act?.Last, l.CreatedAt ?? now, l.Status == "Converted"), now, bands?.HotScore ?? A.Hot, bands?.WarmScore ?? A.Warm);
				if (!existing.TryGetValue(l.ID, out var row)) _db.Set<CrmLeadInsight>().Add(row = new CrmLeadInsight { CompanyId = cid, LeadId = l.ID });
				row.Score = s.Score; row.Band = s.Band; row.ReasonsJson = JsonSerializer.Serialize(s.Reasons, Js); row.ComputedAt = now;
			}
			await _db.SaveChangesAsync();
			return leads.Count;
		}

		public async Task<Dictionary<int, LeadInsightView>> LeadInsightsAsync(int cid, IReadOnlyCollection<int> leadIds)
		{
			var rows = await _db.Set<CrmLeadInsight>().AsNoTracking().Where(x => x.CompanyId == cid && leadIds.Contains(x.LeadId)).ToListAsync();
			return rows.ToDictionary(r => r.LeadId, r => new LeadInsightView(r.Score, r.Band,
				JsonSerializer.Deserialize<List<A.Reason>>(r.ReasonsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()));
		}

		// ------------------------------------------------------------------ one deal
		async Task<int> UnansweredAsync(int cid, int oppId, int? accountId)
		{
			// a conversation is waiting on us when its last message is the customer's
			var convs = await _db.Set<CrmConversation>().AsNoTracking().Where(c => c.CompanyId == cid && c.Status != "Closed" && (c.OpportunityId == oppId || (accountId != null && c.AccountId == accountId)))
				.Select(c => c.ID).ToListAsync();
			var n = 0;
			foreach (var id in convs)
				if (await _db.Set<CrmConversationMessage>().AsNoTracking().Where(m => m.ConversationId == id && (m.Direction == "In" || m.Direction == "Out")).OrderByDescending(m => m.ID).Select(m => m.Direction).FirstOrDefaultAsync() == "In") n++;
			return n;
		}

		public async Task<DealAssist> DealAsync(int cid, DealFile f, IReadOnlyList<string> unmetLabels)
		{
			var o = f.Deal; var now = DateTime.UtcNow;
			var lastAct = f.Timeline.Where(a => a.CreatedAt != null).Select(a => a.CreatedAt!.Value).DefaultIfEmpty(o.CreatedAt ?? now).Max();
			var quiet = (int)(now - lastAct).TotalDays;
			var hasTask = f.Timeline.Any(a => !a.Done);
			var dm = f.Committee.FirstOrDefault(c => c.Role == "DecisionMaker");
			var unanswered = await UnansweredAsync(cid, o.ID, o.AccountId);
			var facts = new A.DealFacts(o.Stage, o.Amount, o.ExpectedCloseDate, quiet, hasTask, unmetLabels, f.Discount == CrmDealRules.DiscountState.Pending,
				f.Discount == CrmDealRules.DiscountState.Rejected, dm != null, unanswered, o.QuotationId != null, o.Probability);
			var actions = A.NextActions(facts, DateTime.Now);
			var risks = Risk.Evaluate(new Risk.Signal { OpportunityId = o.ID, Title = o.Title, AccountId = o.AccountId, Stage = o.Stage, Amount = o.Amount, Probability = o.Probability,
				ExpectedCloseDate = o.ExpectedCloseDate, CreatedAt = o.CreatedAt, OwnerEmployeeId = o.OwnerEmployeeId, LastActivityAt = f.Timeline.Count == 0 ? null : lastAct,
				OpenActivityCount = f.Timeline.Count(a => !a.Done) }, now);
			// the summary: the facts a manager asks first, each with a tone
			var sum = new List<SummaryFact>
			{
				new("stage", f.CurrentIndex >= 0 && f.CurrentIndex < f.Stages.Count ? (IsAr || string.IsNullOrWhiteSpace(f.Stages[f.CurrentIndex].NameEn) ? f.Stages[f.CurrentIndex].Name : f.Stages[f.CurrentIndex].NameEn) : o.Stage, "info"),
				new("amount", o.Amount.ToString("N0"), o.Amount > 0 ? "info" : "warn"),
				new("close", o.ExpectedCloseDate?.ToString("yyyy-MM-dd"), o.ExpectedCloseDate == null ? "warn" : o.ExpectedCloseDate.Value.Date < DateTime.Today ? "bad" : "ok"),
				new("lastTouch", quiet.ToString(), quiet >= 14 ? "bad" : quiet >= 7 ? "warn" : "ok"),
				new("committee", f.Committee.Count.ToString(), dm != null ? "ok" : "warn"),
				new("discount", f.Discount.ToString(), f.Discount switch { CrmDealRules.DiscountState.Rejected => "bad", CrmDealRules.DiscountState.Pending => "warn", _ => "ok" }),
				new("waiting", unanswered.ToString(), unanswered > 0 ? "bad" : "ok"),
			};
			if (unmetLabels.Count > 0) sum.Add(new("unmet", string.Join("، ", unmetLabels), "warn"));
			// the draft: to the decision maker, else the first person on the deal; worded for the most urgent customer-facing action
			var to = dm ?? f.Committee.FirstOrDefault();
			var toName = to?.Name ?? (IsAr ? "العميل" : "the customer");
			var rep = f.Owner?.Name ?? "";
			var purpose = actions.Select(a => a.Code).FirstOrDefault(c => c is "answerCustomer" or "sendQuote" or "reconnect" or "fixCloseDate") ?? "checkIn";
			var title = !IsAr && !string.IsNullOrWhiteSpace(o.TitleEn) ? o.TitleEn! : o.Title;
			var values = new Dictionary<string, string?> { ["first"] = S.FirstName(toName), ["name"] = toName, ["rep"] = rep, ["company"] = IsAr ? f.AccountName : (f.AccountNameEn ?? f.AccountName) };
			string T(string ar, string en) => S.Render(IsAr ? ar : en, values).Replace("{deal}", title);
			var body = purpose switch
			{
				"answerCustomer" => T("أهلاً {first}،\n\nشكراً على رسالتك بخصوص «{deal}». راجعت طلبك وسأرسل لك التفاصيل اليوم.\n\nمع التحية،\n{rep}",
									  "Hello {first},\n\nThank you for your message about \"{deal}\". I have looked at it and will send you the details today.\n\nBest regards,\n{rep}"),
				"sendQuote" => T("أهلاً {first}،\n\nأجهّز لك الآن عرض السعر الخاص بـ «{deal}» ليصلك قبل نهاية الأسبوع. هل هناك أي بند تريد إضافته قبل أن أرسله؟\n\n{rep}",
								 "Hello {first},\n\nI am preparing the quotation for \"{deal}\" so you have it before the end of the week. Is there anything you would like added first?\n\n{rep}"),
				"reconnect" => T("أهلاً {first}،\n\nأردت أن أطمئن على «{deal}». هل ما زال الموضوع قائماً من جهتكم، وهل يناسبك اتصال قصير هذا الأسبوع؟\n\n{rep}",
								 "Hello {first},\n\nI wanted to check in on \"{deal}\". Is it still on your side, and would a short call this week suit you?\n\n{rep}"),
				"fixCloseDate" => T("أهلاً {first}،\n\nكنا نستهدف إتمام «{deal}» في الموعد السابق. ما الموعد الأنسب لكم الآن حتى نرتب التوريد على أساسه؟\n\n{rep}",
									"Hello {first},\n\nWe had aimed to complete \"{deal}\" by the earlier date. What date suits you now, so we can plan the delivery around it?\n\n{rep}"),
				_ => T("أهلاً {first}،\n\nأتابع معك بخصوص «{deal}». هل لديك أي سؤال أستطيع المساعدة فيه؟\n\n{rep}",
					   "Hello {first},\n\nFollowing up on \"{deal}\". Is there anything I can help you with?\n\n{rep}"),
			};
			return new DealAssist(sum, actions, new List<Draft> { new(purpose, toName, body) }, risks);
		}

		/// For the board: each deal's most urgent action and its worst risk, in a handful of queries for the whole page.
		public async Task<Dictionary<int, (int urgency, A.Action? top, Risk.Severity? risk)>> PipelineHintsAsync(int cid, IReadOnlyCollection<int> oppIds)
		{
			var opps = await _db.Opportunities.AsNoTracking().Where(o => o.CompanyID == cid && oppIds.Contains(o.ID)).ToListAsync();
			var acts = await _db.Activities.AsNoTracking().Where(a => a.CompanyID == cid && a.OpportunityId != null && oppIds.Contains(a.OpportunityId.Value))
				.GroupBy(a => a.OpportunityId!.Value).Select(g => new { g.Key, Last = g.Max(a => a.CreatedAt), Open = g.Count(a => !a.Done) }).ToDictionaryAsync(x => x.Key);
			var dms = (await _db.Set<CrmOpportunityContact>().AsNoTracking().Where(c => c.CompanyId == cid && oppIds.Contains(c.OpportunityId) && c.Role == "DecisionMaker").Select(c => c.OpportunityId).ToListAsync()).ToHashSet();
			var appr = await _db.Set<CrmDiscountApproval>().AsNoTracking().Where(a => a.CompanyId == cid && oppIds.Contains(a.OpportunityId)).GroupBy(a => a.OpportunityId)
				.Select(g => new { g.Key, Status = g.OrderByDescending(a => a.ID).Select(a => a.Status).First() }).ToDictionaryAsync(x => x.Key, x => x.Status);
			var now = DateTime.UtcNow; var res = new Dictionary<int, (int, A.Action?, Risk.Severity?)>();
			foreach (var o in opps)
			{
				var a = acts.GetValueOrDefault(o.ID);
				var last = a?.Last ?? o.CreatedAt ?? now;
				var f = new A.DealFacts(o.Stage, o.Amount, o.ExpectedCloseDate, (int)(now - last).TotalDays, (a?.Open ?? 0) > 0, Array.Empty<string>(),
					appr.GetValueOrDefault(o.ID) == "Pending", appr.GetValueOrDefault(o.ID) == "Rejected", dms.Contains(o.ID), await UnansweredAsync(cid, o.ID, o.AccountId), o.QuotationId != null, o.Probability);
				var list = A.NextActions(f, DateTime.Now);
				var risks = Risk.Evaluate(new Risk.Signal { OpportunityId = o.ID, Title = o.Title, Stage = o.Stage, Amount = o.Amount, Probability = o.Probability, ExpectedCloseDate = o.ExpectedCloseDate,
					CreatedAt = o.CreatedAt, LastActivityAt = a?.Last, OpenActivityCount = a?.Open ?? 0 }, now);
				res[o.ID] = (A.Urgency(list), list.FirstOrDefault(), risks.Count == 0 ? null : risks.Max(r => r.Severity));
			}
			return res;
		}

		// ------------------------------------------------------------------ my day
		public async Task<MyDayView> MyDayAsync(int cid, int? me, HashSet<int>? visible)
		{
			PersonRef? person = null;
			if (me is int m)
				person = await _db.Employee.AsNoTracking().Where(e => e.ID == m && e.EmpCompanyID == cid)
					.Select(e => new PersonRef(e.ID, (!IsAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName) ?? "", !string.IsNullOrWhiteSpace(e.ProfileImage))).FirstOrDefaultAsync();
			var items = new List<DayItem>();
			var today = DateTime.Today; var tomorrow = today.AddDays(1);
			// 1) my tasks, calls and meetings due today or overdue
			var acts = me is int me1 ? await _db.Activities.AsNoTracking().Where(a => a.CompanyID == cid && a.OwnerEmployeeId == me1 && !a.Done && a.DueDate != null && a.DueDate < tomorrow)
				.OrderBy(a => a.DueDate).Take(30).ToListAsync() : new();
			foreach (var a in acts)
			{
				var late = a.DueDate!.Value < today;
				var url = a.OpportunityId != null ? "/Crm/Deal/" + a.OpportunityId : a.EntityType == "Contact" && a.EntityId != null ? "/Crm/Contact/" + a.EntityId : "/Crm/Activities";
				items.Add(new DayItem(a.Type, late ? 75 : a.Type == "Meeting" ? 70 : 55, (!IsAr && !string.IsNullOrWhiteSpace(a.SubjectEn) ? a.SubjectEn! : a.Subject), a.Notes, url, a.DueDate, late, null));
			}
			// 2) customers waiting on me in the inbox
			var waiting = me is int me2 ? await _db.Set<CrmConversation>().AsNoTracking().Where(c => c.CompanyId == cid && c.OwnerEmployeeId == me2 && c.Status != "Closed" && c.UnreadCount > 0)
				.OrderByDescending(c => c.LastInboundAt).Take(10).ToListAsync() : new();
			foreach (var c in waiting)
				items.Add(new DayItem("Inbox", 90, c.DisplayName ?? c.Phone ?? c.Email ?? "—", c.LastPreview, "/Crm/Inbox?id=" + c.ID, c.LastInboundAt?.ToLocalTime(), false, c.UnreadCount.ToString()));
			// 3) my open deals that need something, by the assistant's own urgency
			var dq = _db.Opportunities.AsNoTracking().Where(o => o.CompanyID == cid && o.Stage != "Won" && o.Stage != "Lost");
			dq = me is int me3 ? dq.Where(o => o.OwnerEmployeeId == me3) : dq.Where(o => false);
			var deals = await dq.Select(o => new { o.ID, o.Title, o.TitleEn, o.Amount }).Take(200).ToListAsync();
			var hints = await PipelineHintsAsync(cid, deals.Select(d => d.ID).ToList());
			foreach (var d in deals.Where(d => hints.TryGetValue(d.ID, out var h) && h.top != null).OrderByDescending(d => hints[d.ID].urgency).Take(6))
			{
				var h = hints[d.ID];
				items.Add(new DayItem("Deal", h.urgency, (!IsAr && !string.IsNullOrWhiteSpace(d.TitleEn) ? d.TitleEn! : d.Title), "nba:" + h.top!.Code + (h.top.Param != null ? "|" + h.top.Param : ""),
					"/Crm/Deal/" + d.ID, null, h.risk == Risk.Severity.High, d.Amount.ToString("N0")));
			}
			// 4) my hottest leads, with the reason that made them hot
			var leadIds = me is int me4 ? await _db.Leads.AsNoTracking().Where(l => l.CompanyID == cid && l.OwnerEmployeeId == me4 && l.Status != "Converted" && l.Status != "Lost").Select(l => l.ID).ToListAsync() : new();
			var ins = await LeadInsightsAsync(cid, leadIds);
			var hotIds = ins.Where(x => x.Value.Band != "Cold").OrderByDescending(x => x.Value.Score).Take(6).Select(x => x.Key).ToList();
			var leadNames = await _db.Leads.AsNoTracking().Where(l => l.CompanyID == cid && hotIds.Contains(l.ID)).ToDictionaryAsync(l => l.ID, l => new { Name = !IsAr && !string.IsNullOrWhiteSpace(l.NameEn) ? l.NameEn! : l.Name, l.Company });
			var hot = hotIds.Where(leadNames.ContainsKey).Select(i => (i, leadNames[i].Name, leadNames[i].Company, ins[i])).ToList();
			// 5) my month: quota and won (C5)
			var month = DateTime.Today.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
			decimal? quota = me is int me5 ? await _db.Set<CrmQuota>().AsNoTracking().Where(q => q.CompanyId == cid && q.EmployeeId == me5 && q.Period == month).Select(q => (decimal?)q.Amount).FirstOrDefaultAsync() : null;
			var start = new DateTime(today.Year, today.Month, 1);
			var won = me is int me6 ? await _db.Opportunities.AsNoTracking().Where(o => o.CompanyID == cid && o.OwnerEmployeeId == me6 && o.Stage == "Won" && o.ExpectedCloseDate >= start && o.ExpectedCloseDate < start.AddMonths(1)).SumAsync(o => (decimal?)o.Amount) ?? 0 : 0;
			var meetings = acts.Count(a => a.Type == "Meeting" && a.DueDate >= today);
			return new MyDayView(person, items.OrderByDescending(i => i.Weight).ThenBy(i => i.Due ?? DateTime.MaxValue).ToList(), hot,
				acts.Count(a => a.DueDate >= today), acts.Count(a => a.DueDate < today), waiting.Count, meetings, quota, won);
		}
	}
}
