using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H80 — chart completion. Reads every discharged stay of the window (default the last 90 days), works out
	// each chart's deficiencies from the chart's own records, and lets the records department sign a complete chart off.
	// A feature not installed contributes no deficiency (its table is missing, so it has nothing to be missing).
	// ==========================================================================================
	public sealed record HospitalChartRow(int AdmissionId, int PatientId, string Name, string NameEn, string Mrn, DateTime DischargedAt, int DaysSince, string? Attending,
		IReadOnlyList<string> Deficiencies, HospitalChartReview? Review, bool Delinquent);

	public sealed record HospitalChartStats(int Incomplete, int Delinquent, int SignedThisMonth, int ReadyToSign);

	public static class HospitalChartReviewRules
	{
		public const int DelinquentAfterDays = 14;

		/// The deficiencies of one discharged stay, in a fixed order.
		public static IReadOnlyList<string> Deficiencies(bool hasSummary, bool summarySigned, bool medRecSigned, int openConsults, int undecidedInfections, int devicesIn, int openPlans)
		{
			var d = new List<string>();
			if (!hasSummary) d.Add(HospitalChartDeficiency.NoSummary);
			else if (!summarySigned) d.Add(HospitalChartDeficiency.SummaryUnsigned);
			if (!medRecSigned) d.Add(HospitalChartDeficiency.MedRecOpen);
			if (openConsults > 0) d.Add(HospitalChartDeficiency.ConsultOpen);
			if (undecidedInfections > 0) d.Add(HospitalChartDeficiency.InfectionUndecided);
			if (devicesIn > 0) d.Add(HospitalChartDeficiency.DeviceInPlace);
			if (openPlans > 0) d.Add(HospitalChartDeficiency.CarePlanOpen);
			return d;
		}

		/// Days since discharge on the ward calendar (the discharge day is day 0).
		public static int DaysSince(DateTime dischargedLocal, DateTime todayLocal) => Math.Max(0, (todayLocal.Date - dischargedLocal.Date).Days);

		public static bool Delinquent(int daysSince, int deficiencies, bool signed) => !signed && deficiencies > 0 && daysSince > DelinquentAfterDays;

		public static string? SignRefusal(bool discharged, int deficiencies, bool alreadySigned)
		{
			if (!discharged) return "Only a discharged stay's chart is reviewed";
			if (alreadySigned) return "This chart is already signed complete";
			if (deficiencies > 0) return "The chart still has deficiencies";
			return null;
		}
	}

	public sealed class HospitalChartReviewService
	{
		public const string NoTables = "Chart completion is not installed yet (hospital_h80_chart_review.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalChartReviewService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static DateTime Local(DateTime utc) => HospitalNursingService.UtcToWard(utc);

		private static async Task<List<T>> SafeAsync<T>(Func<Task<List<T>>> q)
		{
			try { return await q(); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new List<T>(); }
		}

		/// The deficiencies of these discharged stays, each check reading its own feature's records.
		private async Task<Dictionary<int, IReadOnlyList<string>>> DeficienciesAsync(BusinessContext ctx, IReadOnlyCollection<int> ids, CancellationToken ct)
		{
			int c = ctx.CompanyId;
			var summaries = await SafeAsync(() => _db.Set<HospitalDischargeSummary>().AsNoTracking().Where(s => s.CompanyId == c && ids.Contains(s.AdmissionId)).Select(s => new { s.AdmissionId, s.Status }).ToListAsync(ct));
			var medrec = await SafeAsync(() => _db.Set<HospitalMedRecon>().AsNoTracking().Where(r => r.CompanyId == c && ids.Contains(r.AdmissionId) && r.Stage == HospitalMedReconStage.Discharge).Select(r => r.AdmissionId).ToListAsync(ct));
			var consults = await SafeAsync(() => _db.Set<HospitalConsultRequest>().AsNoTracking().Where(r => r.CompanyId == c && ids.Contains(r.AdmissionId) && (r.Status == HospitalConsultRequestStatus.Requested || r.Status == HospitalConsultRequestStatus.Accepted)).Select(r => r.AdmissionId).ToListAsync(ct));
			var infections = await SafeAsync(() => _db.Set<HospitalInfectionCase>().AsNoTracking().Where(r => r.CompanyId == c && r.AdmissionId != null && ids.Contains(r.AdmissionId.Value) && r.Status == HospitalInfectionStatus.Suspected).Select(r => r.AdmissionId!.Value).ToListAsync(ct));
			var devices = await SafeAsync(() => _db.Set<HospitalInvasiveDevice>().AsNoTracking().Where(r => r.CompanyId == c && ids.Contains(r.AdmissionId) && r.RemovedAt == null).Select(r => r.AdmissionId).ToListAsync(ct));
			var plans = await SafeAsync(() => _db.Set<HospitalCarePlanItem>().AsNoTracking().Where(r => r.CompanyId == c && ids.Contains(r.AdmissionId) && r.Status == HospitalCarePlanStatus.Active).Select(r => r.AdmissionId).ToListAsync(ct));
			return ids.ToDictionary(id => id, id =>
			{
				var s = summaries.FirstOrDefault(x => x.AdmissionId == id);
				return HospitalChartReviewRules.Deficiencies(s != null, s?.Status == HospitalDischargeSummaryStatus.Signed, medrec.Contains(id),
					consults.Count(x => x == id), infections.Count(x => x == id), devices.Count(x => x == id), plans.Count(x => x == id));
			});
		}

		/// The register: discharged stays of the last `days`, filtered (incomplete / ready / signed / all), searched and paged.
		public async Task<(IReadOnlyList<HospitalChartRow> Rows, int Total, HospitalChartStats Stats, bool Ready)> ListAsync(BusinessContext ctx, string? state, string? q, int days, int page, int pageSize, DateTime nowUtc, CancellationToken ct = default)
		{
			days = Math.Clamp(days, 7, 365);
			var since = nowUtc.AddDays(-days);
			var stays = await (from a in _db.HospitalAdmissions.AsNoTracking()
							   join p in _db.HospitalPatients.AsNoTracking() on a.PatientId equals p.ID
							   where a.CompanyId == ctx.CompanyId && a.Status == HospitalAdmissionStatus.Discharged && a.DischargedAt != null && a.DischargedAt >= since
							   select new { a.ID, a.PatientId, a.DischargedAt, a.AttendingEmployeeId, p.Name, p.NameEn, p.Mrn }).ToListAsync(ct);
			List<HospitalChartReview> reviews; bool ready = true;
			try { var ids0 = stays.Select(s => s.ID).ToList(); reviews = await _db.Set<HospitalChartReview>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && ids0.Contains(r.AdmissionId)).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { reviews = new(); ready = false; }
			var def = await DeficienciesAsync(ctx, stays.Select(s => s.ID).ToList(), ct);
			var docIds = stays.Where(s => s.AttendingEmployeeId != null).Select(s => s.AttendingEmployeeId!.Value).Distinct().ToList();
			var docs = await _db.Employee.AsNoTracking().Where(e => docIds.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
			var today = Local(nowUtc);
			var all = stays.Select(s =>
			{
				var d = def[s.ID]; var r = reviews.FirstOrDefault(x => x.AdmissionId == s.ID);
				int since0 = HospitalChartReviewRules.DaysSince(Local(s.DischargedAt!.Value), today);
				return new HospitalChartRow(s.ID, s.PatientId, s.Name, s.NameEn ?? "", s.Mrn, s.DischargedAt!.Value, since0,
					s.AttendingEmployeeId is int a && docs.TryGetValue(a, out var n) ? n : null, d, r, HospitalChartReviewRules.Delinquent(since0, d.Count, r != null));
			}).ToList();
			var monthStart = new DateTime(today.Year, today.Month, 1);
			var stats = new HospitalChartStats(all.Count(x => x.Review == null && x.Deficiencies.Count > 0), all.Count(x => x.Delinquent),
				all.Count(x => x.Review != null && Local(x.Review.ReviewedAt) >= monthStart), all.Count(x => x.Review == null && x.Deficiencies.Count == 0));
			IEnumerable<HospitalChartRow> f = state switch
			{
				"incomplete" => all.Where(x => x.Review == null && x.Deficiencies.Count > 0),
				"ready" => all.Where(x => x.Review == null && x.Deficiencies.Count == 0),
				"signed" => all.Where(x => x.Review != null),
				"delinquent" => all.Where(x => x.Delinquent),
				_ => all,
			};
			if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); f = f.Where(x => x.Name.Contains(s, StringComparison.OrdinalIgnoreCase) || x.NameEn.Contains(s, StringComparison.OrdinalIgnoreCase) || x.Mrn.Contains(s, StringComparison.OrdinalIgnoreCase)); }
			var list = f.OrderByDescending(x => x.Delinquent).ThenByDescending(x => x.Review == null).ThenByDescending(x => x.DaysSince).ToList();
			pageSize = Math.Clamp(pageSize, 5, 100); page = Math.Max(1, page);
			return (list.Skip((page - 1) * pageSize).Take(pageSize).ToList(), list.Count, stats, ready);
		}

		/// The records department signs a complete chart off (it re-checks the deficiencies itself).
		public async Task<HospitalResult> SignAsync(BusinessContext ctx, int admissionId, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			var adm = await _db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(a => a.ID == admissionId && a.CompanyId == ctx.CompanyId, ct);
			if (adm == null) return HospitalResult.Fail("not_found");
			try
			{
				bool signed = await _db.Set<HospitalChartReview>().AnyAsync(r => r.CompanyId == ctx.CompanyId && r.AdmissionId == admissionId, ct);
				var d = (await DeficienciesAsync(ctx, new[] { admissionId }, ct))[admissionId];
				if (HospitalChartReviewRules.SignRefusal(adm.Status == HospitalAdmissionStatus.Discharged, d.Count, signed) is string why) return HospitalResult.Fail(why);
				var row = new HospitalChartReview { CompanyId = ctx.CompanyId, AdmissionId = adm.ID, PatientId = adm.PatientId, ReviewedAt = nowUtc, ReviewedByEmployeeId = employeeId,
					Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(300, note.Trim().Length)] };
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}
	}
}
