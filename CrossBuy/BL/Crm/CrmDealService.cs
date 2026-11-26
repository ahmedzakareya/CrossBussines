using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Crm;
using Microsoft.EntityFrameworkCore;
using R = CrossBuy.BL.CrmIdentity.CrmDealRules;

namespace CrossBuy.BL.CrmIdentity
{
	public sealed record StageView(int Id, string Name, string? NameEn, int Probability, bool IsWon, bool IsLost);
	public sealed record DealLineView(string? ItemCode, string Name, decimal Qty, decimal UnitPrice, decimal DiscountPercent, decimal LineTotal);
	public sealed record CommitteeMember(int LinkId, int ContactId, string Name, string? NameEn, string? Title, string? TitleEn, string? Phone, string Role);
	public sealed record ContactOption(int ContactId, string Name, string? NameEn, string? Title);
	public sealed record ApprovalView(CrmDiscountApproval A, string? RequesterName, bool RequesterPhoto, string? DeciderName, bool DeciderPhoto);
	public sealed record NextStepView(int ActivityId, string Type, string Subject, DateTime? Due, bool Late);
	public sealed record DealFile(Opportunity Deal, string? AccountName, string? AccountNameEn, PersonRef? Owner, IReadOnlyList<StageView> Stages, int CurrentIndex,
		IReadOnlyList<DealLineView> Lines, IReadOnlyList<CommitteeMember> Committee, IReadOnlyList<ContactOption> CanAdd, IReadOnlyList<Activity> Timeline,
		NextStepView? NextStep, decimal MaxDiscount, decimal? MyLimit, R.DiscountState Discount, IReadOnlyList<ApprovalView> Approvals,
		StageView? NextStage, IReadOnlyList<R.Requirement> NextStageNeeds, IReadOnlyList<R.Requirement> NextStageUnmet, IReadOnlyDictionary<int, string> FieldLabels);
	public sealed record PendingApprovalView(ApprovalView View, int OpportunityId, string DealTitle, string? AccountName, decimal DealAmount);
	public sealed record StageRules(int PipelineId, string PipelineName, IReadOnlyList<StageView> Stages, IReadOnlyDictionary<int, IReadOnlyList<R.Requirement>> Required);
	public sealed record DealRulesSettings(IReadOnlyList<StageRules> Pipelines, IReadOnlyDictionary<string, decimal> Limits, IReadOnlyList<(int Id, string Label)> OppFields);

	public interface ICrmDealService
	{
		Task<DealFile?> DealAsync(int cid, HashSet<int>? visible, int id, int? me, bool iAmManager);
		/// The requirements of the stage a move goes INTO that the deal does not meet. Empty = the move may go ahead.
		Task<IReadOnlyList<R.Requirement>> GateAsync(int cid, int oppId, string? targetStageName, int? targetStageId, int? targetPipelineId = null);
		/// Why this person may not turn the deal into a quotation yet (the discount), or null.
		Task<string?> QuotationRefusalAsync(int cid, int oppId, int? me);
		Task<(bool ok, string? error)> AddToCommitteeAsync(int cid, HashSet<int>? visible, int oppId, int contactId, string role, int? me);
		Task<(bool ok, string? error)> RemoveFromCommitteeAsync(int cid, HashSet<int>? visible, int linkId);
		Task<(bool ok, string? error)> RequestDiscountAsync(int cid, HashSet<int>? visible, int oppId, string reason, int? me);
		Task<(bool ok, string? error)> DecideDiscountAsync(int cid, int approvalId, bool approve, string? note, int? me, bool iAmManager);
		Task<(bool ok, string? error)> WithdrawDiscountAsync(int cid, int approvalId, int? me);
		Task<List<PendingApprovalView>> PendingForMeAsync(int cid, int? me, bool iAmManager);
		Task<DealRulesSettings> SettingsAsync(int cid);
		Task<(bool ok, string? error)> SaveStageRequirementsAsync(int cid, int stageId, IEnumerable<string> kinds, IEnumerable<int> fieldIds);
		Task<(bool ok, string? error)> SaveLimitsAsync(int cid, IReadOnlyDictionary<string, decimal?> limits);
	}

