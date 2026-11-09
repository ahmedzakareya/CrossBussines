using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// THE WORKING-HOURS SCHEDULE (2026-11-09) — what Kuwait's Ministerial Resolution 15/2025 has employers record on PAM's
	// Ashal portal since 1 November 2025: daily working hours, rest periods, weekly rest days and official holidays, then
	// print the approved schedule and display it at the workplace. No file format is published (the entry is a form on the
	// portal), so this assembles exactly those facts from what the system already holds — every attendance schedule that
	// has employees in the company, and the company's official holidays — and checks each against the rule pack's limits
	// (hours a day, hours a week, the longest stretch before a break). The screen shows it; the printable document is the
	// Report Studio dataset hr.working-hours, built from this same service.
	// ==========================================================================================
	public sealed record WorkDay(DayOfWeek Day, bool Working, TimeSpan? Start, TimeSpan? End, int BreakMinutes, decimal NetHours, TimeSpan? BreakStart, TimeSpan? BreakEnd);
	public sealed record WorkSchedule(int PolicyId, string NameAr, string NameEn, int Employees, IReadOnlyList<WorkDay> Days, IReadOnlyList<string> WarningsAr, IReadOnlyList<string> WarningsEn)
	{
		public decimal WeeklyHours => Days.Where(d => d.Working).Sum(d => d.NetHours);
		public IEnumerable<DayOfWeek> RestDays => Days.Where(d => !d.Working).Select(d => d.Day);
	}
	public sealed record WorkHoliday(DateTime Date, string NameAr, string? NameEn, bool Recurring);
	public sealed record WorkingHoursSheet(IReadOnlyList<WorkSchedule> Schedules, IReadOnlyList<WorkHoliday> Holidays, decimal HoursPerDay, decimal MaxWeeklyHours, decimal BreakAfterHours, string? PackNameAr, string? PackNameEn);

	public sealed class HrWorkingHoursService
	{
		// Saturday first: the order a Kuwaiti schedule is read in.
		public static readonly DayOfWeek[] WeekOrder = { DayOfWeek.Saturday, DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
		private readonly CrossDbContext _db;
		public HrWorkingHoursService(CrossDbContext db) { _db = db; }

		public async Task<WorkingHoursSheet> BuildAsync(int companyId, int year, CancellationToken ct = default)
		{
			var eff = await new HrRulePackService(_db).EffectiveAsync(companyId, ct);
			var rules = eff?.Rules ?? new HrRules();
			// The company's schedules are the ones its active employees are assigned to (policies carry no company of their own).
			var assigned = await _db.PolicyAssignments.AsNoTracking()
				.Where(a => _db.Employee.Any(e => e.ID == a.EmployeeID && e.EmpCompanyID == companyId && e.IsActive))
				.GroupBy(a => a.LeavePolicyTypeID).Select(g => new { PolicyId = g.Key, Count = g.Count() }).ToListAsync(ct);
			var ids = assigned.Select(a => a.PolicyId).ToList();
			var policies = await _db.AttendancePolicies.AsNoTracking().Where(p => ids.Contains(p.LeavePolicyTypeID)).ToListAsync(ct);
			var names = await _db.Policies.AsNoTracking().Where(p => ids.Contains(p.ID)).ToDictionaryAsync(p => p.ID, p => (p.NameAr, p.NameEn), ct);

			var schedules = new List<WorkSchedule>();
			foreach (var p in policies.GroupBy(x => x.LeavePolicyTypeID).Select(g => g.OrderBy(x => x.ID).First()))
			{
				int brk = p.BreakDurationMinutes ?? (p.BreakStartTime is { } bs && p.BreakEndTime is { } be ? (int)(be - bs).TotalMinutes : 0);
				var span = p.WorkEndTime > p.WorkStartTime ? p.WorkEndTime - p.WorkStartTime : p.WorkEndTime + TimeSpan.FromDays(1) - p.WorkStartTime;
				var net = Math.Round((decimal)(span.TotalMinutes - brk) / 60m, 2);
				bool Works(DayOfWeek d) => d switch
				{
					DayOfWeek.Sunday => p.WorkOnSunday, DayOfWeek.Monday => p.WorkOnMonday, DayOfWeek.Tuesday => p.WorkOnTuesday,
					DayOfWeek.Wednesday => p.WorkOnWednesday, DayOfWeek.Thursday => p.WorkOnThursday, DayOfWeek.Friday => p.WorkOnFriday, _ => p.WorkOnSaturday,
				};
				var days = WeekOrder.Select(d => Works(d)
					? new WorkDay(d, true, p.WorkStartTime, p.WorkEndTime, brk, net, p.BreakStartTime, p.BreakEndTime)
					: new WorkDay(d, false, null, null, 0, 0, null, null)).ToList();

				var wAr = new List<string>(); var wEn = new List<string>();
				if (rules.HoursPerDay > 0 && net > rules.HoursPerDay) { wAr.Add($"{net:0.##} ساعة يومياً تتجاوز الحد {rules.HoursPerDay:0.##}"); wEn.Add($"{net:0.##} hours a day exceed the {rules.HoursPerDay:0.##}-hour limit"); }
				var weekly = days.Where(d => d.Working).Sum(d => d.NetHours);
				if (rules.MaxWeeklyHours > 0 && weekly > rules.MaxWeeklyHours) { wAr.Add($"{weekly:0.##} ساعة أسبوعياً تتجاوز الحد {rules.MaxWeeklyHours:0.##}"); wEn.Add($"{weekly:0.##} hours a week exceed the {rules.MaxWeeklyHours:0.##}-hour limit"); }
				if (rules.BreakAfterHours > 0 && (decimal)span.TotalHours > rules.BreakAfterHours && brk <= 0) { wAr.Add($"لا فترة راحة في يوم يزيد على {rules.BreakAfterHours:0.##} ساعات متصلة"); wEn.Add($"No rest break in a day longer than {rules.BreakAfterHours:0.##} continuous hours"); }
				if (!days.Any(d => !d.Working)) { wAr.Add("لا يوم راحة أسبوعي"); wEn.Add("No weekly rest day"); }

				var nm = names.TryGetValue(p.LeavePolicyTypeID, out var n) ? n : (null, null);
				schedules.Add(new WorkSchedule(p.LeavePolicyTypeID, nm.Item1 ?? nm.Item2 ?? ("#" + p.LeavePolicyTypeID), nm.Item2 ?? nm.Item1 ?? ("#" + p.LeavePolicyTypeID),
					assigned.First(a => a.PolicyId == p.LeavePolicyTypeID).Count, days, wAr, wEn));
			}

			var hol = await _db.OfficialHolidays.AsNoTracking().Where(h => h.CompanyID == companyId && (h.IsRecurring || h.HolidayDate.Year == year))
				.OrderBy(h => h.HolidayDate.Month).ThenBy(h => h.HolidayDate.Day).ToListAsync(ct);
			var holidays = hol.Select(h => new WorkHoliday(h.IsRecurring ? SafeDate(year, h.HolidayDate.Month, h.HolidayDate.Day) : h.HolidayDate.Date, h.NameAr, h.NameEn, h.IsRecurring))
				.OrderBy(h => h.Date).ToList();
			return new WorkingHoursSheet(schedules.OrderByDescending(s => s.Employees).ToList(), holidays, rules.HoursPerDay, rules.MaxWeeklyHours, rules.BreakAfterHours, eff?.Pack.NameAr, eff?.Pack.NameEn);
		}

		private static DateTime SafeDate(int y, int m, int d) => new(y, m, Math.Min(d, DateTime.DaysInMonth(y, m)));
	}
}
