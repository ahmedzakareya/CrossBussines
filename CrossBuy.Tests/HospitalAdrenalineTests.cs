using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H107 — the adrenaline auto-injector on the allergy visit, with no database: a previous anaphylaxis written
	// in Arabic or English, or an allergy visit, makes it needed; a prescription by any of its names makes it prescribed;
	// a patient with neither shows nothing.
	// ==========================================================================================
	public class HospitalAdrenalineTests
	{
		[Fact]
		public void Needed_and_prescribed()
		{
			var a = HospitalAdrenalineRules.Read("صدمة تحسسية سابقة من لدغة نحل", false, null)!;   // the test patient's own record
			Assert.True(a.Anaphylaxis); Assert.True(a.Needed); Assert.False(a.Prescribed);
			Assert.True(HospitalAdrenalineRules.Read("Anaphylaxis to peanuts", false, null)!.Anaphylaxis);
			var v = HospitalAdrenalineRules.Read("Penicillin rash", true, null)!;                     // an allergy visit
			Assert.False(v.Anaphylaxis); Assert.True(v.Needed);
			Assert.Null(HospitalAdrenalineRules.Read("Penicillin rash", false, null));                // nothing to show
			Assert.True(HospitalAdrenalineRules.Read(null, false, new DateTime(2026, 11, 1))!.Prescribed);
		}

		[Fact]
		public void Every_name_of_the_medicine_counts()
		{
			foreach (var n in new[] { "EpiPen 0.3 mg", "Adrenaline auto-injector 300 mcg", "epinephrine 0.15 mg", "حقنة أدرينالين", "Jext 300" })
				Assert.True(HospitalAdrenalineRules.Mentions(n, HospitalAdrenalineRules.AdrenalineWords), n);
			Assert.False(HospitalAdrenalineRules.Mentions("Cetirizine 10 mg", HospitalAdrenalineRules.AdrenalineWords));
		}
	}
}
