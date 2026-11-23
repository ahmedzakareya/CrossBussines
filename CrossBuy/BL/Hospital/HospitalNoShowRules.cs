using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalNoShowInputs(int PriorNoShows, int PriorVisits, int LeadDays, int? AgeYears, string? Confirmation);

	// ==========================================================================================
	// HOSPITAL H263 — the risk that a booked patient does not come, as POINTS (so the desk can see why), from the
	// factors the literature agrees on: earlier no-shows, how far ahead it was booked, a first visit, young adults,
	// and what the confirmation call found. The hospital's own past appointments then VALIDATE it (AUC and the observed
	// rate per band), the same way the readmission model is validated (H253). With few no-shows on record the
	// validation is shown as not yet reliable, rather than hidden or invented.
	// ==========================================================================================
	public static class HospitalNoShowRules
	{
		public const int ReliableEvents = 30;

		public static int Score(HospitalNoShowInputs i)
		{
			int s = i.PriorNoShows <= 0 ? 0 : i.PriorNoShows == 1 ? 3 : 5;
			s += i.LeadDays <= 2 ? 0 : i.LeadDays <= 14 ? 1 : i.LeadDays <= 30 ? 2 : 3;
			if (i.PriorVisits == 0) s += 1;
			if (i.AgeYears is >= 18 and <= 35) s += 1;
			s += i.Confirmation switch { HospitalConfirmStatus.Confirmed => -2, HospitalConfirmStatus.NoAnswer => 1, _ => 0 };
			return Math.Max(0, s);
		}

		public static string Band(int score) => score <= 2 ? "Low" : score <= 5 ? "Medium" : "High";
		public static readonly string[] Bands = { "Low", "Medium", "High" };

		/// Why the score is what it is (labels for the desk).
		public static IReadOnlyList<string> Reasons(HospitalNoShowInputs i)
		{
			var r = new List<string>();
			if (i.PriorNoShows >= 1) r.Add(i.PriorNoShows == 1 ? "Missed one earlier appointment" : "Missed several earlier appointments");
			if (i.LeadDays > 14) r.Add("Booked long in advance");
			if (i.PriorVisits == 0) r.Add("First visit");
			if (i.AgeYears is >= 18 and <= 35) r.Add("Young adult");
			if (i.Confirmation == HospitalConfirmStatus.NoAnswer) r.Add("Did not answer the confirmation call");
			if (i.Confirmation == HospitalConfirmStatus.Confirmed) r.Add("Confirmed by phone");
			return r;
		}

		/// The observed no-show rate per band of past appointments (cases, no-shows).
		public static IReadOnlyList<(string Band, int Cases, int Events)> Calibration(IReadOnlyList<HospitalRiskCase> cases) =>
			Bands.Select(b => { var x = cases.Where(c => Band(c.Score) == b).ToList(); return (b, x.Count, x.Count(c => c.Outcome)); }).ToList();
	}
}
