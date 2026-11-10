using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H14 — the nursing rules, pure: no database, no clock, no access. The service applies them;
	// the tests pin them (CrossBuy.Tests/HospitalNursingRulesTests, database-free).
	// ==========================================================================================

	public static class HospitalNursingRules
	{
		/// A dose is "due" from this many minutes before its hour to this many after; later it is overdue.
		public const int WindowMinutes = 60;

		public const string Upcoming = "upcoming", Due = "due", Overdue = "overdue";

		/// The schedule hours of an order, from "06,14,22" (sorted, distinct); null when the text is not a
		/// list of hours 0–23. An empty text is an empty schedule (an as-needed order).
		public static IReadOnlyList<int>? ParseHours(string? csv)
		{
			if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<int>();
			var hours = new SortedSet<int>();
			foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				if (!int.TryParse(part, out var h) || h is < 0 or > 23) return null;
				hours.Add(h);
			}
			return hours.ToList();
		}

		public static string FormatHours(IEnumerable<int> hours) => string.Join(",", hours.Distinct().OrderBy(h => h).Select(h => h.ToString("00")));

		/// Why an order cannot be written as it is (null = it can): a drug and a dose, a known route, and either
		/// a schedule or "as needed" — not both, not neither.
		public static string? OrderRefusal(string? drug, string? dose, string route, string? hours, bool asNeeded)
		{
			if (string.IsNullOrWhiteSpace(drug)) return "Enter the drug";
			if (string.IsNullOrWhiteSpace(dose)) return "Enter the dose";
			if (!HospitalMedRoute.All.Contains(route)) return "Choose the route";
			var h = ParseHours(hours);
			if (h == null) return "The hours are a list like 06,14,22";
			if (asNeeded && h.Count > 0) return "An as-needed order has no fixed hours";
			if (!asNeeded && h.Count == 0) return "Give the hours of the doses, or mark the order as needed";
			return null;
		}

		/// The scheduled doses of an order on a ward-clock day, as UTC instants, from its start to its stop.
		/// `toUtc` turns a ward-clock time into UTC (the facility's zone lives outside this rule).
		public static IReadOnlyList<DateTime> DosesOn(DateTime wardDay, IReadOnlyList<int> hours, DateTime startUtc, DateTime? stoppedUtc, Func<DateTime, DateTime> toUtc)
			=> hours.Select(h => toUtc(wardDay.Date.AddHours(h)))
				.Where(t => t >= startUtc.AddMinutes(-WindowMinutes) && (stoppedUtc == null || t < stoppedUtc)).ToList();

		/// Where an unrecorded dose stands now.
		public static string DoseState(DateTime dueUtc, DateTime nowUtc)
			=> nowUtc < dueUtc.AddMinutes(-WindowMinutes) ? Upcoming : nowUtc <= dueUtc.AddMinutes(WindowMinutes) ? Due : Overdue;

		/// Why an administration cannot be recorded (null = it can). A dose not given carries its reason; a
		/// controlled drug given carries a witness who is not the nurse recording it; a dose is not recorded
		/// before its window opens.
		public static string? RecordRefusal(string status, string? reason, bool controlled, int? recorder, int? witness, DateTime? dueUtc, DateTime nowUtc)
		{
			if (!HospitalMarStatus.All.Contains(status)) return "Choose what happened to the dose";
			if (status != HospitalMarStatus.Given && string.IsNullOrWhiteSpace(reason)) return "Write why the dose was not given";
			if (status == HospitalMarStatus.Given && controlled)
			{
				if (witness == null) return "A controlled drug needs a witness";
				if (recorder != null && witness == recorder) return "The witness must be someone else";
			}
			if (dueUtc != null && status == HospitalMarStatus.Given && nowUtc < dueUtc.Value.AddMinutes(-WindowMinutes)) return "This dose is not due yet";
			return null;
		}

		/// The shift a ward-clock time falls in: day 07–15, evening 15–23, night 23–07.
		public static string ShiftOf(DateTime wardTime) => wardTime.Hour is >= 7 and < 15 ? HospitalShift.Day : wardTime.Hour is >= 15 and < 23 ? HospitalShift.Evening : HospitalShift.Night;

		/// The date a shift is filed under: the night that starts at 23:00 belongs to the day it started.
		public static DateTime ShiftDate(DateTime wardTime) => wardTime.Hour < 7 ? wardTime.Date.AddDays(-1) : wardTime.Date;
	}
}
