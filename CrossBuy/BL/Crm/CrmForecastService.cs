using CrossBuy.Models;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Crm;
using Microsoft.EntityFrameworkCore;
using F = CrossBuy.BL.CrmIdentity.CrmForecastRules;

namespace CrossBuy.BL.CrmIdentity
{
	// CRM C5 — quotas and forecasting.
	//
	// WHO is in a forecast: the people with a sales role, anyone with a quota in the period, and anyone who owns a deal
	// closing in it — narrowed to the CRM's owner whitelist, so a rep sees their own row and a manager their team.
	// WHEN a deal counts: its expected close date falls in the period (the same rule the old monthly forecast used; a
	// won deal counts in the month it was expected to close). A quarter is its three months; its quota is their sum.
	// A SUBMISSION freezes the numbers as they were; a manager approves it (optionally adjusting the commit — a separate
	// number, so the rep's own call is never overwritten) or returns it with a reason.

	public sealed record SubmissionView(int Id, string Period, string Status, decimal Quota, decimal Won, decimal Commit, decimal BestCase, decimal Pipeline,
		decimal? AdjustedCommit, string? Note, DateTime At, PersonRef Person, PersonRef? Reviewer, string? ReviewNote, DateTime? ReviewedAt);
	public sealed record ForecastPersonRow(PersonRef Person, string? Role, F.Rollup R, SubmissionView? Latest, bool CanReview);
	public sealed record ForecastDeal(int Id, string Title, string? Account, PersonRef? Owner, decimal Amount, int Probability, string Stage, DateTime? Close,
		string Category, bool Chosen, bool CanEdit, bool CanReassign);
	public sealed record ForecastMonth(string Key, F.Rollup R);
	public sealed record ForecastBoard(F.Period Period, List<ForecastPersonRow> People, F.Rollup Total, int? Me, ForecastPersonRow? Mine,
		List<SubmissionView> History, List<ForecastMonth> Months, int? Focus, int Unowned);
	public sealed record QuotaRow(PersonRef Person, string? Role, Dictionary<string, decimal> Months, bool CanEdit);

	public interface ICrmForecastService
	{
		Task<ForecastBoard> BoardAsync(int cid, HashSet<int>? visible, int? me, string? period, int? focus);
		Task<(List<ForecastDeal> rows, int total)> DealsAsync(int cid, HashSet<int>? visible, int? me, bool canEdit, string? period, int? owner, string? category, string? q, int page, int pageSize);
		Task<(bool ok, string? error)> SetCategoryAsync(int cid, HashSet<int>? visible, int? me, int oppId, string category);
		Task<(bool ok, string? error)> SetOwnerAsync(int cid, HashSet<int>? visible, int? me, bool canManage, int oppId, int owner);
		Task<List<PersonRef>> AssignableAsync(int cid, int? me, bool canManage);
		Task<(bool ok, string? error)> SubmitAsync(int cid, int? me, string? period, string? note);
		Task<(bool ok, string? error)> ReviewAsync(int cid, int? me, int submissionId, bool approve, decimal? adjustedCommit, string? note);
		Task<List<QuotaRow>> QuotasAsync(int cid, HashSet<int>? visible, int? me, bool canManage, int year, string? q);
		Task<(bool ok, string? error, int saved)> SaveQuotasAsync(int cid, int? me, bool canManage, int year, IReadOnlyDictionary<(int emp, string month), decimal> values);
	}

	public sealed class CrmForecastService : ICrmForecastService
	{
		private readonly CrossDbContext _db;
		private readonly ICrmAccessService _access;
		private readonly INotificationService _notify;
		public CrmForecastService(CrossDbContext db, ICrmAccessService access, INotificationService notify) { _db = db; _access = access; _notify = notify; }

		static bool IsAr => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
		static readonly string[] SalesRoles = { "SalesRep", "SalesManager" };

		F.Period PeriodOr(string? key) => F.Parse(key) ?? F.Month(DateTime.Today);

		async Task<Dictionary<int, PersonRef>> PeopleAsync(int cid, IEnumerable<int> ids)
		{
			var set = ids.Distinct().ToList();
			if (set.Count == 0) return new();
			return (await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == cid && set.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToListAsync())
				.ToDictionary(e => e.ID, e => new PersonRef(e.ID, (!IsAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName) ?? ("#" + e.ID), !string.IsNullOrWhiteSpace(e.ProfileImage)));
		}

