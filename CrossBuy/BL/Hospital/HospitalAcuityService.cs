using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalAcuityInput { public DateTime Day { get; set; } public string? Shift { get; set; } public List<string>? Factors { get; set; } }
	public sealed class HospitalStaffPlanInput { public DateTime Day { get; set; } public string? Shift { get; set; } public int Nurses { get; set; } }

	public sealed record HospitalAcuityPatient(int AdmissionId, int PatientId, string Name, string? NameEn, string? Photo, string Bed, int? WardId, string? Ward, string? WardEn, HospitalAcuityScore? Score);
	public sealed record HospitalAcuityWard(int? WardId, string Name, string? NameEn, int Patients, int Classified, IReadOnlyDictionary<int, int> ByLevel, int Needed, int? Planned)
	{
		public string Gap => HospitalAcuityRules.Gap(Needed, Planned);
	}
	public sealed record HospitalAcuityBoard(bool Ready, DateTime Day, string Shift, IReadOnlyList<HospitalAcuityWard> Wards, IReadOnlyList<HospitalAcuityPatient> Patients, int PatientTotal);

	// ==========================================================================================
	// HOSPITAL H246 — the acuity board of a day and shift: per ward the patients, their levels, the nurses needed and
	// the nurses planned; per patient the classification. The ward nurse (`admit`) classifies and plans.
	// ==========================================================================================
	public sealed class HospitalAcuityService
	{
		public const string NoTables = "Acuity staffing is not installed yet (hospital_h246_acuity.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalAcuityService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;

		public async Task<HospitalAcuityBoard> BoardAsync(BusinessContext ctx, DateTime day, string shift, string? q, int page, int size, CancellationToken ct = default)
		{
			day = day.Date;
			var stays = await (from s in _db.HospitalBedStays.AsNoTracking()
							   join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
							   join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
							   join a in _db.HospitalAdmissions.AsNoTracking() on s.AdmissionId equals a.ID
							   join p in _db.HospitalPatients.AsNoTracking() on s.PatientId equals p.ID
							   where s.CompanyId == ctx.CompanyId && s.EndedAt == null && a.Status == HospitalAdmissionStatus.Active
							   orderby b.Code
							   select new { a.ID, a.PatientId, p.Name, p.NameEn, p.PhotoPath, Bed = b.Code, r.WardId }).ToListAsync(ct);
			var wards = await _db.Set<HospitalWard>().AsNoTracking().Where(w => w.CompanyId == ctx.CompanyId).Select(w => new { w.ID, w.Name, w.NameEn }).ToDictionaryAsync(w => w.ID, ct);
			bool ready = true;
			Dictionary<int, HospitalAcuityScore> scores = new(); Dictionary<int, int> planned = new();
			try
			{
				var ids = stays.Select(s => s.ID).ToList();
				scores = await _db.Set<HospitalAcuityScore>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.Day == day && x.Shift == shift && ids.Contains(x.AdmissionId)).ToDictionaryAsync(x => x.AdmissionId, ct);
				planned = await _db.Set<HospitalWardStaffing>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.Day == day && x.Shift == shift).ToDictionaryAsync(x => x.WardId, x => x.NursesPlanned, ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { ready = false; }

			var patients = stays.Select(s =>
			{
				var w = s.WardId is int wid ? wards.GetValueOrDefault(wid) : null;
				return new HospitalAcuityPatient(s.ID, s.PatientId, s.Name, s.NameEn, s.PhotoPath, s.Bed, s.WardId, w?.Name, w?.NameEn, scores.GetValueOrDefault(s.ID));
			}).ToList();
			var wardRows = patients.GroupBy(p => p.WardId).Select(g =>
			{
				var levels = g.Select(p => p.Score?.Level ?? HospitalAcuityRules.Unclassified).ToList();
				var w = g.Key is int wid ? wards.GetValueOrDefault(wid) : null;
				return new HospitalAcuityWard(g.Key, w?.Name ?? "—", w?.NameEn, g.Count(), g.Count(p => p.Score != null),
					Enumerable.Range(1, 4).ToDictionary(l => l, l => g.Count(p => p.Score?.Level == l)), HospitalAcuityRules.NursesNeeded(levels), g.Key is int k && planned.TryGetValue(k, out var n) ? n : null);
			}).OrderBy(w => w.Name).ToList();

			IEnumerable<HospitalAcuityPatient> list = patients;
			if (!string.IsNullOrWhiteSpace(q))
			{
				var t = q.Trim();
				list = list.Where(p => p.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || (p.NameEn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || p.Bed.Contains(t, StringComparison.OrdinalIgnoreCase) || (p.Ward ?? "").Contains(t, StringComparison.OrdinalIgnoreCase));
			}
			var ordered = list.OrderBy(p => p.Score == null ? 0 : 1).ThenBy(p => p.Ward).ThenBy(p => p.Bed).ToList();
			return new HospitalAcuityBoard(ready, day, shift, wardRows, ordered.Skip((Math.Max(1, page) - 1) * size).Take(size).ToList(), ordered.Count);
		}

		public async Task<HospitalResult> ClassifyAsync(BusinessContext ctx, int admissionId, HospitalAcuityInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Admit, null, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalShift.All.Contains(i.Shift ?? "")) return HospitalResult.Fail("Choose the shift");
			var today = HospitalNursingService.UtcToWard(nowUtc).Date;
			if (i.Day.Date > today || i.Day.Date < today.AddDays(-1)) return HospitalResult.Fail("A shift is classified on its day or the day after");
			if ((i.Factors ?? new()).Any(f => HospitalAcuityRules.Factors.All(x => x.Code != f))) return HospitalResult.Fail("not_found");
			var a = await _db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(x => x.ID == admissionId && x.CompanyId == ctx.CompanyId, ct);
			if (a == null) return HospitalResult.Fail("not_found");
			if (a.Status != HospitalAdmissionStatus.Active) return HospitalResult.Fail("This admission is already discharged");
			var (_, level) = HospitalAcuityRules.Classify(i.Factors);
			try
			{
				var day = i.Day.Date;
				var row = await _db.Set<HospitalAcuityScore>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.AdmissionId == a.ID && x.Day == day && x.Shift == i.Shift, ct);
				if (row == null) _db.Add(row = new HospitalAcuityScore { CompanyId = ctx.CompanyId, AdmissionId = a.ID, PatientId = a.PatientId, Day = day, Shift = i.Shift! });
				row.Factors = HospitalAcuityRules.FactorText(i.Factors); row.Level = level; row.At = nowUtc; row.ByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}

		public async Task<HospitalResult> PlanAsync(BusinessContext ctx, int wardId, HospitalStaffPlanInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Admit, null, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalShift.All.Contains(i.Shift ?? "")) return HospitalResult.Fail("Choose the shift");
			if (i.Nurses is < 0 or > 100) return HospitalResult.Fail("The number of nurses is 0 to 100");
			if (!await _db.Set<HospitalWard>().AnyAsync(w => w.ID == wardId && w.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var today = HospitalNursingService.UtcToWard(nowUtc).Date;
			if (i.Day.Date < today.AddDays(-1) || i.Day.Date > today.AddDays(14)) return HospitalResult.Fail("Plan from yesterday to the next two weeks");
			try
			{
				var day = i.Day.Date;
				var row = await _db.Set<HospitalWardStaffing>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.WardId == wardId && x.Day == day && x.Shift == i.Shift, ct);
				if (row == null) _db.Add(row = new HospitalWardStaffing { CompanyId = ctx.CompanyId, WardId = wardId, Day = day, Shift = i.Shift! });
				row.NursesPlanned = i.Nurses; row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}
	}
}
