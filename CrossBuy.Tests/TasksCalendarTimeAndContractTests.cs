using System.Text.RegularExpressions;
using CrossBuy.BL.TasksCalendar;
using Xunit;

namespace CrossBuy.Tests
{
    // ==========================================================================================
    // PHASE 7 — THE SHARED TIME MODEL
    //
    // These tests use a zone with a real DST rule so the daylight cases are genuine. The zone id
    // differs between Windows and Linux, so it is resolved by trying both rather than hard-coding one
    // and skipping everywhere else.
    // ==========================================================================================
    public class TasksCalendarTimeModelTests
    {
        private static TimeZoneInfo Berlin() => Resolve("W. Europe Standard Time", "Europe/Berlin");
        private static TimeZoneInfo Cairo() => Resolve("Egypt Standard Time", "Africa/Cairo");

        private static TimeZoneInfo Resolve(params string[] ids)
        {
            foreach (var id in ids)
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
            }
            throw new InvalidOperationException($"none of [{string.Join(", ", ids)}] resolved on this host");
        }

        [Fact]
        public void A_missing_timezone_fails_explicitly_and_never_falls_back_to_the_server()
        {
            var ex = Assert.Throws<TimeZoneUnresolvedException>(() => TaskCalendarTime.ResolveZone(null, null));
            Assert.Null(ex.RequestedZoneId);
            Assert.Contains("cannot be derived", ex.Message);
        }

        [Fact]
        public void An_unknown_timezone_fails_explicitly_and_names_what_was_asked_for()
        {
            var ex = Assert.Throws<TimeZoneUnresolvedException>(
                () => TaskCalendarTime.ResolveZone("Middle/Earth", null));
            Assert.Equal("Middle/Earth", ex.RequestedZoneId);
        }

        [Fact]
        public void An_approved_company_default_is_used_only_when_no_user_zone_is_given()
        {
            var berlin = Berlin();
            Assert.Equal(berlin.Id, TaskCalendarTime.ResolveZone(null, berlin.Id).Id);
            // A user zone always wins over the company default.
            Assert.Equal(Cairo().Id, TaskCalendarTime.ResolveZone(Cairo().Id, berlin.Id).Id);
        }

        [Fact]
        public void A_utc_instant_round_trips_through_a_zone_unchanged()
        {
            var zone = Berlin();
            var utc = new DateTime(2026, 7, 15, 12, 30, 0, DateTimeKind.Utc);

            var offset = TaskCalendarTime.UtcToOffset(utc, zone);
            Assert.Equal(utc, offset.UtcDateTime);

            var back = TaskCalendarTime.LocalWallClockToUtc(offset.DateTime, zone);
            Assert.Equal(utc, back);
        }

        [Fact]
        public void The_same_instant_shows_a_different_wall_clock_in_two_zones()
        {
            var utc = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

            var berlin = TaskCalendarTime.UtcToOffset(utc, Berlin());
            var cairo = TaskCalendarTime.UtcToOffset(utc, Cairo());

            Assert.Equal(utc, berlin.UtcDateTime);
            Assert.Equal(utc, cairo.UtcDateTime);
            // Same instant, and both render correctly for their own viewer.
            Assert.NotEqual(berlin.Offset, cairo.Offset);
        }

        [Fact]
        public void An_all_day_value_keeps_its_date_and_is_never_shifted_by_a_zone()
        {
            // 00:00 local on the 1st. Converting this through a westward zone is exactly what moves a
            // holiday to the previous evening — so an all-day value is never converted at all.
            var stored = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Unspecified);

            var allDay = TaskCalendarTime.AllDay(stored);

