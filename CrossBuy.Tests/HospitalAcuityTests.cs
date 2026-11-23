using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H246 — acuity-based staffing. What must never happen: a ventilated patient below level 4; points mapped to
	// the wrong level; a ward needing zero nurses with patients in it; a short ward shown as matched; a model naming a
	// table its slice does not create.
	// ==========================================================================================
	public class HospitalAcuityTests
	{
		[Theory]
		[InlineData(new string[0], 0, 1)]
		[InlineData(new[] { "VITALS" }, 3, 1)]
		[InlineData(new[] { "VITALS", "IV" }, 5, 2)]
		[InlineData(new[] { "VITALS", "IV", "MOBILITY" }, 8, 3)]
		[InlineData(new[] { "VITALS", "IV", "MOBILITY", "HYGIENE", "WOUND", "ISOLATION" }, 14, 4)]
		[InlineData(new[] { "VENT" }, 13, 4)]
		[InlineData(new[] { "TEACHING", "ONE2ONE" }, 14, 4)]
		public void Points_set_the_level(string[] factors, int points, int level) =>
			Assert.Equal((points, level), HospitalAcuityRules.Classify(factors));

		[Fact]
		public void Nurses_needed_come_from_the_care_hours_of_a_shift()
		{
			// 4 × level 2 (5 h/day → 1.67 h/shift) + 2 × level 4 (16 h → 5.33) = 6.67 + 10.67 = 17.33 h → 3 nurses
			Assert.Equal(3, HospitalAcuityRules.NursesNeeded(new[] { 2, 2, 2, 2, 4, 4 }));
			Assert.Equal(1, HospitalAcuityRules.NursesNeeded(new[] { 1 }));
			Assert.Equal(0, HospitalAcuityRules.NursesNeeded(Array.Empty<int>()));
			Assert.Equal("Short", HospitalAcuityRules.Gap(3, 2));
			Assert.Equal("Matched", HospitalAcuityRules.Gap(3, 4));
			Assert.Equal("Over", HospitalAcuityRules.Gap(3, 5));
			Assert.Equal("Unplanned", HospitalAcuityRules.Gap(3, null));
			Assert.Equal("VITALS,IV", HospitalAcuityRules.FactorText(new[] { "IV", "NOPE", "VITALS" }));
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalAcuityModel.Configure);
		private static string Slice() => HospitalSliceParity.Slice("hospital_h246_acuity.sql");

		[Theory]
		[InlineData(typeof(HospitalAcuityScore), "HospitalAcuityScores")]
		[InlineData(typeof(HospitalWardStaffing), "HospitalWardStaffing")]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table)
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);
		}
	}
}
