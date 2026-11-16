using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H142: what the desk typed in the one search, carried into the registration window. What must never happen:
	//   · a mobile number put in the ID field, or an ID in the mobile field;
	//   · Arabic-Indic digits kept as typed (the patient service accepts 0-9 only, so the save would fail);
	//   · an Arabic name in the English field, or the reverse.
	// ==========================================================================================
	public class HospitalFrontDeskRegisterTests
	{
		[Theory]
		[InlineData("01012345678", null, null, null, "01012345678")]
		[InlineData("+966 55 120 4471", null, null, null, "+966551204471")]
		[InlineData("29001011234567", null, null, "29001011234567", null)]
		[InlineData("٢٩٠٠١٠١١٢٣٤٥٦٧", null, null, "29001011234567", null)]
		[InlineData("٠١٠١٢٣٤٥٦٧٨", null, null, null, "01012345678")]
		[InlineData("أحمد محمد", "أحمد محمد", null, null, null)]
		[InlineData("Ahmed Mohamed", null, "Ahmed Mohamed", null, null)]
		[InlineData("123", null, null, null, null)]   // too short for anything: nothing is guessed
		[InlineData("  ", null, null, null, null)]
		public void The_search_fills_the_right_field(string q, string? name, string? nameEn, string? id, string? phone)
			=> Assert.Equal(new HospitalDeskRegisterPrefill(name, nameEn, id, phone), HospitalDeskRegisterRules.Prefill(q));
	}
}
