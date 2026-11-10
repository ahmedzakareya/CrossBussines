using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H78 — fluid balance. The ward records each volume in or out; the chart adds up the fluid day (07:00 to 07:00
	// on the ward clock), the balance and the last days. Low urine output (under 0.5 ml/kg/h over the last 6 hours) is
	// flagged only when the patient has a recorded weight — the weight used and its date are shown, never guessed — and
	// only while the chart is in use (an output recorded in the last 24 hours), so an empty chart never reads as anuria.
	// ==========================================================================================
	public sealed class HospitalFluidInput
	{
		public int AdmissionId { get; set; }
		public string? Kind { get; set; }
		public int VolumeMl { get; set; }
		public string? Note { get; set; }
		/// Ward-local; empty means now.
		public DateTime? At { get; set; }
	}

	public sealed record HospitalFluidDayTotal(DateTime DayStart, int In, int Out)
	{
		public int Balance => In - Out;
	}

	public sealed record HospitalFluidChart(int AdmissionId, string Name, string NameEn, string Mrn, DateTime DayStart, IReadOnlyList<HospitalFluidEntry> Entries,
		IReadOnlyDictionary<string, int> ByKind, HospitalFluidDayTotal Today, IReadOnlyList<HospitalFluidDayTotal> Days, decimal? WeightKg, DateTime? WeightOn,
		int? UrineLast6h, bool LowUrine, IReadOnlyDictionary<int, string> People, bool Ready, bool IsCurrentDay);

	public sealed record HospitalFluidBadge(int Balance, int In, int Out, bool LowUrine);

	public static class HospitalFluidRules
	{
		public const int DayStartHour = 7;

		public static string? EntryRefusal(string? kind, int volumeMl, DateTime atUtc, DateTime admittedAtUtc, DateTime nowUtc)
		{
			if (!HospitalFluidKind.All.Contains(kind ?? "")) return "Choose what went in or came out";
			if (volumeMl < 1 || volumeMl > 5000) return "The volume is between 1 and 5,000 ml";
			if (atUtc > nowUtc.AddMinutes(5)) return "The time is in the future";
			if (atUtc < admittedAtUtc.AddMinutes(-5)) return "The time is before the admission";
			if (atUtc < nowUtc.AddDays(-3)) return "An entry older than three days cannot be added";
			return null;
		}

		/// The start (ward-local) of the fluid day a ward-local time falls in.
		public static DateTime DayStartOf(DateTime local) => local.Hour >= DayStartHour ? local.Date.AddHours(DayStartHour) : local.Date.AddDays(-1).AddHours(DayStartHour);

		public static HospitalFluidDayTotal Total(DateTime dayStartLocal, IEnumerable<(string Direction, int Ml, DateTime Local)> live)
		{
			var end = dayStartLocal.AddDays(1);
			var day = live.Where(e => e.Local >= dayStartLocal && e.Local < end).ToList();
			return new HospitalFluidDayTotal(dayStartLocal, day.Where(e => e.Direction == HospitalFluidDirection.In).Sum(e => e.Ml), day.Where(e => e.Direction == HospitalFluidDirection.Out).Sum(e => e.Ml));
		}

		/// Oliguria: under 0.5 ml/kg/h over the last 6 hours. Needs a weight, six hours of stay, and a chart in use (an output in
		/// the last 24 hours) — otherwise no flag.
		public static bool LowUrine(decimal? weightKg, int urineLast6hMl, bool outputInLast24h, DateTime admittedAtUtc, DateTime nowUtc) =>
			weightKg is decimal w && w > 0 && outputInLast24h && admittedAtUtc <= nowUtc.AddHours(-6) && urineLast6hMl < (double)w * 0.5 * 6;

		public static string? VoidRefusal(HospitalFluidEntry e, string? reason)
		{
			if (e.VoidedAt != null) return "This entry is already voided";
			if (string.IsNullOrWhiteSpace(reason)) return "Write why it is voided";
			return null;
		}
	}

	public sealed class HospitalFluidService
	{
		public const string NoTables = "The fluid chart is not installed yet (hospital_h78_fluid_balance.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalFluidService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private static DateTime Local(DateTime utc) => HospitalNursingService.UtcToWard(utc);
		private IQueryable<HospitalFluidEntry> Entries(BusinessContext ctx) => _db.Set<HospitalFluidEntry>().Where(x => x.CompanyId == ctx.CompanyId);

		private async Task<bool> WardAsync(BusinessContext ctx, CancellationToken ct) =>
			await _access.CanAsync(ctx, HospitalActions.Admit, null, ct) || await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);

		/// The patient's latest recorded weight (from a consultation's vitals) and its date.
		private async Task<(decimal? Kg, DateTime? On)> WeightAsync(BusinessContext ctx, int patientId, CancellationToken ct)
		{
			var w = await _db.Set<HospitalEncounter>().AsNoTracking().Where(e => e.CompanyId == ctx.CompanyId && e.PatientId == patientId && e.WeightKg != null)
				.OrderByDescending(e => e.CreatedAt).Select(e => new { e.WeightKg, e.CreatedAt }).FirstOrDefaultAsync(ct);
			return (w?.WeightKg, w?.CreatedAt);
		}

		/// Today's balance of these admissions (the nursing station's badge); only admissions with an entry today. Empty when not installed.
		public static async Task<IReadOnlyDictionary<int, HospitalFluidBadge>> BadgesAsync(CrossDbContext db, int companyId, IReadOnlyCollection<int> admissionIds, DateTime nowUtc, CancellationToken ct)
		{
			var result = new Dictionary<int, HospitalFluidBadge>();
			if (admissionIds.Count == 0) return result;
			try
			{
				var dayStart = HospitalFluidRules.DayStartOf(Local(nowUtc));
				var fromUtc = HospitalNursingService.WardToUtc(dayStart);
				var rows = await db.Set<HospitalFluidEntry>().AsNoTracking().Where(e => e.CompanyId == companyId && admissionIds.Contains(e.AdmissionId) && e.VoidedAt == null && e.At >= fromUtc)
					.Select(e => new { e.AdmissionId, e.Direction, e.VolumeMl }).ToListAsync(ct);
				foreach (var g in rows.GroupBy(r => r.AdmissionId))
				{
					int i = g.Where(r => r.Direction == HospitalFluidDirection.In).Sum(r => r.VolumeMl), o = g.Where(r => r.Direction == HospitalFluidDirection.Out).Sum(r => r.VolumeMl);
					result[g.Key] = new HospitalFluidBadge(i - o, i, o, false);
				}
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			return result;
		}

		public async Task<HospitalResult> AddAsync(BusinessContext ctx, HospitalFluidInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await WardAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var adm = await _db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(a => a.ID == input.AdmissionId && a.CompanyId == ctx.CompanyId, ct);
			if (adm == null) return HospitalResult.Fail("not_found");
			if (adm.Status != HospitalAdmissionStatus.Active) return HospitalResult.Fail("This admission is not active");
			var at = input.At == null ? nowUtc : HospitalNursingService.WardToUtc(input.At.Value);
			if (HospitalFluidRules.EntryRefusal(input.Kind, input.VolumeMl, at, adm.AdmittedAt, nowUtc) is string why) return HospitalResult.Fail(why);
			try
			{
				var row = new HospitalFluidEntry
				{
					CompanyId = ctx.CompanyId, AdmissionId = adm.ID, PatientId = adm.PatientId, Direction = HospitalFluidKind.DirectionOf(input.Kind!), Kind = input.Kind!,
					VolumeMl = input.VolumeMl, Note = Clip(input.Note, 200), At = at, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// A wrong entry is voided with a reason by whoever wrote it, or by a doctor; it stays on the chart, struck through.
		public async Task<HospitalResult> VoidAsync(BusinessContext ctx, int id, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await WardAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var e = await Entries(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (e == null) return HospitalResult.Fail("not_found");
				if (e.CreatedByEmployeeId != employeeId && !await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("Only who wrote it or a doctor can void it");
				if (HospitalFluidRules.VoidRefusal(e, reason) is string why) return HospitalResult.Fail(why);
				e.VoidedAt = nowUtc; e.VoidedByEmployeeId = employeeId; e.VoidReason = Clip(reason, 200);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(e.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// One fluid day of the chart (back = 0 today, 1 yesterday…), its totals by kind, and the last five days.
		public async Task<HospitalFluidChart?> ChartAsync(BusinessContext ctx, int admissionId, int back, DateTime nowUtc, CancellationToken ct = default)
		{
			var head = await (from a in _db.HospitalAdmissions.AsNoTracking()
							  join p in _db.HospitalPatients.AsNoTracking() on a.PatientId equals p.ID
							  where a.ID == admissionId && a.CompanyId == ctx.CompanyId
							  select new { a.ID, a.PatientId, a.AdmittedAt, p.Name, p.NameEn, p.Mrn }).FirstOrDefaultAsync(ct);
			if (head == null) return null;
			back = Math.Clamp(back, 0, 30);
			var today = HospitalFluidRules.DayStartOf(Local(nowUtc));
			var dayStart = today.AddDays(-back);
			var (kg, on) = await WeightAsync(ctx, head.PatientId, ct);
			try
			{
				var fromUtc = HospitalNursingService.WardToUtc(today.AddDays(-4 - (back > 4 ? back - 4 : 0)));
				var all = await Entries(ctx).AsNoTracking().Where(e => e.AdmissionId == admissionId && e.At >= fromUtc).OrderBy(e => e.At).ThenBy(e => e.ID).ToListAsync(ct);
				var live = all.Where(e => e.VoidedAt == null).Select(e => (e.Direction, e.VolumeMl, Local(e.At))).ToList();
				var day = all.Where(e => Local(e.At) >= dayStart && Local(e.At) < dayStart.AddDays(1)).ToList();
				var byKind = day.Where(e => e.VoidedAt == null).GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Sum(e => e.VolumeMl));
				var days = Enumerable.Range(0, 5).Select(i => HospitalFluidRules.Total(today.AddDays(-i), live)).ToList();
				var nowLocal = Local(nowUtc);
				int urine6 = all.Where(e => e.VoidedAt == null && e.Kind == HospitalFluidKind.Urine && e.At >= nowUtc.AddHours(-6)).Sum(e => e.VolumeMl);
				bool inUse = all.Any(e => e.VoidedAt == null && e.Direction == HospitalFluidDirection.Out && e.At >= nowUtc.AddHours(-24));
				bool low = HospitalFluidRules.LowUrine(kg, urine6, inUse, head.AdmittedAt, nowUtc);
				var ids = day.SelectMany(e => new[] { e.CreatedByEmployeeId, e.VoidedByEmployeeId }).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
				return new HospitalFluidChart(head.ID, head.Name, head.NameEn ?? "", head.Mrn, dayStart, day, byKind, HospitalFluidRules.Total(dayStart, live), days, kg, on,
					kg == null ? null : urine6, low, people, true, back == 0);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex))
			{
				return new HospitalFluidChart(head.ID, head.Name, head.NameEn ?? "", head.Mrn, dayStart, Array.Empty<HospitalFluidEntry>(), new Dictionary<string, int>(),
					new HospitalFluidDayTotal(dayStart, 0, 0), Array.Empty<HospitalFluidDayTotal>(), kg, on, null, false, new Dictionary<int, string>(), false, back == 0);
			}
		}
	}
}
