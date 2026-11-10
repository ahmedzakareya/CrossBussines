using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H19 — the rules of doctors' fees, with no database: which rule applies, what a part of a line
	// earns, and when the patient's part counts as collected.
	// ==========================================================================================
	public static class HospitalFeeRules
	{
		/// A service's own rule wins over its category's; no rule = the line earns nothing.
		public static HospitalFeeRule? Pick(IEnumerable<HospitalFeeRule> rules, int? serviceId, string category)
		{
			var list = rules.ToList();
			return (serviceId is int s ? list.FirstOrDefault(r => r.ServiceId == s) : null)
				?? list.FirstOrDefault(r => r.ServiceId == null && string.Equals(r.Category, category, StringComparison.Ordinal));
		}

		/// What one part of a line earns: a percent of what that part collected; or, per unit, the amount times the
		/// line's quantity — counted once, on the line's first part (the patient's when he owes anything, else the payer's).
		public static decimal Share(HospitalFeeRule rule, string part, decimal collected, decimal quantity, bool patientOwesAnything, int decimals)
		{
			if (rule.Basis == HospitalFeeBasis.Percent) return Math.Round(collected * rule.Value / 100m, decimals, MidpointRounding.AwayFromZero);
			bool first = patientOwesAnything ? part == HospitalFeePart.Patient : part == HospitalFeePart.Payer;
			return first ? Math.Round(rule.Value * quantity, decimals, MidpointRounding.AwayFromZero) : 0;
		}

		/// A payer's accepted claim line counts toward the fee only when the bill's own split gave that line a payer share,
		/// and only for the payer that split names. A bill closed without a split (self-pay, or before H18) was posted to
		/// the patient in full: a claim found on it is a duplicate, never a second collection.
		public static bool PayerPartCounts(decimal lineInsurerShare, int? splitPayerId, int claimPayerId) =>
			lineInsurerShare > 0 && splitPayerId == claimPayerId;

		/// The day the patient finished paying what he owed on a bill (null = not yet, or he owed nothing).
		public static DateTime? PatientCollectedOn(decimal patientDue, IEnumerable<(DateTime At, decimal Amount)> payments)
		{
			if (patientDue <= 0) return null;
			decimal sum = 0;
			foreach (var p in payments.OrderBy(p => p.At)) { sum += p.Amount; if (sum >= patientDue) return p.At; }
			return null;
		}

		public static string Period(DateTime d) => d.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
		public static bool IsPeriod(string? p) => p is { Length: 7 } && DateTime.TryParseExact(p + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);

		/// Why a rule cannot be kept, or null.
		public static string? RuleRefusal(string category, string basis, decimal value)
		{
			if (!HospitalServiceCategories.IsKnown(category)) return "Choose the category";
			if (!HospitalFeeBasis.All.Contains(basis)) return "Choose how it is calculated";
			if (value < 0) return "A fee cannot be negative";
			if (basis == HospitalFeeBasis.Percent && value > 100) return "A percent is 0 to 100";
			return null;
		}

		/// The approval path: generated → medical director → finance, one step at a time.
		public static string? Next(string status) => status switch
		{
			HospitalFeeStatus.Generated => HospitalFeeStatus.DirectorApproved,
			HospitalFeeStatus.DirectorApproved => HospitalFeeStatus.FinanceApproved,
			_ => null,
		};
	}
}
