using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H48 — the earlier-slot waitlist, with no database: which free time a waiting booking is offered (only
	// earlier than the one it holds, earliest first, never one it passed up), when an emailed offer can still be
	// answered, the days gained, the clash with the patient's other visits, the setting's bounds, and the slice.
	// ==========================================================================================
	public class HospitalPortalWaitlistTests
	{
		private static readonly DateTime D1 = new(2026, 11, 10), D2 = new(2026, 11, 12), D3 = new(2026, 11, 19);
		private static readonly DateTime Now = new(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);
		private static readonly (DateTime, int)[] None = Array.Empty<(DateTime, int)>();

		[Fact]
		public void Only_a_time_earlier_than_the_one_held_is_offered_earliest_first()
		{
			var free = new[] { (D2, 600), (D1, 660), (D1, 540), (D3, 540), (D3, 600) };
			var got = HospitalPortalWaitlistRules.Earlier(free, D3.AddMinutes(600), None);
			Assert.Equal(new[] { (D1, 540), (D1, 660), (D2, 600), (D3, 540) }, got);
		}

		[Fact]
		public void The_time_held_itself_and_later_ones_are_never_offered() =>
			Assert.Empty(HospitalPortalWaitlistRules.Earlier(new[] { (D3, 600), (D3, 615) }, D3.AddMinutes(600), None));

		[Fact]
		public void A_time_the_booking_declined_or_let_lapse_is_not_offered_again()
		{
			var got = HospitalPortalWaitlistRules.Earlier(new[] { (D1, 540), (D2, 600) }, D3.AddMinutes(600), new[] { (D1, 540) });
			Assert.Equal(new[] { (D2, 600) }, got);
		}

		[Fact]
		public void The_sweep_offers_only_a_time_freed_after_the_booking_joined_the_line()
		{
			Assert.True(HospitalPortalWaitlistRules.FreedForThisBooking(Now.AddMinutes(5), Now));
			Assert.True(HospitalPortalWaitlistRules.FreedForThisBooking(Now, Now));
			Assert.False(HospitalPortalWaitlistRules.FreedForThisBooking(Now.AddMinutes(-5), Now));   // already free when they booked: they passed it over
			Assert.False(HospitalPortalWaitlistRules.FreedForThisBooking(null, Now));                  // never booked: always free
		}

		[Fact]
		public void An_offer_is_answerable_only_while_open_and_inside_its_minutes()
		{
			Assert.True(HospitalPortalWaitlistRules.Answerable(HospitalPortalOfferStatus.Open, Now.AddMinutes(1), Now));
			Assert.False(HospitalPortalWaitlistRules.Answerable(HospitalPortalOfferStatus.Open, Now, Now));
			foreach (var s in new[] { HospitalPortalOfferStatus.Taken, HospitalPortalOfferStatus.Declined, HospitalPortalOfferStatus.Lapsed, HospitalPortalOfferStatus.Passed })
				Assert.False(HospitalPortalWaitlistRules.Answerable(s, Now.AddMinutes(30), Now));
		}

		[Fact]
		public void Minutes_left_round_up_and_stop_at_zero()
		{
			Assert.Equal(30, HospitalPortalWaitlistRules.MinutesLeft(Now.AddMinutes(29.2), Now));
			Assert.Equal(0, HospitalPortalWaitlistRules.MinutesLeft(Now.AddMinutes(-5), Now));
		}

		[Fact]
		public void Days_gained_are_the_distance_moved_forward() =>
			Assert.Equal(9, HospitalPortalWaitlistRules.DaysGained(D3.AddMinutes(600), D1.AddMinutes(600)));

		[Theory]
		[InlineData(540, 15, 550, 15, true)]
		[InlineData(540, 15, 555, 15, false)]
		[InlineData(600, 30, 570, 30, false)]
		[InlineData(600, 30, 580, 30, true)]
		public void A_visit_that_clashes_with_another_of_the_patient_is_not_offered(int a, int am, int b, int bm, bool clash) =>
			Assert.Equal(clash, HospitalPortalWaitlistRules.Overlaps(a, am, b, bm));

		[Theory]
		[InlineData(4, false)]
		[InlineData(5, true)]
		[InlineData(30, true)]
		[InlineData(240, true)]
		[InlineData(241, false)]
		public void The_offer_is_held_five_to_240_minutes(int minutes, bool ok) =>
			Assert.Equal(ok, HospitalPortalPolicy.SettingsRefusal(new HospitalPortalSettings { OfferMinutes = minutes }) == null);

		[Fact]
		public void The_default_hold_is_the_prototypes_thirty_minutes_and_a_booking_does_not_wait_unless_asked()
		{
			Assert.Equal(30, new HospitalPortalSettings().OfferMinutes);
			Assert.False(new HospitalPortalBooking().Waitlist);
			Assert.False(new HospitalPortalBookInput().Waitlist);
		}

		[Fact]
		public void The_slice_adds_only_keeps_no_token_and_holds_a_time_for_one_patient_at_a_time()
		{
			var s = HospitalSliceParity.Slice("hospital_h48_portal_waitlist.sql");
			Assert.DoesNotContain("DROP ", s, StringComparison.OrdinalIgnoreCase);
			Assert.Contains("TokenHash         nvarchar(64)", s);
			Assert.DoesNotContain(" Token ", s);
			Assert.Contains("CREATE UNIQUE INDEX UX_HospitalPortalOffers_OpenSlot ON dbo.HospitalPortalOffers (CompanyId, PractitionerId, OfferDate, StartMinute) WHERE Status = 'Open';", s);
			Assert.Contains("CREATE UNIQUE INDEX UX_HospitalPortalOffers_OpenBooking ON dbo.HospitalPortalOffers (BookingId) WHERE Status = 'Open';", s);
			Assert.Contains("CREATE UNIQUE INDEX IX_HospitalPortalOffers_TokenHash ON dbo.HospitalPortalOffers (TokenHash);", s);
			foreach (var st in new[] { HospitalPortalOfferStatus.Open, HospitalPortalOfferStatus.Taken, HospitalPortalOfferStatus.Declined, HospitalPortalOfferStatus.Lapsed, HospitalPortalOfferStatus.Passed })
				Assert.Contains("'" + st + "'", s);
			Assert.Equal(2, HospitalSliceParity.CheckedPrerequisites(s).Count);   // bookings, accounts
		}
	}
}
