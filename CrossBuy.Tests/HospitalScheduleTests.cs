using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H26 — a doctor's times on a day, with no database: H4's single window until the weekly schedule is
	// started, then each weekday's sessions (two of them, each with its own minutes per patient), less the leave;
	// the refusals the schedule screen saves through; and the model against its slice.
	// ==========================================================================================
	public class HospitalScheduleTests
	{
		private static readonly DateTime Wed = new(2026, 11, 4);   // a Wednesday (3)
		private static HospitalPractitioner Doc(string days = "0,1,2,3,4") =>
			new() { ID = 7, IsActive = true, WorkDays = days, SessionStartMinute = 9 * 60, SessionEndMinute = 11 * 60, SlotMinutes = 30 };
		private static HospitalPractitionerSession Session(DayOfWeek d, int from, int to, int slot) =>
			new() { PractitionerId = 7, DayOfWeek = (byte)d, StartMinute = from, EndMinute = to, SlotMinutes = slot };
		private static HospitalPractitionerLeave Leave(DateTime from, DateTime to, int? start = null, int? end = null) =>
			new() { PractitionerId = 7, FromDate = from, ToDate = to, StartMinute = start, EndMinute = end };

		// ---------------- before the weekly schedule: H4's window ----------------

		[Fact]
		public void Without_sessions_the_doctor_books_by_the_single_window()
		{
			var s = new HospitalDoctorSchedule(Doc());
			Assert.False(s.Weekly);
			Assert.Equal(new[] { 540, 570, 600, 630 }, s.SlotsOn(Wed).Select(x => x.Minute));
			Assert.All(s.SlotsOn(Wed), x => Assert.Equal(30, x.Duration));
			Assert.False(new HospitalDoctorSchedule(Doc("0,1,2")).WorksOn(Wed));
		}

		// ---------------- the weekly schedule ----------------

		[Fact]
		public void Two_sessions_of_one_day_each_keep_their_own_minutes_per_patient()
		{
			var s = new HospitalDoctorSchedule(Doc(), new[] { Session(DayOfWeek.Wednesday, 17 * 60, 18 * 60, 20), Session(DayOfWeek.Wednesday, 8 * 60, 9 * 60, 30) });
			Assert.True(s.Weekly);
			Assert.Equal(new[] { 480, 510, 1020, 1040, 1060 }, s.SlotsOn(Wed).Select(x => x.Minute));
			Assert.Equal(30, s.DurationAt(Wed, 510));
			Assert.Equal(20, s.DurationAt(Wed, 1040));
			Assert.Null(s.DurationAt(Wed, 1080));   // the session's end is not a start
			Assert.Null(s.DurationAt(Wed, 600));    // between the sessions
		}

		[Fact]
		public void Once_weekly_the_old_working_days_no_longer_apply()
		{
			// H4 says Sunday–Thursday; the weekly schedule has only Saturday: Wednesday is now a day off
			var s = new HospitalDoctorSchedule(Doc(), new[] { Session(DayOfWeek.Saturday, 600, 720, 15) });
			Assert.False(s.WorksOn(Wed));
			Assert.True(s.WorksOn(Wed.AddDays(3)));
			Assert.Empty(s.SessionsOn(DayOfWeek.Wednesday));
		}

		[Fact]
		public void A_slot_that_does_not_fit_before_the_session_ends_is_not_offered()
		{
			var s = new HospitalDoctorSchedule(Doc(), new[] { Session(DayOfWeek.Wednesday, 600, 650, 20) });
			Assert.Equal(new[] { 600, 620 }, s.SlotsOn(Wed).Select(x => x.Minute));   // 640+20 > 650
		}

		[Fact]
		public void An_inactive_doctor_has_no_times()
		{
			var d = Doc(); d.IsActive = false;
			Assert.Empty(new HospitalDoctorSchedule(d, new[] { Session(DayOfWeek.Wednesday, 600, 720, 15) }).SlotsOn(Wed));
		}

		// ---------------- leave ----------------

		[Fact]
		public void A_whole_day_leave_closes_every_day_it_covers_and_only_those()
		{
			var s = new HospitalDoctorSchedule(Doc(), null, new[] { Leave(Wed, Wed.AddDays(1)) });
			Assert.Empty(s.SlotsOn(Wed));
			Assert.Empty(s.SlotsOn(Wed.AddDays(1)));
			Assert.True(s.WholeDayLeave(Wed));
			Assert.NotEmpty(s.SlotsOn(Wed.AddDays(-1)));
			Assert.NotEmpty(s.SlotsOn(Wed.AddDays(4)));   // Sunday after
		}

		[Fact]
		public void A_part_day_leave_removes_only_the_slots_it_touches()
		{
			// 9:00–11:00 by 30; leave 9:45–10:15 touches 9:30 (ends 10:00) and 10:00 (ends 10:30)
			var s = new HospitalDoctorSchedule(Doc(), null, new[] { Leave(Wed, Wed, 585, 615) });
			Assert.Equal(new[] { 540, 630 }, s.SlotsOn(Wed).Select(x => x.Minute));
			Assert.False(s.WholeDayLeave(Wed));
			Assert.True(s.WorksOn(Wed));
			Assert.Null(s.DurationAt(Wed, 570));
		}

		[Fact]
		public void A_leave_ending_where_a_slot_starts_leaves_that_slot()
		{
			var s = new HospitalDoctorSchedule(Doc(), null, new[] { Leave(Wed, Wed, 540, 570) });
			Assert.Equal(new[] { 570, 600, 630 }, s.SlotsOn(Wed).Select(x => x.Minute));
		}

		// ---------------- on duty now (whom the lab's critical alerts reach first) ----------------

		[Fact]
		public void On_duty_is_inside_a_session_and_outside_any_leave()
		{
			var s = new HospitalDoctorSchedule(Doc(), new[] { Session(DayOfWeek.Wednesday, 8 * 60, 12 * 60, 15), Session(DayOfWeek.Wednesday, 17 * 60, 20 * 60, 20) },
				new[] { Leave(Wed, Wed, 18 * 60, 19 * 60) });
			Assert.True(s.OnDutyAt(Wed.AddHours(8)));
			Assert.False(s.OnDutyAt(Wed.AddHours(12)));              // the morning session's end
			Assert.False(s.OnDutyAt(Wed.AddHours(15)));              // between the sessions
			Assert.True(s.OnDutyAt(Wed.AddHours(17).AddMinutes(30)));
			Assert.False(s.OnDutyAt(Wed.AddHours(18).AddMinutes(30)));   // inside the part-day leave
			Assert.True(s.OnDutyAt(Wed.AddHours(19)));               // the leave's end
			Assert.False(new HospitalDoctorSchedule(Doc(), null, new[] { Leave(Wed, Wed) }).OnDutyAt(Wed.AddHours(10)));
		}

		[Fact]
		public void Without_sessions_on_duty_is_the_single_window_as_before()
		{
			Assert.True(HospitalLabAlertService.OnDuty(Doc(), Wed.AddHours(9)));
			Assert.False(HospitalLabAlertService.OnDuty(Doc(), Wed.AddHours(11)));
			Assert.False(HospitalLabAlertService.OnDuty(Doc("0,1,2"), Wed.AddHours(9)));
		}

		// ---------------- what the schedule screen may save ----------------

		[Theory]
		[InlineData(7, 600, 720, 15, "Choose the day")]
		[InlineData(-1, 600, 720, 15, "Choose the day")]
		[InlineData(3, 600, 720, 4, "A slot is 5 to 120 minutes")]
		[InlineData(3, 600, 720, 121, "A slot is 5 to 120 minutes")]
		[InlineData(3, 720, 600, 15, "The session must start before it ends and hold at least one slot")]
		[InlineData(3, 600, 610, 15, "The session must start before it ends and hold at least one slot")]
		[InlineData(3, 1400, 1450, 15, "The session must start before it ends and hold at least one slot")]
		[InlineData(3, 1020, 1260, 15, null)]
		[InlineData(3, 600, 615, 15, null)]
		public void A_session_is_refused_when_it_does_not_hold_together(int day, int start, int end, int slot, string? why) =>
			Assert.Equal(why, HospitalDoctorSchedule.SessionRefusal(day, start, end, slot, Array.Empty<(int, int)>()));

		[Fact]
		public void Two_sessions_of_one_day_may_touch_but_not_overlap()
		{
			var morning = new[] { (480, 720) };
			Assert.Equal("This session overlaps another one on the same day", HospitalDoctorSchedule.SessionRefusal(3, 700, 900, 15, morning));
			Assert.Equal("This session overlaps another one on the same day", HospitalDoctorSchedule.SessionRefusal(3, 400, 1000, 15, morning));
			Assert.Null(HospitalDoctorSchedule.SessionRefusal(3, 720, 900, 15, morning));
		}

		[Fact]
		public void A_leave_is_refused_when_it_does_not_hold_together()
		{
			Assert.Equal("Enter the dates", HospitalDoctorSchedule.LeaveRefusal(default, Wed, null, null));
			Assert.Equal("The end date is before the start date", HospitalDoctorSchedule.LeaveRefusal(Wed, Wed.AddDays(-1), null, null));
			Assert.Equal("A leave is at most a year", HospitalDoctorSchedule.LeaveRefusal(Wed, Wed.AddDays(400), null, null));
			Assert.Equal("Enter both times, or neither for whole days", HospitalDoctorSchedule.LeaveRefusal(Wed, Wed, 600, null));
			Assert.Equal("The leave must start before it ends", HospitalDoctorSchedule.LeaveRefusal(Wed, Wed, 600, 600));
			Assert.Equal("The leave must start before it ends", HospitalDoctorSchedule.LeaveRefusal(Wed, Wed, -5, 600));
			Assert.Null(HospitalDoctorSchedule.LeaveRefusal(Wed, Wed, null, null));
			Assert.Null(HospitalDoctorSchedule.LeaveRefusal(Wed, Wed.AddDays(6), 600, 720));
		}

		// ---------------- the portal reads the same schedule ----------------

		[Fact]
		public void The_portal_offers_the_weekly_sessions_less_the_leave()
		{
			var settings = new HospitalPortalSettings { WindowDays = 30, LeadHours = 0 };
			var s = new HospitalDoctorSchedule(Doc(), new[] { Session(DayOfWeek.Thursday, 1020, 1080, 20) }, new[] { Leave(Wed.AddDays(1), Wed.AddDays(1), 1020, 1040) });
			Assert.Equal(new[] { 1040, 1060 }, HospitalPortalPolicy.FreeSlots(s, Wed.AddDays(1), Array.Empty<int>(), settings, Wed));
			Assert.False(HospitalPortalPolicy.WorksOn(s, Wed, settings, Wed));
		}

		// ---------------- the model against its slice ----------------

		private static string Slice() => HospitalSliceParity.Slice("hospital_h26_doctor_schedule.sql");
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPractitionerScheduleModel.Configure);

		public static TheoryData<Type, string> Tables => new()
		{
			{ typeof(HospitalPractitionerSession), "HospitalPractitionerSessions" },
			{ typeof(HospitalPractitionerLeave), "HospitalPractitionerLeaves" },
		};

		[Theory, MemberData(nameof(Tables))]
		public void The_model_names_the_tables_its_slice_creates(Type entity, string table) => HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);

		[Theory, MemberData(nameof(Tables))]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table) => HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);

		[Fact]
		public void The_slice_checks_its_prerequisite_and_alters_no_existing_table()
		{
			Assert.Single(HospitalSliceParity.CheckedPrerequisites(Slice()));   // practitioners
			Assert.DoesNotContain("ALTER TABLE", Slice(), StringComparison.OrdinalIgnoreCase);
		}
	}
}
