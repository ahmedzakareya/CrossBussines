using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H79 — the problem list (doctors write and resolve; everyone caring for the patient reads it) and the nursing
	// care plan of a stay (the ward writes and closes it). A coded problem takes its name from the diagnosis list, never
	// from the caller. The plan's suggestions come from the latest Morse and Braden of the stay.
	// ==========================================================================================
	public sealed class HospitalProblemInput
	{
		public int AdmissionId { get; set; }
		/// ICD-10 from the diagnosis list; or none, with the name written.
		public string? Code { get; set; }
		public string? Name { get; set; }
		public string? Kind { get; set; }
		public DateTime? OnsetDate { get; set; }
		public string? Note { get; set; }
	}

	public sealed class HospitalCarePlanInput
	{
		public int AdmissionId { get; set; }
		public string? Need { get; set; }
		public string? NeedText { get; set; }
		public string? Goal { get; set; }
		public string? Interventions { get; set; }
		public DateTime? ReviewBy { get; set; }
	}

	public sealed class HospitalCarePlanCloseInput
	{
		public string? Status { get; set; }
		public string? Outcome { get; set; }
	}

	/// A suggested plan: the need, and the assessment that calls for it (kind, score, level).
	public sealed record HospitalCareSuggestion(string Need, string Kind, int Score, string Level);

	public sealed record HospitalCarePanel(int AdmissionId, int PatientId, string Name, string NameEn, bool Active, IReadOnlyList<HospitalProblem> Problems,
		IReadOnlyList<HospitalCarePlanItem> Plan, IReadOnlyList<HospitalCareSuggestion> Suggestions, IReadOnlyDictionary<int, string> People, DateTime Today, bool Ready);

	public sealed record HospitalCareBadge(int ActivePlans, int ReviewDue, int Suggested);

	public static class HospitalCarePlanRules
	{
		public static string? ProblemRefusal(bool coded, string? name, string? kind, DateTime? onset, DateTime todayLocal)
		{
			if (!coded && string.IsNullOrWhiteSpace(name)) return "Choose the diagnosis or write the problem";
			if (!coded && name!.Trim().Length > 200) return "The problem's name is too long";
			if (!HospitalProblemKind.All.Contains(kind ?? "")) return "Choose chronic or acute";
			if (onset != null && onset.Value.Date > todayLocal.Date) return "The date is in the future";
			return null;
		}

		public static string? PlanRefusal(HospitalCarePlanInput i, DateTime todayLocal)
		{
			if (!HospitalCareNeed.All.Contains(i.Need ?? "")) return "Choose the nursing need";
			if (i.Need == HospitalCareNeed.Other && string.IsNullOrWhiteSpace(i.NeedText)) return "Write the nursing need";
			if (string.IsNullOrWhiteSpace(i.Goal)) return "Write the goal";
			if (string.IsNullOrWhiteSpace(i.Interventions)) return "Write the interventions";
			if (i.ReviewBy == null) return "Choose when it is reviewed";
			if (i.ReviewBy.Value.Date < todayLocal.Date) return "The review date has passed";
			if (i.ReviewBy.Value.Date > todayLocal.Date.AddDays(30)) return "Review it within 30 days";
			return null;
		}

		public static string? CloseRefusal(HospitalCarePlanItem item, string? status, string? outcome)
		{
			if (item.Status != HospitalCarePlanStatus.Active) return "This plan is already closed";
			if (status is not (HospitalCarePlanStatus.Met or HospitalCarePlanStatus.Discontinued)) return "Choose met or discontinued";
			if (string.IsNullOrWhiteSpace(outcome)) return "Write the outcome";
			return null;
		}

		/// A falls plan when the latest Morse is moderate or high, a pressure-injury plan when the latest Braden shows any risk —
		/// unless the stay already has an active plan for that need.
		public static IReadOnlyList<HospitalCareSuggestion> Suggest(HospitalRiskAssessment? morse, HospitalRiskAssessment? braden, IReadOnlyCollection<string> activeNeeds)
		{
			var list = new List<HospitalCareSuggestion>();
			if (morse != null && morse.Level is "Moderate" or "High" && !activeNeeds.Contains(HospitalCareNeed.Falls))
				list.Add(new HospitalCareSuggestion(HospitalCareNeed.Falls, HospitalRiskKind.Morse, morse.Score, morse.Level));
			if (braden != null && braden.Level != "None" && !activeNeeds.Contains(HospitalCareNeed.PressureInjury))
				list.Add(new HospitalCareSuggestion(HospitalCareNeed.PressureInjury, HospitalRiskKind.Braden, braden.Score, braden.Level));
			return list;
		}

		public static bool ReviewDue(HospitalCarePlanItem i, DateTime todayLocal) => i.Status == HospitalCarePlanStatus.Active && i.ReviewBy.Date <= todayLocal.Date;
	}

	public sealed class HospitalCarePlanService
	{
		public const string NoTables = "The problem list and care plans are not installed yet (hospital_h79_care_plans.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalCarePlanService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private static DateTime Today(DateTime nowUtc) => HospitalNursingService.UtcToWard(nowUtc).Date;

		private async Task<bool> WardAsync(BusinessContext ctx, CancellationToken ct) =>
			await _access.CanAsync(ctx, HospitalActions.Admit, null, ct) || await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);

		private static async Task<(HospitalRiskAssessment? Morse, HospitalRiskAssessment? Braden)> LatestRisksAsync(CrossDbContext db, int companyId, int admissionId, CancellationToken ct)
		{
			try
			{
				var r = await db.Set<HospitalRiskAssessment>().AsNoTracking().Where(x => x.CompanyId == companyId && x.AdmissionId == admissionId)
					.OrderByDescending(x => x.AssessedAt).ToListAsync(ct);
				return (r.FirstOrDefault(x => x.Kind == HospitalRiskKind.Morse), r.FirstOrDefault(x => x.Kind == HospitalRiskKind.Braden));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (null, null); }
		}

		/// The nursing station's badge: active plans, plans due for review, and suggestions not yet acted on. Empty when not installed.
		public static async Task<IReadOnlyDictionary<int, HospitalCareBadge>> BadgesAsync(CrossDbContext db, int companyId, IReadOnlyCollection<int> admissionIds, DateTime nowUtc, CancellationToken ct)
		{
			var result = new Dictionary<int, HospitalCareBadge>();
			if (admissionIds.Count == 0) return result;
			try
			{
				var today = Today(nowUtc);
				var plans = await db.Set<HospitalCarePlanItem>().AsNoTracking().Where(x => x.CompanyId == companyId && admissionIds.Contains(x.AdmissionId) && x.Status == HospitalCarePlanStatus.Active).ToListAsync(ct);
				var risks = await db.Set<HospitalRiskAssessment>().AsNoTracking().Where(x => x.CompanyId == companyId && admissionIds.Contains(x.AdmissionId)).ToListAsync(ct);
				foreach (var a in admissionIds)
				{
					var mine = plans.Where(p => p.AdmissionId == a).ToList();
					var r = risks.Where(x => x.AdmissionId == a).OrderByDescending(x => x.AssessedAt).ToList();
					var sug = HospitalCarePlanRules.Suggest(r.FirstOrDefault(x => x.Kind == HospitalRiskKind.Morse), r.FirstOrDefault(x => x.Kind == HospitalRiskKind.Braden), mine.Select(p => p.Need).ToList());
					if (mine.Count > 0 || sug.Count > 0) result[a] = new HospitalCareBadge(mine.Count, mine.Count(p => HospitalCarePlanRules.ReviewDue(p, today)), sug.Count);
				}
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			return result;
		}

		public async Task<HospitalCarePanel?> PanelAsync(BusinessContext ctx, int admissionId, DateTime nowUtc, CancellationToken ct = default)
		{
			var head = await (from a in _db.HospitalAdmissions.AsNoTracking()
							  join p in _db.HospitalPatients.AsNoTracking() on a.PatientId equals p.ID
							  where a.ID == admissionId && a.CompanyId == ctx.CompanyId
							  select new { a.ID, a.PatientId, a.Status, p.Name, p.NameEn }).FirstOrDefaultAsync(ct);
			if (head == null) return null;
			var today = Today(nowUtc);
			try
			{
				var problems = await _db.Set<HospitalProblem>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.PatientId == head.PatientId)
					.OrderBy(x => x.Status == HospitalProblemStatus.Active ? 0 : 1).ThenBy(x => x.Kind).ThenByDescending(x => x.CreatedAt).ToListAsync(ct);
				var plan = await _db.Set<HospitalCarePlanItem>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.AdmissionId == admissionId)
					.OrderBy(x => x.Status == HospitalCarePlanStatus.Active ? 0 : 1).ThenBy(x => x.ReviewBy).ToListAsync(ct);
				var (morse, braden) = await LatestRisksAsync(_db, ctx.CompanyId, admissionId, ct);
				var sug = HospitalCarePlanRules.Suggest(morse, braden, plan.Where(p => p.Status == HospitalCarePlanStatus.Active).Select(p => p.Need).ToList());
				var ids = problems.SelectMany(x => new[] { x.CreatedByEmployeeId, x.ResolvedByEmployeeId }).Concat(plan.SelectMany(x => new[] { x.CreatedByEmployeeId, x.ClosedByEmployeeId }))
					.Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
				return new HospitalCarePanel(head.ID, head.PatientId, head.Name, head.NameEn ?? "", head.Status == HospitalAdmissionStatus.Active, problems, plan, sug, people, today, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex))
			{
				return new HospitalCarePanel(head.ID, head.PatientId, head.Name, head.NameEn ?? "", false, Array.Empty<HospitalProblem>(), Array.Empty<HospitalCarePlanItem>(),
					Array.Empty<HospitalCareSuggestion>(), new Dictionary<int, string>(), today, false);
			}
		}

		/// A doctor adds a problem to the patient's list (from the stay they are treating).
		public async Task<HospitalResult> AddProblemAsync(BusinessContext ctx, HospitalProblemInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			var adm = await _db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(a => a.ID == input.AdmissionId && a.CompanyId == ctx.CompanyId, ct);
			if (adm == null) return HospitalResult.Fail("not_found");
			HospitalDiagnosisCode? dx = null;
			if (!string.IsNullOrWhiteSpace(input.Code))
			{
				var code = input.Code.Trim();
				dx = await _db.Set<HospitalDiagnosisCode>().AsNoTracking().FirstOrDefaultAsync(d => d.CompanyId == ctx.CompanyId && d.Code == code && d.IsActive, ct);
				if (dx == null) return HospitalResult.Fail("Choose the diagnosis or write the problem");
			}
			if (HospitalCarePlanRules.ProblemRefusal(dx != null, input.Name, input.Kind, input.OnsetDate, Today(nowUtc)) is string why) return HospitalResult.Fail(why);
			try
			{
				if (dx != null && await _db.Set<HospitalProblem>().AnyAsync(p => p.CompanyId == ctx.CompanyId && p.PatientId == adm.PatientId && p.Code == dx.Code && p.Status == HospitalProblemStatus.Active, ct))
					return HospitalResult.Fail("This problem is already on the list");
				var row = new HospitalProblem
				{
					CompanyId = ctx.CompanyId, PatientId = adm.PatientId, Code = dx?.Code, Name = dx?.Name ?? input.Name!.Trim(), NameEn = dx?.NameEn,
					Kind = input.Kind!, Status = HospitalProblemStatus.Active, OnsetDate = input.OnsetDate?.Date, Note = Clip(input.Note, 500),
					CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> ResolveProblemAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var p = await _db.Set<HospitalProblem>().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
				if (p == null) return HospitalResult.Fail("not_found");
				if (p.Status != HospitalProblemStatus.Active) return HospitalResult.Fail("This problem is already resolved");
				p.Status = HospitalProblemStatus.Resolved; p.ResolvedAt = nowUtc; p.ResolvedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(p.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> AddPlanAsync(BusinessContext ctx, HospitalCarePlanInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await WardAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalCarePlanRules.PlanRefusal(input, Today(nowUtc)) is string why) return HospitalResult.Fail(why);
			var adm = await _db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(a => a.ID == input.AdmissionId && a.CompanyId == ctx.CompanyId, ct);
			if (adm == null) return HospitalResult.Fail("not_found");
			if (adm.Status != HospitalAdmissionStatus.Active) return HospitalResult.Fail("This admission is not active");
			try
			{
				if (input.Need != HospitalCareNeed.Other && await _db.Set<HospitalCarePlanItem>().AnyAsync(x => x.CompanyId == ctx.CompanyId && x.AdmissionId == adm.ID && x.Need == input.Need && x.Status == HospitalCarePlanStatus.Active, ct))
					return HospitalResult.Fail("This need already has an active plan: review or close it");
				var row = new HospitalCarePlanItem
				{
					CompanyId = ctx.CompanyId, AdmissionId = adm.ID, PatientId = adm.PatientId, Need = input.Need!, NeedText = Clip(input.NeedText, 200),
					Goal = Clip(input.Goal, 500)!, Interventions = Clip(input.Interventions, 2000)!, ReviewBy = input.ReviewBy!.Value.Date,
					Status = HospitalCarePlanStatus.Active, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> ClosePlanAsync(BusinessContext ctx, int id, HospitalCarePlanCloseInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await WardAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var p = await _db.Set<HospitalCarePlanItem>().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
				if (p == null) return HospitalResult.Fail("not_found");
				if (HospitalCarePlanRules.CloseRefusal(p, input.Status, input.Outcome) is string why) return HospitalResult.Fail(why);
				p.Status = input.Status!; p.Outcome = Clip(input.Outcome, 500); p.ClosedAt = nowUtc; p.ClosedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(p.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}
	}
}
