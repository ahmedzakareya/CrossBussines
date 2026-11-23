using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalNicuDayInput
	{
		public DateTime? Day { get; set; } public int? WeightGrams { get; set; } public decimal? LengthCm { get; set; } public decimal? HeadCm { get; set; }
		public int? FeedsMl { get; set; } public string? FeedType { get; set; } public bool Tpn { get; set; } public bool Phototherapy { get; set; } public int? Bilirubin { get; set; }
	}

	public sealed record HospitalNicuBaby(int AdmissionId, int PatientId, string Name, string? NameEn, string? Photo, string? Bed, DateTime BornAt, int? GestationWeeks, int BirthWeight,
		int DayOfLife, (int Weeks, int Days)? Pma, int? CorrectedDays, int? Weight, decimal? PercentOfBirth, decimal? Velocity7, decimal? FeedsPerKg, HospitalNicuDay? Today,
		IReadOnlyList<HospitalNicuDay> Recent);

	public sealed record HospitalNicuBoard(bool Ready, IReadOnlyList<HospitalNicuBaby> Babies, int Total, DateTime Today);

	// ==========================================================================================
	// HOSPITAL H252 — the neonatal board: every admitted newborn (a patient born here, in the last 180 days, with an open
	// stay) with day of life, postmenstrual age, weight against birth weight, the last week's growth velocity and today's
	// feeds per kilo; the nurse (`admit`) records the day. The rules are HospitalNicuRules'.
	// ==========================================================================================
	public sealed class HospitalNicuService
	{
		public const string NoTables = "The neonatal record is not installed yet (hospital_h252_nicu.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalNicuService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static DateTime Ward(DateTime utc) => HospitalNursingService.UtcToWard(utc);

		public async Task<HospitalNicuBoard> BoardAsync(BusinessContext ctx, DateTime nowUtc, string? q, int page, int size, CancellationToken ct = default)
		{
			var today = Ward(nowUtc).Date;
			var since = nowUtc.AddDays(-180);
			var rows = await (from a in _db.HospitalAdmissions.AsNoTracking()
							  join b in _db.Set<HospitalBirth>().AsNoTracking() on a.PatientId equals b.NewbornPatientId
							  join p in _db.HospitalPatients.AsNoTracking() on a.PatientId equals p.ID
							  where a.CompanyId == ctx.CompanyId && a.Status == HospitalAdmissionStatus.Active && b.CompanyId == ctx.CompanyId && b.VoidedAt == null && b.BornAt >= since
							  select new { a.ID, a.PatientId, p.Name, p.NameEn, p.PhotoPath, b.BornAt, b.GestationWeeks, b.WeightGrams }).ToListAsync(ct);
			if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim(); rows = rows.Where(r => r.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || (r.NameEn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)).ToList(); }
			var ids = rows.Select(r => r.ID).ToList();
			var beds = await (from s in _db.HospitalBedStays.AsNoTracking() join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
							  where s.CompanyId == ctx.CompanyId && s.EndedAt == null && ids.Contains(s.AdmissionId) select new { s.AdmissionId, b.Code }).ToListAsync(ct);
			bool ready = true; List<HospitalNicuDay> days = new();
			try
			{
				var from = today.AddDays(-14);
				days = await _db.Set<HospitalNicuDay>().AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && ids.Contains(d.AdmissionId) && d.Day >= from).OrderBy(d => d.Day).ToListAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { ready = false; }
			var babies = rows.OrderBy(r => r.BornAt).Select(r =>
			{
				var mine = days.Where(d => d.AdmissionId == r.ID).ToList();
				int dol = HospitalNicuRules.DayOfLife(Ward(r.BornAt).Date, today);
				var weighed = mine.Where(d => d.WeightGrams != null).ToList();
				var last = weighed.LastOrDefault();
				var weekAgo = weighed.LastOrDefault(d => d.Day <= today.AddDays(-7)) ?? weighed.FirstOrDefault();
				decimal? vel = last != null && weekAgo != null && weekAgo != last ? HospitalNicuRules.Velocity(weekAgo.WeightGrams!.Value, weekAgo.Day, last.WeightGrams!.Value, last.Day) : null;
				var todayRow = mine.FirstOrDefault(d => d.Day == today);
				int? weight = last?.WeightGrams;
				return new HospitalNicuBaby(r.ID, r.PatientId, r.Name, r.NameEn, r.PhotoPath, beds.FirstOrDefault(b => b.AdmissionId == r.ID)?.Code, r.BornAt, r.GestationWeeks, r.WeightGrams,
					dol, HospitalNicuRules.Pma(r.GestationWeeks, dol), HospitalNicuRules.CorrectedDays(r.GestationWeeks, dol), weight, HospitalNicuRules.PercentOfBirth(weight, r.WeightGrams), vel,
					HospitalNicuRules.FeedsPerKg(todayRow?.FeedsMl, todayRow?.WeightGrams ?? weight ?? r.WeightGrams), todayRow, mine.TakeLast(7).ToList());
			}).ToList();
			return new HospitalNicuBoard(ready, babies.Skip((Math.Max(1, page) - 1) * size).Take(size).ToList(), babies.Count, today);
		}

		public async Task<HospitalResult> RecordAsync(BusinessContext ctx, int admissionId, HospitalNicuDayInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Admit, null, ct)) return HospitalResult.Fail("forbidden");
			var a = await _db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(x => x.ID == admissionId && x.CompanyId == ctx.CompanyId, ct);
			if (a == null || !await _db.Set<HospitalBirth>().AnyAsync(b => b.NewbornPatientId == a.PatientId && b.CompanyId == ctx.CompanyId && b.VoidedAt == null, ct)) return HospitalResult.Fail("not_found");
			if (a.Status != HospitalAdmissionStatus.Active) return HospitalResult.Fail("This admission is already discharged");
			var today = Ward(nowUtc).Date;
			var day = (i.Day ?? today).Date;
			if (day > today || day < today.AddDays(-2)) return HospitalResult.Fail("A day is recorded on it or in the two days after");
			if (HospitalNicuRules.DayRefusal(i.WeightGrams, i.LengthCm, i.HeadCm, i.FeedsMl, i.FeedType, i.Bilirubin) is string why) return HospitalResult.Fail(why);
			try
			{
				var row = await _db.Set<HospitalNicuDay>().FirstOrDefaultAsync(d => d.CompanyId == ctx.CompanyId && d.AdmissionId == a.ID && d.Day == day, ct);
				if (row == null) _db.Add(row = new HospitalNicuDay { CompanyId = ctx.CompanyId, AdmissionId = a.ID, PatientId = a.PatientId, Day = day });
				row.WeightGrams = i.WeightGrams ?? row.WeightGrams; row.LengthCm = i.LengthCm ?? row.LengthCm; row.HeadCm = i.HeadCm ?? row.HeadCm;
				row.FeedsMl = i.FeedsMl ?? row.FeedsMl; row.FeedType = i.FeedType ?? row.FeedType; row.Tpn = i.Tpn; row.Phototherapy = i.Phototherapy; row.Bilirubin = i.Bilirubin ?? row.Bilirubin;
				row.RecordedAt = nowUtc; row.RecordedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}
	}
}
