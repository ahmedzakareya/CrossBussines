using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H269 — a drug meets the allergies it belongs to, not only the ones its name spells. The owner's walk-through
	// (2026-11-25): amoxicillin prescribed to a patient recorded as allergic to "بنسلين" raised nothing.
	// ==========================================================================================
	public class HospitalAllergyMatchTests
	{
		[Theory]
		// the owner's case: an Arabic penicillin allergy, the drug written in English
		[InlineData("Amoxicillin 500 mg", "بنسلين\nفول سوداني", "بنسلين")]
		[InlineData("Augmentin 1 g", "البنسلين (طفح جلدي)", "البنسلين")]
		[InlineData("أموكسيسيلين ٥٠٠", "Penicillin", "Penicillin")]
		[InlineData("Flucloxacillin 250 mg", "Amoxicillin", "Amoxicillin")]           // one penicillin is all of them
		[InlineData("Co-trimoxazole 960 mg", "سلفا", "سلفا")]
		[InlineData("Voltaren 50 mg", "Aspirin", "Aspirin")]                           // NSAIDs as a family
		[InlineData("Codeine 30 mg", "مورفين", "مورفين")]
		[InlineData("Azithromycin 500 mg", "Clarithromycin", "Clarithromycin")]
		// what must stay quiet
		[InlineData("Amlodipine 5 mg", "بنسلين\nفول سوداني", "")]
		[InlineData("Ceftriaxone 1 g", "Penicillin", "")]                              // cross-family left out on purpose
		[InlineData("Paracetamol 500 mg", "Aspirin", "")]
		[InlineData("Amoxicillin 500 mg", null, "")]
		[InlineData("Co", "co", "")]
		public void A_drug_meets_the_allergies_of_its_family(string drug, string? allergies, string expected)
			=> Assert.Equal(expected, string.Join(",", HospitalAllergyMatch.Hits(drug, allergies)));

		[Fact]
		public void The_pharmacy_review_uses_the_same_match()
			=> Assert.Equal(new[] { "بنسلين" }, HospitalPharmacyService.AllergiesIn("Amoxicillin 500 mg", "بنسلين").ToArray());

		[Fact]
		public void A_stocked_drug_is_matched_by_its_record_and_class()
		{
			var d = new HospitalDrug { GenericName = "أموكسيسيلين", GenericNameEn = "Amoxicillin", TherapeuticClass = "Penicillins" };
			Assert.Single(HospitalAllergyMatch.Hits(HospitalDrugRules.MatchText("Moxal 500", d), "بنسلين"));
			Assert.Single(HospitalAllergyMatch.Hits("Unknown brand " + d.TherapeuticClass, "Penicillin"));
		}

		[Fact]
		public void Every_family_word_is_long_enough_not_to_match_by_accident()
		{
			foreach (var f in HospitalAllergyMatch.Families)
				Assert.All(f.Names.Concat(f.Members), w => Assert.True(HospitalAllergyMatch.Fold(w).Length >= 4, f.Key + ": " + w));
		}
	}
}
