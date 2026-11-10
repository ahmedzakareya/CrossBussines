using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H81 — quality indicators, with no database: percentages and rates with no denominator are no value (never
	// zero), NPS, met / not met against a target in the right direction, patient-day overlap, the 30-day readmission rule,
	// what a target may be, the twelve indicators, and the table against its slice.
	// ==========================================================================================
	public class HospitalQualityTests
	{
		[Fact]
		public void No_denominator_means_no_value_not_zero()
		{
			Assert.Null(HospitalQualityRules.Pct(0, 0));
			Assert.Equal(25.0m, HospitalQualityRules.Pct(1, 4));
			Assert.Null(HospitalQualityRules.Per1000(3, 0));
			Assert.Equal(2.5m, HospitalQualityRules.Per1000(1, 400));
			Assert.Null(HospitalQualityRules.Nps(Array.Empty<int>()));
			// 2 promoters, 1 passive, 1 detractor of 4 → 50 − 25 = 25
			Assert.Equal(25m, HospitalQualityRules.Nps(new[] { 10, 9, 8, 3 }));
		}

		[Fact]
		public void Met_follows_the_indicator_direction_and_no_value_is_no_data()
		{
			Assert.Equal("Met", HospitalQualityRules.Status(8m, 10m, true));       // readmission 8% vs ≤ 10%
			Assert.Equal("NotMet", HospitalQualityRules.Status(12m, 10m, true));
			Assert.Equal("Met", HospitalQualityRules.Status(92m, 90m, false));     // reconciliation 92% vs ≥ 90%
			Assert.Equal("NotMet", HospitalQualityRules.Status(70m, 90m, false));
			Assert.Equal("Met", HospitalQualityRules.Status(10m, 10m, true));      // on the target counts as met
			Assert.Equal("NoData", HospitalQualityRules.Status(null, 10m, true));
		}

		[Fact]
		public void Patient_days_are_the_part_of_the_stay_inside_the_month()
		{
			var from = new DateTime(2026, 11, 1); var to = new DateTime(2026, 12, 1);
			Assert.Equal(2m, HospitalQualityRules.OverlapDays(new DateTime(2026, 10, 30), new DateTime(2026, 11, 3), from, to));
			Assert.Equal(1.5m, HospitalQualityRules.OverlapDays(new DateTime(2026, 11, 10, 0, 0, 0), new DateTime(2026, 11, 11, 12, 0, 0), from, to));
			Assert.Equal(30m, HospitalQualityRules.OverlapDays(new DateTime(2026, 10, 1), null, from, to));   // still in
			Assert.Equal(0m, HospitalQualityRules.OverlapDays(new DateTime(2026, 12, 2), null, from, to));
		}

		[Fact]
		public void A_readmission_is_a_new_stay_within_thirty_days_after_discharge()
		{
			var d = new DateTime(2026, 11, 1, 10, 0, 0);
			Assert.True(HospitalQualityRules.Readmitted(d, new[] { d.AddDays(12) }));
			Assert.True(HospitalQualityRules.Readmitted(d, new[] { d.AddDays(30) }));
			Assert.False(HospitalQualityRules.Readmitted(d, new[] { d.AddDays(31) }));
			Assert.False(HospitalQualityRules.Readmitted(d, new[] { d.AddDays(-3) }));   // the stay itself, or an earlier one
		}

		[Fact]
		public void A_target_fits_its_unit()
		{
			Assert.Null(HospitalQualityRules.TargetRefusal(HospitalQualityIndicator.Readmission30, 8m));
			Assert.Equal("not_found", HospitalQualityRules.TargetRefusal("Happiness", 1m));
			Assert.Equal("Write the target", HospitalQualityRules.TargetRefusal(HospitalQualityIndicator.Alos, null));
			Assert.Equal("A percentage target is between 0 and 100", HospitalQualityRules.TargetRefusal(HospitalQualityIndicator.Occupancy, 120m));
			Assert.Equal("A satisfaction target is between 1 and 5", HospitalQualityRules.TargetRefusal(HospitalQualityIndicator.Satisfaction, 7m));
			Assert.Equal("An NPS target is between -100 and 100", HospitalQualityRules.TargetRefusal(HospitalQualityIndicator.Nps, 150m));
			Assert.Equal(12, HospitalQualityIndicator.All.Count);
			Assert.Equal(12, HospitalQualityIndicator.All.Select(i => i.Code).Distinct().Count());
		}

		[Fact]
		public void The_table_matches_its_slice_and_names_every_indicator()
		{
			var s = HospitalSliceParity.Slice("hospital_h81_quality_targets.sql");
			var m = HospitalSliceParity.Model(HospitalQualityModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalQualityTarget), "HospitalQualityTargets");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalQualityTarget), "HospitalQualityTargets");
			foreach (var i in HospitalQualityIndicator.All) Assert.Contains("'" + i.Code + "'", s);
		}
	}
}
