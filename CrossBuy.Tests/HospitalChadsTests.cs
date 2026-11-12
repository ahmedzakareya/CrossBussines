using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H108 — CHA₂DS₂-VASc, with no database: no atrial fibrillation, no score (the prototype's "No AF — not
	// scored"); each component from its ICD-10 codes; the age bands; the female point does not raise the advice (ESC 2020).
	// ==========================================================================================
	public class HospitalChadsTests
	{
		[Fact]
		public void No_af_no_score()
		{
			var r = HospitalChadsRules.Score(68, false, new[] { "E11.9", "I10", "N18.3" });   // the prototype's diabetic: no AF
			Assert.False(r.Af); Assert.Null(r.Score);
		}

		[Fact]
		public void Each_component_from_its_codes()
		{
			// 78, woman, AF, heart failure, hypertension, diabetes, an old stroke, a past MI: 1+1+2+1+2+1+1 = 9
			var r = HospitalChadsRules.Score(78, true, new[] { "I48.0", "I50.9", "I10", "E11.9", "Z86.73", "I25.2" });
			Assert.Equal(9, r.Score); Assert.Equal("recommended", r.Advice);
			Assert.Equal(1, HospitalChadsRules.Score(66, false, new[] { "I48.91" }).Score);   // 65–74: one point
			Assert.Equal(2, HospitalChadsRules.Score(75, false, new[] { "I48.91" }).Score);   // 75: two
		}

		[Theory]
		[InlineData(50, false, new[] { "I48.0" }, 0, "none")]
		[InlineData(50, true, new[] { "I48.0" }, 1, "none")]                 // the female point alone: no anticoagulant
		[InlineData(50, false, new[] { "I48.0", "I10" }, 1, "consider")]
		[InlineData(50, true, new[] { "I48.0", "I10" }, 2, "consider")]
		[InlineData(70, false, new[] { "I48.0", "I10" }, 2, "recommended")]
		[InlineData(70, true, new[] { "I48.0", "I10" }, 3, "recommended")]
		public void Esc_2020_advice(int age, bool female, string[] codes, int score, string advice)
		{
			var r = HospitalChadsRules.Score(age, female, codes);
			Assert.Equal((score, advice), (r.Score!.Value, r.Advice));
		}
	}
}
