using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H84 — immunizations. The schedule is the company's (loaded from a default, then changed); a patient's card
	// is the schedule laid over his birth date and the doses he was given; the due list is every child whose next dose is
	// due or overdue. A dose is recorded by the nurse or doctor who gives it, with its lot.
	// ==========================================================================================
	public sealed class HospitalImmunizationInput
	{
		public int PatientId { get; set; }
		/// The schedule dose given; null for a dose outside the schedule (then Code, Name and DoseNo are written).
		public int? ScheduleItemId { get; set; }
		public string? Code { get; set; }
		public string? Name { get; set; }
		public int? DoseNo { get; set; }
		/// Ward-local; empty means now.
		public DateTime? GivenAt { get; set; }
		public string? Lot { get; set; }
		public DateTime? LotExpiry { get; set; }
		public string? Site { get; set; }
		public string? Route { get; set; }
		public string? AdverseEvent { get; set; }
	}

	public static class HospitalImmunizationStatus
	{
		public const string Given = "Given";
		public const string Due = "Due";
		public const string Overdue = "Overdue";
		public const string Upcoming = "Upcoming";
	}

	public sealed record HospitalVaccineDoseRow(HospitalVaccineScheduleItem Item, DateTime DueOn, string Status, HospitalImmunization? Given);
	public sealed record HospitalImmunizationCard(int PatientId, string Name, string NameEn, string Mrn, string? Photo, DateTime? BirthDate, IReadOnlyList<HospitalVaccineDoseRow> Doses,
		IReadOnlyList<HospitalImmunization> OffSchedule, IReadOnlyDictionary<int, string> People, bool Ready, bool HasSchedule, IReadOnlyDictionary<int, string>? Faces = null);
	public sealed record HospitalVaccineDueRow(int PatientId, string Name, string NameEn, string Mrn, string? Phone, DateTime BirthDate, string Vaccine, string VaccineEn, int DoseNo, DateTime DueOn, string Status, int DaysLate);

	public static class HospitalImmunizationRules
	{
		/// A dose given up to four days before its minimum age still counts (the WHO four-day grace).
		public const int GraceDays = 4;
		/// Past due by more than this, a dose is overdue.
		public const int OverdueAfterDays = 30;
		/// The due list looks this far ahead for "upcoming".
		public const int UpcomingWindowDays = 14;

		public static DateTime DueOn(DateTime birthDate, int ageDays) => birthDate.Date.AddDays(ageDays);

		public static string Status(DateTime dueOn, bool given, DateTime today)
		{
			if (given) return HospitalImmunizationStatus.Given;
			if (dueOn.Date > today.Date) return HospitalImmunizationStatus.Upcoming;
			return (today.Date - dueOn.Date).Days > OverdueAfterDays ? HospitalImmunizationStatus.Overdue : HospitalImmunizationStatus.Due;
		}

		public static string? RecordRefusal(DateTime givenLocal, DateTime nowLocal, DateTime? birthDate, DateTime? lotExpiry, string? route, int? minAgeDays, bool alreadyGiven)
		{
			if (givenLocal > nowLocal.AddMinutes(5)) return "The time is in the future";
			if (birthDate != null && givenLocal.Date < birthDate.Value.Date) return "The dose cannot be before the patient's birth";
			if (!HospitalVaccineRoute.All.Contains(route ?? "")) return "Choose the route";
			if (lotExpiry != null && lotExpiry.Value.Date < givenLocal.Date) return "This lot has expired: it cannot be given";
			if (alreadyGiven) return "This dose is already recorded";
			if (minAgeDays != null && birthDate != null && givenLocal.Date < birthDate.Value.Date.AddDays(minAgeDays.Value - GraceDays))
				return "The patient is too young for this dose";
			return null;
		}

		public static string? ScheduleRefusal(string? code, string? name, string? nameEn, int doseNo, int ageDays, string? route)
		{
			if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 12) return "Write the vaccine code (up to 12 characters)";
			if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(nameEn)) return "Write the vaccine's name in Arabic and English";
			if (doseNo is < 1 or > 10) return "The dose number is between 1 and 10";
			if (ageDays is < 0 or > 36500) return "The age is out of range";
			if (!HospitalVaccineRoute.All.Contains(route ?? "")) return "Choose the route";
			return null;
		}

		/// A starting schedule (a WHO EPI-style infant schedule); every company changes it to its own country's.
		public static IReadOnlyList<(string Code, string Name, string NameEn, int Dose, int AgeDays, string Route)> Defaults() => new[]
		{
			("BCG", "الدرن (BCG)", "BCG", 1, 0, HospitalVaccineRoute.ID),
			("HEPB", "الالتهاب الكبدي ب", "Hepatitis B (birth dose)", 1, 0, HospitalVaccineRoute.IM),
			("OPV", "شلل الأطفال الفموي", "Oral polio", 1, 60, HospitalVaccineRoute.Oral),
			("OPV", "شلل الأطفال الفموي", "Oral polio", 2, 120, HospitalVaccineRoute.Oral),
			("OPV", "شلل الأطفال الفموي", "Oral polio", 3, 180, HospitalVaccineRoute.Oral),
			("PENTA", "الخماسي", "Pentavalent (DTP-HepB-Hib)", 1, 60, HospitalVaccineRoute.IM),
			("PENTA", "الخماسي", "Pentavalent (DTP-HepB-Hib)", 2, 120, HospitalVaccineRoute.IM),
			("PENTA", "الخماسي", "Pentavalent (DTP-HepB-Hib)", 3, 180, HospitalVaccineRoute.IM),
			("PCV", "المكورات الرئوية", "Pneumococcal conjugate", 1, 60, HospitalVaccineRoute.IM),
			("PCV", "المكورات الرئوية", "Pneumococcal conjugate", 2, 120, HospitalVaccineRoute.IM),
			("PCV", "المكورات الرئوية", "Pneumococcal conjugate", 3, 365, HospitalVaccineRoute.IM),
			("ROTA", "الروتا", "Rotavirus", 1, 60, HospitalVaccineRoute.Oral),
			("ROTA", "الروتا", "Rotavirus", 2, 120, HospitalVaccineRoute.Oral),
			("IPV", "شلل الأطفال بالحقن", "Inactivated polio", 1, 120, HospitalVaccineRoute.IM),
			("MMR", "الحصبة والنكاف والحصبة الألمانية", "Measles, mumps, rubella", 1, 365, HospitalVaccineRoute.SC),
			("MMR", "الحصبة والنكاف والحصبة الألمانية", "Measles, mumps, rubella", 2, 548, HospitalVaccineRoute.SC),
			("DTPB", "منشطة الثلاثي البكتيري", "DTP booster", 1, 548, HospitalVaccineRoute.IM),
		};
	}

	public sealed class HospitalImmunizationService
	{
		public const string NoTables = "Immunizations are not installed yet (hospital_h84_immunizations.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalImmunizationService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private static DateTime Local(DateTime utc) => HospitalNursingService.UtcToWard(utc);
		private IQueryable<HospitalVaccineScheduleItem> Schedule(BusinessContext ctx) => _db.Set<HospitalVaccineScheduleItem>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalImmunization> Doses(BusinessContext ctx) => _db.Set<HospitalImmunization>().Where(x => x.CompanyId == ctx.CompanyId);

		private async Task<bool> GiverAsync(BusinessContext ctx, CancellationToken ct) =>
			await _access.CanAsync(ctx, HospitalActions.Admit, null, ct) || await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);

		// ---------------------------------------------------------------- the schedule (a setting)

		public async Task<(IReadOnlyList<HospitalVaccineScheduleItem> Rows, bool Ready)> ScheduleAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			try { return (await Schedule(ctx).AsNoTracking().OrderBy(x => x.AgeDays).ThenBy(x => x.SortOrder).ThenBy(x => x.Code).ThenBy(x => x.DoseNo).ToListAsync(ct), true); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalVaccineScheduleItem>(), false); }
		}

		/// Loads the starting schedule into a company that has none.
		public async Task<HospitalResult> SeedDefaultsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				if (await Schedule(ctx).AnyAsync(ct)) return HospitalResult.Fail("The schedule already has doses: change them instead");
				int i = 0;
				foreach (var d in HospitalImmunizationRules.Defaults())
					_db.Add(new HospitalVaccineScheduleItem { CompanyId = ctx.CompanyId, Code = d.Code, Name = d.Name, NameEn = d.NameEn, DoseNo = d.Dose, AgeDays = d.AgeDays, Route = d.Route, IsActive = true, SortOrder = i++ });
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> SaveScheduleItemAsync(BusinessContext ctx, int? id, string? code, string? name, string? nameEn, int doseNo, int ageDays, string? route, bool isActive, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalImmunizationRules.ScheduleRefusal(code, name, nameEn, doseNo, ageDays, route) is string why) return HospitalResult.Fail(why);
			try
			{
				var c = code!.Trim().ToUpperInvariant();
				if (await Schedule(ctx).AnyAsync(x => x.Code == c && x.DoseNo == doseNo && x.ID != (id ?? 0), ct)) return HospitalResult.Fail("This vaccine already has that dose");
				HospitalVaccineScheduleItem row;
				if (id is int k) { row = await Schedule(ctx).FirstOrDefaultAsync(x => x.ID == k, ct) ?? throw new KeyNotFoundException(); }
				else { row = new HospitalVaccineScheduleItem { CompanyId = ctx.CompanyId, SortOrder = 999 }; _db.Add(row); }
				row.Code = c; row.Name = name!.Trim(); row.NameEn = nameEn!.Trim(); row.DoseNo = doseNo; row.AgeDays = ageDays; row.Route = route!; row.IsActive = isActive;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (KeyNotFoundException) { return HospitalResult.Fail("not_found"); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		// ---------------------------------------------------------------- the patient's card

		public async Task<HospitalImmunizationCard?> CardAsync(BusinessContext ctx, int patientId, DateTime nowUtc, CancellationToken ct = default)
		{
			var p = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == patientId && x.CompanyId == ctx.CompanyId)
				.Select(x => new { x.ID, x.Name, x.NameEn, x.Mrn, x.PhotoPath, x.BirthDate }).FirstOrDefaultAsync(ct);
			if (p == null) return null;
			var today = Local(nowUtc).Date;
			try
			{
				var sched = await Schedule(ctx).AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.AgeDays).ThenBy(x => x.SortOrder).ToListAsync(ct);
				var given = await Doses(ctx).AsNoTracking().Where(x => x.PatientId == patientId).OrderBy(x => x.GivenAt).ToListAsync(ct);
				var rows = sched.Select(s =>
				{
					var g = given.FirstOrDefault(x => x.ScheduleItemId == s.ID);
					var due = p.BirthDate == null ? today : HospitalImmunizationRules.DueOn(p.BirthDate.Value, s.AgeDays);
					return new HospitalVaccineDoseRow(s, due, p.BirthDate == null && g == null ? HospitalImmunizationStatus.Upcoming : HospitalImmunizationRules.Status(due, g != null, today), g);
				}).ToList();
				var ids = given.Where(x => x.GivenByEmployeeId != null).Select(x => x.GivenByEmployeeId!.Value).Distinct().ToList();
				var staff = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToListAsync(ct);
				var people = staff.ToDictionary(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn));
				var faces = staff.Select(e => (e.ID, Url: CrossBuy.BL.Platform.EmployeePhotos.Web(e.ProfileImage))).Where(x => x.Url != null).ToDictionary(x => x.ID, x => x.Url!);
				return new HospitalImmunizationCard(p.ID, p.Name, p.NameEn ?? "", p.Mrn, p.PhotoPath, p.BirthDate, rows, given.Where(x => x.ScheduleItemId == null).ToList(), people, true, sched.Count > 0, faces);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex))
			{
				return new HospitalImmunizationCard(p.ID, p.Name, p.NameEn ?? "", p.Mrn, p.PhotoPath, p.BirthDate, Array.Empty<HospitalVaccineDoseRow>(), Array.Empty<HospitalImmunization>(), new Dictionary<int, string>(), false, false);
			}
		}

		public async Task<HospitalResult> RecordAsync(BusinessContext ctx, HospitalImmunizationInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await GiverAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var p = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == input.PatientId && x.CompanyId == ctx.CompanyId).Select(x => new { x.ID, x.BirthDate }).FirstOrDefaultAsync(ct);
			if (p == null) return HospitalResult.Fail("not_found");
			var nowLocal = Local(nowUtc);
			var givenLocal = input.GivenAt ?? nowLocal;
			try
			{
				HospitalVaccineScheduleItem? s = null;
				if (input.ScheduleItemId is int sid)
				{
					s = await Schedule(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == sid, ct);
					if (s == null) return HospitalResult.Fail("not_found");
				}
				else if (string.IsNullOrWhiteSpace(input.Code) || string.IsNullOrWhiteSpace(input.Name) || input.DoseNo is null or < 1 or > 10)
					return HospitalResult.Fail("Write the vaccine, its name and the dose number");
				bool already = s != null && await Doses(ctx).AnyAsync(x => x.PatientId == p.ID && x.ScheduleItemId == s.ID, ct);
				var route = input.Route ?? s?.Route;
				if (HospitalImmunizationRules.RecordRefusal(givenLocal, nowLocal, p.BirthDate, input.LotExpiry, route, s?.AgeDays, already) is string why) return HospitalResult.Fail(why);
				var row = new HospitalImmunization
				{
					CompanyId = ctx.CompanyId, PatientId = p.ID, ScheduleItemId = s?.ID, Code = s?.Code ?? input.Code!.Trim().ToUpperInvariant()[..Math.Min(12, input.Code!.Trim().Length)],
					Name = s?.Name ?? Clip(input.Name, 100)!, NameEn = s?.NameEn ?? Clip(input.Name, 100)!, DoseNo = s?.DoseNo ?? input.DoseNo!.Value,
					GivenAt = HospitalNursingService.WardToUtc(givenLocal), Lot = Clip(input.Lot, 40), LotExpiry = input.LotExpiry?.Date, Site = Clip(input.Site, 60), Route = route!,
					GivenByEmployeeId = employeeId, AdverseEvent = Clip(input.AdverseEvent, 300), CreatedAt = nowUtc,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// An adverse event observed after the dose (usually in the 15–30 minutes the child is watched): written once.
		public async Task<HospitalResult> ReportAdverseEventAsync(BusinessContext ctx, int id, string? text, CancellationToken ct = default)
		{
			if (!await GiverAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(text)) return HospitalResult.Fail("Describe the adverse event");
			try
			{
				var d = await Doses(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (d == null) return HospitalResult.Fail("not_found");
				if (d.AdverseEvent != null) return HospitalResult.Fail("An adverse event is already recorded for this dose");
				d.AdverseEvent = Clip(text, 300);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(d.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// Every patient young enough for the schedule whose next scheduled doses are overdue, due or coming within two weeks.
		public async Task<(IReadOnlyList<HospitalVaccineDueRow> Rows, int Total, bool Ready)> DueAsync(BusinessContext ctx, string? state, string? q, int page, int pageSize, DateTime nowUtc, CancellationToken ct = default)
		{
			var today = Local(nowUtc).Date;
			try
			{
				var sched = await Schedule(ctx).AsNoTracking().Where(x => x.IsActive).ToListAsync(ct);
				if (sched.Count == 0) return (Array.Empty<HospitalVaccineDueRow>(), 0, true);
				var oldest = today.AddDays(-(sched.Max(x => x.AgeDays) + 730));
				var pq = _db.HospitalPatients.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.BirthDate != null && x.BirthDate >= oldest);
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); pq = pq.Where(x => x.Name.Contains(s) || (x.NameEn != null && x.NameEn.Contains(s)) || x.Mrn.Contains(s) || (x.Phone != null && x.Phone.Contains(s))); }
				var pats = await pq.Select(x => new { x.ID, x.Name, x.NameEn, x.Mrn, x.Phone, BirthDate = x.BirthDate!.Value }).ToListAsync(ct);
				var ids = pats.Select(x => x.ID).ToList();
				var given = (await Doses(ctx).AsNoTracking().Where(x => ids.Contains(x.PatientId) && x.ScheduleItemId != null).Select(x => new { x.PatientId, S = x.ScheduleItemId!.Value }).ToListAsync(ct))
					.Select(x => (x.PatientId, x.S)).ToHashSet();
				var rows = new List<HospitalVaccineDueRow>();
				foreach (var p in pats)
					foreach (var s in sched.Where(s => !given.Contains((p.ID, s.ID))))
					{
						var due = HospitalImmunizationRules.DueOn(p.BirthDate, s.AgeDays);
						var st = HospitalImmunizationRules.Status(due, false, today);
						if (st == HospitalImmunizationStatus.Upcoming && due > today.AddDays(HospitalImmunizationRules.UpcomingWindowDays)) continue;
						rows.Add(new HospitalVaccineDueRow(p.ID, p.Name, p.NameEn ?? "", p.Mrn, p.Phone, p.BirthDate, s.Name, s.NameEn, s.DoseNo, due, st, Math.Max(0, (today - due).Days)));
					}
				if (state is HospitalImmunizationStatus.Overdue or HospitalImmunizationStatus.Due or HospitalImmunizationStatus.Upcoming) rows = rows.Where(r => r.Status == state).ToList();
				var ordered = rows.OrderByDescending(r => r.DaysLate).ThenBy(r => r.DueOn).ThenBy(r => r.Name).ToList();
				pageSize = Math.Clamp(pageSize, 5, 100); page = Math.Max(1, page);
				return (ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList(), ordered.Count, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalVaccineDueRow>(), 0, false); }
		}
	}
}
