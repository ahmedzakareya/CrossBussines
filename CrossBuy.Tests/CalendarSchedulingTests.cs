using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context.Calendar;
using Xunit;

namespace CrossBuy.Tests
{
    // ==========================================================================================
    // CALENDAR RECURRENCE — the expansion, tested without a database because it is pure.
    //
    // The test that matters most is the DST one. A weekly meeting expanded by adding 7×24 hours to a
    // UTC instant drifts by an hour twice a year, and nobody reports that as a bug — they just start
    // arriving at the wrong time. So it is asserted explicitly.
    // ==========================================================================================
    public class CalendarRecurrenceTests
    {
        private static CalendarEvent Event(DateTime startUtc, TimeSpan? length = null, string title = "Standup") => new()
        {
            Id = 1, CompanyID = 1, Title = title, StartAt = startUtc,
            EndAt = length.HasValue ? startUtc + length.Value : null
        };

        private static CalendarEventSchedule Weekly(string? zone = "UTC", int interval = 1, string? days = null,
            int? count = null, DateTime? until = null) => new()
        {
            CompanyId = 1, EventId = 1, TimeZoneId = zone, RecurrenceKind = RecurrenceKinds.Weekly,
            Interval = interval, ByWeekdays = days, OccurrenceCount = count, UntilLocalDate = until
        };

        [Fact]
        public void An_event_with_no_schedule_is_a_single_occurrence()
        {
            var ev = Event(new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1));

            var occ = CalendarSchedulingService.Expand(ev, null,
                new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.Single(occ);
            Assert.False(occ[0].IsRepeat);
            Assert.Equal(ev.StartAt, occ[0].StartUtc);
        }

        [Fact]
        public void A_weekly_series_repeats_on_the_same_weekday()
        {
            var ev = Event(new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1)); // a Monday

            var occ = CalendarSchedulingService.Expand(ev, Weekly(count: 4),
                new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc));

            Assert.Equal(4, occ.Count);
            Assert.All(occ, o => Assert.Equal(DayOfWeek.Monday, o.StartUtc.DayOfWeek));
            Assert.Equal(new DateTime(2026, 3, 23, 9, 0, 0, DateTimeKind.Utc), occ[3].StartUtc);
            Assert.False(occ[0].IsRepeat);   // the first one is the event itself
            Assert.True(occ[1].IsRepeat);
        }

        [Fact]
        public void A_weekly_meeting_keeps_its_LOCAL_time_across_a_daylight_saving_change()
        {
            // Europe/London moves to BST on 29 March 2026. A 09:00 London meeting must stay 09:00
            // London — which means its UTC instant MOVES from 09:00Z to 08:00Z.
            var zone = TaskCalendarTime.ResolveZone("Europe/London");
            var firstLocal = new DateTime(2026, 3, 23, 9, 0, 0);           // Monday before the change
            var startUtc = TaskCalendarTime.LocalWallClockToUtc(firstLocal, zone);

            var ev = Event(startUtc, TimeSpan.FromHours(1));
            var schedule = Weekly(zone: "Europe/London", count: 3);

            var occ = CalendarSchedulingService.Expand(ev, schedule,
                new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc));

            Assert.Equal(3, occ.Count);
            foreach (var o in occ)
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(o.StartUtc, DateTimeKind.Utc), zone);
                Assert.Equal(new TimeSpan(9, 0, 0), local.TimeOfDay);   // 09:00 local, every week
            }

            // And the proof that this was NOT a fixed 168-hour step. The clocks go forward on 29 March,
            // which falls between the FIRST occurrence (23 March, GMT) and the second (30 March, BST):
            // that gap is 167 hours. The following week, both ends are in BST, so it is 168 again.
            Assert.Equal(TimeSpan.FromHours(167), occ[1].StartUtc - occ[0].StartUtc);
            Assert.Equal(TimeSpan.FromHours(168), occ[2].StartUtc - occ[1].StartUtc);
        }

        [Fact]
        public void An_interval_of_two_skips_every_other_week()
        {
            var ev = Event(new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc));

            var occ = CalendarSchedulingService.Expand(ev, Weekly(interval: 2, count: 3),
                new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.Equal(3, occ.Count);
            Assert.Equal(new DateTime(2026, 3, 16, 9, 0, 0, DateTimeKind.Utc), occ[1].StartUtc);
            Assert.Equal(new DateTime(2026, 3, 30, 9, 0, 0, DateTimeKind.Utc), occ[2].StartUtc);
        }

        [Fact]
        public void A_weekly_series_can_land_on_several_named_weekdays()
        {
            var ev = Event(new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc));   // Monday

            var occ = CalendarSchedulingService.Expand(ev, Weekly(days: "Monday,Wednesday", count: 4),
                new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.Equal(4, occ.Count);
            Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Monday, DayOfWeek.Wednesday },
                         occ.Select(o => o.StartUtc.DayOfWeek).ToArray());
        }

        [Fact]
        public void An_excepted_date_is_skipped_without_ending_the_series()
        {
            var ev = Event(new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc));
            var schedule = Weekly(count: 4);
            schedule.ExceptionDates = "2026-03-16";

            var occ = CalendarSchedulingService.Expand(ev, schedule,
                new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.DoesNotContain(occ, o => o.LocalDate == new DateOnly(2026, 3, 16));
            // The cancelled one does not consume a count — the series still delivers what was asked for
            // after it, rather than quietly ending one occurrence early.
            Assert.Contains(occ, o => o.LocalDate == new DateOnly(2026, 3, 23));
        }

        [Fact]
        public void A_series_ends_on_its_until_date()
        {
            var ev = Event(new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc));

            var occ = CalendarSchedulingService.Expand(ev, Weekly(until: new DateTime(2026, 3, 20)),
                new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.Equal(3, occ.Count);   // 2, 9, 16 March — the 23rd is past the until date
            Assert.Equal(new DateOnly(2026, 3, 16), occ[^1].LocalDate);
        }

        [Fact]
        public void Only_occurrences_inside_the_window_are_returned()
        {
            var ev = Event(new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc));

            var occ = CalendarSchedulingService.Expand(ev, Weekly(count: 10),
                new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc));

            Assert.All(occ, o => Assert.InRange(o.StartUtc,
                new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc)));
            Assert.NotEmpty(occ);
        }

        [Fact]
        public void A_daily_series_is_bounded_even_when_the_window_is_enormous()
        {
            var ev = Event(new DateTime(2020, 1, 1, 9, 0, 0, DateTimeKind.Utc));
            var schedule = new CalendarEventSchedule
            {
                CompanyId = 1, EventId = 1, TimeZoneId = "UTC",
                RecurrenceKind = RecurrenceKinds.Daily, Interval = 1,
                UntilLocalDate = new DateTime(2400, 1, 1)
            };

            var occ = CalendarSchedulingService.Expand(ev, schedule,
                new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2300, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            // The cap holds. An unbounded expansion inside a web request is a hang, not a slow page.
            Assert.Equal(CalendarSchedulingService.MaxOccurrencesPerEvent, occ.Count);
        }

        [Fact]
        public void An_unparseable_weekday_list_falls_back_to_the_starting_weekday()
        {
            var ev = Event(new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc));   // Monday

            var occ = CalendarSchedulingService.Expand(ev, Weekly(days: "Funday,Notaday", count: 2),
                new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));

            // Never "no days" — a series with no occurrences and no error is the worst outcome.
            Assert.Equal(2, occ.Count);
            Assert.All(occ, o => Assert.Equal(DayOfWeek.Monday, o.StartUtc.DayOfWeek));
        }
    }
}
