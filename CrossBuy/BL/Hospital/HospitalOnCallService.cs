using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H68 — on-call. The administrator plans the shifts on a week board; everyone reads who is on call now.
	// A shift is refused when it would put a doctor on two shifts at once, give a department two firsts (or two
	// backups) at once, or call a doctor who is on leave, inactive or behind an expired licence. Cancelled with why,
	// never deleted. OnCallAtAsync is the one place "who does this department call now" is answered — the lab's
	// escalation asks it before falling back to a doctor in session.
	// ==========================================================================================
	public sealed class HospitalOnCallInput
	{
		public int DepartmentId { get; set; }
		public int PractitionerId { get; set; }
		public string? Level { get; set; }
		/// The shift's first day, yyyy-MM-dd (ward calendar).
		public string? Date { get; set; }
		/// HH:mm, ward clock.
		public string? From { get; set; }
		/// HH:mm; at or before From means the next morning.
		public string? To { get; set; }
		public string? Note { get; set; }
	}

	public sealed record HospitalOnCallRow(HospitalOnCallShift Shift, string Doctor, string DoctorEn, int? EmployeeId, string? Photo, string Department, string? DepartmentEn);

	public sealed record HospitalOnCallWeek(DateTime From, IReadOnlyList<HospitalDepartment> Departments, IReadOnlyList<HospitalOnCallRow> Shifts,
		IReadOnlyList<HospitalOnCallRow> Now, IReadOnlyList<HospitalPractitioner> Doctors, bool Ready);

	public static class HospitalOnCallRules
	{
		/// The span a plan asks for, or a refusal: a real day and two clock times; a "to" at or before "from" ends the next day.
		public static (DateTime Start, DateTime End)? Span(string? date, string? from, string? to)
		{
			var inv = System.Globalization.CultureInfo.InvariantCulture;
			if (!DateTime.TryParseExact(date ?? "", "yyyy-MM-dd", inv, System.Globalization.DateTimeStyles.None, out var day)) return null;
			if (!TimeSpan.TryParseExact(from ?? "", @"hh\:mm", inv, out var f) || !TimeSpan.TryParseExact(to ?? "", @"hh\:mm", inv, out var t)) return null;
			if (f.TotalMinutes >= 1440 || t.TotalMinutes >= 1440) return null;
			var start = day.Date + f;
			var end = day.Date + t;
			if (end <= start) end = end.AddDays(1);
			return (start, end);
		}

		public static bool Overlaps(HospitalOnCallShift a, DateTime start, DateTime end) => a.CancelledAt == null && a.StartsAt < end && start < a.EndsAt;

		/// Why a shift cannot be planned, or null. `others` are the company's live shifts that touch the span.
		public static string? PlanRefusal(HospitalOnCallInput i, (DateTime Start, DateTime End)? span, HospitalPractitioner? doctor, bool departmentExists,
			IEnumerable<HospitalOnCallShift> others, IEnumerable<HospitalPractitionerLeave> leaves, DateTime wardNow)
		{
			if (!departmentExists) return "Choose the department";
			if (doctor == null) return "Choose the doctor";
			if (!HospitalOnCallLevel.All.Contains(i.Level ?? "")) return "Choose first on call or backup";
			if (span is not var (start, end)) return "Write the day and the times";
			if (end <= wardNow) return "This shift has already ended";
			if (!doctor.IsActive) return "This doctor is not active";
			if (!HospitalOutpatientService.LicenseValidOn(doctor, end.Date)) return "The doctor's licence has expired";
			foreach (var l in leaves.Where(l => l.PractitionerId == doctor.ID))
			{
				// a whole-day leave covers its days; a part-day leave its minutes of that day
				var ls = l.StartMinute is int sm ? l.FromDate.Date.AddMinutes(sm) : l.FromDate.Date;
				var le = l.EndMinute is int em ? l.ToDate.Date.AddMinutes(em) : l.ToDate.Date.AddDays(1);
				if (ls < end && start < le) return "The doctor is on leave then";
			}
			var live = others.Where(o => Overlaps(o, start, end)).ToList();
			if (live.Any(o => o.PractitionerId == doctor.ID)) return "The doctor is already on call then";
			if (live.Any(o => o.DepartmentId == i.DepartmentId && o.Level == i.Level))
				return i.Level == HospitalOnCallLevel.First ? "The department already has a first on call then" : "The department already has a backup then";
			return null;
		}

		/// Who a department calls at this moment: the first on call, then the backup.
		public static IReadOnlyList<HospitalOnCallShift> At(IEnumerable<HospitalOnCallShift> shifts, int departmentId, DateTime wardTime) =>
			shifts.Where(s => s.CancelledAt == null && s.DepartmentId == departmentId && s.StartsAt <= wardTime && wardTime < s.EndsAt)
				.OrderBy(s => s.Level == HospitalOnCallLevel.First ? 0 : 1).ToList();

		/// The Saturday that opens the week holding this day (the ward's week starts on Saturday).
		public static DateTime WeekStart(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 1) % 7));
	}

	public sealed class HospitalOnCallService
	{
		public const string NoTables = "On-call is not installed yet (hospital_h68_on_call.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalOnCallService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		/// The departments' on-call doctors at this ward moment (first then backup) — an empty list when on-call is not installed.
		public static async Task<IReadOnlyList<HospitalOnCallShift>> OnCallAtAsync(CrossDbContext db, int companyId, int departmentId, DateTime wardTime, CancellationToken ct)
		{
			try
			{
				var rows = await db.Set<HospitalOnCallShift>().AsNoTracking()
					.Where(s => s.CompanyId == companyId && s.DepartmentId == departmentId && s.CancelledAt == null && s.StartsAt <= wardTime && wardTime < s.EndsAt).ToListAsync(ct);
				return HospitalOnCallRules.At(rows, departmentId, wardTime);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return Array.Empty<HospitalOnCallShift>(); }
		}

		public async Task<HospitalResult> PlanAsync(BusinessContext ctx, HospitalOnCallInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			var span = HospitalOnCallRules.Span(input.Date, input.From, input.To);
			var doctor = await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(p => p.ID == input.PractitionerId && p.CompanyId == ctx.CompanyId, ct);
			bool dept = await _db.HospitalDepartments.AnyAsync(d => d.ID == input.DepartmentId && d.CompanyId == ctx.CompanyId && d.IsActive, ct);
			try
			{
				var others = new List<HospitalOnCallShift>(); var leaves = new List<HospitalPractitionerLeave>();
				if (span is var (s, e))
				{
					others = await _db.Set<HospitalOnCallShift>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && o.CancelledAt == null && o.StartsAt < e && s < o.EndsAt).ToListAsync(ct);
					if (doctor != null)
						try { leaves = await _db.Set<HospitalPractitionerLeave>().AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.PractitionerId == doctor.ID && l.FromDate <= e.Date && l.ToDate >= s.Date.AddDays(-1)).ToListAsync(ct); }
						catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { }
				}
				if (HospitalOnCallRules.PlanRefusal(input, span, doctor, dept, others, leaves, HospitalNursingService.UtcToWard(nowUtc)) is string why) return HospitalResult.Fail(why);
				var row = new HospitalOnCallShift
				{
					CompanyId = ctx.CompanyId, DepartmentId = input.DepartmentId, PractitionerId = doctor!.ID, Level = input.Level!, StartsAt = span!.Value.Start, EndsAt = span.Value.End,
					Note = Clip(input.Note, 300), CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> CancelAsync(BusinessContext ctx, int id, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var row = await _db.Set<HospitalOnCallShift>().FirstOrDefaultAsync(s => s.ID == id && s.CompanyId == ctx.CompanyId, ct);
				if (row == null) return HospitalResult.Fail("not_found");
				if (row.CancelledAt != null) return HospitalResult.Fail("This shift is already cancelled");
				if (row.EndsAt <= HospitalNursingService.UtcToWard(nowUtc)) return HospitalResult.Fail("This shift has already ended");
				if (string.IsNullOrWhiteSpace(reason)) return HospitalResult.Fail("Write why it is cancelled");
				row.CancelledAt = nowUtc; row.CancelledByEmployeeId = employeeId; row.CancelReason = Clip(reason, 300);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// The week board (Saturday to Friday) with the shifts that touch it, and who every department calls now.
		public async Task<HospitalOnCallWeek> WeekAsync(BusinessContext ctx, DateTime anyDay, DateTime nowUtc, CancellationToken ct = default)
		{
			var from = HospitalOnCallRules.WeekStart(anyDay);
			var to = from.AddDays(7);
			var wardNow = HospitalNursingService.UtcToWard(nowUtc);
			var depts = await _db.HospitalDepartments.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && d.IsActive).OrderBy(d => d.SortOrder).ThenBy(d => d.Name).ToListAsync(ct);
			var doctors = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.IsActive).OrderBy(p => p.Name).ToListAsync(ct);
			List<HospitalOnCallShift> shifts;
			try
			{
				shifts = await _db.Set<HospitalOnCallShift>().AsNoTracking()
					.Where(s => s.CompanyId == ctx.CompanyId && ((s.StartsAt < to && from < s.EndsAt) || (s.CancelledAt == null && s.StartsAt <= wardNow && wardNow < s.EndsAt)))
					.OrderBy(s => s.StartsAt).ToListAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new(from, depts, Array.Empty<HospitalOnCallRow>(), Array.Empty<HospitalOnCallRow>(), doctors, false); }
			var docIds = shifts.Select(s => s.PractitionerId).Distinct().ToList();
			var docs = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && docIds.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
			var eids = docs.Values.Where(p => p.EmployeeId != null).Select(p => p.EmployeeId!.Value).Distinct().ToList();
			var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, eids, ct);
			var deptById = await _db.HospitalDepartments.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId).ToDictionaryAsync(d => d.ID, ct);
			HospitalOnCallRow Row(HospitalOnCallShift s)
			{
				var p = docs.GetValueOrDefault(s.PractitionerId); var d = deptById.GetValueOrDefault(s.DepartmentId);
				return new(s, p?.Name ?? "—", p?.NameEn ?? p?.Name ?? "—", p?.EmployeeId, p?.EmployeeId is int e ? photos.GetValueOrDefault(e) : null, d?.Name ?? "—", d?.NameEn);
			}
			var week = shifts.Where(s => s.StartsAt < to && from < s.EndsAt).Select(Row).ToList();
			var now = depts.SelectMany(d => HospitalOnCallRules.At(shifts, d.ID, wardNow)).Select(Row).ToList();
			return new(from, depts, week, now, doctors, true);
		}
	}
}
