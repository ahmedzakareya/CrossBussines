using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H196 — the standard diagnosis list the clinics start from. What must never happen: a rule that reads a
	// code the list cannot offer (a stroke for CHA₂DS₂-VASc, a sudden hearing loss, a thyroid nodule), a code in two
	// rows, or a row without its Arabic and English names.
	// ==========================================================================================
	public class HospitalDiagnosisCodeListTests
	{
		private static readonly IReadOnlyList<string> Codes = HospitalConsultationService.StandardCodes.Select(c => c.Code).ToList();

		[Fact]
		public void Every_code_is_once_well_formed_and_named_in_both_languages()
		{
			Assert.Equal(Codes.Count, Codes.Distinct().Count());
			foreach (var c in HospitalConsultationService.StandardCodes)
			{
				Assert.Matches(@"^[A-Z][0-9][0-9A-Z](\.[0-9A-Z]{1,4})?$", c.Code);
				Assert.False(string.IsNullOrWhiteSpace(c.Ar), c.Code);
				Assert.False(string.IsNullOrWhiteSpace(c.En), c.Code);
				Assert.True(c.Code.Length <= 20 && c.Ar.Length <= 300 && c.En.Length <= 300, c.Code);
			}
		}

		[Fact]
		public void Every_CHA2DS2_VASc_component_can_be_coded_from_the_list()
		{
			var all = Codes.Append("I48.91").ToList();   // with atrial fibrillation on file
			var s = HospitalChadsRules.Score(80, all);
			foreach (var k in new[] { "chf", "htn", "age75", "dm", "stroke", "vasc" }) Assert.Contains(s.Items, i => i.Key == k);
		}

		[Fact]
		public void The_specialties_find_their_conditions()
		{
			foreach (var c in new[] { "I63.9", "G45.9", "H40.9", "H91.20", "E04.1", "L40.0", "M81.0", "F33.9", "T78.2XXA", "N18.6", "Z51.11" })
				Assert.Contains(c, Codes);
		}
	}
}
