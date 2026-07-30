using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// TM-3: timesheet — live server-side timer (one active per employee) + manual entry. Operational ONLY (no GL/stock).
	// A task's ActualHours is kept = Σ of its entries' Hours (never hand-set), recomputed on every change.
	public class TimesheetLineDto
	{
		public int Id { get; set; }
		public int EmployeeId { get; set; }
		public string EmployeeName { get; set; } = "";
		public DateTime WorkDate { get; set; }
		public decimal Hours { get; set; }
		public string? Description { get; set; }
		public string Source { get; set; } = "Manual";
		public bool Running { get; set; }
	}
	public class RunningTimerDto
	{
		public int EntryId { get; set; }
		public int TaskId { get; set; }
		public string TaskTitle { get; set; } = "";
		public DateTime StartedAt { get; set; }
	}

	// TM-6: read-only hours report (per employee, within a date range) — HR feeds these hours into the EXISTING payroll.
	// NO GL/payroll writer here — pure reporting off the timesheet.
	public class HoursReportLineDto { public int TaskId { get; set; } public string TaskTitle { get; set; } = ""; public DateTime WorkDate { get; set; } public decimal Hours { get; set; } public string? Description { get; set; } }
	public class HoursReportEmployeeDto { public int EmployeeId { get; set; } public string EmployeeName { get; set; } = ""; public decimal TotalHours { get; set; } public List<HoursReportLineDto> Lines { get; set; } = new(); }
	public class HoursReportDto { public DateTime From { get; set; } public DateTime To { get; set; } public decimal GrandTotal { get; set; } public List<HoursReportEmployeeDto> Employees { get; set; } = new(); }

	public interface ITimesheetService
	{
		Task<HoursReportDto> GetHoursReportAsync(int companyId, DateTime from, DateTime to, int? employeeId);
		Task<(bool ok, string? error)> StartAsync(int companyId, int taskId, int employeeId);
		Task<(bool ok, string? error)> StopAsync(int companyId, int employeeId);
		Task<RunningTimerDto?> GetRunningAsync(int companyId, int employeeId);
		Task<(bool ok, string? error)> AddManualAsync(int companyId, int taskId, int employeeId, DateTime workDate, decimal hours, string? description);
		Task<(bool ok, string? error)> DeleteEntryAsync(int companyId, int id);
		Task<List<TimesheetLineDto>> GetEntriesAsync(int companyId, int taskId);
	}

	public class TimesheetService : ITimesheetService
	{
		private readonly CrossDbContext _db;
		public TimesheetService(CrossDbContext db) { _db = db; }

		private static decimal HoursBetween(DateTime a, DateTime b) => Math.Round((decimal)(b - a).TotalHours, 2, MidpointRounding.AwayFromZero);

		// keep the task's ActualHours = Σ of its entries (derived cache, never manual)
		private async Task RecalcActualHoursAsync(int companyId, int taskId)
		{
			var sum = await _db.TimesheetEntries.Where(e => e.CompanyId == companyId && e.TaskId == taskId).SumAsync(e => (decimal?)e.Hours) ?? 0m;
			var t = await _db.TaskItems.FirstOrDefaultAsync(x => x.ID == taskId && x.CompanyId == companyId);
			if (t != null) { t.ActualHours = sum; await _db.SaveChangesAsync(); }
		}

		public async Task<(bool ok, string? error)> StartAsync(int companyId, int taskId, int employeeId)
		{
			if (employeeId <= 0) return (false, "لا يمكن تحديد الموظف الحالي");
			var task = await _db.TaskItems.AsNoTracking().FirstOrDefaultAsync(t => t.ID == taskId && t.CompanyId == companyId);
			if (task == null) return (false, "المهمة غير موجودة");

			// one active timer per employee → auto-stop the previous running one (prevents double-counting)
			var open = await _db.TimesheetEntries.Where(e => e.CompanyId == companyId && e.EmployeeId == employeeId && e.Source == "Timer" && e.EndedAt == null).ToListAsync();
			var affectedTasks = new HashSet<int>();
			var now = DateTime.UtcNow;
			foreach (var o in open) { o.EndedAt = now; o.Hours = HoursBetween(o.StartedAt ?? now, now); affectedTasks.Add(o.TaskId); }
			if (open.Count > 0) await _db.SaveChangesAsync();
			foreach (var tid in affectedTasks) await RecalcActualHoursAsync(companyId, tid);

			_db.TimesheetEntries.Add(new TimesheetEntry
			{
				CompanyId = companyId, TaskId = taskId, EmployeeId = employeeId,
				WorkDate = now.Date, Hours = 0m, Source = "Timer", StartedAt = now, EndedAt = null, CreatedAt = now
			});
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> StopAsync(int companyId, int employeeId)
		{
			var open = await _db.TimesheetEntries.FirstOrDefaultAsync(e => e.CompanyId == companyId && e.EmployeeId == employeeId && e.Source == "Timer" && e.EndedAt == null);
			if (open == null) return (false, "لا يوجد مؤقّت شغّال");
			var now = DateTime.UtcNow;
			open.EndedAt = now; open.Hours = HoursBetween(open.StartedAt ?? now, now);
			await _db.SaveChangesAsync();
			await RecalcActualHoursAsync(companyId, open.TaskId);
			return (true, null);
		}

		public async Task<RunningTimerDto?> GetRunningAsync(int companyId, int employeeId)
		{
			var open = await _db.TimesheetEntries.AsNoTracking().FirstOrDefaultAsync(e => e.CompanyId == companyId && e.EmployeeId == employeeId && e.Source == "Timer" && e.EndedAt == null);
			if (open == null) return null;
			var title = await _db.TaskItems.AsNoTracking().Where(t => t.ID == open.TaskId).Select(t => t.Title).FirstOrDefaultAsync() ?? "";
			return new RunningTimerDto { EntryId = open.ID, TaskId = open.TaskId, TaskTitle = title, StartedAt = open.StartedAt ?? open.CreatedAt };
		}

		public async Task<(bool ok, string? error)> AddManualAsync(int companyId, int taskId, int employeeId, DateTime workDate, decimal hours, string? description)
		{
			if (employeeId <= 0) return (false, "لا يمكن تحديد الموظف الحالي");
			var task = await _db.TaskItems.AsNoTracking().FirstOrDefaultAsync(t => t.ID == taskId && t.CompanyId == companyId);
			if (task == null) return (false, "المهمة غير موجودة");
			if (hours <= 0) return (false, "الساعات يجب أن تكون أكبر من صفر");
			if (hours > 24) return (false, "الساعات لا تتجاوز 24 في اليوم");
			_db.TimesheetEntries.Add(new TimesheetEntry
			{
				CompanyId = companyId, TaskId = taskId, EmployeeId = employeeId,
				WorkDate = workDate.Date, Hours = Math.Round(hours, 2), Description = description,
				Source = "Manual", StartedAt = null, EndedAt = null, CreatedAt = DateTime.UtcNow
			});
			await _db.SaveChangesAsync();
			await RecalcActualHoursAsync(companyId, taskId);
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteEntryAsync(int companyId, int id)
		{
			var e = await _db.TimesheetEntries.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == companyId);
			if (e == null) return (false, "السطر غير موجود");
			int taskId = e.TaskId;
			_db.TimesheetEntries.Remove(e);
			await _db.SaveChangesAsync();
			await RecalcActualHoursAsync(companyId, taskId);
			return (true, null);
		}

		public async Task<HoursReportDto> GetHoursReportAsync(int companyId, DateTime from, DateTime to, int? employeeId)
		{
			var f = from.Date; var tEnd = to.Date.AddDays(1);   // inclusive of the 'to' day
			var q = _db.TimesheetEntries.AsNoTracking().Where(e => e.CompanyId == companyId && e.Hours > 0 && e.WorkDate >= f && e.WorkDate < tEnd);
			if (employeeId != null && employeeId > 0) q = q.Where(e => e.EmployeeId == employeeId);
			var raw = await q.Select(e => new { e.EmployeeId, e.TaskId, e.WorkDate, e.Hours, e.Description }).ToListAsync();

			var empIds = raw.Select(r => r.EmployeeId).Distinct().ToList();
			var taskIds = raw.Select(r => r.TaskId).Distinct().ToList();
			var empNames = await _db.Employee.AsNoTracking().Where(x => empIds.Contains(x.ID)).ToDictionaryAsync(x => x.ID, x => x.FullName ?? "");
			var taskTitles = await _db.TaskItems.AsNoTracking().Where(x => taskIds.Contains(x.ID)).ToDictionaryAsync(x => x.ID, x => x.Title);

			var dto = new HoursReportDto { From = f, To = to.Date };
			foreach (var g in raw.GroupBy(r => r.EmployeeId).OrderBy(g => empNames.TryGetValue(g.Key, out var n) ? n : ""))
			{
				var emp = new HoursReportEmployeeDto { EmployeeId = g.Key, EmployeeName = empNames.TryGetValue(g.Key, out var nm) ? nm : "", TotalHours = g.Sum(x => x.Hours) };
				emp.Lines = g.OrderBy(x => x.WorkDate).ThenBy(x => x.TaskId).Select(x => new HoursReportLineDto
				{
					TaskId = x.TaskId, TaskTitle = taskTitles.TryGetValue(x.TaskId, out var tt) ? tt : ("#" + x.TaskId),
					WorkDate = x.WorkDate, Hours = x.Hours, Description = x.Description
				}).ToList();
				dto.Employees.Add(emp);
				dto.GrandTotal += emp.TotalHours;
			}
			return dto;
		}

		public async Task<List<TimesheetLineDto>> GetEntriesAsync(int companyId, int taskId)
		{
			var rows = await _db.TimesheetEntries.AsNoTracking().Where(e => e.CompanyId == companyId && e.TaskId == taskId)
				.OrderByDescending(e => e.StartedAt ?? e.WorkDate).ThenByDescending(e => e.ID)
				.Select(e => new TimesheetLineDto { Id = e.ID, EmployeeId = e.EmployeeId, WorkDate = e.WorkDate, Hours = e.Hours, Description = e.Description, Source = e.Source, Running = e.Source == "Timer" && e.EndedAt == null })
				.ToListAsync();
			var empIds = rows.Select(r => r.EmployeeId).Distinct().ToList();
			var names = await _db.Employee.AsNoTracking().Where(x => empIds.Contains(x.ID)).ToDictionaryAsync(x => x.ID, x => x.FullName ?? "");
			foreach (var r in rows) r.EmployeeName = names.TryGetValue(r.EmployeeId, out var n) ? n : "";
			return rows;
		}
	}
}
