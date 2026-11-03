using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H11 — the insurance rules, pure: no database, no clock, no access. The service applies them;
	// the tests pin them (CrossBuy.Tests/HospitalInsuranceRulesTests, database-free).
	// ==========================================================================================

	/// One billed line as a claim sees it: its amount, and whether the payer's list covers it.
	public sealed record HospitalClaimableLine(int? BillLineId, string Code, string Category, decimal Amount, bool Covered);

	/// A bill split between the payer and the patient. Lines carry what the payer is asked for, per line.
	public sealed record HospitalCoverageSplit(decimal Gross, decimal Covered, decimal Copay, decimal PatientShare, decimal InsurerShare, IReadOnlyList<decimal> InsurerPerLine);

	/// A bill as the claim rules see it.
	public sealed record HospitalClaimCandidate(int BillId, int CompanyId, int PatientId, string Status, DateTime? ClosedAt, int? PriceListId, int? CurrencyId);

	public static class HospitalInsuranceRules
	{
		public const string NoPolicy = "no-policy", PolicyInactive = "policy-inactive", PolicyNotStarted = "policy-not-started", PolicyExpired = "policy-expired",
			ContractInactive = "contract-inactive", ContractNotStarted = "contract-not-started", ContractExpired = "contract-expired";

		/// Why a patient is not covered on `day` (null = eligible): the policy, then the payer's contract, must be
		/// active and valid that day. Dates are calendar days; ValidTo is the last covered day.
		public static string? Ineligibility(HospitalPatientPolicy? policy, HospitalPayer? payer, DateTime day)
		{
			var d = day.Date;
			if (policy == null || payer == null) return NoPolicy;
			if (!policy.IsActive) return PolicyInactive;
			if (d < policy.ValidFrom.Date) return PolicyNotStarted;
			if (policy.ValidTo is DateTime pt && d > pt.Date) return PolicyExpired;
			if (!payer.IsActive) return ContractInactive;
			if (d < payer.ValidFrom.Date) return ContractNotStarted;
			if (payer.ValidTo is DateTime ct && d > ct.Date) return ContractExpired;
			return null;
		}

		/// The split of a bill: the patient pays the co-pay on the covered amount (capped when a cap is set) and
		/// every uncovered line in full; the payer owes the rest of the covered amount. The payer's share is
		/// spread over the covered lines in proportion, the last covered line taking the rounding.
		public static HospitalCoverageSplit Split(IReadOnlyList<HospitalClaimableLine> lines, decimal copayPct, decimal copayCap, int decimals)
		{
			decimal R(decimal v) => Math.Round(v, decimals, MidpointRounding.AwayFromZero);
			decimal gross = R(lines.Sum(l => l.Amount));
			decimal covered = R(lines.Where(l => l.Covered).Sum(l => l.Amount));
			decimal copay = R(covered * Math.Clamp(copayPct, 0m, 100m) / 100m);
			if (copayCap > 0 && copay > copayCap) copay = R(copayCap);
			decimal insurer = covered - copay;
			decimal patient = gross - insurer;
			var per = new decimal[lines.Count];
			int lastCovered = -1;
			for (int i = 0; i < lines.Count; i++) if (lines[i].Covered && lines[i].Amount != 0) lastCovered = i;
			decimal given = 0;
			for (int i = 0; i < lines.Count; i++)
			{
				if (!lines[i].Covered || covered == 0) continue;
				per[i] = i == lastCovered ? insurer - given : R(lines[i].Amount * insurer / covered);
				given += per[i];
			}
			return new HospitalCoverageSplit(gross, covered, copay, patient, insurer, per);
		}

		/// The categories a payer wants approved before the service is given, from its comma-separated setting.
		public static IReadOnlySet<string> PreauthCategories(string? csv)
			=> (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

		/// A batch's state once the payer answered: every line accepted → Paid; every line rejected → Rejected;
		/// some of each → Partial; anything still pending → Submitted. A resubmitted line counts as rejected here
		/// (its new chance is in a later batch).
		public static string BatchStatusAfterResponse(IReadOnlyCollection<string> lineStatuses)
		{
			if (lineStatuses.Count == 0 || lineStatuses.Any(s => s == HospitalClaimLineStatus.Pending)) return HospitalClaimBatchStatus.Submitted;
			bool anyAccepted = lineStatuses.Any(s => s == HospitalClaimLineStatus.Accepted);
			bool anyRejected = lineStatuses.Any(s => s is HospitalClaimLineStatus.Rejected or HospitalClaimLineStatus.Resubmitted);
			return anyAccepted && anyRejected ? HospitalClaimBatchStatus.Partial : anyAccepted ? HospitalClaimBatchStatus.Paid : HospitalClaimBatchStatus.Rejected;
		}

		/// The rejection rate of what the payers answered, in percent (null when nothing was answered yet).
		public static decimal? RejectionRate(decimal acceptedAmount, decimal rejectedAmount)
		{
			var decided = acceptedAmount + rejectedAmount;
			return decided <= 0 ? null : Math.Round(rejectedAmount * 100m / decided, 1, MidpointRounding.AwayFromZero);
		}

		public const string OtherCompany = "other-company", NotClosed = "not-closed", OtherPeriod = "other-period", AlreadyClaimed = "already-claimed", OtherCurrency = "other-currency";

		/// Why a bill does not go into a payer's batch for `period` (null = it is claimed). The query that
		/// feeds this is already scoped to the company; the company is checked again here so a row reaching
		/// the rule from anywhere else cannot cross. The policy is the patient's with THIS payer.
		public static string? ClaimRefusal(HospitalClaimCandidate bill, int companyId, string period, int? contractCurrencyId,
			IReadOnlySet<int> claimedBillIds, HospitalPatientPolicy? policy, HospitalPayer payer)
		{
			if (bill.CompanyId != companyId || payer.CompanyId != companyId || (policy != null && (policy.CompanyId != companyId || policy.PatientId != bill.PatientId || policy.PayerId != payer.ID)))
				return OtherCompany;
			if (bill.Status != HospitalBillStatus.Closed || bill.ClosedAt == null) return NotClosed;
			if (Period(bill.ClosedAt.Value) != period) return OtherPeriod;
			if (claimedBillIds.Contains(bill.BillId)) return AlreadyClaimed;
			// a claim carries no currency of its own: it is in the contract's (its list's, or the facility's)
			if (bill.CurrencyId != contractCurrencyId) return OtherCurrency;
			return Ineligibility(policy, payer, bill.ClosedAt.Value);
		}

		/// What one billed line is worth to the claim: the billed price, less the contract's discount when the
		/// bill was priced on the main list (a payer's own list has it built in). VAT is not claimed: the bill
		/// charges it to the patient, on the patient's part only.
		public static decimal LineAmount(decimal quantity, decimal unitPrice, int? billPriceListId, decimal discountPct, int decimals)
		{
			decimal keep = billPriceListId == null ? 1 - Math.Clamp(discountPct, 0m, 100m) / 100m : 1;
			return Math.Round(quantity * unitPrice * keep, decimals, MidpointRounding.AwayFromZero);
		}

		/// Why the payer's answer for a line cannot be recorded (null = it can).
		public static string? ResponseRefusal(string batchStatus, string lineStatus, bool accept, decimal? acceptedAmount, decimal claimed, string? code, string? reason)
		{
			if (batchStatus == HospitalClaimBatchStatus.Draft) return "Submit the batch before recording the payer's answer";
			if (lineStatus != HospitalClaimLineStatus.Pending) return "The payer's answer for this line is already recorded";
			if (accept)
			{
				var a = acceptedAmount ?? claimed;
				return a < 0 || a > claimed ? "The accepted amount is 0 to what was claimed" : null;
			}
			return string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(reason) ? "Enter the payer's rejection code and reason" : null;
		}

		/// Why a line cannot be resubmitted (null = it can): only a rejected line, once, with a justification.
		public static string? ResubmitRefusal(string lineStatus, string? justification)
		{
			if (string.IsNullOrWhiteSpace(justification)) return "Write the justification the payer will read";
			return lineStatus == HospitalClaimLineStatus.Rejected ? null : "Only a rejected line can be resubmitted";
		}

		/// Why the payer's answer to an approval request cannot be recorded (null = it can).
		public static string? PreauthDecisionRefusal(string status, bool approve, string? approvalRef, string? reason)
		{
			if (status != HospitalPreauthStatus.Pending) return "The payer already answered this request";
			if (approve && string.IsNullOrWhiteSpace(approvalRef)) return "Enter the payer's approval reference";
			return !approve && string.IsNullOrWhiteSpace(reason) ? "Enter the payer's reason for the rejection" : null;
		}

		/// The month a bill belongs to for claiming: the month it was closed.
		public static string Period(DateTime closedAt) => closedAt.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

		public static bool IsPeriod(string? s) => s != null && DateTime.TryParseExact(s + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);
	}
}
