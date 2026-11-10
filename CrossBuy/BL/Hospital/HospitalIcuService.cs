using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H40 — intensive care. The rules are HospitalIcuRules' (no database).
	//   · Who is in intensive care: an open bed stay (H3) in an `Icu` room of an active admission — read, never stored.
	//   · The hourly flowsheet (`admit`, the nurse's): one row per admission and hour, entered for an hour that has
	//     begun, within the last day; entering an hour again corrects it.
	//   · The day's assessment (`admit` or `consult`): SOFA, ventilator, vasopressors — today or a past day of the stay.
	//   · The figures: beds occupied of the ICU's beds, how many are ventilated / on vasopressors (each patient's latest
	//     assessment), and the chosen patient's SOFA against the day before.
	// Nothing leaves the stock or reaches a bill here.
	// ==========================================================================================
	public sealed class HospitalIcuHourInput
	{
		public int AdmissionId { get; set; }
		/// The hour, on the ward's clock.
		public DateTime Hour { get; set; }
		public int? HeartRate { get; set; } public int? Map { get; set; } public int? SpO2 { get; set; } public int? RespRate { get; set; }
		public decimal? TempC { get; set; } public int? UrineMl { get; set; }
	}

	public sealed class HospitalIcuAssessInput
	{
		public int AdmissionId { get; set; }
		/// The ward-clock day; today when empty.
		public DateTime? Day { get; set; }
		public int? Sofa { get; set; } public bool Ventilated { get; set; } public bool Vasopressors { get; set; }
	}

	public sealed record HospitalIcuPatient(int AdmissionId, int PatientId, string Name, string NameEn, string Mrn, string? Photo, string Bed,
		DateTime? BirthDate, string Sex, string? BloodGroup, string? Allergies, string? Diagnosis, DateTime AdmittedAt);

	public sealed record HospitalIcuBoard(IReadOnlyList<HospitalIcuPatient> Patients, int Beds, int Ventilated, int Vasopressors,
		HospitalIcuPatient? Selected, IReadOnlyList<DateTime> Hours, IReadOnlyList<HospitalIcuObservation> Flow,
		HospitalIcuAssessment? Today, HospitalIcuAssessment? Yesterday, DateTime WardNow);

	public static class HospitalIcuRules
	{
		/// The flowsheet's parameters and what each may hold (the slice's CHECK says the same).
		public static readonly IReadOnlyList<(string Key, decimal Min, decimal Max)> Ranges = new[]
		{
			("HR", 20m, 250m), ("MAP", 20m, 200m), ("SpO2", 50m, 100m), ("RR", 4m, 80m), ("Temp", 30m, 45m), ("Urine", 0m, 2000m),
		};

		/// Why this hour cannot be entered, or null. `hourWard` and `nowWard` are on the ward's clock.
		public static string? HourRefusal(DateTime hourWard, DateTime nowWard, int? hr, int? map, int? spo2, int? rr, decimal? temp, int? urine)
		{
			var values = new decimal?[] { hr, map, spo2, rr, temp, urine };
			if (values.All(v => v == null)) return "Enter at least one value";
			if (hourWard > nowWard) return "An hour is entered once it has begun";
			if (hourWard < nowWard.AddHours(-24)) return "Only the last 24 hours are entered here";
			for (int i = 0; i < values.Length; i++)
				if (values[i] is decimal v && (v < Ranges[i].Min || v > Ranges[i].Max)) return "A value is outside what a monitor can read";
			return null;
		}

		/// Why this day's assessment cannot be entered, or null.
		public static string? AssessRefusal(DateTime day, DateTime todayWard, DateTime admittedWard, int? sofa)
		{
			if (sofa is int s && (s < 0 || s > 24)) return "SOFA is between 0 and 24";
			if (day.Date > todayWard.Date) return "A day is assessed once it has begun";
			if (day.Date < admittedWard.Date) return "That day is before the admission";
			return null;
		}

		/// The flowsheet's columns: the last `n` hours on the ward's clock, ending with the current one.
		public static IReadOnlyList<DateTime> Hours(DateTime nowWard, int n)
		{
			var top = new DateTime(nowWard.Year, nowWard.Month, nowWard.Day, nowWard.Hour, 0, 0);
			return Enumerable.Range(0, n).Select(i => top.AddHours(i - n + 1)).ToList();
		}
	}

	public sealed class HospitalIcuService
	{
		public const string NoTables = "The intensive-care tables are not created yet (hospital_h40_icu.sql)";
		public const int FlowHours = 5;   // the prototype draws five
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;

		public HospitalIcuService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private static DateTime ToWard(DateTime utc) => HospitalNursingService.UtcToWard(utc);
		private static DateTime ToUtc(DateTime ward) => HospitalNursingService.WardToUtc(ward);

		/// The patients in intensive care now (an open stay in an Icu room), by bed.
		public async Task<IReadOnlyList<HospitalIcuPatient>> PatientsAsync(BusinessContext ctx, CancellationToken ct = default)
			=> await (from s in _db.HospitalBedStays.AsNoTracking()
					  join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
					  join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
					  join a in _db.HospitalAdmissions.AsNoTracking() on s.AdmissionId equals a.ID
					  join p in _db.HospitalPatients.AsNoTracking() on s.PatientId equals p.ID
					  where s.CompanyId == ctx.CompanyId && s.EndedAt == null && r.Kind == HospitalRoomKinds.Icu && a.Status == HospitalAdmissionStatus.Active && p.CompanyId == ctx.CompanyId
					  orderby b.Code
					  select new HospitalIcuPatient(a.ID, p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath, b.Code, p.BirthDate, p.Sex, p.BloodGroup, p.Allergies, a.Diagnosis, a.AdmittedAt)).ToListAsync(ct);

		public async Task<(HospitalIcuBoard Board, bool Ready)> BoardAsync(BusinessContext ctx, int? admissionId, DateTime nowUtc, CancellationToken ct = default)
		{
			var nowWard = ToWard(nowUtc);
			var patients = await PatientsAsync(ctx, ct);
			int beds = await (from b in _db.HospitalBeds.AsNoTracking() join r in _db.HospitalRooms.AsNoTracking() on b.RoomId equals r.ID
							  where b.CompanyId == ctx.CompanyId && b.IsActive && r.Kind == HospitalRoomKinds.Icu select b.ID).CountAsync(ct);
			var sel = patients.FirstOrDefault(p => p.AdmissionId == admissionId) ?? patients.FirstOrDefault();
			var hours = HospitalIcuRules.Hours(nowWard, FlowHours);
			try
			{
				var ids = patients.Select(p => p.AdmissionId).ToList();
				// each patient's latest assessment up to today
				var latest = await _db.Set<HospitalIcuAssessment>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && ids.Contains(x.AdmissionId) && x.Day <= nowWard.Date)
					.GroupBy(x => x.AdmissionId).Select(g => g.OrderByDescending(x => x.Day).First()).ToListAsync(ct);
				IReadOnlyList<HospitalIcuObservation> flow = Array.Empty<HospitalIcuObservation>();
				HospitalIcuAssessment? today = null, yesterday = null;
				if (sel != null)
				{
					DateTime from = ToUtc(hours[0]), to = ToUtc(hours[^1].AddHours(1));
					flow = await _db.Set<HospitalIcuObservation>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && o.AdmissionId == sel.AdmissionId && o.HourAt >= from && o.HourAt < to).ToListAsync(ct);
					var two = await _db.Set<HospitalIcuAssessment>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.AdmissionId == sel.AdmissionId && x.Day >= nowWard.Date.AddDays(-1) && x.Day <= nowWard.Date).ToListAsync(ct);
					today = two.FirstOrDefault(x => x.Day == nowWard.Date); yesterday = two.FirstOrDefault(x => x.Day == nowWard.Date.AddDays(-1));
				}
				return (new HospitalIcuBoard(patients, beds, latest.Count(x => x.Ventilated), latest.Count(x => x.Vasopressors), sel, hours, flow, today, yesterday, nowWard), true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
			{
				return (new HospitalIcuBoard(patients, beds, 0, 0, sel, hours, Array.Empty<HospitalIcuObservation>(), null, null, nowWard), false);
			}
		}

		/// The ICU patient this admission is, or null when the admission is not in an ICU bed now.
		private async Task<HospitalIcuPatient?> InIcuAsync(BusinessContext ctx, int admissionId, CancellationToken ct)
			=> (await PatientsAsync(ctx, ct)).FirstOrDefault(p => p.AdmissionId == admissionId);

		public async Task<HospitalResult> RecordHourAsync(BusinessContext ctx, HospitalIcuHourInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			var p = await InIcuAsync(ctx, input.AdmissionId, ct);
			if (p == null) return HospitalResult.Fail("This patient is not in an intensive-care bed");
			var hourWard = new DateTime(input.Hour.Year, input.Hour.Month, input.Hour.Day, input.Hour.Hour, 0, 0);
			if (HospitalIcuRules.HourRefusal(hourWard, ToWard(nowUtc), input.HeartRate, input.Map, input.SpO2, input.RespRate, input.TempC, input.UrineMl) is string why) return HospitalResult.Fail(why);
			var hourUtc = ToUtc(hourWard);
			try
			{
				var row = await _db.Set<HospitalIcuObservation>().FirstOrDefaultAsync(o => o.CompanyId == ctx.CompanyId && o.AdmissionId == p.AdmissionId && o.HourAt == hourUtc, ct);
				if (row == null) _db.Add(row = new HospitalIcuObservation { CompanyId = ctx.CompanyId, AdmissionId = p.AdmissionId, PatientId = p.PatientId, HourAt = hourUtc });
				row.HeartRate = input.HeartRate; row.Map = input.Map; row.SpO2 = input.SpO2; row.RespRate = input.RespRate;
				row.TempC = input.TempC is decimal t ? Math.Round(t, 1) : null; row.UrineMl = input.UrineMl;
				row.RecordedByEmployeeId = employeeId; row.RecordedAt = nowUtc;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}

		public async Task<HospitalResult> AssessAsync(BusinessContext ctx, HospitalIcuAssessInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct) && !await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			var p = await InIcuAsync(ctx, input.AdmissionId, ct);
			if (p == null) return HospitalResult.Fail("This patient is not in an intensive-care bed");
			var todayWard = ToWard(nowUtc).Date;
			var day = (input.Day ?? todayWard).Date;
			if (HospitalIcuRules.AssessRefusal(day, todayWard, ToWard(p.AdmittedAt), input.Sofa) is string why) return HospitalResult.Fail(why);
			try
			{
				var row = await _db.Set<HospitalIcuAssessment>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.AdmissionId == p.AdmissionId && x.Day == day, ct);
				if (row == null) _db.Add(row = new HospitalIcuAssessment { CompanyId = ctx.CompanyId, AdmissionId = p.AdmissionId, PatientId = p.PatientId, Day = day });
				row.Sofa = input.Sofa; row.Ventilated = input.Ventilated; row.Vasopressors = input.Vasopressors;
				row.RecordedByEmployeeId = employeeId; row.RecordedAt = nowUtc;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}
	}
}
