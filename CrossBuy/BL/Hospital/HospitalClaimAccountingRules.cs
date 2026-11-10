using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H20 — insurance accounting rules, with no database (the owner's rulings D2 and G1 on H18).
	// ==========================================================================================

	/// What one answer of a payer posts: the event and its amount (none when the receivable simply stands).
	public sealed record HospitalClaimPostingPlan(string Event, decimal Amount);

	public static class HospitalClaimAccountingRules
	{
		/// The payer's receivable: its own override, else the company's. Null = unmapped (closing is refused).
		public static int? PayerReceivable(int? payerOverride, int? companyDefault) => payerOverride ?? companyDefault;

		/// Why a mapping cannot be saved, or null: the payer receivable is never the patients', and no account plays two
		/// roles (the receivable, the rejections, the adjustments).
		public static string? MappingRefusal(int? payerReceivable, int? rejection, int? adjustment, int? patientReceivable)
		{
			if (payerReceivable != null && payerReceivable == patientReceivable) return "The payer receivable cannot be the patients' receivable account";
			var set = new[] { payerReceivable, rejection, adjustment }.Where(x => x != null).ToList();
			return set.Count != set.Distinct().Count() ? "One account cannot play two roles" : null;
		}

		/// What a payer's answer on a claim line posts.
		///   · an original line accepted in full: nothing; accepted short: an adjustment of the difference;
		///   · an original line rejected: a rejection of the whole of the payer's share;
		///   · a resubmitted line (the original was rejected, and written off): accepted = a recovery of what was
		///     accepted; rejected again = nothing (already written off).
		public static HospitalClaimPostingPlan? Plan(bool accept, decimal insurerAmount, decimal? acceptedAmount, bool resubmission)
		{
			decimal accepted = accept ? acceptedAmount ?? insurerAmount : 0;
			if (resubmission) return accept && accepted > 0 ? new(HospitalClaimEvent.Recovery, accepted) : null;
			if (!accept) return insurerAmount > 0 ? new(HospitalClaimEvent.Rejection, insurerAmount) : null;
			decimal shortBy = insurerAmount - accepted;
			return shortBy > 0 ? new(HospitalClaimEvent.Adjustment, shortBy) : null;
		}

		/// The ledger lines of an event against the payer's receivable (Dr / Cr), the counter account being the
		/// event's own: adjustment and rejection accounts, or the bank for a remittance.
		public static IReadOnlyList<HospitalLedgerLine> Lines(string ev, decimal amount, int payerReceivable, int counter, string memo) => ev switch
		{
			HospitalClaimEvent.Recovery => new[] { new HospitalLedgerLine(payerReceivable, amount, 0, memo), new HospitalLedgerLine(counter, 0, amount, memo) },
			_ => new[] { new HospitalLedgerLine(counter, amount, 0, memo), new HospitalLedgerLine(payerReceivable, 0, amount, memo) },
		};

		/// What the payer still owes on a batch: its share of the original lines, less what was adjusted, written off
		/// or remitted, plus what was recovered.
		public static decimal Outstanding(decimal originalInsurerShare, IEnumerable<(string Event, decimal Amount)> posted)
		{
			decimal o = originalInsurerShare;
			foreach (var (ev, amt) in posted) o += ev == HospitalClaimEvent.Recovery ? amt : -amt;
			return o;
		}

		/// Why a remittance cannot be recorded, or null: a reference, more than zero, never more than is owed.
		public static string? RemittanceRefusal(decimal amount, decimal outstanding, string? reference)
		{
			if (string.IsNullOrWhiteSpace(reference)) return "Enter the payer's payment reference";
			if (amount <= 0) return "A remittance is more than zero";
			if (amount > outstanding) return "This is more than the payer still owes on the batch ({0})";
			return null;
		}
	}
}
