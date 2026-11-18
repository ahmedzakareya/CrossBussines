using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H188 — the specialty calculators of the template engine. What must never happen: a QTc off its formula,
	// a thyroid nodule sent for biopsy (or left) against the ACR size thresholds, a PASI weighted wrongly, a T-score
	// classed across the WHO cut-offs, or a calculator fed a wrong number of inputs.
	// ==========================================================================================
	public class HospitalSpecialtyCalculatorTests
	{
		[Fact]
		public void The_QTc_follows_Bazett_and_Fridericia()
		{
			Assert.Equal(400m, HospitalClinicalCalc.QtcBazett(400, 60));        // RR 1 s: no correction
			Assert.Equal(400m, HospitalClinicalCalc.QtcFridericia(400, 60));
			Assert.Equal(516m, HospitalClinicalCalc.QtcBazett(400, 100));       // 400 / √0.6
			Assert.Equal(474m, HospitalClinicalCalc.QtcFridericia(400, 100));   // 400 / ∛0.6
			Assert.Null(HospitalClinicalCalc.QtcBazett(400, null));
			Assert.Null(HospitalClinicalCalc.QtcBazett(400, 10));               // not a heart rate
		}

		[Fact]
		public void TiRads_levels_and_size_thresholds_follow_ACR_2017()
		{
			Assert.Equal(new int?[] { 1, 1, 2, 3, 4, 4, 5, 5 }, new decimal?[] { 0, 1, 2, 3, 4, 6, 7, 12 }.Select(HospitalClinicalCalc.TiRadsLevel).ToArray());
			Assert.Equal("None", HospitalClinicalCalc.TiRadsAdvice(2, 4.0m));
			Assert.Equal("FNA", HospitalClinicalCalc.TiRadsAdvice(3, 2.5m));
			Assert.Equal("FollowUp", HospitalClinicalCalc.TiRadsAdvice(3, 1.5m));
			Assert.Equal("None", HospitalClinicalCalc.TiRadsAdvice(3, 1.4m));
			Assert.Equal("FNA", HospitalClinicalCalc.TiRadsAdvice(4, 1.5m));
			Assert.Equal("FollowUp", HospitalClinicalCalc.TiRadsAdvice(4, 1.0m));
			Assert.Equal("FNA", HospitalClinicalCalc.TiRadsAdvice(5, 1.0m));
			Assert.Equal("FollowUp", HospitalClinicalCalc.TiRadsAdvice(5, 0.5m));
			Assert.Equal("None", HospitalClinicalCalc.TiRadsAdvice(5, 0.4m));
		}

		[Fact]
		public void Pasi_weights_each_region()
		{
			var v = new decimal?[] { 2, 2, 2, 2, 3, 3, 2, 2, 4, 3, 3, 3, 5, 4, 3, 2 };
			Assert.Equal(34.2m, HospitalClinicalCalc.Pasi(v));   // 1.2 + 4.2 + 10.8 + 18.0
			Assert.Equal(72m, HospitalClinicalCalc.Pasi(new decimal?[] { 6, 4, 4, 4, 6, 4, 4, 4, 6, 4, 4, 4, 6, 4, 4, 4 }));
			Assert.Null(HospitalClinicalCalc.Pasi(v.Take(15).ToList()));
			var bad = (decimal?[])v.Clone(); bad[0] = 7; Assert.Null(HospitalClinicalCalc.Pasi(bad));   // area is 0–6
			var gap = (decimal?[])v.Clone(); gap[5] = null; Assert.Null(HospitalClinicalCalc.Pasi(gap));
		}

		[Fact]
		public void The_T_score_is_classed_by_the_WHO_cut_offs()
		{
			Assert.Equal("Normal", HospitalClinicalCalc.TScoreClass(-1.0m));
			Assert.Equal("Osteopenia", HospitalClinicalCalc.TScoreClass(-1.1m));
			Assert.Equal("Osteopenia", HospitalClinicalCalc.TScoreClass(-2.4m));
			Assert.Equal("Osteoporosis", HospitalClinicalCalc.TScoreClass(-2.5m));
			Assert.Null(HospitalClinicalCalc.TScoreClass(null));
		}

		[Fact]
		public void A_calculator_is_given_exactly_its_inputs()
		{
			HospitalConceptInput C(string calc, string inputs) => new() { Code = "X-1", Name = "س", NameEn = "X", ValueType = "Calculated", Calculator = calc, CalcInputs = inputs, Decimals = 0, CodeSystem = "Local" };
			Assert.Null(HospitalConceptRules.Refusal(C(HospitalConceptCalculator.QtcBazett, "QT,HR")));
			Assert.Equal("This calculator reads two codes, in order", HospitalConceptRules.Refusal(C(HospitalConceptCalculator.QtcBazett, "QT")));
			Assert.Equal("This calculator reads one code", HospitalConceptRules.Refusal(C(HospitalConceptCalculator.TScoreClass, "T,Z")));
			Assert.StartsWith("This calculator reads sixteen codes", HospitalConceptRules.Refusal(C(HospitalConceptCalculator.Pasi, "A,B,C,D")));
			Assert.Null(HospitalConceptRules.Refusal(C(HospitalConceptCalculator.Pasi, string.Join(",", Enumerable.Range(1, 16).Select(n => "P" + n)))));
			foreach (var k in HospitalConceptCalculator.All) Assert.True(HospitalConceptCalculator.InputsNeeded(k) >= 1, k);
		}

		[Fact]
		public void QuickDash_averages_the_answered_items_and_needs_ten()
		{
			Assert.Equal(0m, HospitalClinicalCalc.QuickDash(Enumerable.Repeat((decimal?)1, 11).ToList()));
			Assert.Equal(100m, HospitalClinicalCalc.QuickDash(Enumerable.Repeat((decimal?)5, 11).ToList()));
			var v = new decimal?[] { 2, 3, 2, 4, 1, 2, 3, 2, 2, 3, null };   // ten answered: (24/10 − 1) × 25
			Assert.Equal(35m, HospitalClinicalCalc.QuickDash(v));
			var two = (decimal?[])v.Clone(); two[0] = null; Assert.Null(HospitalClinicalCalc.QuickDash(two));   // only nine answered
			var bad = (decimal?[])v.Clone(); bad[1] = 6; Assert.Null(HospitalClinicalCalc.QuickDash(bad));
		}

		[Fact]
		public void The_slice_allows_every_calculator()
		{
			var s = HospitalSliceParity.Slice("hospital_h188_specialty_calculators.sql") + HospitalSliceParity.Slice("hospital_h191_quickdash.sql");
			foreach (var k in HospitalConceptCalculator.All) Assert.Contains("'" + k + "'", s);
		}
	}
}
