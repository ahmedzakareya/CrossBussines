using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H186 — implant traceability and peri-implant maintenance. What must never happen: peri-implantitis called
	// health, a graft placed with no lot to trace, a restored implant left unreviewed without anyone seeing it, or a
	// lot search that misses a graft or an abutment.
	// ==========================================================================================
	public class HospitalImplantReviewTests
	{
		private static readonly DateTime Today = new(2026, 11, 18);

		[Fact]
		public void Each_review_is_classified_by_the_2017_case_definitions()
		{
			Assert.Equal(HospitalPeriImplantStatus.Health, HospitalImplantReviewRules.Classify(false, false, 4, 1, false));
			Assert.Equal(HospitalPeriImplantStatus.Health, HospitalImplantReviewRules.Classify(false, false, 7, 4, false));   // no inflammation: not a disease
			Assert.Equal(HospitalPeriImplantStatus.Mucositis, HospitalImplantReviewRules.Classify(true, false, 4, 1, false));
			Assert.Equal(HospitalPeriImplantStatus.Mucositis, HospitalImplantReviewRules.Classify(false, true, 5.5m, null, false));
			Assert.Equal(HospitalPeriImplantStatus.Periimplantitis, HospitalImplantReviewRules.Classify(true, false, 4, 3, false));
			Assert.Equal(HospitalPeriImplantStatus.Periimplantitis, HospitalImplantReviewRules.Classify(true, false, 6, null, false));
			Assert.Equal(HospitalPeriImplantStatus.Failing, HospitalImplantReviewRules.Classify(false, false, 3, 0, true));
		}

		[Fact]
		public void A_review_is_of_a_restored_implant_within_bounds()
		{
			var fitted = Today.AddMonths(-6);
			Assert.Null(HospitalImplantReviewRules.Refusal(HospitalImplantStage.Restored, 4, 1, Today, Today, fitted));
			Assert.Equal("A peri-implant review is of a restored implant", HospitalImplantReviewRules.Refusal(HospitalImplantStage.Healing, 4, 1, Today, Today, null));
			Assert.Equal("The deepest probing is 1 to 15 mm", HospitalImplantReviewRules.Refusal(HospitalImplantStage.Restored, 0, 1, Today, Today, fitted));
			Assert.Equal("The bone loss is 0 to 15 mm", HospitalImplantReviewRules.Refusal(HospitalImplantStage.Restored, 4, -1, Today, Today, fitted));
			Assert.Equal("A date cannot be in the future", HospitalImplantReviewRules.Refusal(HospitalImplantStage.Restored, 4, null, Today.AddDays(1), Today, fitted));
			Assert.Equal("A review cannot be before the restoration was fitted", HospitalImplantReviewRules.Refusal(HospitalImplantStage.Restored, 4, null, fitted.AddDays(-1), Today, fitted));
		}

		[Fact]
		public void A_restored_implant_is_reviewed_yearly()
		{
			Assert.Equal(new DateTime(2026, 3, 1), HospitalImplantReviewRules.NextReview(new DateTime(2025, 3, 1), null));
			Assert.Equal(new DateTime(2027, 1, 5), HospitalImplantReviewRules.NextReview(new DateTime(2025, 3, 1), new DateTime(2026, 1, 5)));
			Assert.True(HospitalImplantReviewRules.Overdue(HospitalImplantStage.Restored, new DateTime(2025, 3, 1), null, Today));
			Assert.False(HospitalImplantReviewRules.Overdue(HospitalImplantStage.Restored, new DateTime(2025, 3, 1), new DateTime(2026, 1, 5), Today));
			Assert.False(HospitalImplantReviewRules.Overdue(HospitalImplantStage.Failed, new DateTime(2024, 3, 1), null, Today));
		}

		[Fact]
		public void A_graft_carries_its_lot_and_lots_are_searched_as_written()
		{
			Assert.Equal("A graft is recorded with its lot", HospitalImplantReviewRules.GraftRefusal("xenograft 0.5 g", " "));
			Assert.Null(HospitalImplantReviewRules.GraftRefusal("xenograft 0.5 g", "BG-221"));
			Assert.Null(HospitalImplantReviewRules.GraftRefusal(null, null));
			Assert.Equal("STR-24K118", HospitalImplantReviewRules.LotKey("  str-24k118 "));
			Assert.Null(HospitalImplantReviewRules.LotKey(" "));
		}

		[Fact]
		public void The_tables_match_their_slices_and_a_review_follows_the_patient()
		{
			var model = HospitalSliceParity.Model(HospitalImplantReviewModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h186_implant_reviews.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalImplantReview), "HospitalImplantReviews");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalImplantReview), "HospitalImplantReviews");
			foreach (var st in HospitalPeriImplantStatus.All) Assert.Contains("'" + st + "'", s);
			Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == typeof(HospitalImplantReview));
		}
	}
}
