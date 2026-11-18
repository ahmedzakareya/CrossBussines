using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H187 — the lab case's two checks and each lab's record (from the dental review: "a lab case is a courier
	// tracker; no infection control on the impression, no check of the work before the patient's chair, no lab
	// performance"). The rules, without a database:
	//   · the prescription names the impression: a digital scan, or a conventional impression — which leaves for the lab
	//     only disinfected, with the disinfectant recorded (every time it is sent, a remake's new impression too);
	//   · the work is checked when it arrives — fit on the model, margins, contacts, occlusion, shade. All five pass, or it
	//     is not received: what fails goes back as a remake. It is not fitted before that check (a case received before
	//     H187 takes the check on its way to the chair); a remake clears the check, its new work is checked again;
	//   · each lab's record: its cases, on time, remakes and those of its own fault, and the days from sending to receipt.
	// ==========================================================================================
	public static class HospitalLabImpression
	{
		public const string Digital = "Digital";
		public const string Conventional = "Conventional";
		public static readonly IReadOnlyList<string> All = new[] { Digital, Conventional };
	}

	public sealed record HospitalLabPerformance(int LabId, string Name, string NameEn, int Cases, decimal? OnTimeRate, decimal? RemakeRate, int LabFaultRemakes, decimal? AvgTurnaroundDays);

	public static class HospitalDentalLabQcRules
	{
		public static readonly IReadOnlyList<string> QcItems = new[] { "Fit", "Margins", "Contacts", "Occlusion", "Shade" };
		public const string QcFailed = "The work passes every check to be received: send what fails back as a remake";

		public static string? ImpressionRefusal(string? kind) => HospitalLabImpression.All.Contains(kind ?? "") ? null : "Choose the impression: digital scan or conventional";

		/// Sending to the lab: a conventional impression leaves disinfected.
		public static string? SendRefusal(string? impression, string? disinfectant) =>
			impression == HospitalLabImpression.Conventional && string.IsNullOrWhiteSpace(disinfectant) ? "Record the disinfectant the impression was treated with before it leaves" : null;

		public static bool QcPassed(IEnumerable<string>? passed) => passed != null && QcItems.All(i => passed.Contains(i));

		/// Received and Fitted need the check; Fitted only when it was not done on receipt.
		public static string? QcRefusal(string to, DateTime? qcAt, IEnumerable<string>? passed) =>
			(to == HospitalLabCaseStage.Received || (to == HospitalLabCaseStage.Fitted && qcAt == null)) && !QcPassed(passed) ? QcFailed : null;

		public static IReadOnlyList<HospitalLabPerformance> Performance(IEnumerable<HospitalDentalLab> labs,
			IReadOnlyList<(int LabId, DateTime Due, DateTime? SentAt, DateTime? ReceivedAt, int Remakes, string? Fault)> cases, DateTime today)
		{
			return labs.Select(l =>
			{
				var mine = cases.Where(c => c.LabId == l.ID).ToList();
				var turn = mine.Where(c => c.SentAt != null && c.ReceivedAt != null && c.ReceivedAt >= c.SentAt).Select(c => (decimal)(c.ReceivedAt!.Value - c.SentAt!.Value).TotalDays).ToList();
				return new HospitalLabPerformance(l.ID, l.Name, l.NameEn, mine.Count,
					HospitalDentalPracticeRules.OnTimeRate(mine.Select(c => (c.Due, c.ReceivedAt)), today),
					HospitalDentalPracticeRules.RemakeRate(mine.Count, mine.Sum(c => c.Remakes)),
					mine.Count(c => c.Fault == "Lab"),
					turn.Count == 0 ? null : Math.Round(turn.Average(), 1));
			}).Where(p => p.Cases > 0).OrderByDescending(p => p.Cases).ToList();
		}
	}
}
