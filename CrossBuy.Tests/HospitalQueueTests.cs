using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H83 — the waiting-room display and the self check-in kiosk, with no database: what the kiosk asks for, the
	// identity check that never tells a wrong number from a wrong year, the hour-early window, and the display's change
	// stamp that makes it chime only on a new call.
	// ==========================================================================================
	public class HospitalQueueTests
	{
		[Fact]
		public void The_kiosk_needs_a_file_number_and_a_plausible_birth_year()
		{
			Assert.Null(HospitalKioskRules.FindRefusal("MRN-100001", 1980, 2026));
			Assert.Equal("Enter your file number", HospitalKioskRules.FindRefusal(" ", 1980, 2026));
			Assert.Equal("Enter your year of birth", HospitalKioskRules.FindRefusal("MRN-1", null, 2026));
			Assert.Equal("Enter your year of birth", HospitalKioskRules.FindRefusal("MRN-1", 2030, 2026));
			Assert.Equal("Enter your year of birth", HospitalKioskRules.FindRefusal("MRN-1", 1880, 2026));
		}

		[Fact]
		public void A_wrong_number_and_a_wrong_year_get_the_same_answer()
		{
			const string Desk = "We could not find your appointment: please go to the desk";
			Assert.Null(HospitalKioskRules.IdentityRefusal(true, new DateTime(1980, 5, 1), 1980));
			Assert.Equal(Desk, HospitalKioskRules.IdentityRefusal(false, null, 1980));
			Assert.Equal(Desk, HospitalKioskRules.IdentityRefusal(true, new DateTime(1980, 5, 1), 1981));
			Assert.Equal(Desk, HospitalKioskRules.IdentityRefusal(true, null, 1980));   // no birth date on file: the desk
		}

		[Fact]
		public void Check_in_opens_an_hour_before_the_slot_and_stays_open_after_it()
		{
			Assert.True(HospitalKioskRules.TooEarly(10 * 60, 8 * 60 + 59));
			Assert.False(HospitalKioskRules.TooEarly(10 * 60, 9 * 60));
			Assert.False(HospitalKioskRules.TooEarly(10 * 60, 11 * 60));   // late is still checked in
		}

		[Fact]
		public void The_stamp_changes_only_when_a_new_number_is_called()
		{
			var t = new DateTime(2026, 11, 10, 9, 0, 0);
			var a = HospitalKioskRules.Stamp(new[] { (1, (int?)3, (DateTime?)t), (2, (int?)null, (DateTime?)null) });
			var same = HospitalKioskRules.Stamp(new[] { (2, (int?)null, (DateTime?)null), (1, (int?)3, (DateTime?)t) });   // order does not matter
			var next = HospitalKioskRules.Stamp(new[] { (1, (int?)4, (DateTime?)t.AddMinutes(9)), (2, (int?)null, (DateTime?)null) });
			Assert.Equal(a, same);
			Assert.NotEqual(a, next);
			Assert.Equal("", HospitalKioskRules.Stamp(Array.Empty<(int, int?, DateTime?)>()));
		}
	}
}