		/// The deals of a period in scope, each with the category it is in.
		async Task<List<(Opportunity o, string cat, bool chosen)>> PeriodDealsAsync(int cid, HashSet<int>? visible, F.Period p)
		{
			var q = _db.Opportunities.AsNoTracking().Where(o => o.CompanyID == cid && o.ExpectedCloseDate >= p.Start && o.ExpectedCloseDate < p.End);
			if (visible != null) q = q.Where(o => o.OwnerEmployeeId != null && visible.Contains(o.OwnerEmployeeId.Value));
			var deals = await q.ToListAsync();
			var ids = deals.Select(d => d.ID).ToList();
			var chosen = await _db.Set<CrmDealForecast>().AsNoTracking().Where(f => f.CompanyId == cid && ids.Contains(f.OpportunityId)).ToDictionaryAsync(f => f.OpportunityId, f => f.Category);
			return deals.Select(d => (d, F.CategoryOf(d.Stage, d.Probability, chosen.GetValueOrDefault(d.ID)), chosen.ContainsKey(d.ID))).ToList();
		}

		async Task<Dictionary<int, decimal>> QuotaOfAsync(int cid, F.Period p)
		{
			var months = p.MonthKeys.ToList();
			return await _db.Set<CrmQuota>().AsNoTracking().Where(x => x.CompanyId == cid && months.Contains(x.Period))
				.GroupBy(x => x.EmployeeId).Select(g => new { g.Key, A = g.Sum(x => x.Amount) }).ToDictionaryAsync(x => x.Key, x => x.A);
		}

		async Task<Dictionary<int, string>> RolesAsync(int cid)
			=> (await _db.CrmUserRoles.AsNoTracking().Where(r => r.CompanyID == cid && SalesRoles.Contains(r.Role)).ToListAsync())
				.GroupBy(r => r.EmployeeId).ToDictionary(g => g.Key, g => g.Any(r => r.Role == "SalesManager") ? "SalesManager" : "SalesRep");

		SubmissionView View(CrmForecastSubmission s, Dictionary<int, PersonRef> people)
			=> new(s.ID, s.Period, s.Status, s.Quota, s.Won, s.CommitAmount, s.BestCase, s.Pipeline, s.AdjustedCommit, s.Note, s.SubmittedAt,
				people.GetValueOrDefault(s.EmployeeId) ?? new PersonRef(s.EmployeeId, "#" + s.EmployeeId, false),
				s.ReviewedByEmployeeId is int r ? people.GetValueOrDefault(r) : null, s.ReviewNote, s.ReviewedAt);

