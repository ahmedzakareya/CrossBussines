using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H80 — chart completion, with no database: which deficiencies a chart has and in what order, who owes each,
	// days since discharge and delinquency, when a chart can be signed, and the table against its slice.
	// ==========================================================================================
	public class HospitalChartReviewTests
	{
		[Fact]
		public void A_complete_chart_has_no_deficiency_and_each_gap_is_named_once()
		{
			Assert.Empty(HospitalChartReviewRules.Deficiencies(true, true, true, 0, 0, 0, 0));
			Assert.Equal(new[] { HospitalChartDeficiency.NoSummary, HospitalChartDeficiency.MedRecOpen }, HospitalChartReviewRules.Deficiencies(false, false, false, 0, 0, 0, 0));
			// an unsigned summary is one deficiency, not "no summary" as well
			Assert.Equal(new[] { HospitalChartDeficiency.SummaryUnsigned }, HospitalChartReviewRules.Deficiencies(true, false, true, 0, 0, 0, 0));
			Assert.Equal(HospitalChartDeficiency.All.Skip(1), HospitalChartReviewRules.Deficiencies(true, false, false, 2, 1, 1, 3));
		}

		[Fact]
		public void Each_deficiency_has_its_owner()
		{
			Assert.Equal("Attending", HospitalChartDeficiency.OwnerOf(HospitalChartDeficiency.SummaryUnsigned));
			Assert.Equal("Attending", HospitalChartDeficiency.OwnerOf(HospitalChartDeficiency.MedRecOpen));
			Assert.Equal("Consultant", HospitalChartDeficiency.OwnerOf(HospitalChartDeficiency.ConsultOpen));
			Assert.Equal("InfectionControl", HospitalChartDeficiency.OwnerOf(HospitalChartDeficiency.InfectionUndecided));
			Assert.Equal("Ward", HospitalChartDeficiency.OwnerOf(HospitalChartDeficiency.DeviceInPlace));
			Assert.Equal("Ward", HospitalChartDeficiency.OwnerOf(HospitalChartDeficiency.CarePlanOpen));
		}

		[Fact]
		public void A_chart_incomplete_more_than_fourteen_days_after_discharge_is_delinquent()
		{
			var d = new DateTime(2026, 11, 1, 22, 0, 0);
			Assert.Equal(0, HospitalChartReviewRules.DaysSince(d, new DateTime(2026, 11, 1, 23, 0, 0)));
			Assert.Equal(15, HospitalChartReviewRules.DaysSince(d, new DateTime(2026, 11, 16, 1, 0, 0)));
			Assert.True(HospitalChartReviewRules.Delinquent(15, 1, false));
			Assert.False(HospitalChartReviewRules.Delinquent(14, 1, false));
			Assert.False(HospitalChartReviewRules.Delinquent(30, 0, false));   // complete, waiting for the sign-off
			Assert.False(HospitalChartReviewRules.Delinquent(30, 1, true));
		}

		[Fact]
		public void Only_a_discharged_complete_unsigned_chart_can_be_signed()
		{
			Assert.Null(HospitalChartReviewRules.SignRefusal(true, 0, false));
			Assert.Equal("Only a discharged stay's chart is reviewed", HospitalChartReviewRules.SignRefusal(false, 0, false));
			Assert.Equal("This chart is already signed complete", HospitalChartReviewRules.SignRefusal(true, 0, true));
			Assert.Equal("The chart still has deficiencies", HospitalChartReviewRules.SignRefusal(true, 2, false));
		}

		[Fact]
		public void The_table_matches_its_slice_and_moves_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h80_chart_review.sql");
			var m = HospitalSliceParity.Model(HospitalChartReviewModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalChartReview), "HospitalChartReviews");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalChartReview), "HospitalChartReviews");
			Assert.Equal(new[] { "HospitalAdmissions", "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s).OrderBy(x => x));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalChartReview));
		}
	}
}
