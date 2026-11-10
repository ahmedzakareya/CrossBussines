using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H16 — the rules of the dental practice, with no database: chair time, the lab pipeline and its
	// alert two days before the fit, the hygiene recall, and the sterilisation hold.
	// ==========================================================================================
	public static class HospitalDentalPracticeRules
	{
		/// The chair time of a visit with no procedure chosen.
		public const int DefaultMinutes = 30;
		/// The working day a chair's occupancy is measured against (08:00–16:00).
		public const int OpenMinutesPerChair = 480;
		/// A lab case not in the practice this many days before its fit is flagged.
		public const int LabAlertDays = 2;

		public static bool Overlaps(int start, int minutes, int otherStart, int otherMinutes) => start < otherStart + otherMinutes && otherStart < start + minutes;

		/// The occupancy of the chairs: booked minutes over the open minutes, as a whole percent.
		public static int? Occupancy(int bookedMinutes, int chairs) => chairs == 0 ? null : (int)Math.Round(Math.Min(100m, bookedMinutes * 100m / (chairs * OpenMinutesPerChair)));

		/// The moves a lab case may make: along the pipeline one step at a time; a remake from what came back
		/// (shipped, received or tried in), and a remake goes back to the lab.
		public static bool CanMove(string from, string to) => (from, to) switch
		{
			(HospitalLabCaseStage.Prescribed, HospitalLabCaseStage.Sent) => true,
			(HospitalLabCaseStage.Sent, HospitalLabCaseStage.Fabricating) => true,
			(HospitalLabCaseStage.Fabricating, HospitalLabCaseStage.Shipped) => true,
			(HospitalLabCaseStage.Shipped, HospitalLabCaseStage.Received) => true,
			(HospitalLabCaseStage.Received, HospitalLabCaseStage.Fitted) => true,
			(HospitalLabCaseStage.Shipped or HospitalLabCaseStage.Received, HospitalLabCaseStage.Remake) => true,
			(HospitalLabCaseStage.Remake, HospitalLabCaseStage.Sent) => true,
			_ => false,
		};

		public static string? NextStage(string stage) => stage switch
		{
			HospitalLabCaseStage.Remake => HospitalLabCaseStage.Sent,
			HospitalLabCaseStage.Fitted => null,
			_ => HospitalLabCaseStage.Pipeline[HospitalLabCaseStage.Pipeline.ToList().IndexOf(stage) + 1],
		};

		public static bool InPractice(string stage) => stage is HospitalLabCaseStage.Received or HospitalLabCaseStage.Fitted;

		/// A case still out when its fit is two days away or less (or past) puts the appointment at risk.
		public static bool AtRisk(string stage, DateTime? fitAt, DateTime now) =>
			!InPractice(stage) && fitAt is DateTime f && f - now <= TimeSpan.FromDays(LabAlertDays);

		/// Of the cases back in the last 90 days, the share that came back by their due date.
		public static decimal? OnTimeRate(IEnumerable<(DateTime Due, DateTime? Received)> cases, DateTime today)
		{
			var back = cases.Where(c => c.Received is DateTime r && r >= today.AddDays(-90)).ToList();
			return back.Count == 0 ? null : Math.Round(back.Count(c => c.Received!.Value.Date <= c.Due.Date) * 100m / back.Count, 1);
		}

		public static decimal? RemakeRate(int cases, int remakes) => cases == 0 ? null : Math.Round(remakes * 100m / cases, 1);

		public static DateTime NextRecall(DateTime lastVisit, int intervalMonths) => lastVisit.Date.AddMonths(intervalMonths);

		/// Booked when the patient has a visit on or after today; overdue when the due date passed; otherwise not due.
		public static string RecallState(DateTime due, DateTime? nextVisit, DateTime today) =>
			nextVisit is DateTime n && n.Date >= today.Date ? "Booked" : due.Date < today.Date ? "Overdue" : "NotDue";

		/// A kit is not used from a cycle whose chemical or biological indicator failed; a biological still
		/// incubating may be released on its chemical result (parametric release).
		public static string? UseRefusal(HospitalSterilCycle c) =>
			c.Biological == HospitalIndicator.Fail || c.Chemical == HospitalIndicator.Fail ? "This cycle failed its indicator: its kits are held"
			: c.Chemical != HospitalIndicator.Pass ? "Release the cycle on its chemical indicator first" : null;

		/// "LC-0418", "AC-2026-1102-01": the next number after the highest one with this prefix.
		public static string NextNo(IEnumerable<string> existing, string prefix, int width)
		{
			int n = 0;
			foreach (var no in existing) if (no.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(no.AsSpan(prefix.Length), out var v) && v > n) n = v;
			return prefix + (n + 1).ToString(new string('0', width));
		}
	}
}