	/// <summary>CRM C3 — the deal workspace: stage requirements, the buying committee, discount approval.</summary>
	/// <remarks>
	/// THE GATE IS ON EVERY DOOR. A deal reaches a stage two ways — dragged on the board (MoveOpportunity) or saved from
	/// the form (SaveOpportunity) — and both ask GateAsync before the CRM's own transition runs, so a requirement cannot
	/// be skipped by choosing the other door. Leaving a stage backwards is never gated; only entering one is.
	/// THE DISCOUNT IS JUDGED FOR THE PERSON ACTING: whoever turns the deal into a quotation must be within their own
	/// limit, or hold an approval for these exact lines. The stage requirement "Discount" judges the deal's OWNER.
	/// </remarks>
	public sealed class CrmDealService : ICrmDealService
	{
		private readonly CrossDbContext _db;
		private readonly INotificationService _notify;
		public CrmDealService(CrossDbContext db, INotificationService notify) { _db = db; _notify = notify; }

		static bool IsAr => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

		IQueryable<Opportunity> Deals(int cid, HashSet<int>? visible)
		{
			var q = _db.Opportunities.AsNoTracking().Where(o => o.CompanyID == cid);
			if (visible != null) q = q.Where(o => visible.Count > 0 && (o.OwnerEmployeeId == null || visible.Contains(o.OwnerEmployeeId.Value)));
			return q;
		}

		async Task<List<string>> RolesOfAsync(int cid, int? emp) => emp is not int e ? new List<string>()
			: await _db.CrmUserRoles.AsNoTracking().Where(r => r.CompanyID == cid && r.EmployeeId == e).Select(r => r.Role).Distinct().ToListAsync();

		async Task<Dictionary<string, decimal>> LimitsAsync(int cid) =>
			await _db.Set<CrmDiscountLimit>().AsNoTracking().Where(l => l.CompanyId == cid).ToDictionaryAsync(l => l.Role, l => l.MaxPercent);

		async Task<List<R.Line>> LinesAsync(int cid, int oppId) =>
			(await _db.OpportunityProducts.AsNoTracking().Where(p => p.CompanyID == cid && p.OpportunityId == oppId).ToListAsync())
				.Select(p => new R.Line(p.ItemId, p.ItemDescription, p.Qty, p.UnitPrice, p.DiscountPercent)).ToList();

		async Task<List<R.ApprovalFacts>> ApprovalFactsAsync(int cid, int oppId) =>
			(await _db.Set<CrmDiscountApproval>().AsNoTracking().Where(a => a.CompanyId == cid && a.OpportunityId == oppId).ToListAsync())
				.Select(a => new R.ApprovalFacts(a.Status, a.RequestedPercent, a.LinesFingerprint)).ToList();

		async Task<R.DiscountState> DiscountStateAsync(int cid, int oppId, int? person)
		{
			var lines = await LinesAsync(cid, oppId);
			var limit = R.LimitFor(await RolesOfAsync(cid, person), await LimitsAsync(cid));
			return R.StateOf(R.MaxDiscount(lines), limit, R.Fingerprint(lines), await ApprovalFactsAsync(cid, oppId));
		}

		async Task<R.DealFacts> FactsAsync(int cid, Opportunity o)
		{
			var roles = (await _db.Set<CrmOpportunityContact>().AsNoTracking().Where(c => c.CompanyId == cid && c.OpportunityId == o.ID).Select(c => c.Role).ToListAsync()).ToHashSet();
			var lines = await _db.OpportunityProducts.AsNoTracking().CountAsync(p => p.CompanyID == cid && p.OpportunityId == o.ID);
			var next = await _db.Activities.AsNoTracking().AnyAsync(a => a.CompanyID == cid && a.OpportunityId == o.ID && !a.Done && a.DueDate != null);
			var filled = (await _db.CrmCustomFieldValues.AsNoTracking().Where(v => v.CompanyID == cid && v.EntityType == "Opportunity" && v.EntityId == o.ID && v.Value != null && v.Value != "")
				.Select(v => v.FieldId).ToListAsync()).ToHashSet();
			var disc = R.DiscountCleared(await DiscountStateAsync(cid, o.ID, o.OwnerEmployeeId));
			return new R.DealFacts(o.Amount, o.ExpectedCloseDate, lines, roles, next, disc, o.QuotationId != null, filled);
		}

