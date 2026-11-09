using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// LEAVE PAID IN TIERS (2026-11-09) — Kuwait Art. 69 sick leave: 15 days full, 10 at 75%, 10 at 50%, 10 at 25%, 30 unpaid.
	//
	// A leave rule in the country pack may carry PayTiers. Payroll asks this class, per employee and month, how much of
	// the wage the approved days of such leave DO NOT earn: each day takes the tier its position in the YEAR gives it
	// (the 16th sick day of the year is a 75% day whatever month it falls in), and a day beyond the last tier is unpaid.
	// The deduction is the daily wage (the pack's wage basis ÷ WageDaysPerMonth) × (100 − paid %) for each day.
	// A request is attributed to the month it starts in, using its working-day count (LeaveRequest.Days).
	// ==========================================================================================
	public sealed record LeavePayCut(decimal Amount, int ReducedDays, string? Detail);

	public static class HrLeavePay
	{
		/// The paid % of the n-th day (1-based) of a tiered leave in a year. Pure, so every boundary is testable.
		public static decimal PaidPct(IReadOnlyList<HrPayTier> tiers, int n)
		{
			decimal upTo = 0;
			foreach (var t in tiers)
			{
				upTo += t.Days;
				if (n <= upTo) return t.PaidPct;
			}
			return 0m;   // beyond the last tier: unpaid
		}

		/// Employee → the month's cut for tiered leave, given each employee's daily wage.
		public static async Task<Dictionary<int, LeavePayCut>> CutsAsync(CrossDbContext db, HrRules pack, int companyId, int year, int month,
			IReadOnlyDictionary<int, decimal> dailyWage, CancellationToken ct = default)
		{
			var result = new Dictionary<int, LeavePayCut>();
			var tieredTypes = new Dictionary<int, HrLeaveRule>();
			foreach (var t in await db.LeaveTypes.AsNoTracking().Select(t => new { t.ID, t.NameAr, t.NameEn }).ToListAsync(ct))
			{
				var law = HrLeaveMatch.For(pack, t.NameEn, t.NameAr);
				if (law is { PayTiers.Count: > 0 }) tieredTypes[t.ID] = law;
			}
			if (tieredTypes.Count == 0 || dailyWage.Count == 0) return result;

			var ids = dailyWage.Keys.ToList();
			var typeIds = tieredTypes.Keys.ToList();
			var monthStart = new DateTime(year, month, 1); var monthEnd = monthStart.AddMonths(1).AddDays(-1);
			var reqs = await db.LeaveRequests.AsNoTracking()
				.Where(r => r.Status == 1 && ids.Contains(r.EmployeeID) && typeIds.Contains(r.LeaveTypeID) && r.StartDate.Year == year && r.StartDate <= monthEnd)
				.OrderBy(r => r.StartDate).ThenBy(r => r.ID)
				.Select(r => new { r.EmployeeID, r.LeaveTypeID, r.StartDate, r.Days }).ToListAsync(ct);

			foreach (var g in reqs.GroupBy(r => r.EmployeeID))
			{
				decimal amount = 0; int reduced = 0;
				foreach (var byType in g.GroupBy(r => r.LeaveTypeID))
				{
					var tiers = tieredTypes[byType.Key].PayTiers;
					int before = 0;
					foreach (var r in byType)
					{
						bool thisMonth = r.StartDate >= monthStart;
						for (int k = 1; k <= r.Days; k++)
						{
							if (thisMonth)
							{
								var pct = PaidPct(tiers, before + k);
								if (pct < 100) { amount += dailyWage[g.Key] * (100 - pct) / 100m; reduced++; }
							}
						}
						before += r.Days;
					}
				}
				if (amount > 0) result[g.Key] = new LeavePayCut(Math.Round(amount, 3, MidpointRounding.AwayFromZero), reduced, null);
			}
			return result;
		}
	}
}
