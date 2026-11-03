using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H11 — the insurance rules, DATABASE-FREE: no context, no SQLite, no SQL Server. Every case
	// is a pure call on HospitalInsuranceRules (eligibility, the payer/patient split, the batch state after
	// the payer's answer, the rejection rate, the claim month).
	//
	// What must never happen: a patient counted as covered on a day the policy or contract does not cover,
	// a split whose two shares do not add up to the bill, an uncovered line claimed from the payer, a co-pay
	// above its cap, or a batch called paid while a line is still waiting for the payer. Amounts are KWD (3 decimals).
	// ==========================================================================================
	public class HospitalInsuranceRulesTests
	{
		private static readonly DateTime Day = new(2026, 11, 2);

		private static HospitalPayer Payer(bool active = true, DateTime? from = null, DateTime? to = null) =>
			new() { ID = 1, Code = "GIG", Name = "الخليج للتأمين", NameEn = "Gulf Insurance", IsActive = active, ValidFrom = from ?? Day.AddYears(-1), ValidTo = to };

		private static HospitalPatientPolicy Policy(bool active = true, DateTime? from = null, DateTime? to = null) =>
			new() { ID = 1, PayerId = 1, PolicyNo = "KW-100", IsActive = active, ValidFrom = from ?? Day.AddMonths(-6), ValidTo = to };

		private static HospitalClaimableLine L(decimal amount, bool covered = true, string cat = "Lab") => new(null, "X", cat, amount, covered);

		// ------------------------------------------------------------------ eligibility

		[Fact]
		public void Eligible_when_policy_and_contract_both_valid_on_the_day()
			=> Assert.Null(HospitalInsuranceRules.Ineligibility(Policy(), Payer(), Day));

		[Fact]
		public void No_policy_or_no_payer_is_not_covered()
		{
			Assert.Equal(HospitalInsuranceRules.NoPolicy, HospitalInsuranceRules.Ineligibility(null, Payer(), Day));
			Assert.Equal(HospitalInsuranceRules.NoPolicy, HospitalInsuranceRules.Ineligibility(Policy(), null, Day));
		}

		[Fact]
		public void Policy_state_and_dates_are_checked_before_the_contract()
		{
			Assert.Equal(HospitalInsuranceRules.PolicyInactive, HospitalInsuranceRules.Ineligibility(Policy(active: false), Payer(active: false), Day));
			Assert.Equal(HospitalInsuranceRules.PolicyNotStarted, HospitalInsuranceRules.Ineligibility(Policy(from: Day.AddDays(1)), Payer(), Day));
			Assert.Equal(HospitalInsuranceRules.PolicyExpired, HospitalInsuranceRules.Ineligibility(Policy(to: Day.AddDays(-1)), Payer(), Day));
		}

		[Fact]
		public void Contract_state_and_dates_are_checked()
		{
			Assert.Equal(HospitalInsuranceRules.ContractInactive, HospitalInsuranceRules.Ineligibility(Policy(), Payer(active: false), Day));
			Assert.Equal(HospitalInsuranceRules.ContractNotStarted, HospitalInsuranceRules.Ineligibility(Policy(), Payer(from: Day.AddDays(1)), Day));
			Assert.Equal(HospitalInsuranceRules.ContractExpired, HospitalInsuranceRules.Ineligibility(Policy(), Payer(to: Day.AddDays(-1)), Day));
		}

		[Fact]
		public void ValidTo_is_the_last_covered_day_and_time_of_day_is_ignored()
		{
			Assert.Null(HospitalInsuranceRules.Ineligibility(Policy(to: Day), Payer(to: Day), Day.AddHours(23).AddMinutes(59)));
			Assert.Null(HospitalInsuranceRules.Ineligibility(Policy(from: Day.AddHours(18)), Payer(), Day.AddHours(1)));
		}

		// ------------------------------------------------------------------ split

		[Fact]
		public void Copay_is_a_percentage_of_the_covered_amount()
		{
			var s = HospitalInsuranceRules.Split(new[] { L(40.000m), L(60.000m) }, 10, 0, 3);
			Assert.Equal(100.000m, s.Gross);
			Assert.Equal(10.000m, s.Copay);
			Assert.Equal(90.000m, s.InsurerShare);
			Assert.Equal(10.000m, s.PatientShare);
			Assert.Equal(new[] { 36.000m, 54.000m }, s.InsurerPerLine);
		}

		[Fact]
		public void Copay_never_exceeds_its_cap()
		{
			var s = HospitalInsuranceRules.Split(new[] { L(500.000m) }, 20, 25.000m, 3);
			Assert.Equal(25.000m, s.Copay);
			Assert.Equal(475.000m, s.InsurerShare);
		}

		[Fact]
		public void A_zero_cap_means_no_cap()
			=> Assert.Equal(100.000m, HospitalInsuranceRules.Split(new[] { L(500.000m) }, 20, 0, 3).Copay);

		[Fact]
		public void An_uncovered_line_is_the_patients_in_full_and_never_claimed()
		{
			var s = HospitalInsuranceRules.Split(new[] { L(80.000m), L(20.000m, covered: false) }, 10, 0, 3);
			Assert.Equal(80.000m, s.Covered);
			Assert.Equal(72.000m, s.InsurerShare);
			Assert.Equal(28.000m, s.PatientShare);          // 8.000 co-pay + 20.000 uncovered
			Assert.Equal(0m, s.InsurerPerLine[1]);
		}

		[Fact]
		public void The_shares_always_add_up_and_the_last_covered_line_takes_the_rounding()
		{
			var lines = new[] { L(10.001m), L(10.001m), L(10.001m), L(5m, covered: false) };
			var s = HospitalInsuranceRules.Split(lines, 33.3333m, 0, 3);
			Assert.Equal(s.Gross, s.PatientShare + s.InsurerShare);
			Assert.Equal(s.InsurerShare, s.InsurerPerLine.Sum());
			Assert.All(s.InsurerPerLine, v => Assert.Equal(v, Math.Round(v, 3)));
		}

		[Fact]
		public void Copay_percentage_is_clamped_to_0_100()
		{
			Assert.Equal(0m, HospitalInsuranceRules.Split(new[] { L(10m) }, 150, 0, 3).InsurerShare);
			Assert.Equal(10m, HospitalInsuranceRules.Split(new[] { L(10m) }, -5, 0, 3).InsurerShare);
		}

		[Fact]
		public void A_bill_with_nothing_covered_claims_nothing()
		{
			var s = HospitalInsuranceRules.Split(new[] { L(30m, covered: false) }, 10, 0, 3);
			Assert.Equal(0m, s.InsurerShare);
			Assert.Equal(30m, s.PatientShare);
		}

		// ------------------------------------------------------------------ settings, batch state, rate, period

		[Fact]
		public void Preauth_categories_parse_trimmed_and_case_insensitive()
		{
			var c = HospitalInsuranceRules.PreauthCategories(" Radiology , procedure,,");
			Assert.Equal(2, c.Count);
			Assert.Contains("Procedure", c);
			Assert.Empty(HospitalInsuranceRules.PreauthCategories(null));
		}

		[Theory]
		[InlineData(new[] { "Accepted", "Accepted" }, "Paid")]
		[InlineData(new[] { "Rejected", "Rejected" }, "Rejected")]
		[InlineData(new[] { "Accepted", "Rejected" }, "Partial")]
		[InlineData(new[] { "Accepted", "Resubmitted" }, "Partial")]
		[InlineData(new[] { "Accepted", "Pending" }, "Submitted")]
		[InlineData(new string[0], "Submitted")]
		public void Batch_state_follows_the_payers_answers(string[] lines, string expected)
			=> Assert.Equal(expected, HospitalInsuranceRules.BatchStatusAfterResponse(lines));

		[Fact]
		public void Rejection_rate_is_by_amount_and_null_before_any_answer()
		{
			Assert.Null(HospitalInsuranceRules.RejectionRate(0, 0));
			Assert.Equal(6.1m, HospitalInsuranceRules.RejectionRate(93.9m, 6.1m));
			Assert.Equal(100m, HospitalInsuranceRules.RejectionRate(0, 5));
		}

		[Fact]
		public void The_claim_month_is_the_month_the_bill_closed()
		{
			Assert.Equal("2026-10", HospitalInsuranceRules.Period(new DateTime(2026, 10, 31, 23, 59, 0)));
			Assert.True(HospitalInsuranceRules.IsPeriod("2026-10"));
			Assert.False(HospitalInsuranceRules.IsPeriod("2026-13"));
			Assert.False(HospitalInsuranceRules.IsPeriod("10-2026"));
			Assert.False(HospitalInsuranceRules.IsPeriod(null));
		}
	}
}