		async Task<List<R.Requirement>> RequiredForAsync(int cid, int stageId) =>
			(await _db.Set<CrmStageRequirement>().AsNoTracking().Where(r => r.CompanyId == cid && r.StageId == stageId).OrderBy(r => r.ID).ToListAsync())
				.Select(r => new R.Requirement(r.Requirement, r.FieldId)).ToList();

		async Task<List<StageView>> StagesAsync(int cid, int pipelineId) =>
			await _db.CrmPipelineStages.AsNoTracking().Where(s => s.CompanyID == cid && s.PipelineId == pipelineId).OrderBy(s => s.Sort)
				.Select(s => new StageView(s.ID, s.Name, s.NameEn, s.Probability, s.IsWon, s.IsLost)).ToListAsync();

		async Task<int> PipelineOfAsync(int cid, Opportunity o) => o.PipelineId ?? await _db.CrmPipelines.AsNoTracking()
			.Where(p => p.CompanyID == cid && p.IsActive).OrderByDescending(p => p.IsDefault).ThenBy(p => p.ID).Select(p => p.ID).FirstOrDefaultAsync();

		// ---------------- the gate ----------------

		public async Task<IReadOnlyList<R.Requirement>> GateAsync(int cid, int oppId, string? targetStageName, int? targetStageId, int? targetPipelineId = null)
		{
			var o = await _db.Opportunities.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyID == cid && x.ID == oppId);
			if (o == null) return Array.Empty<R.Requirement>();   // the CRM's own transition refuses an unknown deal
			var pid = targetPipelineId ?? await PipelineOfAsync(cid, o);
			var stages = await StagesAsync(cid, pid);
			var target = targetStageId is int sid && sid > 0 ? stages.FirstOrDefault(s => s.Id == sid)
				: stages.FirstOrDefault(s => s.Name == targetStageName);
			if (target == null || target.Id == o.StageId) return Array.Empty<R.Requirement>();
			// only ENTERING a later stage is gated; moving back, or to Lost, never is
			var cur = stages.FindIndex(s => s.Id == o.StageId);
			var tgt = stages.IndexOf(target);
			if (target.IsLost || (cur >= 0 && tgt < cur)) return Array.Empty<R.Requirement>();
			return R.Unmet(await FactsAsync(cid, o), await RequiredForAsync(cid, target.Id));
		}

		public async Task<string?> QuotationRefusalAsync(int cid, int oppId, int? me)
		{
			var state = await DiscountStateAsync(cid, oppId, me);
			return state switch
			{
				R.DiscountState.NeedsApproval => "The discount is above your limit. Ask for approval first.",
				R.DiscountState.Pending => "The discount approval is still waiting for a decision.",
				R.DiscountState.Rejected => "The discount was rejected. Change the lines or ask again with a reason.",
				_ => null,
			};
		}

		// ---------------- the workspace ----------------

		async Task<Dictionary<int, PersonRef>> PeopleAsync(int cid, IEnumerable<int?> ids)
		{
			var set = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
			if (set.Count == 0) return new();
			return (await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == cid && set.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToListAsync())
				.ToDictionary(e => e.ID, e => new PersonRef(e.ID, (!IsAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName) ?? "", !string.IsNullOrWhiteSpace(e.ProfileImage)));
		}

		public async Task<DealFile?> DealAsync(int cid, HashSet<int>? visible, int id, int? me, bool iAmManager)
		{
			var o = await Deals(cid, visible).FirstOrDefaultAsync(x => x.ID == id);
			if (o == null) return null;
			var acc = o.AccountId is int aid ? await _db.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == cid && a.ID == aid).Select(a => new { a.Name, a.NameEn }).FirstOrDefaultAsync() : null;
			var pid = await PipelineOfAsync(cid, o);
			var stages = await StagesAsync(cid, pid);
			var cur = stages.FindIndex(s => s.Id == o.StageId);
			if (cur < 0) cur = stages.FindIndex(s => s.Name == o.Stage);

