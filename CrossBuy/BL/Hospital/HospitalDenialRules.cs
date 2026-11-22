using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalDenialCauseStat(string Cause, int Count, decimal Amount, int Overturned, int Closed)
	{
		/// Of the items closed, the share resubmitted and then paid.
		public decimal? OverturnPct => Closed == 0 ? null : Math.Round(Overturned * 100m / Closed, 0, MidpointRounding.AwayFromZero);
	}

	// ==========================================================================================
	// HOSPITAL H240 — the denial work-queue's rules, free of the database.
	//   · The ROOT CAUSE is suggested from the line itself (no pre-authorisation → Authorization) and then the payer's
	//     code and reason, by keyword in English or Arabic; the biller can correct it.
	//   · The APPEAL DEADLINE is the payer's answer day plus the appeal window (30 days by default).
	//   · An item is OVERDUE past its deadline while still open or working.
	// ==========================================================================================
	public static class HospitalDenialRules
	{
		public const int DefaultWindowDays = 30;

		private static readonly (string Cause, string[] Words)[] Keys =
		{
			(HospitalDenialCause.Duplicate, new[] { "duplicate", "already paid", "already billed", "مكرر", "تكرار" }),
			(HospitalDenialCause.Timely, new[] { "late", "timely", "time limit", "submission period", "expired claim", "متأخر", "انتهاء مدة" }),
			(HospitalDenialCause.Authorization, new[] { "preauth", "pre-auth", "prior auth", "authori", "approval", "موافقة مسبقة", "تصريح" }),
			(HospitalDenialCause.Eligibility, new[] { "eligib", "not covered", "coverage", "policy", "member", "inactive", "expired", "غير مشمول", "التغطية", "الوثيقة" }),
			(HospitalDenialCause.Coding, new[] { "code", "coding", "icd", "cpt", "mismatch", "diagnosis", "ترميز", "رمز", "التشخيص" }),
			(HospitalDenialCause.Necessity, new[] { "necess", "not indicated", "not justified", "unnecessary", "ضرورة", "غير مبرر" }),
			(HospitalDenialCause.Documentation, new[] { "document", "report", "attach", "missing", "record", "مستند", "تقرير", "مرفق" }),
			(HospitalDenialCause.Pricing, new[] { "price", "tariff", "rate", "exceed", "limit", "amount", "سعر", "التعرفة", "يتجاوز" }),
		};

		public static string SuggestCause(bool missingPreauth, string? code, string? reason)
		{
			if (missingPreauth) return HospitalDenialCause.Authorization;
			var text = ((code ?? "") + " " + (reason ?? "")).ToLowerInvariant();
			foreach (var (cause, words) in Keys)
				if (words.Any(w => text.Contains(w))) return cause;
			return HospitalDenialCause.Other;
		}

		public static DateTime DueDate(DateTime decidedDay, int? windowDays) => decidedDay.Date.AddDays(windowDays is > 0 ? windowDays.Value : DefaultWindowDays);

		public static bool Overdue(string status, DateTime due, DateTime today) => HospitalDenialStatus.Active(status) && today.Date > due.Date;

		public static string? UpdateRefusal(string current, string? cause, string? status, string? note)
		{
			if (!HospitalDenialStatus.Active(current)) return "This denial is already closed";
			if (cause != null && !HospitalDenialCause.All.Contains(cause)) return "Choose the root cause";
			if (status != null && status is not (HospitalDenialStatus.Open or HospitalDenialStatus.Working or HospitalDenialStatus.NotPursued)) return "not_found";
			if (status == HospitalDenialStatus.NotPursued && string.IsNullOrWhiteSpace(note)) return "Write why it is not pursued";
			return (note ?? "").Trim().Length > 500 ? "The note is 500 characters at most" : null;
		}

		/// Per root cause: how many, how much was denied, and of those closed how many were overturned (resubmitted and paid).
		public static IReadOnlyList<HospitalDenialCauseStat> ByCause(IEnumerable<(string Cause, decimal Amount, string Status, bool Paid)> items) =>
			HospitalDenialCause.All.Select(c =>
			{
				var x = items.Where(i => i.Cause == c).ToList();
				return new HospitalDenialCauseStat(c, x.Count, x.Sum(i => i.Amount), x.Count(i => i.Status == HospitalDenialStatus.Resubmitted && i.Paid), x.Count(i => !HospitalDenialStatus.Active(i.Status)));
			}).Where(s => s.Count > 0).OrderByDescending(s => s.Amount).ToList();
	}
}
