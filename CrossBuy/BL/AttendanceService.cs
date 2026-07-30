using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class AttendanceMonthRow
	{
		public int EmployeeID { get; set; }
		public string EmployeeName { get; set; } = "";
		public int WorkDays { get; set; }       // scheduled work days in the month (excl. holidays/rest)
		public int PresentDays { get; set; }
		public int LateCount { get; set; }
		public int LateMinutes { get; set; }
		public int OvertimeMinutes { get; set; }
		public int LeaveDays { get; set; }
		public int AbsentDays { get; set; }
	}

	public interface IAttendanceService
	{
		Task<AttendancePolicies?> PolicyForAsync(int employeeId);
		Task<(bool ok, string? error, AttendanceRecord? rec)> RecordAsync(int companyId, int employeeId, DateTime date, DateTime? checkIn, DateTime? checkOut, string source, string? notes, string? userId);
		Task<List<AttendanceRecord>> ForMonthAsync(int companyId, int? employeeId, int year, int month);
		Task<List<AttendanceMonthRow>> MonthlySummaryAsync(int companyId, int year, int month);
	}

	public class AttendanceService : IAttendanceService
	{
		private readonly CrossDbContext _db;
		private readonly IHolidayService _holidays;
		public AttendanceService(CrossDbContext db, IHolidayService holidays) { _db = db; _holidays = holidays; }

		public async Task<AttendancePolicies?> PolicyForAsync(int employeeId)
		{
			var a = await _db.PolicyAssignments.AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeID == employeeId);
			if (a == null) return null;
			return await _db.AttendancePolicies.AsNoTracking().FirstOrDefaultAsync(x => x.LeavePolicyTypeID == a.LeavePolicyTypeID);
		}

		private static bool IsWorkDay(AttendancePolicies p, DateTime d)
		{
			return d.DayOfWeek switch
			{
				DayOfWeek.Sunday => p.WorkOnSunday,
				DayOfWeek.Monday => p.WorkOnMonday,
				DayOfWeek.Tuesday => p.WorkOnTuesday,
				DayOfWeek.Wednesday => p.WorkOnWednesday,
				DayOfWeek.Thursday => p.WorkOnThursday,
				DayOfWeek.Friday => p.WorkOnFriday,
				DayOfWeek.Saturday => p.WorkOnSaturday,
				_ => false
			};
		}

		private async Task<bool> OnApprovedLeaveAsync(int employeeId, DateTime date)
			=> await _db.LeaveRequests.AsNoTracking().AnyAsync(r => r.EmployeeID == employeeId && r.Status == 1 && r.StartDate.Date <= date.Date && r.EndDate.Date >= date.Date);

		public async Task<(bool ok, string? error, AttendanceRecord? rec)> RecordAsync(int companyId, int employeeId, DateTime date, DateTime? checkIn, DateTime? checkOut, string source, string? notes, string? userId)
		{
			if (employeeId <= 0) return (false, "الموظف مطلوب", null);
			var day = date.Date;
			var policy = await PolicyForAsync(employeeId);
			bool isHoliday = (await _holidays.HolidayDatesAsync(companyId, day, day)).Count > 0;
			bool isRest = policy != null && !IsWorkDay(policy, day);
			bool onLeave = await OnApprovedLeaveAsync(employeeId, day);

			int late = 0, early = 0, ot = 0; decimal worked = 0; string status;
			if (isHoliday) status = "Holiday";
			else if (isRest) status = "RestDay";
			else if (onLeave) status = "Leave";
			else if (checkIn == null) status = "Absent";
			else
			{
				if (policy != null)
				{
					var grace = TimeSpan.FromMinutes(policy.AllowedGraceMinutes);
					var lateSpan = checkIn.Value.TimeOfDay - (policy.WorkStartTime + grace);
					late = lateSpan.TotalMinutes > 0 ? (int)Math.Round(lateSpan.TotalMinutes) : 0;
					if (checkOut != null)
					{
						var otSpan = checkOut.Value.TimeOfDay - policy.WorkEndTime;
						ot = otSpan.TotalMinutes > 0 ? (int)Math.Round(otSpan.TotalMinutes) : 0;
						var earlySpan = policy.WorkEndTime - checkOut.Value.TimeOfDay;
						early = earlySpan.TotalMinutes > 0 ? (int)Math.Round(earlySpan.TotalMinutes) : 0;
					}
				}
				if (checkOut != null) worked = Math.Round((decimal)(checkOut.Value - checkIn.Value).TotalHours, 2);
				status = late > 0 ? "Late" : "Present";
			}

			var rec = await _db.AttendanceRecords.FirstOrDefaultAsync(r => r.CompanyID == companyId && r.EmployeeID == employeeId && r.WorkDate == day);
			if (rec == null)
			{
				rec = new AttendanceRecord { CompanyID = companyId, EmployeeID = employeeId, WorkDate = day, CreatedBy = userId, CreatedAt = DateTime.UtcNow };
				_db.AttendanceRecords.Add(rec);
			}
			rec.CheckIn = checkIn; rec.CheckOut = checkOut; rec.Source = source ?? "Web"; rec.Notes = notes;
			rec.LateMinutes = late; rec.EarlyLeaveMinutes = early; rec.OvertimeMinutes = ot; rec.WorkedHours = worked; rec.Status = status;
			await _db.SaveChangesAsync();
			return (true, null, rec);
		}

		public Task<List<AttendanceRecord>> ForMonthAsync(int companyId, int? employeeId, int year, int month)
		{
			var s = new DateTime(year, month, 1); var e = s.AddMonths(1).AddDays(-1);
			return _db.AttendanceRecords.AsNoTracking()
				.Where(r => r.CompanyID == companyId && (employeeId == null || r.EmployeeID == employeeId) && r.WorkDate >= s && r.WorkDate <= e)
				.OrderBy(r => r.EmployeeID).ThenBy(r => r.WorkDate).ToListAsync();
		}

		// HR-8: total approved-permission minutes per (employee, day) in a range — waives late minutes at aggregation.
		private async Task<Dictionary<(int emp, DateTime day), int>> PermittedMinutesMapAsync(int companyId, DateTime s, DateTime e)
		{
			var perms = await _db.EmployeeRequests.AsNoTracking()
				.Where(r => r.CompanyID == companyId && r.RequestType == "Permission" && r.Status == 1
					&& r.PermissionDate != null && r.PermissionDate >= s && r.PermissionDate <= e && r.FromTime != null && r.ToTime != null)
				.Select(r => new { r.EmployeeID, r.PermissionDate, r.FromTime, r.ToTime }).ToListAsync();
			var map = new Dictionary<(int, DateTime), int>();
			foreach (var p in perms)
			{
				var mins = Math.Max(0, (int)Math.Round((p.ToTime!.Value - p.FromTime!.Value).TotalMinutes));
				var key = (p.EmployeeID, p.PermissionDate!.Value.Date);
				map[key] = (map.TryGetValue(key, out var v) ? v : 0) + mins;
			}
			return map;
		}

		public async Task<List<AttendanceMonthRow>> MonthlySummaryAsync(int companyId, int year, int month)
		{
			var s = new DateTime(year, month, 1); var e = s.AddMonths(1).AddDays(-1);
			var holidays = await _holidays.HolidayDatesAsync(companyId, s, e);

			var emps = await _db.Employee.AsNoTracking().Where(x => x.EmpCompanyID == companyId && x.IsActive).Select(x => new { x.ID, x.FullName, x.FullNameEn }).ToListAsync();
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var recs = await _db.AttendanceRecords.AsNoTracking().Where(r => r.CompanyID == companyId && r.WorkDate >= s && r.WorkDate <= e).ToListAsync();
			var leaves = await _db.LeaveRequests.AsNoTracking().Where(r => r.Status == 1 && r.StartDate <= e && r.EndDate >= s).ToListAsync();
			var permMap = await PermittedMinutesMapAsync(companyId, s, e);   // HR-8 hourly permissions

			var rows = new List<AttendanceMonthRow>();
			foreach (var emp in emps)
			{
				var policy = await PolicyForAsync(emp.ID);
				if (policy == null) continue;   // only employees with a schedule are summarized
				var myRecs = recs.Where(r => r.EmployeeID == emp.ID).ToDictionary(r => r.WorkDate.Date);
				int workDays = 0, present = 0, lateCnt = 0, lateMin = 0, ot = 0, leaveDays = 0, absent = 0;
				for (var d = s; d <= e; d = d.AddDays(1))
				{
					if (holidays.Contains(d) || !IsWorkDay(policy, d)) continue;   // not a scheduled work day
					workDays++;
					bool onLeave = leaves.Any(l => l.EmployeeID == emp.ID && l.StartDate.Date <= d && l.EndDate.Date >= d);
					if (myRecs.TryGetValue(d, out var r) && (r.Status == "Present" || r.Status == "Late"))
					{
						present++; ot += r.OvertimeMinutes;
						var permitted = permMap.TryGetValue((emp.ID, d.Date), out var pm) ? pm : 0;   // HR-8: waive late within permission window
						var effLate = Math.Max(0, r.LateMinutes - permitted);
						lateMin += effLate; if (effLate > 0) lateCnt++;
					}
					else if (onLeave) leaveDays++;
					else absent++;
				}
				rows.Add(new AttendanceMonthRow { EmployeeID = emp.ID, EmployeeName = !isAr && !string.IsNullOrWhiteSpace(emp.FullNameEn) ? emp.FullNameEn : emp.FullName, WorkDays = workDays, PresentDays = present, LateCount = lateCnt, LateMinutes = lateMin, OvertimeMinutes = ot, LeaveDays = leaveDays, AbsentDays = absent });
			}
			return rows.OrderBy(r => r.EmployeeName).ToList();
		}
	}
}
