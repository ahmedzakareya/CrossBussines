using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H243 — radiology category, follow-up and dose. What must never happen: a BI-RADS 3 with no 6-month
	// follow-up; a benign category given a follow-up; a closed follow-up shown as overdue; a dose for an MRI; an
	// effective dose from the wrong region factor; a high DLP not flagged; a model naming a table its slice lacks.
	// ==========================================================================================
	public class HospitalRadQualityTests
	{
		[Fact]
		public void Each_category_carries_its_follow_up()
		{
			var day = new DateTime(2026, 11, 22);
			Assert.Equal(new DateTime(2027, 5, 22), HospitalRadQualityRules.DueDate(HospitalRadQualityRules.Find("BI-RADS", "3")!, day));
			Assert.Null(HospitalRadQualityRules.DueDate(HospitalRadQualityRules.Find("BI-RADS", "2")!, day));
			Assert.Equal(new DateTime(2027, 2, 22), HospitalRadQualityRules.DueDate(HospitalRadQualityRules.Find("Lung-RADS", "4A")!, day));
			Assert.Equal(new DateTime(2026, 12, 22), HospitalRadQualityRules.DueDate(HospitalRadQualityRules.Find("PI-RADS", "5")!, day));
			Assert.Null(HospitalRadQualityRules.Find("BI-RADS", "7"));
			Assert.Equal(5, HospitalRadQualityRules.Systems.Count);
			Assert.All(HospitalRadQualityRules.Categories, c => Assert.True(c.Code.Length <= 10 && c.System.Length <= 12));
			Assert.All(HospitalRadQualityRules.Categories.Where(c => c.FollowUpMonths != null), c => Assert.False(string.IsNullOrWhiteSpace(c.Recommendation)));
		}

		[Fact]
		public void Only_an_open_follow_up_past_its_day_is_overdue()
		{
			var f = new HospitalRadFollowUp { Status = HospitalRadFollowUpStatus.Open, DueDate = new DateTime(2026, 11, 1) };
			Assert.True(HospitalRadQualityRules.Overdue(f, new DateTime(2026, 11, 2)));
			Assert.False(HospitalRadQualityRules.Overdue(f, new DateTime(2026, 11, 1)));
			f.Status = HospitalRadFollowUpStatus.Done;
			Assert.False(HospitalRadQualityRules.Overdue(f, new DateTime(2027, 1, 1)));
		}

		[Fact]
		public void The_effective_dose_uses_the_regions_factor_and_flags_a_high_dose()
		{
			Assert.Equal(2.1m, HospitalRadQualityRules.EffectiveMsv("Head", 1000m));
			Assert.Equal(7m, HospitalRadQualityRules.EffectiveMsv("Chest", 500m));
			Assert.Null(HospitalRadQualityRules.EffectiveMsv("Other", 500m));
			Assert.Null(HospitalRadQualityRules.EffectiveMsv("Chest", null));
			Assert.True(HospitalRadQualityRules.AboveReference("Chest", 10m, 700m));
			Assert.True(HospitalRadQualityRules.AboveReference("Head", 65m, 800m));
			Assert.False(HospitalRadQualityRules.AboveReference("AbdPelvis", 12m, 800m));
		}

		[Fact]
		public void A_dose_belongs_to_an_ionising_exam_with_its_values()
		{
			Assert.Null(HospitalRadQualityRules.DoseRefusal(HospitalRadModality.CT, "Chest", 8m, 350m, null));
			Assert.Equal("A CT dose needs its DLP", HospitalRadQualityRules.DoseRefusal(HospitalRadModality.CT, "Chest", 8m, null, null));
			Assert.Equal("This exam uses no ionising radiation", HospitalRadQualityRules.DoseRefusal(HospitalRadModality.MRI, "Head", null, 10m, null));
			Assert.Null(HospitalRadQualityRules.DoseRefusal(HospitalRadModality.XRay, "Chest", null, null, 0.12m));
			Assert.NotNull(HospitalRadQualityRules.DoseRefusal(HospitalRadModality.XRay, "Chest", null, null, null));
			Assert.NotNull(HospitalRadQualityRules.DoseRefusal(HospitalRadModality.XRay, "Leg", null, null, 1m));
			Assert.NotNull(HospitalRadQualityRules.DoseRefusal(HospitalRadModality.CT, "Chest", -1m, 10m, null));
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalRadQualityModel.Configure);
		private static string Slice() => HospitalSliceParity.Slice("hospital_h243_rad_quality.sql");

		[Theory]
		[InlineData(typeof(HospitalRadFollowUp), "HospitalRadFollowUps")]
		[InlineData(typeof(HospitalRadDose), "HospitalRadDoses")]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table)
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);
		}
	}
}
