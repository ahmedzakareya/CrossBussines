using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalDischargePlanInput
	{
		public DateTime ExpectedDate { get; set; }
		public string? Confidence { get; set; }
		public List<string>? Barriers { get; set; }
		public string? Note { get; set; }
	}

	public sealed record HospitalCapacityWard(string Name, string? NameEn, int Beds, int InService, int Occupied, int DischargesToday)
	{
		public decimal? OccupancyPct => InService == 0 ? null : Math.Round(Occupied * 100m / InService, 0, MidpointRounding.AwayFromZero);
	}

	public sealed record HospitalCapacityPatient(int AdmissionId, int PatientId, string Name, string? NameEn, string? Photo, string Bed, string? Ward, string? WardEn,
		DateTime AdmittedAt, int LosDays, string? Diagnosis, DateTime? Expected, bool Planned, string? Confidence, string? Barriers, string? Note);

	public sealed record HospitalCapacityCentre(bool Ready, int Beds, int InService, int Occupied, int Vacant, int Cleaning, int Reserved, int ErWaiting, int ErInDepartment,
		IReadOnlyList<HospitalCapacityDay> Forecast, IReadOnlyList<HospitalCapacityWard> Wards, IReadOnlyList<HospitalCapacityPatient> Patients, int PatientTotal,
		IReadOnlyDictionary<string, int> BarrierCounts, DateTime Today)
	{
		public decimal? OccupancyPct => InService == 0 ? null : Math.Round(Occupied * 100m / InService, 0, MidpointRounding.AwayFromZero);
		public int DischargesToday => Forecast.Count > 0 ? Forecast[0].Discharges : 0;
	}

	// ==========================================================================================
	// HOSPITAL H239 — the capacity centre: the house's beds now, the next four days' forecast, each ward, and every
	// inpatient with the expected discharge (the team's plan, else the length-of-stay estimate) and its barriers. The
	// bed manager or the ward (`admit`) sets a plan; anyone who reads the hospital sees the centre.
	// ==========================================================================================
	public sealed class HospitalCapacityService
	{
		public const string NoTables = "Discharge plans are not installed yet (hospital_h239_capacity.sql).";
		public const int PageSize = 15;
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalCapacityService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static DateTime Ward(DateTime utc) => HospitalNursingService.UtcToWard(utc);

		public async Task<HospitalCapacityCentre> CentreAsync(BusinessContext ctx, DateTime nowUtc, string? q, int page, CancellationToken ct = default)
		{
			var today = Ward(nowUtc).Date;
			var beds = await (from b in _db.HospitalBeds.AsNoTracking()
							  join r in _db.HospitalRooms.AsNoTracking() on b.RoomId equals r.ID
							  where b.CompanyId == ctx.CompanyId && b.IsActive && (r.Kind == HospitalRoomKinds.Ward || r.Kind == HospitalRoomKinds.Icu)
							  select new { b.ID, b.Status, r.WardId }).ToListAsync(ct);
			var wards = await _db.Set<HospitalWard>().AsNoTracking().Where(w => w.CompanyId == ctx.CompanyId).Select(w => new { w.ID, w.Name, w.NameEn }).ToDictionaryAsync(w => w.ID, ct);

			var stays = await (from s in _db.HospitalBedStays.AsNoTracking()
							   join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
							   join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
							   join a in _db.HospitalAdmissions.AsNoTracking() on s.AdmissionId equals a.ID
							   join p in _db.HospitalPatients.AsNoTracking() on s.PatientId equals p.ID
							   where s.CompanyId == ctx.CompanyId && s.EndedAt == null && a.Status == HospitalAdmissionStatus.Active && p.CompanyId == ctx.CompanyId
							   select new { a.ID, a.PatientId, p.Name, p.NameEn, p.PhotoPath, p.Mrn, Bed = b.Code, r.WardId, a.AdmittedAt, a.Diagnosis }).ToListAsync(ct);

			// the length-of-stay history: discharges of the last year
			var since = nowUtc.AddDays(-365);
			var past = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.Status == HospitalAdmissionStatus.Discharged && a.DischargedAt != null && a.DischargedAt >= since)
				.Select(a => new { a.AdmittedAt, a.DischargedAt, a.Diagnosis }).ToListAsync(ct);
			int Los(DateTime from, DateTime to) => Math.Max(1, (Ward(to).Date - Ward(from).Date).Days);
			int? overall = HospitalCapacityRules.MedianDays(past.Select(a => Los(a.AdmittedAt, a.DischargedAt!.Value)));
			var byDx = past.Where(a => !string.IsNullOrWhiteSpace(a.Diagnosis)).GroupBy(a => a.Diagnosis!.Trim().ToLowerInvariant())
				.ToDictionary(g => g.Key, g => HospitalCapacityRules.MedianDays(g.Select(a => Los(a.AdmittedAt, a.DischargedAt!.Value))));
			var admittedDays = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.AdmittedAt >= nowUtc.AddDays(-7 * HospitalCapacityRules.Weeks - 1))
				.Select(a => a.AdmittedAt).ToListAsync(ct);
			var admittedWard = admittedDays.Select(Ward).ToList();

			bool ready = true;
			Dictionary<int, HospitalDischargePlan> plans = new();
			try
			{
				var ids = stays.Select(s => s.ID).ToList();
				plans = await _db.Set<HospitalDischargePlan>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && ids.Contains(x.AdmissionId)).ToDictionaryAsync(x => x.AdmissionId, ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { ready = false; }

			var patients = stays.Select(s =>
			{
				var plan = plans.GetValueOrDefault(s.ID);
				int? dx = s.Diagnosis is string d && byDx.TryGetValue(d.Trim().ToLowerInvariant(), out var m) ? m : null;
				var e = HospitalCapacityRules.ExpectedDischarge(plan?.ExpectedDate, Ward(s.AdmittedAt), dx, overall, today);
				var w = s.WardId is int wid ? wards.GetValueOrDefault(wid) : null;
				return new HospitalCapacityPatient(s.ID, s.PatientId, s.Name, s.NameEn, s.PhotoPath, s.Bed, w?.Name, w?.NameEn, s.AdmittedAt, Math.Max(0, (today - Ward(s.AdmittedAt).Date).Days),
					s.Diagnosis, e?.Day, e?.Planned ?? false, plan?.Confidence, plan?.Barriers, plan?.Note);
			}).ToList();

			int inService = beds.Count(b => b.Status != HospitalBedStatus.Blocked);
			var expected = patients.Where(p => p.Expected != null).Select(p => p.Expected!.Value).ToList();
			var forecast = HospitalCapacityRules.Forecast(today, stays.Count, inService, expected, d => HospitalCapacityRules.ExpectedAdmissions(admittedWard, d, today));

			var wardRows = beds.GroupBy(b => b.WardId).Select(g =>
			{
				var w = g.Key is int wid ? wards.GetValueOrDefault(wid) : null;
				var here = patients.Where(p => stays.First(s => s.ID == p.AdmissionId).WardId == g.Key).ToList();
				return new HospitalCapacityWard(w?.Name ?? "—", w?.NameEn, g.Count(), g.Count(b => b.Status != HospitalBedStatus.Blocked), here.Count, here.Count(p => p.Expected == today));
			}).OrderByDescending(w => w.OccupancyPct ?? 0).ToList();

			var barrierCounts = HospitalDischargeBarrier.All.ToDictionary(b => b, b => patients.Count(p => (p.Barriers ?? "").Split(',').Contains(b)));

			var er = await _db.Set<HospitalErVisit>().AsNoTracking().Where(v => v.CompanyId == ctx.CompanyId && v.Stage != HospitalErStage.Closed).Select(v => v.Stage).ToListAsync(ct);

			IEnumerable<HospitalCapacityPatient> list = patients;
			if (!string.IsNullOrWhiteSpace(q))
			{
				var t = q.Trim();
				var mrns = stays.Where(s => (s.Mrn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)).Select(s => s.ID).ToHashSet();
				list = list.Where(p => p.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || (p.NameEn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
					|| p.Bed.Contains(t, StringComparison.OrdinalIgnoreCase) || (p.Diagnosis ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || mrns.Contains(p.AdmissionId));
			}
			var ordered = list.OrderBy(p => p.Expected ?? DateTime.MaxValue).ThenBy(p => p.Bed).ToList();
			page = Math.Max(1, page);

			return new HospitalCapacityCentre(ready, beds.Count, inService, stays.Count, beds.Count(b => b.Status == HospitalBedStatus.Vacant), beds.Count(b => b.Status == HospitalBedStatus.Cleaning),
				beds.Count(b => b.Status == HospitalBedStatus.Reserved), er.Count(s => s == HospitalErStage.Disposition), er.Count, forecast, wardRows,
				ordered.Skip((page - 1) * PageSize).Take(PageSize).ToList(), ordered.Count, barrierCounts, today);
		}

		public async Task<HospitalResult> SavePlanAsync(BusinessContext ctx, int admissionId, HospitalDischargePlanInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Admit, null, ct)) return HospitalResult.Fail("forbidden");
			var a = await _db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(x => x.ID == admissionId && x.CompanyId == ctx.CompanyId, ct);
			if (a == null) return HospitalResult.Fail("not_found");
			if (a.Status != HospitalAdmissionStatus.Active) return HospitalResult.Fail("This admission is already discharged");
			if (HospitalCapacityRules.PlanRefusal(i.ExpectedDate, Ward(nowUtc), Ward(a.AdmittedAt), i.Confidence, i.Barriers, i.Note) is string why) return HospitalResult.Fail(why);
			try
			{
				var row = await _db.Set<HospitalDischargePlan>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.AdmissionId == a.ID, ct);
				if (row == null) _db.Add(row = new HospitalDischargePlan { CompanyId = ctx.CompanyId, AdmissionId = a.ID, PatientId = a.PatientId });
				row.ExpectedDate = i.ExpectedDate.Date; row.Confidence = i.Confidence!; row.Barriers = HospitalCapacityRules.BarrierText(i.Barriers);
				row.Note = string.IsNullOrWhiteSpace(i.Note) ? null : i.Note.Trim(); row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}

		public async Task<HospitalResult> ClearPlanAsync(BusinessContext ctx, int admissionId, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Admit, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var row = await _db.Set<HospitalDischargePlan>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.AdmissionId == admissionId, ct);
				if (row == null) return HospitalResult.Fail("not_found");
				_db.Remove(row); await _db.SaveChangesAsync(ct); return HospitalResult.Success(admissionId);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}
	}
}
