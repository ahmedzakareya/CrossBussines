using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H12 — the estimate rules, DATABASE-FREE: pure calls on HospitalEstimateRules (which splits
	// through H11's HospitalInsuranceRules.Split). No context, no SQLite, no SQL Server.
	//
	// What must never happen: a package's included nights charged again as a stay, a stay without nights
	// priced, an uncovered line put on the payer, a self-pay patient given a payer share, the shares not
	// adding up to the estimate, or a deposit short of the patient's expected share. Amounts are KWD.
	// ==========================================================================================
	public class HospitalEstimateRulesTests
	{
		private static HospitalPackage Cath(int nights = 2) => new() { ID = 1, Code = "CATH1", Name = "قسطرة", NameEn = "Cath + one stent", ServiceId = 40, Price = 1800m, IncludedNights = nights };

		private static HospitalEstimateDraftLine Svc(decimal qty, decimal price, bool covered = true, string cat = "Lab") =>
			new(HospitalEstimateRules.KindService, 7, "LAB-1", cat, "تحليل", "Test", qty, price, 0, covered);

		[Fact]
		public void Extra_nights_are_what_the_stay_exceeds_the_package_by()
		{
			Assert.Equal(1, HospitalEstimateRules.ExtraNights(3, 2));
			Assert.Equal(0, HospitalEstimateRules.ExtraNights(2, 2));
			Assert.Equal(0, HospitalEstimateRules.ExtraNights(1, 2));
			Assert.Equal(3, HospitalEstimateRules.ExtraNights(3, -1));   // a bad setting never adds nights
		}

		[Fact]
		public void A_package_is_its_fixed_price_and_only_the_extra_nights_are_a_stay()
		{
			var lines = HospitalEstimateRules.Lines(Cath(2), true, "pvt", 45m, 3, Array.Empty<HospitalEstimateDraftLine>(), 3);
			Assert.Equal(2, lines.Count);
			Assert.Equal(HospitalEstimateRules.KindPackage, lines[0].Kind);
			Assert.Equal(1800m, lines[0].Amount);
			Assert.Equal(HospitalEstimateRules.KindStay, lines[1].Kind);
			Assert.Equal(1m, lines[1].Quantity);
			Assert.Equal(45m, lines[1].Amount);
			Assert.Equal("ACC-PVT", lines[1].Code);
		}

		[Fact]
		public void A_stay_within_the_package_adds_no_line()
			=> Assert.Single(HospitalEstimateRules.Lines(Cath(3), true, "pvt", 45m, 3, Array.Empty<HospitalEstimateDraftLine>(), 3));

		[Fact]
		public void Without_a_package_every_night_is_a_stay_and_services_are_priced_by_quantity()
		{
			var lines = HospitalEstimateRules.Lines(null, true, "pvt", 45m, 2, new[] { Svc(3, 12.5m) }, 3);
			Assert.Equal(new[] { 90m, 37.5m }, lines.Select(l => l.Amount));
			Assert.Equal(HospitalEstimateRules.KindService, lines[1].Kind);
		}

		[Fact]
		public void No_room_rate_or_no_nights_means_no_stay_line()
		{
			Assert.Empty(HospitalEstimateRules.Lines(null, true, null, 0, 3, Array.Empty<HospitalEstimateDraftLine>(), 3));
			Assert.Empty(HospitalEstimateRules.Lines(null, true, "pvt", 45m, 0, Array.Empty<HospitalEstimateDraftLine>(), 3));
			Assert.Empty(HospitalEstimateRules.Lines(null, true, "pvt", 45m, -2, Array.Empty<HospitalEstimateDraftLine>(), 3));
		}

		[Fact]
		public void An_insured_estimate_splits_like_a_claim_and_the_shares_add_up()
		{
			var lines = HospitalEstimateRules.Lines(Cath(2), true, "pvt", 45m, 3, new[] { Svc(1, 55m, covered: false) }, 3);
			var t = HospitalEstimateRules.Totals(lines, 10, 0, insured: true, marginPct: 20, decimals: 3);
			Assert.Equal(1900m, t.Gross);
			Assert.Equal(1660.5m, t.PayerShare);            // (1800 + 45) less 10% co-pay
			Assert.Equal(239.5m, t.PatientShare);           // 184.5 co-pay + 55 uncovered
			Assert.Equal(t.Gross, t.PayerShare + t.PatientShare);
			Assert.Equal(0m, t.PayerPerLine[2]);            // the uncovered line is never the payer's
			Assert.Equal(287.4m, t.Deposit);                // 239.5 + 20%
		}

		[Fact]
		public void A_self_pay_estimate_has_no_payer_share_whatever_the_contract_says()
		{
			var lines = HospitalEstimateRules.Lines(Cath(2), true, "pvt", 45m, 2, Array.Empty<HospitalEstimateDraftLine>(), 3);
			var t = HospitalEstimateRules.Totals(lines, 10, 0, insured: false, marginPct: 0, decimals: 3);
			Assert.Equal(0m, t.PayerShare);
			Assert.Equal(1800m, t.PatientShare);
			Assert.Equal(1800m, t.Deposit);
		}

		[Fact]
		public void The_copay_cap_holds_on_an_estimate_too()
		{
			var lines = HospitalEstimateRules.Lines(Cath(2), true, null, 0, 0, Array.Empty<HospitalEstimateDraftLine>(), 3);
			var t = HospitalEstimateRules.Totals(lines, 20, 150m, insured: true, marginPct: 0, decimals: 3);
			Assert.Equal(150m, t.PatientShare);
			Assert.Equal(1650m, t.PayerShare);
		}

		[Fact]
		public void The_deposit_rounds_up_never_short_and_ignores_a_negative_margin()
		{
			var lines = new[] { Svc(1, 10.001m, covered: false) };
			var priced = HospitalEstimateRules.Lines(null, true, null, 0, 0, lines, 3);
			Assert.Equal(12.002m, HospitalEstimateRules.Totals(priced, 0, 0, false, 20, 3).Deposit);   // 12.0012 → up to 12.002
			Assert.Equal(10.001m, HospitalEstimateRules.Totals(priced, 0, 0, false, -50, 3).Deposit);
		}

		[Fact]
		public void An_issued_estimate_expires_after_its_validity()
		{
			var issued = new DateTime(2026, 11, 2, 18, 0, 0);
			Assert.False(HospitalEstimateRules.IsExpired(issued, issued.AddDays(HospitalEstimateRules.ValidDays)));
			Assert.True(HospitalEstimateRules.IsExpired(issued, issued.AddDays(HospitalEstimateRules.ValidDays + 1)));
		}
	}
}
