using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H23 — the patient portal's booking rules, DATABASE-FREE: the defaults per clinic, what the settings
	// screen may save, which visits and doctors a patient is offered, and when booking and cancelling online stop.
	// ==========================================================================================
	public class HospitalPortalPolicyTests
	{
		private static HospitalPortalClinic Clinic(string mode = HospitalPortalModes.Doctor, bool own = true, bool referral = false, bool video = true, params string[] types) =>
			new(1, "IM", "الباطنة", "Internal medicine", mode, own, referral, video, types.Length == 0 ? new[] { HospitalVisitTypes.New, HospitalVisitTypes.FollowUp, HospitalVisitTypes.Video } : types, false);
		private static readonly DateTime Now = new(2026, 11, 4, 10, 0, 0);

		// ---------------- defaults (the approved prototype's) ----------------

		[Theory]
		[InlineData("IM", HospitalPortalModes.Doctor)]
		[InlineData("CARD", HospitalPortalModes.Doctor)]
		[InlineData("PED", HospitalPortalModes.Next)]
		[InlineData("DERM", HospitalPortalModes.Doctor)]
		[InlineData("ER", HospitalPortalModes.Off)]
		[InlineData("ENT", HospitalPortalModes.Doctor)]
		[InlineData("DENT", HospitalPortalModes.Request)]
		[InlineData("dent", HospitalPortalModes.Request)]
		[InlineData(null, HospitalPortalModes.Doctor)]
		public void Each_clinic_has_a_default_mode(string? code, string mode) => Assert.Equal(mode, HospitalPortalPolicy.Default(code).Mode);

		[Fact]
		public void Dentistry_takes_requests_for_new_and_follow_up_visits_only()
		{
			var r = HospitalPortalPolicy.Default("DENT");
			Assert.Equal(new[] { HospitalVisitTypes.New, HospitalVisitTypes.FollowUp }, HospitalPortalPolicy.ParseTypes(r.VisitTypes));
			Assert.False(r.Video);
			Assert.False(r.ReferralForNew);
		}

		[Theory]
		[InlineData("DENT", true)]
		[InlineData("dent", true)]
		[InlineData("IM", false)]
		[InlineData("DENTAL", false)]
		[InlineData(null, false)]
		public void Only_the_dentistry_clinic_books_on_chairs(string? code, bool dental) => Assert.Equal(dental, HospitalPortalPolicy.IsDental(code));

		[Fact]
		public void Cardiology_needs_a_referral_for_a_new_visit_by_default() => Assert.True(HospitalPortalPolicy.Default("CARD").ReferralForNew);

		[Fact]
		public void Every_default_passes_its_own_refusal()
		{
			foreach (var code in new[] { "IM", "CARD", "PED", "OBG", "ORTH", "DERM", "ER", "SURG", "ENT" })
			{
				var r = HospitalPortalPolicy.Default(code);
				Assert.Null(HospitalPortalPolicy.ClinicRefusal(r.Mode, HospitalPortalPolicy.ParseTypes(r.VisitTypes), r.Video));
			}
		}

		[Fact]
		public void Visit_types_are_parsed_known_only_and_saved_in_one_order()
		{
			Assert.Equal(new[] { HospitalVisitTypes.FollowUp, HospitalVisitTypes.New }, HospitalPortalPolicy.ParseTypes("FollowUp, New,Bogus,New"));
			Assert.Equal("New,FollowUp,Video", HospitalPortalPolicy.FormatTypes(new[] { HospitalVisitTypes.Video, HospitalVisitTypes.FollowUp, HospitalVisitTypes.New }));
		}

		// ---------------- what may be saved ----------------

		[Theory]
		[InlineData(0, 2, 24, 3, "None")]
		[InlineData(366, 2, 24, 3, "None")]
		[InlineData(60, -1, 24, 3, "None")]
		[InlineData(60, 73, 24, 3, "None")]
		[InlineData(60, 2, 169, 3, "None")]
		[InlineData(60, 2, 24, 0, "None")]
		[InlineData(60, 2, 24, 21, "None")]
		[InlineData(60, 2, 24, 3, "Card")]
		public void Out_of_range_general_rules_are_refused(int window, int lead, int cancel, int max, string payment) =>
			Assert.NotNull(HospitalPortalPolicy.SettingsRefusal(new HospitalPortalSettings { WindowDays = window, LeadHours = lead, CancelHours = cancel, MaxActive = max, Payment = payment }));

		[Fact]
		public void The_default_general_rules_are_accepted() => Assert.Null(HospitalPortalPolicy.SettingsRefusal(new HospitalPortalSettings()));

		[Fact]
		public void A_clinic_needs_a_known_mode_and_a_visit_type()
		{
			Assert.NotNull(HospitalPortalPolicy.ClinicRefusal("Sometimes", new[] { HospitalVisitTypes.New }, false));
			Assert.NotNull(HospitalPortalPolicy.ClinicRefusal(HospitalPortalModes.Doctor, Array.Empty<string>(), false));
		}

		[Fact]
		public void A_video_visit_is_offered_only_with_video_on() =>
			Assert.NotNull(HospitalPortalPolicy.ClinicRefusal(HospitalPortalModes.Doctor, new[] { HospitalVisitTypes.Video }, false));

		// ---------------- what a patient is offered ----------------

		[Fact]
		public void A_clinic_booked_by_phone_offers_nothing_online() =>
			Assert.NotNull(HospitalPortalPolicy.OfferRefusal(Clinic(HospitalPortalModes.Off), HospitalVisitTypes.New, false));

		[Fact]
		public void A_visit_type_the_clinic_does_not_offer_is_refused() =>
			Assert.NotNull(HospitalPortalPolicy.OfferRefusal(Clinic(types: HospitalVisitTypes.New), HospitalVisitTypes.FollowUp, true));

		[Fact]
		public void A_follow_up_is_for_patients_already_seen_in_the_clinic()
		{
			Assert.NotNull(HospitalPortalPolicy.OfferRefusal(Clinic(), HospitalVisitTypes.FollowUp, seenInClinic: false));
			Assert.Null(HospitalPortalPolicy.OfferRefusal(Clinic(), HospitalVisitTypes.FollowUp, seenInClinic: true));
		}

		private static readonly HospitalPortalDoctor[] Docs =
		{
			new(10, 1, Online: true, NewPatients: true),
			new(11, 1, Online: true, NewPatients: false),
			new(12, 1, Online: false, NewPatients: true),
			new(20, 2, Online: true, NewPatients: true),
		};

		[Fact]
		public void Only_online_doctors_of_the_clinic_are_offered() =>
			Assert.Equal(new[] { 10, 11 }, HospitalPortalPolicy.DoctorsFor(Clinic(own: false), HospitalVisitTypes.Video, Array.Empty<int>(), Docs));

		[Fact]
		public void A_follow_up_stays_with_the_patients_own_doctor_when_the_clinic_says_so()
		{
			Assert.Equal(new[] { 11 }, HospitalPortalPolicy.DoctorsFor(Clinic(own: true), HospitalVisitTypes.FollowUp, new[] { 11 }, Docs));
			Assert.Equal(new[] { 10, 11 }, HospitalPortalPolicy.DoctorsFor(Clinic(own: false), HospitalVisitTypes.FollowUp, new[] { 11 }, Docs));
		}

		[Fact]
		public void A_new_visit_skips_a_doctor_closed_to_new_patients_unless_already_theirs()
		{
			Assert.Equal(new[] { 10 }, HospitalPortalPolicy.DoctorsFor(Clinic(), HospitalVisitTypes.New, Array.Empty<int>(), Docs));
			Assert.Equal(new[] { 10, 11 }, HospitalPortalPolicy.DoctorsFor(Clinic(), HospitalVisitTypes.New, new[] { 11 }, Docs));
		}

		// ---------------- booking and cancelling online ----------------

		private static readonly HospitalPortalSettings S = new() { WindowDays = 60, LeadHours = 2, CancelHours = 24, MaxActive = 3 };

		[Fact]
		public void A_slot_inside_the_minimum_notice_is_refused() =>
			Assert.NotNull(HospitalPortalPolicy.BookingRefusal(S, Clinic(), HospitalVisitTypes.New, Now.AddMinutes(90), Now, 0, null));

		[Fact]
		public void A_slot_beyond_the_window_is_refused() =>
			Assert.NotNull(HospitalPortalPolicy.BookingRefusal(S, Clinic(), HospitalVisitTypes.New, Now.AddDays(61), Now, 0, null));

		[Fact]
		public void The_open_bookings_limit_is_enforced()
		{
			Assert.NotNull(HospitalPortalPolicy.BookingRefusal(S, Clinic(), HospitalVisitTypes.New, Now.AddDays(1), Now, 3, null));
			Assert.Null(HospitalPortalPolicy.BookingRefusal(S, Clinic(), HospitalVisitTypes.New, Now.AddDays(1), Now, 2, null));
		}

		[Fact]
		public void A_new_visit_in_a_referral_clinic_needs_the_referral()
		{
			Assert.NotNull(HospitalPortalPolicy.BookingRefusal(S, Clinic(referral: true), HospitalVisitTypes.New, Now.AddDays(1), Now, 0, " "));
			Assert.Null(HospitalPortalPolicy.BookingRefusal(S, Clinic(referral: true), HospitalVisitTypes.New, Now.AddDays(1), Now, 0, "REF-1"));
			Assert.Null(HospitalPortalPolicy.BookingRefusal(S, Clinic(referral: true), HospitalVisitTypes.FollowUp, Now.AddDays(1), Now, 0, null));
		}

		[Fact]
		public void Changing_online_stops_at_the_cut_off()
		{
			Assert.True(HospitalPortalPolicy.CanChangeOnline(S, Now.AddHours(24), Now));
			Assert.False(HospitalPortalPolicy.CanChangeOnline(S, Now.AddHours(23), Now));
		}

		// ---------------- the model against its slice ----------------

		private static string Slice23() => HospitalSliceParity.Slice("hospital_h23_portal_rules.sql");
		// H48 adds the settings' OfferMinutes (ALTER)
		private static string Slice() => Slice23() + Environment.NewLine + HospitalSliceParity.Slice("hospital_h48_portal_waitlist.sql");
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPortalRulesModel.Configure);

		public static IEnumerable<object[]> Tables() => new[]
		{
			new object[] { typeof(HospitalPortalSettings), "HospitalPortalSettings" }, new object[] { typeof(HospitalPortalClinicRule), "HospitalPortalClinicRules" },
			new object[] { typeof(HospitalPortalDoctorRule), "HospitalPortalDoctorRules" },
		};

		[Theory]
		[MemberData(nameof(Tables))]
		public void The_model_names_the_tables_its_slice_creates(Type entity, string table) => HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);

		[Theory]
		[MemberData(nameof(Tables))]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table) => HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);

		[Fact]
		public void Every_table_the_slice_references_but_does_not_create_is_a_checked_prerequisite() =>
			Assert.Equal(2, HospitalSliceParity.CheckedPrerequisites(Slice23()).Count);   // departments, practitioners

		[Fact]
		public void The_slice_alters_no_existing_table() => Assert.DoesNotContain("ALTER TABLE", Slice23(), StringComparison.OrdinalIgnoreCase);   // H23's own; H48 adds OfferMinutes by design
	}
}
