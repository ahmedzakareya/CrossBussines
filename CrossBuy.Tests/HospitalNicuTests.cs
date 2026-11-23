using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H252 — the neonatal unit. What must never happen: a postmenstrual age counted from the wrong day; a term
	// baby given a corrected age; growth velocity by the linear shortcut; feeds per kilo against a missing weight; an
	// impossible weight recorded; a model naming a table its slice does not create.
	// ==========================================================================================
	public class HospitalNicuTests
	{
		[Fact]
		public void Age_counts_from_the_day_of_birth()
		{
			var born = new DateTime(2026, 11, 1);
			Assert.Equal(0, HospitalNicuRules.DayOfLife(born, born));
			Assert.Equal(22, HospitalNicuRules.DayOfLife(born, new DateTime(2026, 11, 23)));
			Assert.Equal((31, 1), HospitalNicuRules.Pma(28, 22));   // 28+0 and 22 days = 31+1
			Assert.Null(HospitalNicuRules.Pma(null, 22));
			Assert.Equal(-62, HospitalNicuRules.CorrectedDays(28, 22));   // 12 weeks short of term
			Assert.Null(HospitalNicuRules.CorrectedDays(39, 22));
			Assert.True(HospitalNicuRules.Preterm(36)); Assert.False(HospitalNicuRules.Preterm(37));
		}

		[Fact]
		public void Growth_is_measured_exponentially_against_birth_weight()
		{
			Assert.Equal(92.5m, HospitalNicuRules.PercentOfBirth(1110, 1200));
			// 1200 g → 1350 g in 7 days: 1000 × ln(1.125) / 7 = 16.8 g/kg/day
			Assert.Equal(16.8m, HospitalNicuRules.Velocity(1200, new DateTime(2026, 11, 1), 1350, new DateTime(2026, 11, 8)));
			Assert.Null(HospitalNicuRules.Velocity(1200, new DateTime(2026, 11, 8), 1350, new DateTime(2026, 11, 8)));
			Assert.Equal(150m, HospitalNicuRules.FeedsPerKg(180, 1200));
			Assert.Null(HospitalNicuRules.FeedsPerKg(180, null));
		}

		[Fact]
		public void Birth_weight_classes_and_a_days_values()
		{
			Assert.Equal("ELBW", HospitalNicuRules.BirthWeightClass(900));
			Assert.Equal("VLBW", HospitalNicuRules.BirthWeightClass(1400));
			Assert.Equal("LBW", HospitalNicuRules.BirthWeightClass(2400));
			Assert.Null(HospitalNicuRules.BirthWeightClass(3200));
			Assert.NotNull(HospitalNicuRules.DayRefusal(200, null, null, null, null, null));
			Assert.NotNull(HospitalNicuRules.DayRefusal(null, null, null, null, null, null));
			Assert.NotNull(HospitalNicuRules.DayRefusal(1200, null, null, 100, "Juice", null));
			Assert.Null(HospitalNicuRules.DayRefusal(1200, 40.5m, 29m, 180, HospitalNicuFeed.Expressed, 210));
		}

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates()
		{
			var model = HospitalSliceParity.Model(HospitalNicuModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h252_nicu.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalNicuDay), "HospitalNicuDays");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalNicuDay), "HospitalNicuDays");
		}
	}
}