		public async Task<ForecastBoard> BoardAsync(int cid, HashSet<int>? visible, int? me, string? period, int? focus)
		{
			var p = PeriodOr(period);
			var deals = await PeriodDealsAsync(cid, visible, p);
			var quotas = await QuotaOfAsync(cid, p);
			var roles = await RolesAsync(cid);
			bool InScope(int e) => visible == null || visible.Contains(e);
			var ids = roles.Keys.Concat(quotas.Keys).Concat(deals.Where(d => d.o.OwnerEmployeeId != null).Select(d => d.o.OwnerEmployeeId!.Value)).Where(InScope).Distinct().ToList();
			var subs = await _db.Set<CrmForecastSubmission>().AsNoTracking().Where(s => s.CompanyId == cid && s.Period == p.Key && ids.Contains(s.EmployeeId))
				.OrderByDescending(s => s.ID).ToListAsync();
			var myTeam = me is int m && roles.GetValueOrDefault(m) == "SalesManager" ? await _access.TeamOwnerIdsAsync(m) : new HashSet<int>();
			var people = await PeopleAsync(cid, ids.Concat(subs.Where(s => s.ReviewedByEmployeeId != null).Select(s => s.ReviewedByEmployeeId!.Value)));
			var rows = ids.Select(e =>
			{
				var latest = subs.FirstOrDefault(s => s.EmployeeId == e && s.Status != "Superseded");
				var r = F.Roll(quotas.GetValueOrDefault(e), deals.Where(d => d.o.OwnerEmployeeId == e).Select(d => new F.Deal(e, d.o.Amount, d.cat)));
				var lv = latest == null ? null : View(latest, people);
				var canReview = me is int mm && latest != null && F.ReviewRefusal(mm, e, myTeam, latest.Status) == null;
				return new ForecastPersonRow(people.GetValueOrDefault(e) ?? new PersonRef(e, "#" + e, false), roles.GetValueOrDefault(e), r, lv, canReview);
			})
			// the people who carry a number first, biggest first; the rest after, by name
			.OrderByDescending(r => r.R.Quota > 0).ThenByDescending(r => r.R.Quota).ThenBy(r => r.Person.Name).ToList();
			var total = rows.Aggregate(F.Rollup.Zero, (a, r) => a + r.R);
			var mine = me is int mi ? rows.FirstOrDefault(r => r.Person.EmployeeId == mi) : null;
			// history: the person in focus (yourself by default), every submission of this period, newest first
			var who = focus ?? me;
			var history = who is int w && InScope(w) ? subs.Where(s => s.EmployeeId == w).Select(s => View(s, people)).ToList() : new();
			// the months around: three before, the period, two after — the trend the old monthly screen showed
			var months = new List<ForecastMonth>();
			var first = F.Month(p.Start.AddMonths(-3));
			for (var i = 0; i < 6 + (p.Months - 1); i++)
			{
				var mp = F.Month(first.Start.AddMonths(i));
				var md = await PeriodDealsAsync(cid, visible, mp);
				var mq = (await QuotaOfAsync(cid, mp)).Where(x => InScope(x.Key)).Sum(x => x.Value);
				months.Add(new ForecastMonth(mp.Key, F.Roll(mq, md.Select(d => new F.Deal(d.o.OwnerEmployeeId ?? 0, d.o.Amount, d.cat)))));
			}
			var unowned = visible == null ? deals.Count(d => d.o.OwnerEmployeeId == null && d.cat != "Lost") : 0;
			return new ForecastBoard(p, rows, total, me, mine, history, months, who, unowned);
		}

		public async Task<(List<ForecastDeal> rows, int total)> DealsAsync(int cid, HashSet<int>? visible, int? me, bool canEdit, string? period, int? owner, string? category, string? q, int page, int pageSize)
		{
			var p = PeriodOr(period);
			var all = await PeriodDealsAsync(cid, visible, p);
			IEnumerable<(Opportunity o, string cat, bool chosen)> list = all;
			if (owner is int ow) list = list.Where(d => d.o.OwnerEmployeeId == ow);
			if (!string.IsNullOrEmpty(category)) list = list.Where(d => d.cat == category);
			var accIds = all.Where(d => d.o.AccountId != null).Select(d => d.o.AccountId!.Value).Distinct().ToList();
			var accounts = await _db.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == cid && accIds.Contains(a.ID)).ToDictionaryAsync(a => a.ID, a => !IsAr && !string.IsNullOrWhiteSpace(a.NameEn) ? a.NameEn! : a.Name);
			string Title(Opportunity o) => !IsAr && !string.IsNullOrWhiteSpace(o.TitleEn) ? o.TitleEn! : o.Title;
			foreach (var t in SearchTerms.Parse(q))
			{
				var s = t;
				list = list.Where(d => Title(d.o).Contains(s, StringComparison.OrdinalIgnoreCase) || d.o.Title.Contains(s, StringComparison.OrdinalIgnoreCase)
					|| (d.o.AccountId is int a && accounts.TryGetValue(a, out var an) && an.Contains(s, StringComparison.OrdinalIgnoreCase)));
			}
			// the order a forecast call is made in: commit, best case, pipeline, then what is already decided
			int Rank(string c) => c switch { "Commit" => 0, "BestCase" => 1, "Pipeline" => 2, "Won" => 3, "Omitted" => 4, _ => 5 };
			var filtered = list.OrderBy(d => Rank(d.cat)).ThenByDescending(d => d.o.Amount).ToList();
			pageSize = Math.Clamp(pageSize, 10, 100); page = Math.Max(1, page);
			var slice = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
			var people = await PeopleAsync(cid, slice.Where(d => d.o.OwnerEmployeeId != null).Select(d => d.o.OwnerEmployeeId!.Value));
			var team = me is int m ? await TeamOfAsync(cid, m) : new HashSet<int>();
			return (slice.Select(d => new ForecastDeal(d.o.ID, Title(d.o), d.o.AccountId is int a ? accounts.GetValueOrDefault(a) : null,
				d.o.OwnerEmployeeId is int e ? people.GetValueOrDefault(e) : null, d.o.Amount, d.o.Probability, d.o.Stage, d.o.ExpectedCloseDate, d.cat, d.chosen,
				canEdit && d.cat is not ("Won" or "Lost") && d.o.OwnerEmployeeId is int oe && (oe == me || team.Contains(oe)),
				canEdit && d.cat is not ("Won" or "Lost") && team.Count > 1 && (d.o.OwnerEmployeeId == null || team.Contains(d.o.OwnerEmployeeId.Value)))).ToList(), filtered.Count);
		}

