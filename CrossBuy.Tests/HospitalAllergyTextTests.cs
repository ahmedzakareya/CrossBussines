using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	/// The allergy banner in the reader's language: translated only when every word is known, otherwise as written.
	public class HospitalAllergyTextTests
	{
		[Theory]
		[InlineData("البنسلين", "en", "Penicillin")]
		[InlineData("بنسلين", "fr", "Pénicilline")]
		[InlineData("الأسبرين", "en", "Aspirin")]
		[InlineData("الفول السوداني", "en", "Peanuts")]
		[InlineData("صبغة اليود (طفح جلدي وضيق تنفس)", "en", "Iodine contrast (rash and shortness of breath)")]
		[InlineData("البنسلين (طفح جلدي) · Penicillin (rash)", "en", "Penicillin (rash)")]   // the bilingual entry collapses to one
		[InlineData("Latex", "en", "Latex")]
		[InlineData("المكسرات، البيض", "en", "Nuts, Eggs")]
		public void Translates_known_entries(string stored, string lang, string shown)
			=> Assert.Equal(shown, HospitalAllergyText.Display(stored, lang));

		[Fact]
		public void Arabic_screen_shows_the_file_as_written()
			=> Assert.Equal("البنسلين (طفح جلدي)", HospitalAllergyText.Display("البنسلين (طفح جلدي)", "ar"));

		[Theory]
		[InlineData("صدمة تحسسية سابقة من لدغة نحل")]       // a sentence, not a term: kept whole
		[InlineData("البنسلين (طفح شديد جدا)")]              // a reaction word it does not know: never half-translated
		public void Unknown_words_keep_the_original(string stored)
			=> Assert.Equal(stored, HospitalAllergyText.Display(stored, "en"));

		[Fact]
		public void Empty_is_empty() => Assert.Equal("", HospitalAllergyText.Display(null, "en"));
	}
}
