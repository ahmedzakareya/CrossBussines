using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H272 — the modality of a radiology service nobody set up is guessed from its code's words. "EYE-OCT" holds
	// the letters "CT" and was sent to the CT room.
	// ==========================================================================================
	public class HospitalRadModalityGuessTests
	{
		[Theory]
		[InlineData("RAD-CT", HospitalRadModality.CT)]
		[InlineData("RAD-CTA-HEAD", HospitalRadModality.CT)]
		[InlineData("RAD-MRI", HospitalRadModality.MRI)]
		[InlineData("RAD-USAB", HospitalRadModality.Ultrasound)]
		[InlineData("RAD-USPEL", HospitalRadModality.Ultrasound)]
		[InlineData("CARD-ECHO", HospitalRadModality.Ultrasound)]
		[InlineData("RAD-CXR", HospitalRadModality.XRay)]
		[InlineData("RAD-OPG", HospitalRadModality.XRay)]
		[InlineData("EYE-OCT", HospitalRadModality.XRay)]   // not a CT: the letters sit inside another word
		public void The_modality_is_read_from_the_codes_words(string code, string modality)
			=> Assert.Equal(modality, HospitalRadiologyService.GuessModality(code));
	}
}
