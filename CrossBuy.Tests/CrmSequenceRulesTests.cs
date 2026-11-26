using Xunit;
using S = CrossBuy.BL.CrmIdentity.CrmSequenceRules;

namespace CrossBuy.Tests
{
	// CRM C6 — sequence timing, message filling, validation, the stop rule and booking slots.
	public class CrmSequenceRulesTests
	{
		// Kuwait: Friday and Saturday off; 2026-11-12 is a Thursday
		static bool Work(DateTime d) => d.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday) && d != new DateTime(2026, 11, 15);
		static readonly TimeSpan Eight = TimeSpan.FromHours(8);

		[Fact]
		public void Day_zero_runs_at_enrolment_and_later_days_at_the_start_of_the_working_day()
		{
			var enrolled = new DateTime(2026, 11, 10, 14, 30, 0);   // Tuesday
			Assert.Equal(enrolled, S.DueAt(enrolled, 0, Work, Eight, true));
			Assert.Equal(new DateTime(2026, 11, 11, 8, 0, 0), S.DueAt(enrolled, 1, Work, Eight, true));
		}

		[Fact]
		public void A_rest_day_or_holiday_moves_the_step_to_the_next_working_day_unless_told_not_to()
		{
			var thu = new DateTime(2026, 11, 12, 10, 0, 0);
			// +1 = Friday, Saturday off, Sunday 15th is a holiday → Monday 16th
			Assert.Equal(new DateTime(2026, 11, 16, 8, 0, 0), S.DueAt(thu, 1, Work, Eight, true));
			Assert.Equal(new DateTime(2026, 11, 13, 8, 0, 0), S.DueAt(thu, 1, Work, Eight, false));
			// enrolled ON a rest day: day 0 waits for the next working morning
			Assert.Equal(new DateTime(2026, 11, 16, 8, 0, 0), S.DueAt(new DateTime(2026, 11, 13, 20, 0, 0), 0, Work, Eight, true));
			// a calendar with no working day never parks a step forever
			Assert.Equal(new DateTime(2026, 12, 12, 8, 0, 0), S.DueAt(thu, 0, _ => false, Eight, true));
		}

		[Fact]
		public void Tokens_fill_and_a_missing_value_is_empty_never_raw_braces()
		{
			var v = new Dictionary<string, string?> { ["name"] = "هبة يوسف", ["first"] = "هبة", ["rep"] = "سارة", ["company"] = null };
			Assert.Equal("أهلاً هبة، معك سارة من ", S.Render("أهلاً {first}، معك {REP} من {company}", v));
			Assert.Equal("رابط: ", S.Render("رابط: {booking}", v));
			Assert.Equal("{other} stays", S.Render("{other} stays", v));
			Assert.Equal("هبة", S.FirstName("  هبة   يوسف "));
		}

		[Fact]
		public void Steps_must_be_complete_and_in_day_order()
		{
			S.Step E(int d) => new(d, "Email", "s", "b", null);
			Assert.Null(S.StepsRefusal(new[] { E(0), new S.Step(1, "Call", "اتصل", null, null), new S.Step(3, "WhatsApp", null, null, "welcome_back") }));
			Assert.NotNull(S.StepsRefusal(Array.Empty<S.Step>()));
			Assert.NotNull(S.StepsRefusal(new[] { E(3), E(1) }));
			Assert.NotNull(S.StepsRefusal(new[] { new S.Step(0, "WhatsApp", null, "body", null) }));
			Assert.NotNull(S.StepsRefusal(new[] { new S.Step(0, "Email", "", "b", null) }));
			Assert.NotNull(S.StepsRefusal(new[] { new S.Step(0, "Fax", "s", "b", null) }));
			Assert.NotNull(S.StepsRefusal(Enumerable.Range(0, 13).Select(E).ToArray()));
		}

		[Fact]
		public void The_strongest_thing_that_happened_names_the_stop()
		{
			Assert.Equal("Converted", S.StopReason(true, true, true, true));
			Assert.Equal("Booked", S.StopReason(false, true, true, false));
			Assert.Equal("Replied", S.StopReason(false, false, true, true));
			Assert.Equal("OptOut", S.StopReason(false, false, false, true));
			Assert.Null(S.StopReason(false, false, false, false));
		}

		[Fact]
		public void Slots_respect_hours_notice_and_busy_time_with_its_buffer()
		{
			var day = new DateTime(2026, 11, 16);
			var all = S.Slots(day, TimeSpan.FromHours(9), TimeSpan.FromHours(11), 30, 0, Array.Empty<(DateTime, DateTime)>(), DateTime.MinValue);
			Assert.Equal(new[] { 9.0, 9.5, 10.0, 10.5 }, all.Select(t => t.TimeOfDay.TotalHours));
			// a meeting 10:00–10:30 with a 15-minute buffer blocks 9:30 (ends 10:00 + buffer) through 10:30
			var busy = new[] { (day.AddHours(10), day.AddHours(10.5)) };
			var free = S.Slots(day, TimeSpan.FromHours(9), TimeSpan.FromHours(12), 30, 15, busy, DateTime.MinValue);
			Assert.Equal(new[] { 9.0, 11.25 }, free.Select(t => t.TimeOfDay.TotalHours));
			// nothing before the notice period
			Assert.Equal(new[] { 10.5 }, S.Slots(day, TimeSpan.FromHours(9), TimeSpan.FromHours(11), 30, 0, Array.Empty<(DateTime, DateTime)>(), day.AddHours(10.25)).Select(t => t.TimeOfDay.TotalHours));
			Assert.Empty(S.Slots(day, TimeSpan.FromHours(11), TimeSpan.FromHours(9), 30, 0, Array.Empty<(DateTime, DateTime)>(), DateTime.MinValue));
		}
	}
}
