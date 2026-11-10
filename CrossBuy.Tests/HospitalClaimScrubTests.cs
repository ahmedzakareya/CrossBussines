using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H62 — claim scrubbing, with no database: what is an error (blocks the batch) and what is a warning,
	// the cover checked on the first AND the last day, the diagnosis by visit or stay, double billing, the late claim,
	// and the submit refusal counting claims (not issues).
	// ==========================================================================================
	public class HospitalClaimScrubTests
	{
		private static readonly DateTime Today = new(2026, 11, 8);
		private static HospitalPayer Payer() => new() { ID = 1, IsActive = true, ValidFrom = new(2026, 1, 1), ValidTo = new(2026, 12, 31) };
		private static HospitalPatientPolicy Policy() => new() { ID = 2, PayerId = 1, PolicyNo = "GIG-1", IsActive = true, ValidFrom = new(2026, 1, 1), ValidTo = new(2026, 12, 31) };
		private static HospitalBill Bill() => new() { ID = 3, BillNo = "BILL-1", EncounterId = 4, OpenedAt = new(2026, 11, 2), ClosedAt = new(2026, 11, 3) };
		private static HospitalScrubFacts Clean(Func<HospitalScrubFacts, HospitalScrubFacts>? f = null)
		{
			var x = new HospitalScrubFacts(9, Bill(), Policy(), Payer(), "2850", Array.Empty<string>(), Array.Empty<string>(), HospitalScrubDiagnosis.Present, Array.Empty<(string, DateTime, string)>());
			return f == null ? x : f(x);
		}
		private static IReadOnlyList<HospitalScrubIssue> Check(HospitalScrubFacts f) => HospitalClaimScrubRules.Check(f, Today);
		private static bool Has(IReadOnlyList<HospitalScrubIssue> l, string sev, string msg) => l.Any(i => i.Severity == sev && i.Message == msg);

		[Fact]
		public void A_clean_claim_has_nothing_to_say()
		{
			Assert.Empty(Check(Clean()));
			Assert.Null(HospitalClaimScrubRules.SubmitRefusal(Check(Clean()), out int n));
			Assert.Equal(0, n);
		}

		[Fact]
		public void Missing_approval_member_number_and_code_are_errors()
		{
			var l = Check(Clean(f => f with { Unapproved = new[] { "CPT-47562" }, UncodedLines = new[] { "Dressing" } }));
			Assert.True(Has(l, HospitalScrubSeverity.Error, "This line needs the payer's prior approval and has none: {0}"));
			Assert.True(Has(l, HospitalScrubSeverity.Error, "A line has no service code: {0}"));
			var p = Policy(); p.PolicyNo = " ";
			Assert.True(Has(Check(Clean(f => f with { Policy = p })), HospitalScrubSeverity.Error, "The member number is missing"));
		}

		[Fact]
		public void The_cover_must_hold_on_the_first_and_the_last_day()
		{
			// the policy ends on the 2nd: the stay closed on the 3rd is not covered to its end
			var p = Policy(); p.ValidTo = new(2026, 11, 2);
			Assert.Contains(Check(Clean(f => f with { Policy = p })), i => i.Severity == HospitalScrubSeverity.Error && i.Message == "inelig:" + HospitalInsuranceRules.PolicyExpired);
			var y = Payer(); y.ValidFrom = new(2026, 11, 3);   // the contract starts on the last day only
			Assert.Contains(Check(Clean(f => f with { Payer = y })), i => i.Severity == HospitalScrubSeverity.Error && i.Message == "inelig:" + HospitalInsuranceRules.ContractNotStarted);
		}

		[Fact]
		public void The_diagnosis_by_visit_or_stay()
		{
			Assert.True(Has(Check(Clean(f => f with { Diagnosis = HospitalScrubDiagnosis.VisitMissing })), HospitalScrubSeverity.Error, "The visit has no diagnosis"));
			Assert.True(Has(Check(Clean(f => f with { Diagnosis = HospitalScrubDiagnosis.StayMissing })), HospitalScrubSeverity.Error, "The stay has no final diagnosis (write the discharge summary)"));
			Assert.True(Has(Check(Clean(f => f with { Diagnosis = HospitalScrubDiagnosis.NoPrimary })), HospitalScrubSeverity.Warning, "No diagnosis is marked primary"));
			Assert.True(Has(Check(Clean(f => f with { Diagnosis = HospitalScrubDiagnosis.NotLinked })), HospitalScrubSeverity.Warning, "The bill is not linked to a visit or a stay: the payer may ask for the diagnosis"));
		}

		[Fact]
		public void Warnings_never_block_the_batch()
		{
			var b = Bill(); b.ClosedAt = Today.AddDays(-(HospitalClaimScrubRules.LateDays + 1)); b.OpenedAt = b.ClosedAt.Value;
			var l = Check(Clean(f => f with { Bill = b, NationalId = null, Duplicates = new[] { ("CPT-44970", new DateTime(2026, 11, 2), "BILL-7") } }));
			Assert.True(Has(l, HospitalScrubSeverity.Warning, "The patient has no national ID"));
			Assert.True(Has(l, HospitalScrubSeverity.Warning, "Possibly billed twice: {0} on {1} is also in bill {2}"));
			Assert.True(Has(l, HospitalScrubSeverity.Warning, "Closed more than {0} days ago: it may be past the payer's submission window"));
			Assert.All(l, i => Assert.Equal(HospitalScrubSeverity.Warning, i.Severity));
			Assert.Null(HospitalClaimScrubRules.SubmitRefusal(l, out _));
		}

		[Fact]
		public void The_submit_refusal_counts_claims_not_issues()
		{
			var a = Check(Clean(f => f with { Unapproved = new[] { "X", "Y" }, Diagnosis = HospitalScrubDiagnosis.VisitMissing }));   // three errors, one claim
			var b = Check(Clean(f => f with { ClaimId = 10, UncodedLines = new[] { "Z" } }));
			Assert.Equal("Fix or hold the {0} claims with errors before submitting", HospitalClaimScrubRules.SubmitRefusal(a.Concat(b), out int n));
			Assert.Equal(2, n);
		}

		[Fact]
		public void A_rejected_line_earns_the_doctor_nothing_because_fees_are_on_what_was_collected()
		{
			var rule = new HospitalFeeRule { Basis = HospitalFeeBasis.Percent, Value = 30 };
			Assert.Equal(0m, HospitalFeeRules.Share(rule, HospitalFeePart.Payer, 0m, 1, false, 3));   // nothing collected from the payer
			Assert.Equal(30m, HospitalFeeRules.Share(rule, HospitalFeePart.Payer, 100m, 1, false, 3));
		}
	}
}
