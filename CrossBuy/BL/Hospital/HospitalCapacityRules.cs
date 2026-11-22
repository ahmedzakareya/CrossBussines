using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalCapacityDay(DateTime Day, int Occupied, int Discharges, int Admissions, int Projected, int Beds)
	{
		public decimal? OccupancyPct => Beds == 0 ? null : Math.Round(Projected * 100m / Beds, 0, MidpointRounding.AwayFromZero);
	}

	// ==========================================================================================
	// HOSPITAL H239 — the capacity centre's arithmetic, free of the database.
	//   · An admission's EXPECTED DISCHARGE is the team's plan when there is one; otherwise the admission day plus the
	//     median length of stay of past discharges with the same diagnosis (at least 5 of them), else of all discharges
	//     (at least 5), else none. An estimate already in the past moves to today: the patient is still here.
	//   · EXPECTED ADMISSIONS on a day are the mean of the same weekday over the last 8 weeks.
	//   · The FORECAST walks the days: tomorrow's occupied = today's − today's discharges + today's admissions, never
	//     below zero; the projected occupancy is against the beds in service (not blocked).
	//   · Occupancy at or above 85 % is the strain threshold (the commonly used safe-occupancy line); 95 % is critical.
	// ==========================================================================================
	public static class HospitalCapacityRules
	{
		public const int MinSample = 5, Weeks = 8;
		public const decimal Strain = 85m, Critical = 95m;

		public static int? MedianDays(IEnumerable<int> stays)
		{
			var s = stays.OrderBy(x => x).ToList();
			if (s.Count < MinSample) return null;
			return s.Count % 2 == 1 ? s[s.Count / 2] : (int)Math.Round((s[s.Count / 2 - 1] + s[s.Count / 2]) / 2m, MidpointRounding.AwayFromZero);
		}

		/// The expected discharge day and whether it is the team's plan (true) or an estimate (false); null when nothing is known.
		public static (DateTime Day, bool Planned)? ExpectedDischarge(DateTime? planned, DateTime admittedDay, int? diagnosisMedian, int? overallMedian, DateTime today)
		{
			if (planned is DateTime p) return (p.Date, true);
			var los = diagnosisMedian ?? overallMedian;
			if (los is not int d) return null;
			var e = admittedDay.Date.AddDays(Math.Max(1, d));
			return (e < today.Date ? today.Date : e, false);
		}

		/// The mean admissions on `day`'s weekday over the `Weeks` weeks before `today`.
		public static decimal ExpectedAdmissions(IEnumerable<DateTime> admittedDays, DateTime day, DateTime today)
		{
			var from = today.Date.AddDays(-7 * Weeks);
			var n = admittedDays.Count(a => a.Date >= from && a.Date < today.Date && a.DayOfWeek == day.DayOfWeek);
			return Math.Round(n / (decimal)Weeks, 1, MidpointRounding.AwayFromZero);
		}

		public static IReadOnlyList<HospitalCapacityDay> Forecast(DateTime today, int occupiedNow, int beds, IReadOnlyList<DateTime> expectedDischarges, Func<DateTime, decimal> admissions, int days = 4)
		{
			var list = new List<HospitalCapacityDay>();
			int occ = occupiedNow;
			for (int i = 0; i < days; i++)
			{
				var d = today.Date.AddDays(i);
				int dis = expectedDischarges.Count(x => x.Date == d);
				int adm = (int)Math.Round(admissions(d), MidpointRounding.AwayFromZero);
				int proj = Math.Max(0, occ - dis + adm);
				list.Add(new HospitalCapacityDay(d, occ, dis, adm, proj, beds));
				occ = proj;
			}
			return list;
		}

		public static string Level(decimal? pct) => pct is not decimal p ? "None" : p >= Critical ? "Critical" : p >= Strain ? "Strain" : "Normal";

		public static string? PlanRefusal(DateTime expected, DateTime today, DateTime admittedDay, string? confidence, IEnumerable<string>? barriers, string? note)
		{
			if (expected.Date < today.Date) return "The expected day is today or later";
			if (expected.Date > today.Date.AddDays(60)) return "The expected day is within the next 60 days";
			if (expected.Date < admittedDay.Date) return "That day is before the admission";
			if (!HospitalDischargeConfidence.All.Contains(confidence ?? "")) return "Choose how sure the plan is";
			if ((barriers ?? Array.Empty<string>()).Any(b => !HospitalDischargeBarrier.All.Contains(b))) return "not_found";
			return (note ?? "").Trim().Length > 300 ? "The note is 300 characters at most" : null;
		}

		public static string? BarrierText(IEnumerable<string>? barriers)
		{
			var kept = HospitalDischargeBarrier.All.Where(b => (barriers ?? Array.Empty<string>()).Contains(b)).ToList();
			return kept.Count == 0 ? null : string.Join(",", kept);
		}
	}
}
