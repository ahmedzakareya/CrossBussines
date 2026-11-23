using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H253 — the readmission model and its local validation. What must never happen: a LACE component on the
	// wrong band; a Charlson group counted twice or a mild form counted beside its severe one; an AUC that is not the
	// Mann-Whitney statistic; a drift index that explodes on an empty band.
	// ==========================================================================================
	public class HospitalRiskTests
	{
		[Theory]
		[InlineData(0, 0)] [InlineData(1, 1)] [InlineData(3, 3)] [InlineData(5, 4)] [InlineData(10, 5)] [InlineData(20, 7)]
		public void Length_of_stay_points(int days, int pts) => Assert.Equal(pts, HospitalReadmissionRules.LosPoints(days));

		[Fact]
		public void Lace_adds_its_four_parts()
		{
			Assert.Equal(4 + 3 + 2 + 4, HospitalReadmissionRules.Lace(5, true, 2, 6));   // E capped at 4
			Assert.Equal(1 + 0 + 5 + 0, HospitalReadmissionRules.Lace(1, false, 6, 0)); // C ≥ 4 is 5
		}

		[Fact]
		public void Charlson_counts_each_group_once()
		{
			Assert.Equal(1, HospitalReadmissionRules.CharlsonScore(new[] { "I21.4", "I22.0" }));           // MI once
			Assert.Equal(4, HospitalReadmissionRules.CharlsonScore(new[] { "I50.9", "E11.9", "N18.3" }));  // heart failure 1 + diabetes 1 + renal 2
		}

		[Fact]
		public void A_severe_form_replaces_its_mild_one()
		{
			Assert.Equal(2, HospitalReadmissionRules.CharlsonScore(new[] { "E11.9", "E11.2" }));   // complicated diabetes only
			Assert.Equal(6, HospitalReadmissionRules.CharlsonScore(new[] { "C50.9", "C78.0" }));   // metastatic only
			Assert.Equal(0, HospitalReadmissionRules.CharlsonScore(new[] { "J06.9", "" }));
		}

		[Fact]
		public void The_auc_is_the_chance_a_case_outranks_a_non_case()
		{
			var perfect = new[] { new HospitalRiskCase(12, true), new HospitalRiskCase(4, false), new HospitalRiskCase(14, true), new HospitalRiskCase(6, false) };
			Assert.Equal(1m, HospitalReadmissionRules.Auc(perfect));
			var tied = new[] { new HospitalRiskCase(8, true), new HospitalRiskCase(8, false) };
			Assert.Equal(0.5m, HospitalReadmissionRules.Auc(tied));
			Assert.Null(HospitalReadmissionRules.Auc(new[] { new HospitalRiskCase(8, false) }));
			var cal = HospitalReadmissionRules.Calibration(perfect);
			Assert.Equal(new[] { 1, 1, 2, 0 }, cal.Select(b => b.Cases));
			Assert.Equal(100m, cal[2].ObservedPct);
		}

		[Fact]
		public void Drift_is_the_population_stability_index()
		{
			var same = new[] { 2, 6, 11, 16, 3, 7 };
			Assert.Equal(0m, HospitalReadmissionRules.Psi(same, same));
			Assert.Equal("Stable", HospitalReadmissionRules.Drift(0m));
			var moved = HospitalReadmissionRules.Psi(new[] { 2, 2, 2, 3 }, new[] { 16, 17, 18, 15 });
			Assert.True(moved > 0.25m);
			Assert.Equal("Shifted", HospitalReadmissionRules.Drift(moved));
			Assert.Null(HospitalReadmissionRules.Psi(System.Array.Empty<int>(), same));
		}
	}
}