		/// A manager's team (self included); anyone else is a team of one.
		async Task<HashSet<int>> TeamOfAsync(int cid, int me)
			=> await _db.CrmUserRoles.AsNoTracking().AnyAsync(r => r.CompanyID == cid && r.EmployeeId == me && r.Role == "SalesManager")
				? await _access.TeamOwnerIdsAsync(me) : new HashSet<int> { me };

		public async Task<(bool ok, string? error)> SetCategoryAsync(int cid, HashSet<int>? visible, int? me, int oppId, string category)
		{
			if (me is not int m) return (false, "Your login is not linked to an employee");
			if (!F.Categories.Contains(category)) return (false, "Unknown category");
			var o = await _db.Opportunities.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyID == cid && x.ID == oppId);
			if (o == null || (visible != null && (o.OwnerEmployeeId == null || !visible.Contains(o.OwnerEmployeeId.Value)))) return (false, "Record not found");
			if (o.Stage is "Won" or "Lost") return (false, "A closed deal keeps its result");
			// the owner places their own deals; their manager may move them too (that is what a forecast call is)
			if (o.OwnerEmployeeId is not int owner || (owner != m && !(await TeamOfAsync(cid, m)).Contains(owner))) return (false, "Only the deal's owner or their manager can change its category");
			var row = await _db.Set<CrmDealForecast>().FirstOrDefaultAsync(x => x.CompanyId == cid && x.OpportunityId == oppId);
			if (row == null) _db.Set<CrmDealForecast>().Add(row = new CrmDealForecast { CompanyId = cid, OpportunityId = oppId });
			row.Category = category; row.SetByEmployeeId = m; row.SetAt = DateTime.UtcNow;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		/// The people a deal may be handed to: a manager's own team; with CRM settings rights, everyone with a sales role.
		public async Task<List<PersonRef>> AssignableAsync(int cid, int? me, bool canManage)
		{
			var ids = canManage ? (await RolesAsync(cid)).Keys.ToHashSet() : me is int m ? await TeamOfAsync(cid, m) : new HashSet<int>();
			if (me is int mm && !canManage && ids.Count <= 1) return new();   // a rep hands nothing on
			return (await PeopleAsync(cid, ids)).Values.OrderBy(p => p.Name).ToList();
		}

		/// A forecast is by person, so a deal must be able to change hands: a manager gives a deal of the team (or one
		/// with no owner) to someone in the team. The new owner is told.
		public async Task<(bool ok, string? error)> SetOwnerAsync(int cid, HashSet<int>? visible, int? me, bool canManage, int oppId, int owner)
		{
			if (me is not int m) return (false, "Your login is not linked to an employee");
			var o = await _db.Opportunities.FirstOrDefaultAsync(x => x.CompanyID == cid && x.ID == oppId);
			if (o == null || (visible != null && o.OwnerEmployeeId != null && !visible.Contains(o.OwnerEmployeeId.Value))) return (false, "Record not found");
			if (o.Stage is "Won" or "Lost") return (false, "A closed deal keeps its result");
			if (o.OwnerEmployeeId == owner) return (true, null);
			var allowed = (await AssignableAsync(cid, m, canManage)).Select(p => p.EmployeeId).ToHashSet();
			if (!allowed.Contains(owner) || (o.OwnerEmployeeId is int cur && !allowed.Contains(cur))) return (false, "You can hand deals only within your own team");
			o.OwnerEmployeeId = owner;
			await _db.SaveChangesAsync();
			if (owner != m)
				await _notify.NotifyAsync(owner, "صفقة أُسندت إليك: " + o.Title, "A deal was handed to you: " + (string.IsNullOrWhiteSpace(o.TitleEn) ? o.Title : o.TitleEn),
					o.Amount.ToString("N0"), o.Amount.ToString("N0"), NotificationTypes.CrmAssigned, o.ID, url: "/Crm/Deal/" + o.ID, companyId: cid, actorEmployeeId: m, dedupKey: "crm-deal-owner:" + o.ID + ":" + owner + ":" + DateTime.UtcNow.Ticks);
			return (true, null);
		}

