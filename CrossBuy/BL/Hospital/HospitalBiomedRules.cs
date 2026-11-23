using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H245 — biomedical rules, free of the database.
	//   · The ELECTRICAL-SAFETY TEST is due every 6 months for a high-risk device and every 12 months otherwise
	//     (IEC 62353 practice); a device never tested is due now.
	//   · PM COMPLIANCE is the share of the active preventive / calibration / inspection schedules not past due.
	//   · DOWNTIME over a window is the sum of each down→up interval clipped to the window (an open one runs to now);
	//     AVAILABILITY = 1 − downtime / window.
	// ==========================================================================================
	public static class HospitalBiomedRules
	{
		public static int SafetyIntervalMonths(string risk) => risk == HospitalBiomedRisk.High ? 6 : 12;

		public static DateTime? SafetyDue(string risk, DateTime? lastPassOrTest) => lastPassOrTest?.Date.AddMonths(SafetyIntervalMonths(risk));
		public static bool SafetyOverdue(string risk, DateTime? lastTest, DateTime today) => lastTest == null || SafetyDue(risk, lastTest)!.Value < today.Date;

		public static decimal? PmCompliance(IEnumerable<DateTime> nextDue, DateTime today)
		{
			var l = nextDue.ToList();
			return l.Count == 0 ? null : Math.Round(l.Count(d => d.Date >= today.Date) * 100m / l.Count, 0, MidpointRounding.AwayFromZero);
		}

		public static double DowntimeHours(IEnumerable<HospitalBiomedEvent> events, DateTime from, DateTime to)
		{
			double h = 0; DateTime? down = null;
			foreach (var e in events.Where(e => e.Kind is HospitalBiomedEventKind.Down or HospitalBiomedEventKind.Up).OrderBy(e => e.At))
			{
				if (e.Kind == HospitalBiomedEventKind.Down) down ??= e.At;
				else if (down is DateTime d) { h += Clip(d, e.At, from, to); down = null; }
			}
			if (down is DateTime open) h += Clip(open, to, from, to);
			return Math.Round(h, 1);
		}

		private static double Clip(DateTime a, DateTime b, DateTime from, DateTime to)
		{
			var s = a < from ? from : a; var e = b > to ? to : b;
			return e > s ? (e - s).TotalHours : 0;
		}

		public static decimal? Availability(double downHours, DateTime from, DateTime to)
		{
			var total = (to - from).TotalHours;
			return total <= 0 ? null : Math.Round((decimal)(100 * (1 - Math.Min(downHours, total) / total)), 1, MidpointRounding.AwayFromZero);
		}

		public static string? DeviceRefusal(string? category, string? risk, string? serial, string? maker, string? model)
		{
			if (!HospitalBiomedCategory.All.Contains(category ?? "")) return "Choose the category";
			if (!HospitalBiomedRisk.All.Contains(risk ?? "")) return "Choose the risk class";
			return (serial ?? "").Length > 60 || (maker ?? "").Length > 100 || (model ?? "").Length > 100 ? "A value is too long" : null;
		}

		/// Why this event cannot be recorded on a device in `status`, or null.
		public static string? EventRefusal(string status, string? kind, bool? passed, string? detail)
		{
			if (status == HospitalBiomedStatus.Retired) return "This device is retired";
			return kind switch
			{
				HospitalBiomedEventKind.Down => status == HospitalBiomedStatus.Down ? "The device is already out of service" : string.IsNullOrWhiteSpace(detail) ? "Write what is wrong" : null,
				HospitalBiomedEventKind.Up => status != HospitalBiomedStatus.Down ? "The device is in service" : null,
				HospitalBiomedEventKind.SafetyTest => passed == null ? "Choose pass or fail" : passed == false && string.IsNullOrWhiteSpace(detail) ? "Write what failed" : null,
				HospitalBiomedEventKind.Recall => string.IsNullOrWhiteSpace(detail) ? "Write the recall reference" : null,
				_ => "not_found",
			};
		}
	}
}