			var products = await _db.OpportunityProducts.AsNoTracking().Where(p => p.CompanyID == cid && p.OpportunityId == id).OrderBy(p => p.ID).ToListAsync();
			var itemIds = products.Where(p => p.ItemId != null).Select(p => p.ItemId!.Value).Distinct().ToList();
			var items = await _db.Items.AsNoTracking().Where(i => i.CompanyID == cid && itemIds.Contains(i.ID)).Select(i => new { i.ID, i.ItemCode, i.Name }).ToDictionaryAsync(i => i.ID);
			var lines = products.Select(p => p.ItemId is int ii && items.TryGetValue(ii, out var it)
				? new DealLineView(it.ItemCode, it.Name ?? p.ItemDescription ?? "", p.Qty, p.UnitPrice, p.DiscountPercent, p.LineTotal)
				: new DealLineView(null, p.ItemDescription ?? "", p.Qty, p.UnitPrice, p.DiscountPercent, p.LineTotal)).ToList();

			var committee = await (from l in _db.Set<CrmOpportunityContact>().AsNoTracking().Where(l => l.CompanyId == cid && l.OpportunityId == id)
								   join c in _db.CrmContacts.AsNoTracking().Where(c => c.CompanyID == cid) on l.ContactId equals c.ID
								   orderby l.ID
								   select new CommitteeMember(l.ID, c.ID, c.Name, c.NameEn, c.Title, c.TitleEn, c.Phone, l.Role)).ToListAsync();
			var onIt = committee.Select(c => c.ContactId).ToHashSet();
			var canAdd = o.AccountId is int a2 ? (await _db.CrmContacts.AsNoTracking().Where(c => c.CompanyID == cid && c.AccountId == a2).OrderBy(c => c.Name)
				.Select(c => new ContactOption(c.ID, c.Name, c.NameEn, c.Title)).ToListAsync()).Where(c => !onIt.Contains(c.ContactId)).ToList() : new List<ContactOption>();

			var timeline = await _db.Activities.AsNoTracking().Where(a => a.CompanyID == cid && (a.OpportunityId == id || (a.EntityType == "Opportunity" && a.EntityId == id)))
				.OrderByDescending(a => a.CreatedAt).Take(30).ToListAsync();
			var next = await _db.Activities.AsNoTracking().Where(a => a.CompanyID == cid && a.OpportunityId == id && !a.Done && a.DueDate != null)
				.OrderBy(a => a.DueDate).FirstOrDefaultAsync();

			var ruleLines = products.Select(p => new R.Line(p.ItemId, p.ItemDescription, p.Qty, p.UnitPrice, p.DiscountPercent)).ToList();
			var limit = R.LimitFor(await RolesOfAsync(cid, me), await LimitsAsync(cid));
			var approvals = await _db.Set<CrmDiscountApproval>().AsNoTracking().Where(a => a.CompanyId == cid && a.OpportunityId == id).OrderByDescending(a => a.RequestedAt).ToListAsync();
			var state = R.StateOf(R.MaxDiscount(ruleLines), limit, R.Fingerprint(ruleLines), approvals.Select(a => new R.ApprovalFacts(a.Status, a.RequestedPercent, a.LinesFingerprint)));
			var people = await PeopleAsync(cid, approvals.SelectMany(a => new int?[] { a.RequestedByEmployeeId, a.DecidedByEmployeeId }).Append(o.OwnerEmployeeId));
			ApprovalView V(CrmDiscountApproval a) => new(a, people.GetValueOrDefault(a.RequestedByEmployeeId)?.Name, people.GetValueOrDefault(a.RequestedByEmployeeId)?.HasPhoto == true,
				a.DecidedByEmployeeId is int d ? people.GetValueOrDefault(d)?.Name : null, a.DecidedByEmployeeId is int d2 && people.GetValueOrDefault(d2)?.HasPhoto == true);

			// the next stage forward that is not "lost": what it will ask for, and what is still missing
			var nextStage = cur >= 0 ? stages.Skip(cur + 1).FirstOrDefault(s => !s.IsLost) : stages.FirstOrDefault(s => !s.IsLost);
			var needs = nextStage == null ? new List<R.Requirement>() : await RequiredForAsync(cid, nextStage.Id);
			var unmet = needs.Count == 0 ? (IReadOnlyList<R.Requirement>)Array.Empty<R.Requirement>() : R.Unmet(await FactsAsync(cid, o), needs);
			var fieldIds = needs.Where(n => n.FieldId != null).Select(n => n.FieldId!.Value).ToList();
			var labels = await _db.CrmCustomFields.AsNoTracking().Where(f => f.CompanyID == cid && fieldIds.Contains(f.ID))
				.ToDictionaryAsync(f => f.ID, f => !IsAr && !string.IsNullOrWhiteSpace(f.LabelEn) ? f.LabelEn! : f.Label);

