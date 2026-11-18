using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H182 — the payer's dental contract terms. What must never happen: the quotation promising a payer share the
	// contract does not pay — an excluded procedure, major work inside its waiting period, a procedure repeated inside its
	// frequency or over its yearly count — or a line uncovered without saying why.
	// ==========================================================================================
	public class HospitalDentalContractTests
	{
		private static readonly DateTime Day = new(2026, 11, 18);

		[Fact]
		public void Each_contract_rule_names_why_it_does_not_pay()
		{
			var none = Array.Empty<(int?, DateTime)>();
			Assert.Equal(HospitalDentalRules.Excluded, HospitalDentalRules.Uncovered(new HospitalDentalBenefitRule { Excluded = true }, 0, null, HospitalDentalCategory.Cosmetic, null, Day, none));
			Assert.Equal(HospitalDentalRules.Waiting, HospitalDentalRules.Uncovered(null, 6, Day.AddMonths(-3), HospitalDentalCategory.Prosthetic, 36, Day, none));
			Assert.Null(HospitalDentalRules.Uncovered(null, 6, Day.AddMonths(-7), HospitalDentalCategory.Prosthetic, 36, Day, none));
			Assert.Null(HospitalDentalRules.Uncovered(null, 6, Day.AddMonths(-3), HospitalDentalCategory.Preventive, null, Day, none));   // the waiting period is for major work
			var cleaning = new HospitalDentalBenefitRule { FrequencyMonths = 6 };
			Assert.Equal(HospitalDentalRules.Frequency, HospitalDentalRules.Uncovered(cleaning, 0, null, HospitalDentalCategory.Preventive, null, Day, new (int?, DateTime)[] { (null, Day.AddMonths(-4)) }));
			Assert.Null(HospitalDentalRules.Uncovered(cleaning, 0, null, HospitalDentalCategory.Preventive, null, Day, new (int?, DateTime)[] { (null, Day.AddMonths(-7)) }));
			var crown = new HospitalDentalBenefitRule { FrequencyMonths = 60 };
			Assert.Null(HospitalDentalRules.Uncovered(crown, 0, null, HospitalDentalCategory.Prosthetic, 36, Day, new (int?, DateTime)[] { (46, Day.AddYears(-1)) }));   // another tooth
			Assert.Equal(HospitalDentalRules.Frequency, HospitalDentalRules.Uncovered(crown, 0, null, HospitalDentalCategory.Prosthetic, 36, Day, new (int?, DateTime)[] { (36, Day.AddYears(-1)) }));
			var bitewing = new HospitalDentalBenefitRule { PerYear = 2 };
			Assert.Equal(HospitalDentalRules.PerYear, HospitalDentalRules.Uncovered(bitewing, 0, null, HospitalDentalCategory.Diagnostic, null, Day, new (int?, DateTime)[] { (null, new DateTime(2026, 2, 1)), (null, new DateTime(2026, 6, 1)) }));
		}

		[Fact]
		public void The_quotation_pays_nothing_on_an_uncovered_line_and_keeps_the_reason()
		{
			var b = new HospitalDentalBenefit { PreventivePct = 100, ProstheticPct = 50 };
			var q = HospitalDentalRules.Quote(new[]
			{
				new HospitalDentalRules.QuoteInput(1, 25m, HospitalDentalCategory.Preventive, false, false, false, HospitalDentalRules.Frequency),
				new HospitalDentalRules.QuoteInput(2, 200m, HospitalDentalCategory.Prosthetic, false, false, false),
			}, b, 0, 3);
			Assert.Equal((0m, HospitalDentalRules.Frequency), (q.Lines[0].PayerShare, q.Lines[0].Uncovered));
			Assert.Equal(100m, q.Lines[1].PayerShare);
			Assert.Equal(100m, q.PayerShare);
		}

		[Fact]
		public void A_rule_is_bounded()
		{
			Assert.Null(HospitalDentalRules.RuleRefusal(6, 2));
			Assert.Equal("A frequency is 1 to 120 months", HospitalDentalRules.RuleRefusal(0, null));
			Assert.Equal("A yearly count is 1 to 52", HospitalDentalRules.RuleRefusal(null, 60));
		}

		[Fact]
		public void The_rule_table_matches_its_slice()
		{
			var model = HospitalSliceParity.Model(HospitalDentalModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h182_dental_contract_rules.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalDentalBenefitRule), "HospitalDentalBenefitRules");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalDentalBenefitRule), "HospitalDentalBenefitRules");
		}
	}
}
