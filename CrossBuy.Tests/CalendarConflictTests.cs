using CrossBuy.BL.Platform;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Calendar;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ==========================================================================================
    // CONFLICT DETECTION AND AVAILABILITY.
    //
    // Two things here are easy to get wrong and expensive to discover in use:
    //   1. Touching intervals. A meeting ending at 10:00 does NOT conflict with one starting at
    //      10:00 — treating it as a conflict makes back-to-back scheduling impossible.
    //   2. Overlapping busy blocks. Subtracting them one at a time invents a free gap between two
    //      meetings that overlap, and the app then offers a slot that is not free.
    // ==========================================================================================
    internal sealed class SchedulingFixture : IDisposable
    {
        private readonly SqliteConnection _conn;
        private readonly CompanyScopeHolder _holder = new();

        public CrossDbContext Db { get; }
        public CalendarSchedulingService Svc { get; }

        public SchedulingFixture(int companyId = 1)
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();
            _holder.Set(companyId, null);

            var o = new DbContextOptionsBuilder<CrossDbContext>()
                .UseSqlite(_conn)
                .AddInterceptors(new CompanyWriteGuardInterceptor(NullLogger<CompanyWriteGuardInterceptor>.Instance))
                .Options;
            Db = new CrossDbContext(o, _holder);
            Db.Database.EnsureCreated();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys = OFF;";
            cmd.ExecuteNonQuery();

            Svc = new CalendarSchedulingService(Db);
        }

        public async Task<int> EventAsync(string title, DateTime startUtc, TimeSpan length,
            int[]? attendees = null, int[]? resources = null, int companyId = 1)
        {
            var ev = new CalendarEvent
            {
                CompanyID = companyId, Title = title, StartAt = startUtc, EndAt = startUtc + length,
                Scope = "Company", OwnerEmpId = 10
            };
            Db.CalendarEvents.Add(ev);
            await Db.SaveChangesAsync();

            foreach (var a in attendees ?? Array.Empty<int>())
                Db.CalendarEventAttendees.Add(new CalendarEventAttendee { EventId = ev.Id, EmployeeId = a });
            foreach (var r in resources ?? Array.Empty<int>())
                Db.CalendarEventResources.Add(new CalendarEventResource { CompanyId = companyId, EventId = ev.Id, ResourceId = r });
            await Db.SaveChangesAsync();
            return ev.Id;
        }

        public async Task SeedPeopleAsync(params int[] ids)
        {
            foreach (var id in ids)
                Db.Employee.Add(new Employee
                {
                    ID = id, EmpCompanyID = 1, IsActive = true,
                    FirstName = $"Emp{id}", LastName = "T", FullName = $"Emp {id}",
                    Address = "-", PhoneNumber = "-", Email = $"e{id}@t.local",
                    ProfileImage = "-", Gender = "-", MaritalStatus = "-", UserId = $"u{id}"
                });
            await Db.SaveChangesAsync();
        }

        public async Task<int> ResourceAsync(string name)
        {
            var r = new CalendarResource { CompanyId = 1, Name = name, Kind = "Room", IsActive = true, CreatedAt = DateTime.UtcNow };
            Db.CalendarResources.Add(r);
            await Db.SaveChangesAsync();
            return r.ID;
        }

        public void Dispose() { Db.Dispose(); _conn.Dispose(); }
    }

    public class CalendarConflictTests
    {
        private static DateTime At(int day, int hour) => new(2026, 6, day, hour, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task An_overlapping_meeting_for_the_same_person_is_a_conflict()
        {
            using var f = new SchedulingFixture();
            await f.SeedPeopleAsync(10);
            await f.EventAsync("Existing", At(1, 9), TimeSpan.FromHours(2), attendees: new[] { 10 });

            var conflicts = await f.Svc.DetectConflictsAsync(1, At(1, 10), At(1, 11), new[] { 10 }, Array.Empty<int>());

            Assert.Single(conflicts);
            Assert.Equal(10, conflicts[0].EmployeeId);
            Assert.Equal("Emp 10", conflicts[0].SubjectName);   // it names WHO, not just "conflict"
        }

        [Fact]
        public async Task Back_to_back_meetings_do_not_conflict()
        {
            using var f = new SchedulingFixture();
            await f.SeedPeopleAsync(10);
            await f.EventAsync("Morning", At(1, 9), TimeSpan.FromHours(1), attendees: new[] { 10 });

            // Starts exactly when the other ends. Treating this as a conflict would make back-to-back
            // scheduling impossible — the single most common thing people actually do.
            var conflicts = await f.Svc.DetectConflictsAsync(1, At(1, 10), At(1, 11), new[] { 10 }, Array.Empty<int>());

            Assert.Empty(conflicts);
        }

        [Fact]
        public async Task A_different_person_is_not_a_conflict()
        {
            using var f = new SchedulingFixture();
            await f.SeedPeopleAsync(10, 11);
            await f.EventAsync("Theirs", At(1, 9), TimeSpan.FromHours(2), attendees: new[] { 11 });

            Assert.Empty(await f.Svc.DetectConflictsAsync(1, At(1, 9), At(1, 10), new[] { 10 }, Array.Empty<int>()));
        }

        [Fact]
        public async Task A_double_booked_room_is_a_conflict()
        {
            using var f = new SchedulingFixture();
            int room = await f.ResourceAsync("Board room");
            await f.EventAsync("Existing", At(1, 9), TimeSpan.FromHours(2), resources: new[] { room });

            var conflicts = await f.Svc.DetectConflictsAsync(1, At(1, 10), At(1, 11), Array.Empty<int>(), new[] { room });

            Assert.Single(conflicts);
            Assert.Equal(room, conflicts[0].ResourceId);
            Assert.Equal("Board room", conflicts[0].SubjectName);
        }

        [Fact]
        public async Task An_event_moved_onto_itself_does_not_conflict_with_itself()
        {
            using var f = new SchedulingFixture();
            await f.SeedPeopleAsync(10);
            int id = await f.EventAsync("Mine", At(1, 9), TimeSpan.FromHours(2), attendees: new[] { 10 });

            var conflicts = await f.Svc.DetectConflictsAsync(1, At(1, 10), At(1, 11),
                new[] { 10 }, Array.Empty<int>(), excludeEventId: id);

            Assert.Empty(conflicts);
        }

        [Fact]
        public async Task A_recurring_meeting_conflicts_on_the_week_it_actually_lands_in()
        {
            using var f = new SchedulingFixture();
            await f.SeedPeopleAsync(10);
            int id = await f.EventAsync("Weekly", At(1, 9), TimeSpan.FromHours(1), attendees: new[] { 10 });
            await f.Svc.SaveScheduleAsync(1, new CalendarEventSchedule
            {
                EventId = id, TimeZoneId = "UTC", RecurrenceKind = RecurrenceKinds.Weekly,
                Interval = 1, OccurrenceCount = 5
            }, 10);

            // Three weeks later — no row starts then, but an OCCURRENCE does. Checking raw rows would
            // report the slot as free.
            var conflicts = await f.Svc.DetectConflictsAsync(1, At(22, 9), At(22, 10), new[] { 10 }, Array.Empty<int>());

            Assert.Single(conflicts);
        }

        [Fact]
        public async Task An_inverted_slot_is_refused_rather_than_reported_free()
        {
            using var f = new SchedulingFixture();
            await f.SeedPeopleAsync(10);
            await f.EventAsync("Existing", At(1, 9), TimeSpan.FromHours(8), attendees: new[] { 10 });

            Assert.Empty(await f.Svc.DetectConflictsAsync(1, At(1, 11), At(1, 10), new[] { 10 }, Array.Empty<int>()));
        }
    }

    public class CalendarAvailabilityTests
    {
        private static DateTime At(int hour) => new(2026, 6, 1, hour, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task Free_slots_are_the_gaps_between_meetings()
        {
            using var f = new SchedulingFixture();
            await f.SeedPeopleAsync(10);
            await f.EventAsync("A", At(10), TimeSpan.FromHours(1), attendees: new[] { 10 });
            await f.EventAsync("B", At(13), TimeSpan.FromHours(1), attendees: new[] { 10 });

            var slots = await f.Svc.FindFreeSlotsAsync(1, At(9), At(17), new[] { 10 }, Array.Empty<int>(), 30);

            Assert.Equal(3, slots.Count);
            Assert.Equal((At(9), At(10)), (slots[0].StartUtc, slots[0].EndUtc));
            Assert.Equal((At(11), At(13)), (slots[1].StartUtc, slots[1].EndUtc));
            Assert.Equal((At(14), At(17)), (slots[2].StartUtc, slots[2].EndUtc));
        }

        [Fact]
        public async Task Overlapping_meetings_are_merged_into_one_busy_stretch()
        {
            using var f = new SchedulingFixture();
            await f.SeedPeopleAsync(10, 11);
            await f.EventAsync("A", At(10), TimeSpan.FromHours(2), attendees: new[] { 10 });   // 10–12
            await f.EventAsync("B", At(11), TimeSpan.FromHours(2), attendees: new[] { 11 });   // 11–13

            var slots = await f.Svc.FindFreeSlotsAsync(1, At(9), At(17), new[] { 10, 11 }, Array.Empty<int>(), 30);

            // Two slots, not three: subtracting the busy blocks one at a time would invent a free
            // 11–12 gap that is not free for either person.
            Assert.Equal(2, slots.Count);
            Assert.Equal((At(9), At(10)), (slots[0].StartUtc, slots[0].EndUtc));
            Assert.Equal((At(13), At(17)), (slots[1].StartUtc, slots[1].EndUtc));
        }

        [Fact]
        public async Task A_gap_shorter_than_the_minimum_is_not_offered()
        {
            using var f = new SchedulingFixture();
            await f.SeedPeopleAsync(10);
            await f.EventAsync("A", At(9), TimeSpan.FromHours(1), attendees: new[] { 10 });
            await f.EventAsync("B", At(10), TimeSpan.FromMinutes(30), attendees: new[] { 10 });

            var slots = await f.Svc.FindFreeSlotsAsync(1, At(9), At(11), new[] { 10 }, Array.Empty<int>(), 60);

            // 10:30–11:00 is only 30 minutes; a 60-minute meeting does not fit and is not offered.
            Assert.Empty(slots);
        }

        [Fact]
        public async Task An_empty_diary_is_one_free_slot()
        {
            using var f = new SchedulingFixture();
            await f.SeedPeopleAsync(10);

            var slots = await f.Svc.FindFreeSlotsAsync(1, At(9), At(17), new[] { 10 }, Array.Empty<int>(), 30);

            Assert.Single(slots);
            Assert.Equal((At(9), At(17)), (slots[0].StartUtc, slots[0].EndUtc));
        }
    }

    public class CalendarScheduleValidationTests
    {
        [Fact]
        public async Task A_repeating_series_without_an_end_is_refused()
        {
            using var f = new SchedulingFixture();
            int id = await f.EventAsync("E", new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1));

            var (ok, err) = await f.Svc.SaveScheduleAsync(1, new CalendarEventSchedule
            { EventId = id, RecurrenceKind = RecurrenceKinds.Weekly, Interval = 1 }, 10);

            Assert.False(ok);
            Assert.Contains("end", err!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Two_ends_at_once_are_refused()
        {
            using var f = new SchedulingFixture();
            int id = await f.EventAsync("E", new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1));

            var (ok, err) = await f.Svc.SaveScheduleAsync(1, new CalendarEventSchedule
            {
                EventId = id, RecurrenceKind = RecurrenceKinds.Weekly, Interval = 1,
                OccurrenceCount = 5, UntilLocalDate = new DateTime(2026, 12, 31)
            }, 10);

            Assert.False(ok);
            Assert.Contains("one end", err!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task A_zero_interval_is_refused()
        {
            using var f = new SchedulingFixture();
            int id = await f.EventAsync("E", new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1));

            var (ok, _) = await f.Svc.SaveScheduleAsync(1, new CalendarEventSchedule
            { EventId = id, RecurrenceKind = RecurrenceKinds.Daily, Interval = 0, OccurrenceCount = 5 }, 10);

            Assert.False(ok);
        }

        [Fact]
        public async Task An_unknown_time_zone_is_refused_where_the_user_can_fix_it()
        {
            using var f = new SchedulingFixture();
            int id = await f.EventAsync("E", new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1));

            var (ok, err) = await f.Svc.SaveScheduleAsync(1, new CalendarEventSchedule
            {
                EventId = id, TimeZoneId = "Mars/Olympus_Mons",
                RecurrenceKind = RecurrenceKinds.Weekly, Interval = 1, OccurrenceCount = 3
            }, 10);

            Assert.False(ok);
            Assert.Contains("Mars/Olympus_Mons", err!);
        }

        [Fact]
        public async Task A_schedule_for_another_companys_event_is_refused()
        {
            using var f = new SchedulingFixture();
            int theirs = await f.EventAsync("Theirs", new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
                TimeSpan.FromHours(1), companyId: 2);

            var (ok, _) = await f.Svc.SaveScheduleAsync(1, new CalendarEventSchedule
            { EventId = theirs, RecurrenceKind = RecurrenceKinds.Weekly, Interval = 1, OccurrenceCount = 3 }, 10);

            Assert.False(ok);
        }

        [Fact]
        public async Task Saving_twice_edits_the_schedule_rather_than_adding_a_second_one()
        {
            using var f = new SchedulingFixture();
            int id = await f.EventAsync("E", new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1));

            await f.Svc.SaveScheduleAsync(1, new CalendarEventSchedule
            { EventId = id, RecurrenceKind = RecurrenceKinds.Weekly, Interval = 1, OccurrenceCount = 3 }, 10);
            await f.Svc.SaveScheduleAsync(1, new CalendarEventSchedule
            { EventId = id, RecurrenceKind = RecurrenceKinds.Daily, Interval = 2, OccurrenceCount = 7 }, 10);

            Assert.Equal(1, await f.Db.CalendarEventSchedules.CountAsync());
            var saved = await f.Svc.GetScheduleAsync(1, id);
            Assert.Equal(RecurrenceKinds.Daily, saved!.RecurrenceKind);
            Assert.Equal(7, saved.OccurrenceCount);
        }
    }
}