			return new DealFile(o, acc?.Name, acc?.NameEn, o.OwnerEmployeeId is int ow ? people.GetValueOrDefault(ow) : null, stages, cur, lines, committee, canAdd, timeline,
				next == null ? null : new NextStepView(next.ID, next.Type, (!IsAr && !string.IsNullOrWhiteSpace(next.SubjectEn) ? next.SubjectEn : next.Subject) ?? "", next.DueDate, next.DueDate < DateTime.Now),
				R.MaxDiscount(ruleLines), limit, state, approvals.Select(V).ToList(), nextStage, needs, unmet, labels);
		}

		// ---------------- the buying committee ----------------

		public async Task<(bool ok, string? error)> AddToCommitteeAsync(int cid, HashSet<int>? visible, int oppId, int contactId, string role, int? me)
		{
			if (!CrmDealVocabulary.Roles.Contains(role)) return (false, "Unknown role");
			var o = await Deals(cid, visible).FirstOrDefaultAsync(x => x.ID == oppId);
			if (o == null) return (false, "Record not found");
			// a deal's people are its account's people: a contact of another company cannot sit on this deal
			if (!await _db.CrmContacts.AsNoTracking().AnyAsync(c => c.CompanyID == cid && c.ID == contactId && c.AccountId == o.AccountId))
				return (false, "This person is not a contact of the deal's account");
			var existing = await _db.Set<CrmOpportunityContact>().FirstOrDefaultAsync(l => l.CompanyId == cid && l.OpportunityId == oppId && l.ContactId == contactId);
			if (existing != null) existing.Role = role;
			else _db.Set<CrmOpportunityContact>().Add(new CrmOpportunityContact { CompanyId = cid, OpportunityId = oppId, ContactId = contactId, Role = role, AddedAt = DateTime.UtcNow, AddedByEmployeeId = me });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> RemoveFromCommitteeAsync(int cid, HashSet<int>? visible, int linkId)
		{
			var l = await _db.Set<CrmOpportunityContact>().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == linkId);
			if (l == null || !await Deals(cid, visible).AnyAsync(o => o.ID == l.OpportunityId)) return (false, "Record not found");
			_db.Set<CrmOpportunityContact>().Remove(l);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// ---------------- discount approval ----------------

		public async Task<(bool ok, string? error)> RequestDiscountAsync(int cid, HashSet<int>? visible, int oppId, string reason, int? me)
		{
			if (me is not int requester) return (false, "You do not have permission to perform this action");
			reason = (reason ?? "").Trim();
			if (reason.Length < 5) return (false, "Write the reason for the discount");
			if (reason.Length > 500) reason = reason[..500];
			var o = await Deals(cid, visible).FirstOrDefaultAsync(x => x.ID == oppId);
			if (o == null) return (false, "Record not found");
			var lines = await LinesAsync(cid, oppId);
			if (lines.Count == 0) return (false, "Add lines to the deal first");
			var max = R.MaxDiscount(lines);
			var limit = R.LimitFor(await RolesOfAsync(cid, requester), await LimitsAsync(cid));
			if (limit == null || max <= limit.Value) return (false, "The discount is within your limit; no approval is needed");
			if (await _db.Set<CrmDiscountApproval>().AnyAsync(a => a.CompanyId == cid && a.OpportunityId == oppId && a.Status == "Pending"))
				return (false, "A request for this deal is already waiting for a decision");
			var a = new CrmDiscountApproval
			{
				CompanyId = cid, OpportunityId = oppId, RequestedPercent = max, LinesFingerprint = R.Fingerprint(lines), Amount = o.Amount, Reason = reason,
				RequestedByEmployeeId = requester, RequestedAt = DateTime.UtcNow, Status = "Pending",
			};
			_db.Set<CrmDiscountApproval>().Add(a);
			await _db.SaveChangesAsync();
			// who can decide: anyone whose own limit covers it, and the sales managers
			var limits = await LimitsAsync(cid);
			var covering = limits.Where(l => l.Value >= max).Select(l => l.Key).ToList();
			var approvers = await _db.CrmUserRoles.AsNoTracking().Where(r => r.CompanyID == cid && (covering.Contains(r.Role) || r.Role == "SalesManager") && r.EmployeeId != requester)
				.Select(r => r.EmployeeId).Distinct().ToListAsync();
			foreach (var e in approvers)
				await _notify.NotifyAsync(e, "طلب موافقة على خصم " + max.ToString("0.##") + "%: " + o.Title, "Discount approval requested (" + max.ToString("0.##") + "%): " + o.Title,
					reason, reason, NotificationTypes.CrmAssigned, oppId, url: "/Crm/Deal/" + oppId, companyId: cid, actorEmployeeId: requester, dedupKey: "crm-discount:" + a.ID + ":" + e);
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DecideDiscountAsync(int cid, int approvalId, bool approve, string? note, int? me, bool iAmManager)
		{
			if (me is not int decider) return (false, "You do not have permission to perform this action");
			var a = await _db.Set<CrmDiscountApproval>().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == approvalId);
			if (a == null) return (false, "Record not found");
			if (a.Status != "Pending") return (false, "This request was already decided");
			var limit = R.LimitFor(await RolesOfAsync(cid, decider), await LimitsAsync(cid));
			var isManagerRole = await _db.CrmUserRoles.AsNoTracking().AnyAsync(r => r.CompanyID == cid && r.EmployeeId == decider && r.Role == "SalesManager");
			if (!R.MayApprove(decider, a.RequestedByEmployeeId, a.RequestedPercent, limit, iAmManager || isManagerRole))
				return (false, a.RequestedByEmployeeId == decider ? "You cannot decide your own request" : "This discount is above your own limit");
			note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
			if (!approve && note == null) return (false, "Write why the discount is rejected");
			a.Status = approve ? "Approved" : "Rejected"; a.DecidedByEmployeeId = decider; a.DecidedAt = DateTime.UtcNow; a.DecisionNote = note is { Length: > 500 } ? note[..500] : note;
			await _db.SaveChangesAsync();
			var title = await _db.Opportunities.AsNoTracking().Where(o => o.CompanyID == cid && o.ID == a.OpportunityId).Select(o => o.Title).FirstOrDefaultAsync();
			await _notify.NotifyAsync(a.RequestedByEmployeeId, (approve ? "تمت الموافقة على الخصم: " : "رُفض الخصم: ") + title, (approve ? "Discount approved: " : "Discount rejected: ") + title,
				note, note, NotificationTypes.CrmAssigned, a.OpportunityId, url: "/Crm/Deal/" + a.OpportunityId, companyId: cid, actorEmployeeId: decider, dedupKey: "crm-discount-decided:" + a.ID);
			return (true, null);
		}

		public async Task<(bool ok, string? error)> WithdrawDiscountAsync(int cid, int approvalId, int? me)
		{
			var a = await _db.Set<CrmDiscountApproval>().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == approvalId);
			if (a == null || a.RequestedByEmployeeId != me) return (false, "Record not found");
			if (a.Status != "Pending") return (false, "This request was already decided");
			a.Status = "Withdrawn";
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<List<PendingApprovalView>> PendingForMeAsync(int cid, int? me, bool iAmManager)
		{
			if (me is not int e) return new();
			var limit = R.LimitFor(await RolesOfAsync(cid, e), await LimitsAsync(cid));
			var isManagerRole = iAmManager || await _db.CrmUserRoles.AsNoTracking().AnyAsync(r => r.CompanyID == cid && r.EmployeeId == e && r.Role == "SalesManager");
			var rows = await (from a in _db.Set<CrmDiscountApproval>().AsNoTracking().Where(a => a.CompanyId == cid && a.Status == "Pending")
							  join o in _db.Opportunities.AsNoTracking().Where(o => o.CompanyID == cid) on a.OpportunityId equals o.ID
							  join ac in _db.CrmAccounts.AsNoTracking().Where(x => x.CompanyID == cid) on o.AccountId equals (int?)ac.ID into aj
							  from ac in aj.DefaultIfEmpty()
							  orderby a.RequestedAt
							  select new { a, o.Title, o.Amount, Acc = ac == null ? null : ac.Name }).ToListAsync();
			rows = rows.Where(r => R.MayApprove(e, r.a.RequestedByEmployeeId, r.a.RequestedPercent, limit, isManagerRole)).ToList();
			var people = await PeopleAsync(cid, rows.Select(r => (int?)r.a.RequestedByEmployeeId));
			return rows.Select(r => new PendingApprovalView(new ApprovalView(r.a, people.GetValueOrDefault(r.a.RequestedByEmployeeId)?.Name, people.GetValueOrDefault(r.a.RequestedByEmployeeId)?.HasPhoto == true, null, false),
				r.a.OpportunityId, r.Title, r.Acc, r.Amount)).ToList();
		}

		// ---------------- settings ----------------

		public async Task<DealRulesSettings> SettingsAsync(int cid)
		{
			var pipelines = await _db.CrmPipelines.AsNoTracking().Where(p => p.CompanyID == cid && p.IsActive).OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync();
			var reqs = await _db.Set<CrmStageRequirement>().AsNoTracking().Where(r => r.CompanyId == cid).ToListAsync();
			var list = new List<StageRules>();
			foreach (var p in pipelines)
			{
				var stages = await StagesAsync(cid, p.ID);
				list.Add(new StageRules(p.ID, !IsAr && !string.IsNullOrWhiteSpace(p.NameEn) ? p.NameEn! : p.Name, stages,
					stages.ToDictionary(s => s.Id, s => (IReadOnlyList<R.Requirement>)reqs.Where(r => r.StageId == s.Id).Select(r => new R.Requirement(r.Requirement, r.FieldId)).ToList())));
			}
			var fields = (await _db.CrmCustomFields.AsNoTracking().Where(f => f.CompanyID == cid && f.EntityType == "Opportunity" && f.IsActive).OrderBy(f => f.SortOrder).ToListAsync())
				.Select(f => (f.ID, !IsAr && !string.IsNullOrWhiteSpace(f.LabelEn) ? f.LabelEn! : f.Label)).ToList();
			return new DealRulesSettings(list, await LimitsAsync(cid), fields);
		}

		public async Task<(bool ok, string? error)> SaveStageRequirementsAsync(int cid, int stageId, IEnumerable<string> kinds, IEnumerable<int> fieldIds)
		{
			if (!await _db.CrmPipelineStages.AsNoTracking().AnyAsync(s => s.CompanyID == cid && s.ID == stageId)) return (false, "Record not found");
			var wanted = kinds.Where(k => CrmDealVocabulary.Requirements.Contains(k) && k != "CustomField").Distinct().ToList();
			var fields = fieldIds.Distinct().ToList();
			if (fields.Count > 0 && await _db.CrmCustomFields.AsNoTracking().CountAsync(f => f.CompanyID == cid && f.EntityType == "Opportunity" && fields.Contains(f.ID)) != fields.Count)
				return (false, "Unknown custom field");
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			await _db.Set<CrmStageRequirement>().Where(r => r.CompanyId == cid && r.StageId == stageId).ExecuteDeleteAsync();
			foreach (var k in wanted) _db.Set<CrmStageRequirement>().Add(new CrmStageRequirement { CompanyId = cid, StageId = stageId, Requirement = k });
			foreach (var f in fields) _db.Set<CrmStageRequirement>().Add(new CrmStageRequirement { CompanyId = cid, StageId = stageId, Requirement = "CustomField", FieldId = f });
			await _db.SaveChangesAsync();
			await tx.CommitAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> SaveLimitsAsync(int cid, IReadOnlyDictionary<string, decimal?> limits)
		{
			if (limits.Values.Any(v => v is < 0 or > 100)) return (false, "A limit must be between 0 and 100");
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			await _db.Set<CrmDiscountLimit>().Where(l => l.CompanyId == cid).ExecuteDeleteAsync();
			foreach (var (role, v) in limits.Where(l => CrmDealVocabulary.CrmRoles.Contains(l.Key) && l.Value != null))
				_db.Set<CrmDiscountLimit>().Add(new CrmDiscountLimit { CompanyId = cid, Role = role, MaxPercent = v!.Value });
			await _db.SaveChangesAsync();
			await tx.CommitAsync();
			return (true, null);
		}
	}
}
