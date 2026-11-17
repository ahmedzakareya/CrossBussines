using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H108/H172 — CHA₂DS₂-VA (ESC 2024), with no database: no atrial fibrillation, no score (the prototype's
	// "No AF — not scored"); each component from its ICD-10 codes; the age bands; sex is not a factor any more.
	// ==========================================================================================
	public class HospitalChadsTests
	{
		[Fact]
		public void No_af_no_score()
		{
			var r = HospitalChadsRules.Score(68, new[] { "E11.9", "I10", "N18.3" });   // the prototype's diabetic: no AF
			Assert.False(r.Af); Assert.Null(r.Score);
		}

		[Fact]
		public void Each_component_from_its_codes()
		{
			// 78, AF, heart failure, hypertension, diabetes, an old stroke, a past MI: 1+1+2+1+2+1 = 8, the maximum
			var r = HospitalChadsRules.Score(78, new[] { "I48.0", "I50.9", "I10", "E11.9", "Z86.73", "I25.2" });
			Assert.Equal(HospitalChadsRules.Max, r.Score); Assert.Equal("recommended", r.Advice);
			Assert.DoesNotContain(r.Items, i => i.Key == "female");
			Assert.Equal(1, HospitalChadsRules.Score(66, new[] { "I48.91" }).Score);   // 65–74: one point
			Assert.Equal(2, HospitalChadsRules.Score(75, new[] { "I48.91" }).Score);   // 75: two
		}

		[Theory]
		[InlineData(50, new[] { "I48.0" }, 0, "none")]
		[InlineData(50, new[] { "I48.0", "I10" }, 1, "consider")]
		[InlineData(66, new[] { "I48.0" }, 1, "consider")]
		[InlineData(70, new[] { "I48.0", "I10" }, 2, "recommended")]
		[InlineData(50, new[] { "I48.0", "G45.9" }, 2, "recommended")]   // a TIA alone
		public void Esc_2024_advice(int age, string[] codes, int score, string advice)
		{
			var r = HospitalChadsRules.Score(age, codes);
			Assert.Equal((score, advice), (r.Score!.Value, r.Advice));
		}
	}
}