            Assert.Equal(AgendaTimeKind.AllDayDate, allDay.Kind);
            Assert.Equal(new DateOnly(2026, 3, 1), allDay.Date);
            Assert.Null(allDay.Utc);
            Assert.Null(allDay.TimeZoneId);
            Assert.Equal("2026-03-01", allDay.ToApiString());
        }

        [Fact]
        public void An_all_day_api_value_carries_no_time_and_no_offset()
        {
            var api = TaskCalendarTime.AllDay(new DateOnly(2026, 12, 25)).ToApiString();

            Assert.Equal("2026-12-25", api);
            Assert.DoesNotContain("T", api);
            Assert.DoesNotContain("+", api);
            Assert.DoesNotContain("Z", api);
        }

        [Fact]
        public void A_timed_api_value_always_carries_an_explicit_offset()
        {
            var api = TaskCalendarTime
                .Timed(new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc), Berlin())
                .ToApiString();

            // yyyy-MM-ddTHH:mm:ss+HH:mm — an offset is present, so no consumer has to guess.
            Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}$"), api);
        }

        [Fact]
        public void A_wall_clock_in_the_spring_forward_gap_is_resolved_forward_not_thrown()
        {
            var zone = Berlin();
            // 2026-03-29 02:30 local does not exist in Berlin: the clock jumps 02:00 -> 03:00.
            var gap = new DateTime(2026, 3, 29, 2, 30, 0, DateTimeKind.Unspecified);
            Assert.True(zone.IsInvalidTime(gap));

            var utc = TaskCalendarTime.LocalWallClockToUtc(gap, zone);

            // It becomes a real instant, and the user's intent (03:30 local) is preserved.
            Assert.Equal(DateTimeKind.Utc, utc.Kind);
            Assert.Equal(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc), utc);
        }

        [Fact]
        public void An_ambiguous_autumn_wall_clock_resolves_deterministically_to_the_first_occurrence()
        {
            var zone = Berlin();
            // 2026-10-25 02:30 local happens twice in Berlin.
            var ambiguous = new DateTime(2026, 10, 25, 2, 30, 0, DateTimeKind.Unspecified);
            Assert.True(zone.IsAmbiguousTime(ambiguous));

            var a = TaskCalendarTime.LocalWallClockToUtc(ambiguous, zone);
            var b = TaskCalendarTime.LocalWallClockToUtc(ambiguous, zone);

            Assert.Equal(a, b);                                                     // deterministic
            Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), a); // the DST (first) occurrence
        }

        [Fact]
        public void A_local_date_range_covers_the_whole_of_the_last_day()
        {
            var zone = Cairo();
            var (from, to) = TaskCalendarTime.LocalRangeToUtcWindow(
                new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 3), zone);

            // An event at 23:30 local on the 3rd must fall INSIDE the window.
            var lateOnLastDay = TaskCalendarTime.LocalWallClockToUtc(
                new DateTime(2026, 5, 3, 23, 30, 0), zone);

            Assert.True(from <= lateOnLastDay);
            Assert.True(lateOnLastDay < to);
        }

        [Fact]
        public void An_unspecified_value_is_labelled_utc_and_not_shifted()
        {
            var unspecified = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Unspecified);

            var utc = TaskCalendarTime.AssumeUtc(unspecified);

            Assert.Equal(DateTimeKind.Utc, utc.Kind);
            Assert.Equal(8, utc.Hour);      // labelled, not converted — no server zone was consulted
        }

        [Fact]
        public void Every_time_helper_orders_an_all_day_item_at_the_start_of_its_local_day()
        {
            var zone = Cairo();
            var allDay = TaskCalendarTime.AllDay(new DateOnly(2026, 5, 1));
            var earlyTimed = TaskCalendarTime.Timed(
                TaskCalendarTime.LocalWallClockToUtc(new DateTime(2026, 5, 1, 6, 0, 0), zone), zone);

            Assert.True(allDay.SortKeyUtc(zone) < earlyTimed.SortKeyUtc(zone));
        }

        // ------------------------------------------------------------------------------------------
        // The guard the brief asks for by name: no server-local clock in the new integration code.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public void No_new_integration_source_file_uses_DateTime_Now()
        {
            var dir = IntegrationSourceDirectory();
            var offenders = new List<string>();

            foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                // Comments are STRIPPED first. These files document the DateTime.Now defect they exist to
                // prevent, and a guard that cannot tell prose from a call would forbid explaining itself.
                var code = StripComments(File.ReadAllText(file));

                // Matches DateTime.Now and DateTimeOffset.Now, but NOT DateTime.UtcNow / DateTimeOffset.UtcNow.
                if (Regex.IsMatch(code, @"DateTime(Offset)?\s*\.\s*Now\b"))
                    offenders.Add(Path.GetFileName(file));
            }

            Assert.Empty(offenders);
        }

        private static string StripComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);   // block comments
            source = Regex.Replace(source, @"//[^\r\n]*", " ");                           // line comments
            return source;
        }

        private static string IntegrationSourceDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "CrossBuy", "BL", "TasksCalendar")))
                dir = dir.Parent;

            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "CrossBuy", "BL", "TasksCalendar");
        }
    }

    // ==========================================================================================
    // PHASES 3-6 — REGISTRY ONBOARDING AND BUSINESS-EVENT CONTRACTS
    // ==========================================================================================
    public class TasksCalendarContractTests
    {
        [Fact]
        public void The_task_registration_request_is_scoped_and_never_permissive()
        {
            var req = TaskCalendarRegistryOnboarding.TaskRequest();

            Assert.Equal("Task", req.Definition.Code);
            // The registry currently carries Project with PermissionScope = None even though an access
            // service exists. Tasks must NOT repeat that: TasksAccessService has 8 actions.
            Assert.Equal("Tasks", req.Definition.PermissionScope);
            Assert.NotEqual("None", req.Definition.PermissionScope);
            Assert.Equal(CapabilityReadiness.Forbidden, req.PublicVisibility);
            Assert.Equal(CapabilityReadiness.Forbidden, req.ExternalPrincipalAccess);
            Assert.Equal("InternalOnly", req.PrivacyCeiling);
            Assert.True(req.ExternallyPending);          // the kernel, not this tab, adopts it
            Assert.False(string.IsNullOrWhiteSpace(req.ReadAccessResolver));
            Assert.False(string.IsNullOrWhiteSpace(req.RetentionPolicyPlaceholder));
            Assert.False(string.IsNullOrWhiteSpace(req.AuditPolicyPlaceholder));
        }

        [Fact]
        public void Comment_surfaces_stay_closed_until_an_access_proof_exists()
        {
            // A comment surface is a READ surface. Both entities keep it pending until the resolver is
            // wired and tested — fail closed, not open.
            Assert.Equal(CapabilityReadiness.PendingAccessProof,
                TaskCalendarRegistryOnboarding.TaskRequest().Comments);
            Assert.Equal(CapabilityReadiness.PendingAccessProof,
                TaskCalendarRegistryOnboarding.CalendarEventRequest().Comments);
        }

        [Fact]
        public void The_calendar_registration_defers_external_attendees_and_claims_no_module_scope()
        {
            var req = TaskCalendarRegistryOnboarding.CalendarEventRequest();

            Assert.Equal("CalendarEvent", req.Definition.Code);
            // Calendar has no access service; its real rule is record-level (organiser/attendee), so
            // inventing a module scope here would create an authorization surface nobody owns.
            Assert.Equal("None", req.Definition.PermissionScope);
            Assert.Contains("attendee", req.ReadAccessResolver, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(CapabilityReadiness.Forbidden, req.ExternalPrincipalAccess);
            // Attendees ARE the follower set; a second concept would diverge from the attendee list.
            Assert.False(req.Definition.SupportsFollowers);
        }

        [Fact]
        public void Every_business_event_is_versioned_and_named_for_its_registered_entity()
        {
            foreach (var c in TaskCalendarBusinessEvents.All())
            {
                Assert.Equal(1, c.EventVersion);
                Assert.Contains(".", c.EventName);
                Assert.Matches(new Regex(@"^[A-Za-z]+\.[A-Z][A-Za-z]+$"), c.EventName);
                Assert.Contains(c.EntityCode, new[]
                {
                    TaskCalendarEntityCodes.Task, TaskCalendarEntityCodes.CalendarEvent
                });
                Assert.False(string.IsNullOrWhiteSpace(c.Trigger));
            }
        }

        [Fact]
        public void Every_business_event_carries_the_full_envelope()
        {
            foreach (var c in TaskCalendarBusinessEvents.All())
            {
                var names = c.Fields.Select(f => f.Name).ToList();
                Assert.Contains("CompanyID", names);
                Assert.Contains("OccurredAtUtc", names);
                Assert.Contains("CorrelationID", names);
                Assert.Contains("ActorID", names);
                Assert.Contains("SourceModule", names);

                // The aggregate id is present and required.
                var idField = c.EntityCode == TaskCalendarEntityCodes.Task ? "TaskID" : "CalendarEventID";
                Assert.Contains(names, n => n == idField);
                Assert.True(c.Fields.Single(f => f.Name == idField).Required);
                Assert.True(c.Fields.Single(f => f.Name == "CompanyID").Required);
            }
        }

        [Fact]
        public void No_event_payload_carries_a_description_or_an_attachment()
        {
            foreach (var c in TaskCalendarBusinessEvents.All())
            {
                Assert.DoesNotContain(c.Fields, f =>
                    f.Name.Contains("Description", StringComparison.OrdinalIgnoreCase) ||
                    f.Name.Contains("Attachment", StringComparison.OrdinalIgnoreCase) ||
                    f.Name.Contains("Body", StringComparison.OrdinalIgnoreCase));

                // And the omission is stated rather than left to be rediscovered.
                Assert.NotEmpty(c.DeliberatelyExcluded);
            }
        }

        [Fact]
        public void No_event_payload_carries_commercial_task_data()
        {
            foreach (var c in TaskCalendarBusinessEvents.TaskEvents())
            {
                Assert.DoesNotContain(c.Fields, f =>
                    f.Name.Contains("BillRate", StringComparison.OrdinalIgnoreCase) ||
                    f.Name.Contains("CostRate", StringComparison.OrdinalIgnoreCase) ||
                    f.Name.Contains("Customer", StringComparison.OrdinalIgnoreCase));
            }
        }

        [Fact]
        public void The_required_event_set_is_complete()
        {
            var names = TaskCalendarBusinessEvents.All().Select(c => c.EventName).ToList();

            foreach (var required in new[]
            {
                "Task.Created", "Task.Assigned", "Task.Reassigned", "Task.StatusChanged",
                "Task.DueDateChanged", "Task.BecameOverdue", "Task.Completed", "Task.Reopened", "Task.Cancelled",
                "CalendarEvent.Created", "CalendarEvent.Updated", "CalendarEvent.Cancelled",
                "CalendarEvent.AttendeeAdded", "CalendarEvent.AttendeeRemoved", "CalendarEvent.ReminderTriggered",
                "CalendarEvent.Started", "CalendarEvent.Completed"
            })
                Assert.Contains(required, names);

            Assert.Equal(names.Count, names.Distinct().Count());
        }

        [Fact]
        public void A_calendar_event_expresses_all_day_as_a_local_date_and_timed_as_utc()
        {
            var created = TaskCalendarBusinessEvents.CalendarEvents()
                .Single(c => c.EventName == "CalendarEvent.Created");
            var names = created.Fields.Select(f => f.Name).ToList();

            Assert.Contains("IsAllDay", names);
            Assert.Contains("StartUtc", names);
            Assert.Contains("StartLocalDate", names);     // the all-day half — never a midnight UTC stamp
            Assert.Contains("TimeZoneID", names);

            Assert.Equal("DateOnly?", created.Fields.Single(f => f.Name == "StartLocalDate").Type);
            Assert.Equal("DateTime?", created.Fields.Single(f => f.Name == "StartUtc").Type);
        }

        [Fact]
        public void Reschedule_is_a_separate_event_from_update()
        {
            var names = TaskCalendarBusinessEvents.CalendarEvents().Select(c => c.EventName).ToList();

            // Moving a meeting and renaming it are not the same fact: only one is worth interrupting
            // every attendee for. Collapsing them is how notification fatigue starts.
            Assert.Contains("CalendarEvent.Rescheduled", names);
            Assert.Contains("CalendarEvent.Updated", names);

            var resched = TaskCalendarBusinessEvents.CalendarEvents()
                .Single(c => c.EventName == "CalendarEvent.Rescheduled");
            Assert.Contains(resched.Fields, f => f.Name == "PreviousStartUtc");
        }
    }
}
