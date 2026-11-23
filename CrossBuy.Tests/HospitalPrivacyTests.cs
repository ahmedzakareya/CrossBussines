using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H255 — privacy monitoring. What must never happen: two different families matched by a one-word name or by
	// the article "ال"; a busy day flagged without a baseline that makes it unusual; after-hours read on the wrong clock;
	// an alert reviewed twice or without what was found; a model naming a table its slice does not create.
	// ==========================================================================================
	public class HospitalPrivacyTests
	{
		[Theory]
		[InlineData("أحمد محمد العنزي", null, "سارة خالد عنزي", null, true)]        // the article and its absence
		[InlineData("أحمد محمد الأحمد", null, "منى علي الاحمد", null, true)]        // hamza forms
		[InlineData(null, "Ahmed Ali Saleh", null, "Mona Saleh", true)]
		[InlineData("أحمد العنزي", null, "خالد المطيري", null, false)]
		[InlineData("أحمد", null, "أحمد", null, false)]                              // one word is not a family name
		[InlineData(null, "Ahmed Al", null, "Sara Al", false)]                      // two letters are not a family name
		public void Family_names_are_compared_without_the_article_or_the_hamza(string? ea, string? ee, string? pa, string? pe, bool same) =>
			Assert.Equal(same, HospitalPrivacyRules.SameFamily(ea, ee, pa, pe));

		[Fact]
		public void A_busy_day_is_flagged_only_against_the_employees_own_usual()
		{
			Assert.False(HospitalPrivacyRules.VolumeAnomaly(25, new[] { 2, 3, 2 }));             // under the floor
			Assert.True(HospitalPrivacyRules.VolumeAnomaly(45, new[] { 10, 12, 8, 0, 0 }));      // median 10
			Assert.False(HospitalPrivacyRules.VolumeAnomaly(45, new[] { 20, 18, 22 }));          // a registrar's normal day
			Assert.True(HospitalPrivacyRules.VolumeAnomaly(31, System.Array.Empty<int>()));      // no history: over the floor
		}

		[Fact]
		public void After_hours_and_the_review()
		{
			Assert.True(HospitalPrivacyRules.AfterHours(new DateTime(2026, 11, 23, 23, 10, 0)));
			Assert.True(HospitalPrivacyRules.AfterHours(new DateTime(2026, 11, 23, 5, 59, 0)));
			Assert.False(HospitalPrivacyRules.AfterHours(new DateTime(2026, 11, 23, 6, 0, 0)));
			Assert.Null(HospitalPrivacyRules.ReviewRefusal(HospitalPrivacyStatus.Open, HospitalPrivacyStatus.Cleared, "Covering for a colleague, confirmed with the ward"));
			Assert.NotNull(HospitalPrivacyRules.ReviewRefusal(HospitalPrivacyStatus.Open, HospitalPrivacyStatus.Confirmed, " "));
			Assert.NotNull(HospitalPrivacyRules.ReviewRefusal(HospitalPrivacyStatus.Cleared, HospitalPrivacyStatus.Confirmed, "x"));
			Assert.Equal("not_found", HospitalPrivacyRules.ReviewRefusal(HospitalPrivacyStatus.Open, HospitalPrivacyStatus.Open, "x"));
		}

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates()
		{
			var model = HospitalSliceParity.Model(HospitalPrivacyModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h255_privacy.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalPrivacyAlert), "HospitalPrivacyAlerts");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalPrivacyAlert), "HospitalPrivacyAlerts");
		}
	}
}
