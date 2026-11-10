using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H26 — editing a doctor's weekly schedule and leave (`manage`). The rules are HospitalDoctorSchedule's.
	// Adding a leave over booked appointments does NOT cancel them: the screen shows how many a leave covers, and the
	// front desk moves them (a cancellation is the patient's to hear about, not a side effect).
	// ==========================================================================================

	public sealed class HospitalSessionInput { public int DayOfWeek { get; set; } public int StartMinute { get; set; } public int EndMinute { get; set; } public int SlotMinutes { get; set; } = 15; }
	public sealed class HospitalLeaveInput { public DateTime FromDate { get; set; } public DateTime ToDate { get; set; } public int? StartMinute { get; set; } public int? EndMinute { get; set; } public string? Reason { get; set; } }

	public sealed record HospitalLeaveRow(HospitalPractitionerLeave Leave, int BookedInside);

	public sealed class HospitalScheduleService
	{
		public const string NoTables = "The doctor schedule tables are not created yet (hospital_h26_doctor_schedule.sql)";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;

		public HospitalScheduleService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private Task<bool> CanManage(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
		private Task<HospitalPractitioner?> DoctorAsync(BusinessContext ctx, int id, CancellationToken ct) =>
			_db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(p => p.ID == id && p.CompanyId == ctx.CompanyId, ct)!;

		/// The doctor's schedule (sessions, and the leave from today on, each with the appointments it covers).
		public async Task<(HospitalDoctorSchedule Schedule, IReadOnlyList<HospitalLeaveRow> Leaves, bool Ready)?> ScheduleAsync(BusinessContext ctx, int practitionerId, DateTime today, CancellationToken ct = default)
		{
			var doc = await DoctorAsync(ctx, practitionerId, ct);
			if (doc == null) return null;
			bool ready = true;
			try { await _db.Set<HospitalPractitionerSession>().AsNoTracking().AnyAsync(ct); } catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { ready = false; }
			var schedule = await HospitalScheduleLoader.OneAsync(_db, ctx.CompanyId, doc, today, today.AddYears(1), ct);
			var rows = new List<HospitalLeaveRow>();
			foreach (var l in schedule.Leaves.OrderBy(l => l.FromDate))
			{
				var inside = await _db.HospitalAppointments.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.PractitionerId == practitionerId && a.Status == HospitalAppointmentStatus.Booked
					&& a.AppointmentDate >= l.FromDate.Date && a.AppointmentDate <= l.ToDate.Date).Select(a => new { a.StartMinute, a.DurationMinutes }).ToListAsync(ct);
				rows.Add(new HospitalLeaveRow(l, inside.Count(a => l.StartMinute == null || (a.StartMinute < l.EndMinute && l.StartMinute < a.StartMinute + a.DurationMinutes))));
			}
			return (schedule, rows, ready);
		}

		/// Starts the weekly schedule from H4's single window: one session per working day, the same hours.
		public async Task<HospitalResult> StartWeeklyAsync(BusinessContext ctx, int practitionerId, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			var doc = await DoctorAsync(ctx, practitionerId, ct);
			if (doc == null) return HospitalResult.Fail("not_found");
			try
			{
				if (await _db.Set<HospitalPractitionerSession>().AnyAsync(s => s.CompanyId == ctx.CompanyId && s.PractitionerId == practitionerId, ct)) return HospitalResult.Fail("The weekly schedule is already started");
				foreach (var d in doc.Days())
					_db.Add(new HospitalPractitionerSession { CompanyId = ctx.CompanyId, PractitionerId = practitionerId, DayOfWeek = (byte)d, StartMinute = doc.SessionStartMinute, EndMinute = doc.SessionEndMinute, SlotMinutes = doc.SlotMinutes });
				await _db.SaveChangesAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			return HospitalResult.Success(practitionerId);
		}

		public async Task<HospitalResult> AddSessionAsync(BusinessContext ctx, int practitionerId, HospitalSessionInput input, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (await DoctorAsync(ctx, practitionerId, ct) == null) return HospitalResult.Fail("not_found");
			try
			{
				var same = await _db.Set<HospitalPractitionerSession>().AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.PractitionerId == practitionerId && s.DayOfWeek == input.DayOfWeek)
					.Select(s => new { s.StartMinute, s.EndMinute }).ToListAsync(ct);
				if (HospitalDoctorSchedule.SessionRefusal(input.DayOfWeek, input.StartMinute, input.EndMinute, input.SlotMinutes, same.Select(s => (s.StartMinute, s.EndMinute))) is string why) return HospitalResult.Fail(why);
				var row = new HospitalPractitionerSession { CompanyId = ctx.CompanyId, PractitionerId = practitionerId, DayOfWeek = (byte)input.DayOfWeek, StartMinute = input.StartMinute, EndMinute = input.EndMinute, SlotMinutes = input.SlotMinutes };
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> RemoveSessionAsync(BusinessContext ctx, int sessionId, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			int n = await _db.Set<HospitalPractitionerSession>().Where(s => s.ID == sessionId && s.CompanyId == ctx.CompanyId).ExecuteDeleteAsync(ct);
			return n == 1 ? HospitalResult.Success(sessionId) : HospitalResult.Fail("not_found");
		}

		public async Task<HospitalResult> AddLeaveAsync(BusinessContext ctx, int practitionerId, HospitalLeaveInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (await DoctorAsync(ctx, practitionerId, ct) == null) return HospitalResult.Fail("not_found");
			if (HospitalDoctorSchedule.LeaveRefusal(input.FromDate, input.ToDate, input.StartMinute, input.EndMinute) is string why) return HospitalResult.Fail(why);
			try
			{
				var row = new HospitalPractitionerLeave
				{
					CompanyId = ctx.CompanyId, PractitionerId = practitionerId, FromDate = input.FromDate.Date, ToDate = input.ToDate.Date, StartMinute = input.StartMinute, EndMinute = input.EndMinute,
					Reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim()[..Math.Min(200, input.Reason.Trim().Length)], CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> RemoveLeaveAsync(BusinessContext ctx, int leaveId, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			int n = await _db.Set<HospitalPractitionerLeave>().Where(l => l.ID == leaveId && l.CompanyId == ctx.CompanyId).ExecuteDeleteAsync(ct);
			return n == 1 ? HospitalResult.Success(leaveId) : HospitalResult.Fail("not_found");
		}
	}
}
