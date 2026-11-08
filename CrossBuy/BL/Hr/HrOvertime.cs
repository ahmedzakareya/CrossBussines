using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// HR5 — OVERTIME IS APPROVED BEFORE IT IS PAID, WITHIN THE YEARLY CAP (2026-11-08).
	//
	// Attendance measures overtime (working day / rest day / official holiday, the daily cap already applied). When the rule
	// pack's overtime.requiresApproval is on, payroll pays only the minutes approved here for that month; a month nobody
	// decided pays no overtime and says so on the approval screen. The YEARLY cap (overtime.yearlyCapHours) is enforced at
	// approval: approving a month takes what attendance shows, working-day minutes first, then rest-day, then holiday, up to
	// what is left of the year — and records what was asked beside what was approved, so a clipped month is visible.
	// ==========================================================================================
	public sealed record OvertimeMonthRow(int EmployeeId, string Name, int Working, int RestDay, int Holiday, OvertimeApproval? Decision, int YearApprovedBefore)
	{
		public int Measured => Working + RestDay + Holiday;
	}

	public sealed class HrOvertimeService
	{
		private readonly CrossDbContext _db;
		private readonly IAttendanceService _attendance;
		public HrOvertimeService(CrossDbContext db, IAttendanceService attendance) { _db = db; _attendance = attendance; }

		public async Task<(bool RequiresApproval, int YearlyCapMinutes, string? Reference)> SettingsAsync(int companyId, CancellationToken ct = default)
		{
			var ot = (await new HrRulePackService(_db).EffectiveAsync(companyId, ct))?.Rules.Overtime;
			return (ot?.RequiresApproval ?? false, ot is { YearlyCapHours: > 0 } ? (int)Math.Round(ot.YearlyCapHours * 60m) : 0, ot?.Reference);
		}

		/// Every employee with overtime in the month (or a decision on it), with the decision and the year's approvals before it.
		public async Task<List<OvertimeMonthRow>> MonthAsync(int companyId, int year, int month, CancellationToken ct = default)
		{
			var summary = await _attendance.MonthlySummaryAsync(companyId, year, month);
			var decisions = await _db.OvertimeApprovals.AsNoTracking().Where(a => a.CompanyID == companyId && a.Year == year && a.Month == month).ToListAsync(ct);
			var before = (await _db.OvertimeApprovals.AsNoTracking().Where(a => a.CompanyID == companyId && a.Year == year && a.Month < month && a.Status == "Approved")
				.Select(a => new { a.EmployeeID, a.WorkingMinutes, a.RestDayMinutes, a.HolidayMinutes }).ToListAsync(ct))
				.GroupBy(a => a.EmployeeID).ToDictionary(g => g.Key, g => g.Sum(x => x.WorkingMinutes + x.RestDayMinutes + x.HolidayMinutes));
			var rows = summary.Where(s => s.OvertimeMinutes + s.RestDayOvertimeMinutes + s.HolidayOvertimeMinutes > 0 || decisions.Any(d => d.EmployeeID == s.EmployeeID))
				.Select(s => new OvertimeMonthRow(s.EmployeeID, s.EmployeeName ?? "", s.OvertimeMinutes, s.RestDayOvertimeMinutes, s.HolidayOvertimeMinutes,
					decisions.FirstOrDefault(d => d.EmployeeID == s.EmployeeID), before.TryGetValue(s.EmployeeID, out var b) ? b : 0)).ToList();
			return rows;
		}

		/// Approves (within the yearly cap) or rejects one employee's month. Re-deciding replaces the earlier decision.
		public async Task<(bool Ok, string? Ar, string? En)> DecideAsync(int companyId, int employeeId, int year, int month, bool approve, string? note, int? byEmployeeId, CancellationToken ct = default)
		{
			if (await _db.Payslips.AsNoTracking().AnyAsync(p => p.CompanyID == companyId && p.Year == year && p.Month == month, ct))
				return (false, "رواتب هذا الشهر رُحّلت بالفعل؛ لا يمكن تغيير اعتماد عمله الإضافي", "This month's payroll is already posted; its overtime decision can no longer change");
			var row = (await MonthAsync(companyId, year, month, ct)).FirstOrDefault(r => r.EmployeeId == employeeId);
			if (row == null) return (false, "لا يوجد عمل إضافي مسجل لهذا الموظف في الشهر", "No overtime is recorded for this employee in the month");
			var (_, yearlyCap, _) = await SettingsAsync(companyId, ct);
			int w = 0, r = 0, h = 0;
			if (approve)
			{
				var left = yearlyCap > 0 ? Math.Max(0, yearlyCap - row.YearApprovedBefore) : int.MaxValue;
				w = Math.Min(row.Working, left); left -= w;
				r = Math.Min(row.RestDay, left); left -= r;
				h = Math.Min(row.Holiday, left);
			}
			var d = await _db.OvertimeApprovals.FirstOrDefaultAsync(a => a.CompanyID == companyId && a.EmployeeID == employeeId && a.Year == year && a.Month == month, ct);
			if (d == null) { d = new OvertimeApproval { CompanyID = companyId, EmployeeID = employeeId, Year = year, Month = month }; _db.OvertimeApprovals.Add(d); }
			d.Status = approve ? "Approved" : "Rejected";
			d.WorkingMinutes = w; d.RestDayMinutes = r; d.HolidayMinutes = h; d.RequestedMinutes = row.Measured;
			d.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(); d.DecidedByEmployeeId = byEmployeeId; d.DecidedAt = DateTime.UtcNow;
			await _db.SaveChangesAsync(ct);
			if (approve && w + r + h < row.Measured)
				return (true, $"اعتُمد {Hm(w + r + h)} من {Hm(row.Measured)}: السقف السنوي لا يتسع لأكثر.", $"Approved {Hm(w + r + h)} of {Hm(row.Measured)}: the yearly cap leaves no more.");
			return (true, null, null);
		}

		/// For payroll: the approved minutes by day type, per employee, for the month (absent = none approved).
		public async Task<Dictionary<int, (int Working, int RestDay, int Holiday)>> ApprovedAsync(int companyId, int year, int month, CancellationToken ct = default)
			=> await _db.OvertimeApprovals.AsNoTracking().Where(a => a.CompanyID == companyId && a.Year == year && a.Month == month && a.Status == "Approved")
				.ToDictionaryAsync(a => a.EmployeeID, a => (a.WorkingMinutes, a.RestDayMinutes, a.HolidayMinutes), ct);

		public static string Hm(int minutes) => $"{minutes / 60}:{minutes % 60:00}";
	}
}
