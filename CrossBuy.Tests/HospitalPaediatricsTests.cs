using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H95 — the paediatric visit, with no database: the WHO references read back their own published values,
	// z and percentile by the LMS method (and WHO's restricted method past ±3 SD), the ages each reference covers, the
	// crossing of percentile lines, and a child's dose by weight capped by the adult limit.
	// ==========================================================================================
	public class HospitalPaediatricsTests
	{
		[Fact]
		public void The_WHO_tables_read_back_their_published_values()
		{
			// weianthro.txt, sex 1 / 2, day 0: M = 3.3464 / 3.2322 — the medians WHO publishes for birth weight
			Assert.Equal(3.3464, HospitalGrowth.Lms(HospitalGrowthIndicator.Weight, "M", 0)!.M);
			Assert.Equal(3.2322, HospitalGrowth.Lms(HospitalGrowthIndicator.Weight, "F", 0)!.M);
			Assert.Equal(0, HospitalGrowth.Z(HospitalGrowthIndicator.Weight, "M", 0, 3.3464));
			// WHO's weight-for-age table, boys at birth: −2 SD 2.5 kg, +2 SD 4.4 kg (one decimal)
			Assert.Equal(2.5, Math.Round(HospitalGrowth.ValueAt(HospitalGrowthIndicator.Weight, "M", 0, -2)!.Value, 1));
			Assert.Equal(4.4, Math.Round(HospitalGrowth.ValueAt(HospitalGrowthIndicator.Weight, "M", 0, 2)!.Value, 1));
			// the 2007 reference, girls at 85 months: M = 22.5762
			Assert.InRange(HospitalGrowth.Lms(HospitalGrowthIndicator.Weight, "F", (int)Math.Round(85 * 30.4375))!.M, 22.56, 22.58);   // a day short of 85 months, interpolated
		}

		[Fact]
		public void Each_reference_covers_only_the_ages_WHO_publishes()
		{
			Assert.NotNull(HospitalGrowth.Lms(HospitalGrowthIndicator.Weight, "F", (int)(120 * 30.4375)));
			Assert.Null(HospitalGrowth.Lms(HospitalGrowthIndicator.Weight, "F", (int)(121 * 30.4375)));   // no weight-for-age after ten years
			Assert.NotNull(HospitalGrowth.Lms(HospitalGrowthIndicator.Height, "M", (int)(228 * 30.4375)));
			Assert.Null(HospitalGrowth.Lms(HospitalGrowthIndicator.Height, "M", (int)(229 * 30.4375)));
			Assert.NotNull(HospitalGrowth.Lms(HospitalGrowthIndicator.Head, "M", 1826));   // the last day of the 0–5 tables
			Assert.Null(HospitalGrowth.Lms(HospitalGrowthIndicator.Head, "M", 1900));   // head circumference only to five years
			Assert.Null(HospitalGrowth.Z(HospitalGrowthIndicator.Weight, "F", 5000, 40));   // not known, never a guess
		}

		[Fact]
		public void Percentiles_and_the_restricted_method_past_three_SD()
		{
			Assert.Equal(50.0, HospitalGrowth.Percentile(0));
			Assert.Equal(3.0, HospitalGrowth.Percentile(-1.881));
			Assert.Equal(85.0, HospitalGrowth.Percentile(1.036));
			// at +3 SD exactly the restricted method gives 3; beyond it, it grows by the SD2→SD3 distance
			var sd3 = HospitalGrowth.ValueAt(HospitalGrowthIndicator.Weight, "M", 365, 3)!.Value;
			var sd2 = HospitalGrowth.ValueAt(HospitalGrowthIndicator.Weight, "M", 365, 2)!.Value;
			Assert.Equal(3.0, HospitalGrowth.Z(HospitalGrowthIndicator.Weight, "M", 365, sd3)!.Value, 1);
			Assert.Equal(4.0, HospitalGrowth.Z(HospitalGrowthIndicator.Weight, "M", 365, sd3 + (sd3 - sd2))!.Value, 1);
		}

		[Fact]
		public void Crossing_two_percentile_lines_is_flagged_one_is_not()
		{
			HospitalGrowthPoint P(int d, double z) => new(DateTime.Today, d, 10, z, HospitalGrowth.Percentile(z));
			Assert.False(HospitalGrowth.Crossing(new[] { P(1100, -0.37), P(2700, -1.2) }).Crossed);   // P35 → P11: one line
			Assert.True(HospitalGrowth.Crossing(new[] { P(1100, 0.5), P(2000, -0.3), P(2700, -1.2) }).Crossed);   // P69 → P11: two lines
			Assert.True(HospitalGrowth.Crossing(new[] { P(1100, -1.2), P(2700, 0.5) }).Crossed);   // a rise counts too
			Assert.False(HospitalGrowth.Crossing(new[] { P(1100, 0.5) }).Crossed);
		}

		[Fact]
		public void A_childs_dose_is_by_weight_and_never_above_the_adult_limit()
		{
			var para = new HospitalDoseLimit { Drug = "paracetamol", Unit = "mg", MaxSingle = 1000, MaxDaily = 4000, PerKgMg = 15, EveryHours = 6, StrengthMgPerMl = 24 };
			Assert.Equal((288m, false), HospitalVisitWidgetRules.ChildDose(para, 19.2m));
			Assert.Equal((1000m, true), HospitalVisitWidgetRules.ChildDose(para, 80m));
			Assert.Equal((null, false), HospitalVisitWidgetRules.ChildDose(para, null));   // no weight: no dose (the screen says why)
			Assert.Equal(4, HospitalVisitWidgetRules.DosesPerDay(para, 288m));
			para.MaxDaily = 1000; Assert.Equal(3, HospitalVisitWidgetRules.DosesPerDay(para, 288m));
			HospitalDoseLimitInput I() => new() { Drug = "paracetamol", Unit = "mg", MaxSingle = 1000, MaxDaily = 4000, PerKgMg = 15, EveryHours = 6 };
			Assert.Null(HospitalDrugSafetyRules.LimitRefusal(I()));
			var i = I(); i.MaxSingle = null; Assert.Equal("A dose per kg needs the adult single limit as its cap", HospitalDrugSafetyRules.LimitRefusal(i));
			i = I(); i.EveryHours = 0; Assert.Equal("Write the mg per kg and every how many hours", HospitalDrugSafetyRules.LimitRefusal(i));
			i = I(); i.Unit = "mcg"; Assert.Equal("A dose per kg is written in mg", HospitalDrugSafetyRules.LimitRefusal(i));
			Assert.True(HospitalVisitWidgetRules.IsDevelopment(new HospitalClinicalTemplateField { SectionEn = " development " }));
		}
	}
}