		public async Task<(bool ok, string? error)> SubmitAsync(int cid, int? me, string? period, string? note)
		{
			if (me is not int m) return (false, "Your login is not linked to an employee");
			var p = F.Parse(period);
			if (p == null) return (false, "Choose a month or a quarter");
			if (p.End <= DateTime.Today.AddMonths(-1)) return (false, "This period is closed");
			// the numbers are the person's OWN deals and quota, frozen now
			var deals = await PeriodDealsAsync(cid, new HashSet<int> { m }, p);
			var r = F.Roll((await QuotaOfAsync(cid, p)).GetValueOrDefault(m), deals.Select(d => new F.Deal(m, d.o.Amount, d.cat)));
			await using var tx = await _db.Database.BeginTransactionAsync();
			await _db.Set<CrmForecastSubmission>().Where(s => s.CompanyId == cid && s.EmployeeId == m && s.Period == p.Key && s.Status == "Submitted")
				.ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, "Superseded"));
			var sub = new CrmForecastSubmission
			{
				CompanyId = cid, EmployeeId = m, Period = p.Key, Quota = r.Quota, Won = r.Won, CommitAmount = r.Commit, BestCase = r.BestCase, Pipeline = r.Pipeline,
				Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(500, note.Trim().Length)], SubmittedAt = DateTime.UtcNow, Status = "Submitted",
			};
			_db.Set<CrmForecastSubmission>().Add(sub);
			await _db.SaveChangesAsync();
			await tx.CommitAsync();
			// tell the managers whose team this person is in
			var managers = await _db.CrmUserRoles.AsNoTracking().Where(x => x.CompanyID == cid && x.Role == "SalesManager" && x.EmployeeId != m).Select(x => x.EmployeeId).Distinct().ToListAsync();
			var name = (await PeopleAsync(cid, new[] { m })).GetValueOrDefault(m)?.Name ?? "";
			foreach (var mg in managers)
			{
				if (!(await _access.TeamOwnerIdsAsync(mg)).Contains(m)) continue;
				await _notify.NotifyAsync(mg, "توقع جديد للاعتماد: " + name + " — " + p.Key, "Forecast to sign off: " + name + " — " + p.Key,
					"مؤكد " + r.Forecast.ToString("N0") + " من هدف " + r.Quota.ToString("N0"), "Commit " + r.Forecast.ToString("N0") + " of quota " + r.Quota.ToString("N0"),
					NotificationTypes.CrmAssigned, sub.ID, url: "/Crm/Forecast?period=" + p.Key + "&person=" + m, companyId: cid, actorEmployeeId: m, dedupKey: "crm-forecast:" + sub.ID + ":" + mg);
			}
			return (true, null);
		}

		public async Task<(bool ok, string? error)> ReviewAsync(int cid, int? me, int submissionId, bool approve, decimal? adjustedCommit, string? note)
		{
			if (me is not int m) return (false, "Your login is not linked to an employee");
			var s = await _db.Set<CrmForecastSubmission>().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == submissionId);
			if (s == null) return (false, "Record not found");
			var refusal = F.ReviewRefusal(m, s.EmployeeId, await TeamOfAsync(cid, m), s.Status);
			if (refusal != null) return (false, refusal);
			if (!approve && string.IsNullOrWhiteSpace(note)) return (false, "Say why it is returned");
			if (adjustedCommit is < 0) return (false, "The adjusted forecast cannot be negative");
			s.Status = approve ? "Approved" : "Returned"; s.ReviewedByEmployeeId = m; s.ReviewedAt = DateTime.UtcNow;
			s.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(500, note.Trim().Length)];
			s.AdjustedCommit = approve && adjustedCommit is decimal a && a != s.Won + s.CommitAmount ? a : null;
			await _db.SaveChangesAsync();
			await _notify.NotifyAsync(s.EmployeeId, (approve ? "اعتُمد توقعك: " : "أُعيد توقعك للمراجعة: ") + s.Period, (approve ? "Your forecast was signed off: " : "Your forecast was returned: ") + s.Period,
				s.ReviewNote, s.ReviewNote, NotificationTypes.CrmAssigned, s.ID, url: "/Crm/Forecast?period=" + s.Period, companyId: cid, actorEmployeeId: m, dedupKey: "crm-forecast-review:" + s.ID);
			return (true, null);
		}

		public async Task<List<QuotaRow>> QuotasAsync(int cid, HashSet<int>? visible, int? me, bool canManage, int year, string? q)
		{
			var roles = await RolesAsync(cid);
			var prefix = year + "-";
			var quotas = await _db.Set<CrmQuota>().AsNoTracking().Where(x => x.CompanyId == cid && x.Period.StartsWith(prefix)).ToListAsync();
			var ids = roles.Keys.Concat(quotas.Select(x => x.EmployeeId)).Where(e => visible == null || visible.Contains(e)).Distinct().ToList();
			var people = await PeopleAsync(cid, ids);
			var team = me is int m ? await TeamOfAsync(cid, m) : new HashSet<int>();
			var rows = ids.Select(e => new QuotaRow(people.GetValueOrDefault(e) ?? new PersonRef(e, "#" + e, false), roles.GetValueOrDefault(e),
				quotas.Where(x => x.EmployeeId == e).ToDictionary(x => x.Period, x => x.Amount),
				// a manager sets the team's numbers, never their own; "manage" sets anyone's
				canManage || (me is int mm && e != mm && team.Contains(e) && roles.GetValueOrDefault(mm) == "SalesManager")));
			foreach (var t in SearchTerms.Parse(q)) { var s = t; rows = rows.Where(r => r.Person.Name.Contains(s, StringComparison.OrdinalIgnoreCase)); }
			return rows.OrderBy(r => r.Role == "SalesManager" ? 0 : 1).ThenBy(r => r.Person.Name).ToList();
		}

		public async Task<(bool ok, string? error, int saved)> SaveQuotasAsync(int cid, int? me, bool canManage, int year, IReadOnlyDictionary<(int emp, string month), decimal> values)
		{
			if (me is not int m && !canManage) return (false, "Your login is not linked to an employee", 0);
			if (values.Any(v => v.Value < 0)) return (false, "A target cannot be negative", 0);
			if (values.Keys.Any(k => F.Parse(k.month) is not { IsQuarter: false } p || p.Year != year)) return (false, "Choose a month or a quarter", 0);
			var allowed = (await QuotasAsync(cid, null, me, canManage, year, null)).Where(r => r.CanEdit).Select(r => r.Person.EmployeeId).ToHashSet();
			var refused = values.Keys.Select(k => k.emp).Distinct().Where(e => !allowed.Contains(e)).ToList();
			if (refused.Count > 0) return (false, "You can set targets only for your own team", 0);
			var emps = values.Keys.Select(k => k.emp).Distinct().ToList();
			var prefix = year + "-";
			var existing = await _db.Set<CrmQuota>().Where(x => x.CompanyId == cid && emps.Contains(x.EmployeeId) && x.Period.StartsWith(prefix)).ToListAsync();
			int n = 0;
			foreach (var ((emp, month), amount) in values)
			{
				var row = existing.FirstOrDefault(x => x.EmployeeId == emp && x.Period == month);
				if (row == null && amount == 0) continue;
				if (row != null && row.Amount == amount) continue;
				if (row == null) _db.Set<CrmQuota>().Add(row = new CrmQuota { CompanyId = cid, EmployeeId = emp, Period = month });
				row.Amount = amount; row.UpdatedByEmployeeId = me; row.UpdatedAt = DateTime.UtcNow; n++;
			}
			await _db.SaveChangesAsync();
			return (true, null, n);
		}
	}
}
