using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class LeaveBalanceRow
	{
		public int LeaveTypeId { get; set; }
		public string? NameAr { get; set; }
		public string? NameEn { get; set; }
		public int Entitlement { get; set; }
		public int Used { get; set; }
		public int Remaining => Entitlement - Used;
	}

	public class TypeSlice
	{
		public int LeaveTypeId { get; set; }
		public string? NameAr { get; set; }
		public string? NameEn { get; set; }
		public int Count { get; set; }
		public int Pct { get; set; }
	}

	public class MonthPoint
	{
		public int Year { get; set; }
		public int Month { get; set; }
		public int Count { get; set; }
	}

	public class PeopleDashboardDto
	{
		public bool HasPolicy { get; set; }
		public string? PolicyNameAr { get; set; }
		public string? PolicyNameEn { get; set; }
		public List<LeaveBalanceRow> Balances { get; set; } = new();
		public int MyPending { get; set; }
		public int MyApprovedThisYear { get; set; }
		public int PendingApprovals { get; set; }   // requests from my reports awaiting my decision
		public int TeamSize { get; set; }           // direct reports

		// request stats (over the employee's own leave requests)
		public int Total { get; set; }
		public int Approved { get; set; }
		public int Pending { get; set; }
		public int Rejected { get; set; }
		public List<TypeSlice> ByType { get; set; } = new();   // distribution donut
		public List<MonthPoint> Monthly { get; set; } = new(); // last 6 months trend
		public int ThisMonth { get; set; }
		public int LastMonth { get; set; }
		public int PctChange { get; set; }
	}

	public interface ILeaveDashboardService
	{
		Task<PeopleDashboardDto> BuildAsync(int employeeId);

		/// Remaining balance (days) for a leave type = entitlement + carried-in − approved-used − encashed, this year.
		/// Returns a negative/zero value when there is no entitlement or it is exhausted.
		Task<int> RemainingForTypeAsync(int employeeId, int leaveTypeId);

		/// Same as RemainingForTypeAsync but for an explicit year (used by carry-over).
		Task<int> RemainingForTypeInYearAsync(int employeeId, int leaveTypeId, int year);

		/// Work-day flags for the employee's policy, indexed by DayOfWeek (0=Sunday … 6=Saturday).
		/// Returns null when the policy defines no work days (no restriction → any day allowed).
		Task<bool[]?> WorkDayFlagsAsync(int employeeId);

		/// Number of WORKING days in [start, end] (inclusive) per the employee's policy work days.
		/// When the policy has no work-day definition, counts all calendar days.
		Task<int> WorkingDaysAsync(int employeeId, DateTime start, DateTime end);
	}

	public class LeaveDashboardService : ILeaveDashboardService
	{
		private readonly CrossDbContext _context;
		private readonly IHolidayService _holidays;
		public LeaveDashboardService(CrossDbContext context, IHolidayService holidays) { _context = context; _holidays = holidays; }

		public async Task<PeopleDashboardDto> BuildAsync(int employeeId)
		{
			var dto = new PeopleDashboardDto();
			var year = DateTime.UtcNow.Year;

			// the employee's assigned policy (list)
			var assignment = await _context.PolicyAssignments.AsNoTracking()
				.FirstOrDefaultAsync(p => p.EmployeeID == employeeId);

			var leaveTypes = await _context.LeaveTypes.AsNoTracking()
				.ToDictionaryAsync(t => t.ID, t => new { t.NameAr, t.NameEn });

			// approved days used this year, per leave type
			var usedByType = (await _context.LeaveRequests.AsNoTracking()
				.Where(r => r.EmployeeID == employeeId && r.Status == 1 && r.StartDate.Year == year)
				.ToListAsync())
				.GroupBy(r => r.LeaveTypeID)
				.ToDictionary(g => g.Key, g => g.Sum(r => r.Days));

			if (assignment != null)
			{
				var policy = await _context.Policies.AsNoTracking().FirstOrDefaultAsync(p => p.ID == assignment.LeavePolicyTypeID);
				dto.HasPolicy = true;
				dto.PolicyNameAr = policy?.NameAr;
				dto.PolicyNameEn = policy?.NameEn;

				// entitlement per leave type (dedupe duplicates by taking the max)
				var entitlements = (await _context.LeavePolicies.AsNoTracking()
					.Where(lp => lp.LeavePolicyTypeID == assignment.LeavePolicyTypeID)
					.ToListAsync())
					.GroupBy(lp => lp.LeaveTypeID)
					.Select(g => new { TypeId = g.Key, Days = g.Max(x => x.EntitlementDaysPerYear) });

				foreach (var e in entitlements)
				{
					var nm = leaveTypes.TryGetValue(e.TypeId, out var n) ? n : null;
					dto.Balances.Add(new LeaveBalanceRow
					{
						LeaveTypeId = e.TypeId,
						NameAr = nm?.NameAr,
						NameEn = nm?.NameEn,
						Entitlement = e.Days,
						Used = usedByType.TryGetValue(e.TypeId, out var u) ? u : 0,
					});
				}
				dto.Balances = dto.Balances.OrderByDescending(b => b.Entitlement).ToList();
			}

			// all my requests → status totals, type distribution, monthly trend
			var myReqs = await _context.LeaveRequests.AsNoTracking()
				.Include(r => r.LeaveType)
				.Where(r => r.EmployeeID == employeeId)
				.ToListAsync();

			dto.Total = myReqs.Count;
			dto.Approved = myReqs.Count(r => r.Status == 1);
			dto.Pending = myReqs.Count(r => r.Status == 0);
			dto.Rejected = myReqs.Count(r => r.Status == 2);

			dto.ByType = myReqs.GroupBy(r => r.LeaveTypeID).Select(g => new TypeSlice
			{
				LeaveTypeId = g.Key,
				NameAr = g.First().LeaveType?.NameAr,
				NameEn = g.First().LeaveType?.NameEn,
				Count = g.Count(),
				Pct = dto.Total > 0 ? (int)Math.Round(100.0 * g.Count() / dto.Total) : 0,
			}).OrderByDescending(s => s.Count).ToList();

			// last 6 months trend (by request creation/start date)
			DateTime When(Models.Context.Admin.LeaveRequest r) => (r.CreatedAt ?? r.StartDate);
			var now = DateTime.UtcNow;
			for (var i = 5; i >= 0; i--)
			{
				var m = new DateTime(now.Year, now.Month, 1).AddMonths(-i);
				dto.Monthly.Add(new MonthPoint
				{
					Year = m.Year,
					Month = m.Month,
					Count = myReqs.Count(r => { var d = When(r); return d.Year == m.Year && d.Month == m.Month; }),
				});
			}
			var thisM = new DateTime(now.Year, now.Month, 1);
			var lastM = thisM.AddMonths(-1);
			dto.ThisMonth = myReqs.Count(r => { var d = When(r); return d.Year == thisM.Year && d.Month == thisM.Month; });
			dto.LastMonth = myReqs.Count(r => { var d = When(r); return d.Year == lastM.Year && d.Month == lastM.Month; });
			dto.PctChange = dto.LastMonth > 0
				? (int)Math.Round(100.0 * (dto.ThisMonth - dto.LastMonth) / dto.LastMonth)
				: (dto.ThisMonth > 0 ? 100 : 0);

			// my request counts (cards)
			dto.MyPending = dto.Pending;
			dto.MyApprovedThisYear = myReqs.Count(r => r.Status == 1 && r.StartDate.Year == year);

			// team / approvals (via org tree)
			var subIds = await SubordinateEmployeeIdsAsync(employeeId);
			dto.TeamSize = subIds.Count;
			dto.PendingApprovals = subIds.Count == 0 ? 0
				: await _context.LeaveRequests.CountAsync(r => r.Status == 0 && subIds.Contains(r.EmployeeID));

			return dto;
		}

		public Task<int> RemainingForTypeAsync(int employeeId, int leaveTypeId) =>
			RemainingForTypeInYearAsync(employeeId, leaveTypeId, DateTime.UtcNow.Year);

		public async Task<int> RemainingForTypeInYearAsync(int employeeId, int leaveTypeId, int year)
		{
			var assignment = await _context.PolicyAssignments.AsNoTracking()
				.FirstOrDefaultAsync(p => p.EmployeeID == employeeId);
			if (assignment == null) return 0; // not assigned to any policy → no balance

			var entitlement = await _context.LeavePolicies.AsNoTracking()
				.Where(lp => lp.LeavePolicyTypeID == assignment.LeavePolicyTypeID && lp.LeaveTypeID == leaveTypeId)
				.Select(lp => (int?)lp.EntitlementDaysPerYear)
				.MaxAsync() ?? 0;

			// balance carried in from the prior year (capped at run time by CarryOverLimit)
			var carriedIn = await _context.LeaveCarryOvers.AsNoTracking()
				.Where(c => c.EmployeeID == employeeId && c.LeaveTypeID == leaveTypeId && c.Year == year)
				.SumAsync(c => (int?)c.Days) ?? 0;

			var used = (await _context.LeaveRequests.AsNoTracking()
				.Where(r => r.EmployeeID == employeeId && r.LeaveTypeID == leaveTypeId && r.Status == 1 && r.StartDate.Year == year)
				.ToListAsync()).Sum(r => r.Days);

			// days already paid out as cash (HR-2f) also reduce the remaining balance
			var encashed = await _context.LeaveEncashments.AsNoTracking()
				.Where(x => x.EmployeeID == employeeId && x.LeaveTypeID == leaveTypeId && x.Year == year)
				.SumAsync(x => (int?)x.Days) ?? 0;

			return entitlement + carriedIn - used - encashed;
		}

		public async Task<bool[]?> WorkDayFlagsAsync(int employeeId)
		{
			var assignment = await _context.PolicyAssignments.AsNoTracking()
				.FirstOrDefaultAsync(p => p.EmployeeID == employeeId);
			if (assignment == null) return null; // no policy → no restriction

			var ap = await _context.AttendancePolicies.AsNoTracking()
				.FirstOrDefaultAsync(a => a.LeavePolicyTypeID == assignment.LeavePolicyTypeID);
			if (ap == null) return null; // policy has no work-days defined → no restriction

			// index by DayOfWeek: Sunday=0 … Saturday=6
			var flags = new[]
			{
				ap.WorkOnSunday, ap.WorkOnMonday, ap.WorkOnTuesday, ap.WorkOnWednesday,
				ap.WorkOnThursday, ap.WorkOnFriday, ap.WorkOnSaturday
			};
			// if nothing is marked as a work day, treat as no restriction (avoid blocking everything)
			return flags.Any(f => f) ? flags : null;
		}

		public async Task<int> WorkingDaysAsync(int employeeId, DateTime start, DateTime end)
		{
			var s = start.Date;
			var e = end.Date;
			if (e < s) return 0;

			// official holidays for the employee's company are never counted as leave days
			var companyId = await _context.Employee.AsNoTracking().Where(x => x.ID == employeeId).Select(x => x.EmpCompanyID).FirstOrDefaultAsync();
			var holidays = await _holidays.HolidayDatesAsync(companyId, s, e);

			var flags = await WorkDayFlagsAsync(employeeId);
			if (flags == null)
			{
				// no weekly restriction → all calendar days except official holidays
				var c2 = 0;
				for (var d = s; d <= e; d = d.AddDays(1)) if (!holidays.Contains(d)) c2++;
				return c2;
			}

			var count = 0;
			for (var d = s; d <= e; d = d.AddDays(1))
				if (flags[(int)d.DayOfWeek] && !holidays.Contains(d)) count++;
			return count;
		}

		private async Task<List<int>> SubordinateEmployeeIdsAsync(int employeeId)
		{
			var all = await _context.Hierarchicals.AsNoTracking().ToListAsync();
			var myNode = all.FirstOrDefault(h => h.H_Type == 5 && h.H_ObjectID == employeeId);
			if (myNode == null) return new List<int>();
			var childPositions = all.Where(h => h.H_Parent == myNode.H_ID).Select(h => h.H_ID).ToHashSet();
			return all.Where(h => h.H_Type == 5 && h.H_Parent.HasValue && childPositions.Contains(h.H_Parent.Value) && h.H_ObjectID.HasValue)
				.Select(h => h.H_ObjectID!.Value).Distinct().ToList();
		}
	}
}
