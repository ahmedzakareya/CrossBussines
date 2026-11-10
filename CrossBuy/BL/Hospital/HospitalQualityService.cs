using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H81 — quality indicators. One month at a time (and the five before it for the trend), every value worked
	// out from the records. An indicator whose feature is not installed, or that has no denominator that month, has no
	// value — never a zero that would read as perfect.
	// ==========================================================================================
	public sealed record HospitalQualityValue(string Code, string Unit, bool LowerBetter, decimal Target, bool TargetIsDefault, decimal? Value, int? Numerator, int? Denominator,
		IReadOnlyList<decimal?> Trend)
	{
		/// Met, NotMet or NoData — never a third colour.
		public string Status => HospitalQualityRules.Status(Value, Target, LowerBetter);
	}

	public sealed record HospitalQualityPage(int Year, int Month, IReadOnlyList<HospitalQualityValue> Values, IReadOnlyList<(int Year, int Month)> TrendMonths, bool TargetsReady);

	public static class HospitalQualityRules
	{
		public static decimal? Pct(int n, int d) => d == 0 ? null : Math.Round(n * 100m / d, 1);
		public static decimal? Per1000(int n, decimal days) => days <= 0 ? null : Math.Round(n * 1000m / days, 2);

		/// NPS: % promoters (9–10) minus % detractors (0–6).
		public static decimal? Nps(IReadOnlyCollection<int> recommend) => recommend.Count == 0 ? null
			: Math.Round((recommend.Count(r => r >= 9) - recommend.Count(r => r <= 6)) * 100m / recommend.Count, 0);

		public static string Status(decimal? value, decimal target, bool lowerBetter) =>
			value == null ? "NoData" : (lowerBetter ? value.Value <= target : value.Value >= target) ? "Met" : "NotMet";

		/// The hours of [start, end) that fall inside [from, to), as days.
		public static decimal OverlapDays(DateTime start, DateTime? end, DateTime from, DateTime to)
		{
			var a = start > from ? start : from;
			var e = end ?? to;
			var b = e < to ? e : to;
			return b <= a ? 0 : (decimal)(b - a).TotalHours / 24m;
		}

		/// Readmitted: another stay of the same patient starts after this discharge and within 30 days of it.
		public static bool Readmitted(DateTime dischargedAt, IEnumerable<DateTime> laterAdmissions) =>
			laterAdmissions.Any(a => a > dischargedAt && a <= dischargedAt.AddDays(30));

		public static string? TargetRefusal(string? code, decimal? target)
		{
			if (code == null || !HospitalQualityIndicator.Known(code)) return "not_found";
			if (target == null) return "Write the target";
			var unit = HospitalQualityIndicator.All.First(i => i.Code == code).Unit;
			if (unit is "%" && (target < 0 || target > 100)) return "A percentage target is between 0 and 100";
			if (unit is "of5" && (target < 1 || target > 5)) return "A satisfaction target is between 1 and 5";
			if (unit is "score" && (target < -100 || target > 100)) return "An NPS target is between -100 and 100";
			if (unit is "days" or "per1000" && (target < 0 || target > 1000)) return "The target is out of range";
			return null;
		}
	}

	public sealed class HospitalQualityService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalQualityService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static DateTime Utc(DateTime local) => HospitalNursingService.WardToUtc(local);

		private static async Task<int?> SafeCountAsync(Func<Task<int>> q)
		{
			try { return await q(); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}

		private static async Task<T?> SafeAsync<T>(Func<Task<T>> q) where T : class
		{
			try { return await q(); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}

		/// The values of one month ([from, to) on the ward calendar, the current month up to now).
		private async Task<Dictionary<string, (decimal? V, int? N, int? D)>> MonthAsync(BusinessContext ctx, DateTime fromLocal, DateTime nowUtc, CancellationToken ct)
		{
			int c = ctx.CompanyId;
			var fromUtc = Utc(fromLocal);
			var toUtc = Utc(fromLocal.AddMonths(1));
			if (toUtc > nowUtc) toUtc = nowUtc;
			var r = new Dictionary<string, (decimal?, int?, int?)>();
			if (toUtc <= fromUtc) return r;
			var span = (decimal)(toUtc - fromUtc).TotalHours / 24m;

			// stays: patient-days, discharges, length of stay, readmissions, occupancy
			var stays = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == c && a.AdmittedAt < toUtc && (a.DischargedAt == null || a.DischargedAt >= fromUtc))
				.Select(a => new { a.ID, a.PatientId, a.AdmittedAt, a.DischargedAt }).ToListAsync(ct);
			decimal patientDays = stays.Sum(s => HospitalQualityRules.OverlapDays(s.AdmittedAt, s.DischargedAt, fromUtc, toUtc));
			var discharged = stays.Where(s => s.DischargedAt != null && s.DischargedAt >= fromUtc && s.DischargedAt < toUtc).ToList();
			r[HospitalQualityIndicator.Alos] = (discharged.Count == 0 ? null : Math.Round((decimal)discharged.Average(s => (s.DischargedAt!.Value - s.AdmittedAt).TotalHours) / 24m, 1), null, discharged.Count);
			int beds = await _db.HospitalBeds.AsNoTracking().CountAsync(b => b.CompanyId == c && b.IsActive, ct);
			r[HospitalQualityIndicator.Occupancy] = (beds == 0 || span == 0 ? null : Math.Round(patientDays * 100m / (beds * span), 1), (int)Math.Round(patientDays), beds);
			var pids = discharged.Select(s => s.PatientId).Distinct().ToList();
			var later = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == c && pids.Contains(a.PatientId) && a.AdmittedAt >= fromUtc)
				.Select(a => new { a.PatientId, a.AdmittedAt }).ToListAsync(ct);
			int readmitted = discharged.Count(s => HospitalQualityRules.Readmitted(s.DischargedAt!.Value, later.Where(l => l.PatientId == s.PatientId).Select(l => l.AdmittedAt)));
			r[HospitalQualityIndicator.Readmission30] = (HospitalQualityRules.Pct(readmitted, discharged.Count), readmitted, discharged.Count);

			var deaths = await SafeCountAsync(() => _db.Set<HospitalDeath>().AsNoTracking().CountAsync(d => d.CompanyId == c && d.AdmissionId != null && d.DiedAt >= fromUtc && d.DiedAt < toUtc, ct));
			r[HospitalQualityIndicator.Mortality] = deaths is int dn ? (HospitalQualityRules.Pct(dn, discharged.Count), dn, discharged.Count) : (null, null, null);

			var fromDate = fromLocal.Date; var toDate = HospitalNursingService.UtcToWard(toUtc).Date;
			var hai = await SafeCountAsync(() => _db.Set<HospitalInfectionCase>().AsNoTracking().CountAsync(x => x.CompanyId == c && x.Status == HospitalInfectionStatus.Confirmed && x.HospitalAcquired && x.OnsetDate >= fromDate && x.OnsetDate <= toDate, ct));
			r[HospitalQualityIndicator.Hai] = hai is int h ? (HospitalQualityRules.Per1000(h, patientDays), h, (int)Math.Round(patientDays)) : (null, null, null);

			var inc = await SafeAsync(() => _db.Set<HospitalIncident>().AsNoTracking().Where(x => x.CompanyId == c && x.ReportedAt >= fromUtc && x.ReportedAt < toUtc && (x.Kind == HospitalIncidentKind.Fall || x.Kind == HospitalIncidentKind.MedicationError))
				.Select(x => x.Kind).ToListAsync(ct));
			r[HospitalQualityIndicator.Falls] = inc == null ? (null, null, null) : (HospitalQualityRules.Per1000(inc.Count(k => k == HospitalIncidentKind.Fall), patientDays), inc.Count(k => k == HospitalIncidentKind.Fall), (int)Math.Round(patientDays));
			r[HospitalQualityIndicator.MedErrors] = inc == null ? (null, null, null) : (HospitalQualityRules.Per1000(inc.Count(k => k == HospitalIncidentKind.MedicationError), patientDays), inc.Count(k => k == HospitalIncidentKind.MedicationError), (int)Math.Round(patientDays));

			// admission reconciliation within 24 hours, of the stays admitted this month (and at least a day old)
			var admitted = stays.Where(s => s.AdmittedAt >= fromUtc && s.AdmittedAt < toUtc && s.AdmittedAt <= nowUtc.AddHours(-24)).ToList();
			var admIds = admitted.Select(s => s.ID).ToList();
			var rec = await SafeAsync(() => _db.Set<HospitalMedRecon>().AsNoTracking().Where(x => x.CompanyId == c && x.Stage == HospitalMedReconStage.Admission && admIds.Contains(x.AdmissionId))
				.Select(x => new { x.AdmissionId, x.CompletedAt }).ToListAsync(ct));
			if (rec != null) { int ok = admitted.Count(s => rec.Any(x => x.AdmissionId == s.ID && x.CompletedAt <= s.AdmittedAt.AddHours(24))); r[HospitalQualityIndicator.MedRec24h] = (HospitalQualityRules.Pct(ok, admitted.Count), ok, admitted.Count); }

			var cons = await SafeAsync(() => _db.Set<HospitalConsultRequest>().AsNoTracking().Where(x => x.CompanyId == c && x.AnsweredAt != null && x.AnsweredAt >= fromUtc && x.AnsweredAt < toUtc)
				.Select(x => new { x.AnsweredAt, x.DueAt }).ToListAsync(ct));
			if (cons != null) { int ok = cons.Count(x => x.AnsweredAt <= x.DueAt); r[HospitalQualityIndicator.ConsultOnTime] = (HospitalQualityRules.Pct(ok, cons.Count), ok, cons.Count); }

			// charts signed within 14 days, of the month's discharges whose 14 days are over (or that are signed already)
			var disIds = discharged.Select(s => s.ID).ToList();
			var reviews = await SafeAsync(() => _db.Set<HospitalChartReview>().AsNoTracking().Where(x => x.CompanyId == c && disIds.Contains(x.AdmissionId)).Select(x => new { x.AdmissionId, x.ReviewedAt }).ToListAsync(ct));
			if (reviews != null)
			{
				var due = discharged.Where(s => s.DischargedAt!.Value.AddDays(14) <= nowUtc || reviews.Any(x => x.AdmissionId == s.ID)).ToList();
				int ok = due.Count(s => reviews.Any(x => x.AdmissionId == s.ID && x.ReviewedAt <= s.DischargedAt!.Value.AddDays(14)));
				r[HospitalQualityIndicator.ChartOnTime] = (HospitalQualityRules.Pct(ok, due.Count), ok, due.Count);
			}

			var surveys = await SafeAsync(() => _db.Set<HospitalSurveyResponse>().AsNoTracking().Where(x => x.CompanyId == c && x.At >= fromUtc && x.At < toUtc).Select(x => new { x.Average, x.Recommend }).ToListAsync(ct));
			if (surveys != null)
			{
				r[HospitalQualityIndicator.Satisfaction] = (surveys.Count == 0 ? null : Math.Round(surveys.Average(x => x.Average), 2), null, surveys.Count);
				r[HospitalQualityIndicator.Nps] = (HospitalQualityRules.Nps(surveys.Select(x => x.Recommend).ToList()), null, surveys.Count);
			}
			return r;
		}

		public async Task<HospitalQualityPage> PageAsync(BusinessContext ctx, int year, int month, DateTime nowUtc, CancellationToken ct = default)
		{
			var sel = new DateTime(year, month, 1);
			var months = Enumerable.Range(0, 6).Select(i => sel.AddMonths(i - 5)).ToList();
			var data = new List<Dictionary<string, (decimal? V, int? N, int? D)>>();
			foreach (var m in months) data.Add(await MonthAsync(ctx, m, nowUtc, ct));
			Dictionary<string, decimal> targets; bool ready = true;
			try { targets = await _db.Set<HospitalQualityTarget>().AsNoTracking().Where(t => t.CompanyId == ctx.CompanyId).ToDictionaryAsync(t => t.Indicator, t => t.Target, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { targets = new(); ready = false; }
			var cur = data[^1];
			var values = HospitalQualityIndicator.All.Select(i =>
			{
				cur.TryGetValue(i.Code, out var v);
				bool own = targets.TryGetValue(i.Code, out var t);
				return new HospitalQualityValue(i.Code, i.Unit, i.LowerBetter, own ? t : i.Default, !own, v.V, v.N, v.D,
					data.Select(d => d.TryGetValue(i.Code, out var x) ? x.V : null).ToList());
			}).ToList();
			return new HospitalQualityPage(year, month, values, months.Select(m => (m.Year, m.Month)).ToList(), ready);
		}

		public async Task<HospitalResult> SetTargetAsync(BusinessContext ctx, string? code, decimal? target, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalQualityRules.TargetRefusal(code, target) is string why) return HospitalResult.Fail(why);
			try
			{
				var row = await _db.Set<HospitalQualityTarget>().FirstOrDefaultAsync(t => t.CompanyId == ctx.CompanyId && t.Indicator == code, ct);
				if (row == null) { row = new HospitalQualityTarget { CompanyId = ctx.CompanyId, Indicator = code! }; _db.Add(row); }
				row.Target = target!.Value; row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail("Quality targets are not installed yet (hospital_h81_quality_targets.sql)."); }
		}
	}
}
