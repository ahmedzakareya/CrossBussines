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

		// H169: a cycle is RELEASED by a person, not just "not failed". Release rests on the physical record of the run
		// (time at temperature, temperature, pressure, and the operator's word that the programme was met), the chemical
		// indicator, and — for a load holding implantable items — the biological indicator (no parametric release for an
		// implant load). A failure found after release recalls the cycle; a recalled cycle is never used again.
		public const int MaxExposureMinutes = 240;

		public static bool Held(HospitalSterilCycle c) =>
			c.RecalledAt != null || c.PhysicalOk == false || c.Chemical == HospitalIndicator.Fail || c.Biological == HospitalIndicator.Fail;

		/// The physical record of a run: exposure 1–240 min, temperature 100–150 °C, pressure (optional) 0–400 kPa, and
		/// the operator's verdict. Recorded once, before release.
		public static string? PhysicalRefusal(HospitalSterilCycle c, int? minutes, decimal? temperature, decimal? pressure, bool? ok) =>
			c.PhysicalOk != null ? "The physical readings of this cycle are already recorded"
			: c.ReleasedAt != null || c.RecalledAt != null ? "This cycle is already released or recalled"
			: minutes is not (>= 1 and <= MaxExposureMinutes) ? "Enter the exposure time in minutes"
			: temperature is not (>= 100m and <= 150m) ? "Enter the sterilisation temperature (100–150 °C)"
			: pressure is < 0m or > 400m ? "The pressure is out of range"
			: ok == null ? "Say whether the run met its programme" : null;

		/// Released by a person once the run is proven: physical readings met, chemical passed, biological passed when the
		/// load holds implantable items; never a held or recalled cycle, never twice.
		public static string? ReleaseRefusal(HospitalSterilCycle c) =>
			c.RecalledAt != null ? "This cycle was recalled"
			: c.ReleasedAt != null ? "This cycle is already released"
			: Held(c) ? "This cycle failed its checks: its kits are held"
			: c.PhysicalOk == null ? "Record the physical readings of the run first"
			: c.Chemical != HospitalIndicator.Pass ? "Release the cycle on its chemical indicator first"
			: c.ImplantLoad && c.Biological != HospitalIndicator.Pass ? "A load with implantable items waits for its biological indicator" : null;

		/// A kit is used only from a released cycle that is not held or recalled, and only from a pack checked intact
		/// (wrap, seal, dry, its own indicator turned) just before it is opened.
		public static string? UseRefusal(HospitalSterilCycle c, bool packIntact = true) =>
			Held(c) ? "This cycle failed its checks or was recalled: its kits are held"
			: c.ReleasedAt == null ? "This cycle is not released yet"
			: !packIntact ? "A pack that is not intact is not used: send it back to be re-sterilised" : null;

		/// A failure recorded after release recalls the cycle: its kits already reached patients.
		public static bool RecallsOnFail(HospitalSterilCycle c) => c.ReleasedAt != null && c.RecalledAt == null;

		/// "LC-0418", "AC-2026-1102-01": the next number after the highest one with this prefix.
		public static string NextNo(IEnumerable<string> existing, string prefix, int width)
		{
			int n = 0;
			foreach (var no in existing) if (no.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(no.AsSpan(prefix.Length), out var v) && v > n) n = v;
			return prefix + (n + 1).ToString(new string('0', width));
		}
	}
}
