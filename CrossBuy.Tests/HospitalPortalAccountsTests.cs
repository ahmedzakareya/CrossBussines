using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H24 — the portal booking engine's decisions, DATABASE-FREE: the free slots it offers (H4's own slot
	// rules plus the portal's notice and window), what an account may do, whom it may book for, the request
	// refusals, and the model against its slice.
	// ==========================================================================================
	public class HospitalPortalAccountsTests
	{
		private static readonly DateTime Now = new(2026, 11, 4, 10, 0, 0);   // a Wednesday
		private static readonly HospitalPortalSettings S = new() { WindowDays = 30, LeadHours = 2 };
		private static HospitalPractitioner Doc(string days = "0,1,2,3,4", DateTime? licence = null) =>
			new() { ID = 1, IsActive = true, WorkDays = days, SessionStartMinute = 9 * 60, SessionEndMinute = 13 * 60, SlotMinutes = 30, LicenseExpiry = licence };

		// ---------------- free slots ----------------

		[Fact]
		public void Free_slots_are_the_doctors_session_less_what_is_taken()
		{
			var tomorrow = Now.Date.AddDays(1);   // Thursday
			var free = HospitalPortalPolicy.FreeSlots(new HospitalDoctorSchedule(Doc()), tomorrow, new[] { 9 * 60, 10 * 60 }, S, Now);
			Assert.Equal(new[] { 570, 630, 660, 690, 720, 750 }, free);
		}

		[Fact]
		public void Today_starts_after_the_minimum_notice()
		{
			var free = HospitalPortalPolicy.FreeSlots(new HospitalDoctorSchedule(Doc()), Now.Date, Array.Empty<int>(), S, Now);   // 10:00 now, 2 h notice
			Assert.Equal(12 * 60, free.First());
		}

		[Fact]
		public void A_day_the_doctor_does_not_work_has_no_slots() =>
			Assert.Empty(HospitalPortalPolicy.FreeSlots(new HospitalDoctorSchedule(Doc("0,1,2")), Now.Date.AddDays(1), Array.Empty<int>(), S, Now));   // Thursday is 4

		[Fact]
		public void An_expired_licence_stops_booking() =>
			Assert.Empty(HospitalPortalPolicy.FreeSlots(new HospitalDoctorSchedule(Doc(licence: Now.Date)), Now.Date.AddDays(1), Array.Empty<int>(), S, Now));

		[Fact]
		public void Beyond_the_window_or_in_the_past_there_are_no_slots()
		{
			Assert.Empty(HospitalPortalPolicy.FreeSlots(new HospitalDoctorSchedule(Doc("0,1,2,3,4,5,6")), Now.Date.AddDays(31), Array.Empty<int>(), S, Now));
			Assert.Empty(HospitalPortalPolicy.FreeSlots(new HospitalDoctorSchedule(Doc("0,1,2,3,4,5,6")), Now.Date.AddDays(-1), Array.Empty<int>(), S, Now));
		}

		// the owner, 2026-11-04: the whole session shows — another patient's time as booked, a too-soon one as unavailable
		[Fact]
		public void The_whole_session_shows_each_time_in_its_state()
		{
			var states = HospitalPortalPolicy.DaySlots(new HospitalDoctorSchedule(Doc()), Now.Date, new[] { 12 * 60 }, S, Now).ToDictionary(x => x.Minute, x => x.State);
			Assert.Equal(8, states.Count);                                                   // 09:00 … 12:30, every 30 minutes
			Assert.Equal(HospitalPortalPolicy.SlotUnavailable, states[9 * 60]);              // past
			Assert.Equal(HospitalPortalPolicy.SlotUnavailable, states[11 * 60 + 30]);        // inside the 2 h notice
			Assert.Equal(HospitalPortalPolicy.SlotBooked, states[12 * 60]);                  // another patient's
			Assert.Equal(HospitalPortalPolicy.SlotFree, states[12 * 60 + 30]);
		}

		[Fact]
		public void Free_slots_are_exactly_the_free_states_of_the_session()
		{
			var day = Now.Date.AddDays(1);
			var free = HospitalPortalPolicy.DaySlots(new HospitalDoctorSchedule(Doc()), day, new[] { 600 }, S, Now).Where(x => x.State == HospitalPortalPolicy.SlotFree).Select(x => x.Minute);
			Assert.Equal(free, HospitalPortalPolicy.FreeSlots(new HospitalDoctorSchedule(Doc()), day, new[] { 600 }, S, Now));
		}

		[Fact]
		public void A_day_off_has_no_session_and_is_not_a_working_day()
		{
			Assert.Empty(HospitalPortalPolicy.DaySlots(new HospitalDoctorSchedule(Doc("0,1,2")), Now.Date.AddDays(1), Array.Empty<int>(), S, Now));
			Assert.False(HospitalPortalPolicy.WorksOn(new HospitalDoctorSchedule(Doc("0,1,2")), Now.Date.AddDays(1), S, Now));
			Assert.True(HospitalPortalPolicy.WorksOn(new HospitalDoctorSchedule(Doc()), Now.Date.AddDays(1), S, Now));
		}

		[Fact]
		public void An_inactive_doctor_has_no_slots()
		{
			var d = Doc(); d.IsActive = false;
			Assert.Empty(HospitalPortalPolicy.FreeSlots(new HospitalDoctorSchedule(d), Now.Date.AddDays(1), Array.Empty<int>(), S, Now));
		}

		// ---------------- the account ----------------

		[Fact]
		public void A_disabled_account_does_nothing()
		{
			Assert.NotNull(HospitalPortalPolicy.AccountRefusal(HospitalPortalAccountStatus.Disabled, isRequest: false));
			Assert.NotNull(HospitalPortalPolicy.AccountRefusal(HospitalPortalAccountStatus.Disabled, isRequest: true));
		}

		[Fact]
		public void An_unverified_account_sends_requests_but_does_not_book()
		{
			Assert.NotNull(HospitalPortalPolicy.AccountRefusal(HospitalPortalAccountStatus.Unverified, isRequest: false));
			Assert.Null(HospitalPortalPolicy.AccountRefusal(HospitalPortalAccountStatus.Unverified, isRequest: true));
		}

		[Fact]
		public void A_verified_account_books() => Assert.Null(HospitalPortalPolicy.AccountRefusal(HospitalPortalAccountStatus.Verified, isRequest: false));

		[Fact]
		public void An_unknown_status_is_refused() => Assert.NotNull(HospitalPortalPolicy.AccountRefusal("Pending", isRequest: true));

		[Fact]
		public void An_account_books_for_its_own_file_and_its_family_only_when_allowed()
		{
			Assert.True(HospitalPortalPolicy.MayBookFor(5, new[] { 7 }, familyBooking: true, patientId: 5));
			Assert.True(HospitalPortalPolicy.MayBookFor(5, new[] { 7 }, familyBooking: true, patientId: 7));
			Assert.False(HospitalPortalPolicy.MayBookFor(5, new[] { 7 }, familyBooking: false, patientId: 7));
			Assert.False(HospitalPortalPolicy.MayBookFor(5, new[] { 7 }, familyBooking: true, patientId: 9));
			Assert.False(HospitalPortalPolicy.MayBookFor(null, Array.Empty<int>(), familyBooking: true, patientId: 0));
		}

		// ---------------- requests ----------------

		[Theory]
		[InlineData(null, "Sunday")]
		[InlineData("Noon", "Sunday")]
		[InlineData("Morning", null)]
		[InlineData("Morning", " ")]
		[InlineData("Morning", "a")]
		public void A_request_needs_a_time_of_day_and_the_days(string? time, string? days) => Assert.NotNull(HospitalPortalPolicy.RequestRefusal(time, days));

		[Fact]
		public void A_complete_request_is_accepted() => Assert.Null(HospitalPortalPolicy.RequestRefusal(HospitalPortalTimeOfDay.Evening, "Sunday or Tuesday"));

		// ---------------- the model against its slice ----------------

		// H46 adds the account's Email and PasswordChangedAt (ALTER) and the reset links' table; H48 the booking's
		// Waitlist (ALTER) and the offers' table
		private static string Slice() => HospitalSliceParity.Slice("hospital_h24_portal_accounts.sql") + Environment.NewLine + HospitalSliceParity.Slice("hospital_h46_portal_password.sql")
			+ Environment.NewLine + HospitalSliceParity.Slice("hospital_h48_portal_waitlist.sql") + Environment.NewLine + HospitalSliceParity.Slice("hospital_h251_portal_proxy_payment.sql");   // H251 proxy scope and end date
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPortalAccountsModel.Configure);

		public static IEnumerable<object[]> Tables() => new[]
		{
			new object[] { typeof(HospitalPortalAccount), "HospitalPortalAccounts" }, new object[] { typeof(HospitalPortalFamilyLink), "HospitalPortalFamilyLinks" },
			new object[] { typeof(HospitalPortalBooking), "HospitalPortalBookings" }, new object[] { typeof(HospitalPortalRequest), "HospitalPortalRequests" },
			new object[] { typeof(HospitalPortalReset), "HospitalPortalResets" }, new object[] { typeof(HospitalPortalOffer), "HospitalPortalOffers" },
		};

		[Theory]
		[MemberData(nameof(Tables))]
		public void The_model_names_the_tables_its_slice_creates(Type entity, string table) => HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);

		[Theory]
		[MemberData(nameof(Tables))]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table) => HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);

		[Theory]
		[InlineData("UX_HospitalPortalAccounts_UserId", "[UserId] IS NOT NULL", "(UserId)")]
		[InlineData("UX_HospitalPortalAccounts_Patient", "[PatientId] IS NOT NULL", "(CompanyId, PatientId)")]
		public void One_sign_in_and_one_file_per_account_in_model_and_slice(string name, string filter, string keys)
		{
			var ix = Model().FindEntityType(typeof(HospitalPortalAccount))!.GetIndexes().Single(i => i.GetDatabaseName() == name);
			Assert.True(ix.IsUnique);
			Assert.Equal(filter, ix.GetFilter());
			Assert.Contains($"CREATE UNIQUE INDEX {name} ON dbo.HospitalPortalAccounts {keys} WHERE {filter};", Slice().Split('\n').Select(l => l.Trim()));
		}

		[Fact]
		public void A_portal_booking_points_at_one_appointment_once() =>
			Assert.True(Model().FindEntityType(typeof(HospitalPortalBooking))!.GetIndexes().Single(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(HospitalPortalBooking.AppointmentId)).IsUnique);

		[Fact]
		public void Every_table_the_slice_references_but_does_not_create_is_a_checked_prerequisite() =>
			Assert.Equal(3, HospitalSliceParity.CheckedPrerequisites(Slice()).Count);   // patients, departments, appointments

		[Fact]
		public void The_slice_alters_no_existing_table() => Assert.DoesNotContain("ALTER TABLE", HospitalSliceParity.Slice("hospital_h24_portal_accounts.sql"), StringComparison.OrdinalIgnoreCase);   // H24's own; H46 adds two columns by design
	}
}
