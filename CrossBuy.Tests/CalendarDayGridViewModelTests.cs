using System.Text.RegularExpressions;
using CrossBuy.BL.TasksCalendar;
using Xunit;

namespace CrossBuy.Tests
{
    // ==========================================================================================
    // REGRESSION — /Calendar/Timeline returned HTTP 500, and /Calendar/ResourceView was one row of
    // data away from doing the same.
    //
    //   Microsoft.CSharp.RuntimeBinder.RuntimeBinderException:
    //       'object' does not contain a definition for 'ID'
    //       at AspNetCore.Views_Calendar_Timeline.ExecuteAsync()
    //
    // WHAT WAS ACTUALLY WRONG. CalendarController projected into an ANONYMOUS type
    // (`Select(e => new { e.ID, e.FullName })`), put it in ViewBag, and Razor read it back as
    // `IEnumerable<dynamic>`. An anonymous type is INTERNAL to the assembly that declares it —
    // CrossBuy.dll. In Development the application calls AddRazorRuntimeCompilation(), so each view
    // is compiled into its OWN dynamic assembly, and the C# runtime binder is then asked to resolve
    // `emp.ID` against a type that assembly may not see. The binder does not say "inaccessible"; it
    // says the member does not exist. Hence a 500 whose message names no calendar concept at all.
    //
    // ResourceView carried the identical bug on `b.ResourceId`. It reported 200 only because
    // CalendarResources held no rows, so the `foreach` that binds it never executed — a green light
    // produced by absent data, not by working code. Seeding one room turned it into a 500 too.
    //
    // WHY THESE TESTS WOULD HAVE FAILED ON THE OLD CODE.
    //   * The source guards below read CalendarController.cs and the two .cshtml files from disk and
    //     fail on the exact constructs the old implementation was built out of — a ViewBag anonymous
    //     projection in these two actions, and an `IEnumerable<dynamic>` cast in these two views.
    //   * The accessibility guard asserts the property the binder actually consults (is the type
    //     PUBLIC?) on every type the page model hands the view. An anonymous type is not public, so
    //     the old payload fails it; `Anonymous_types_are_not_public` pins that the guard really does
    //     discriminate, rather than passing vacuously.
    //   * CrossBuy.Tests is a SEPARATE ASSEMBLY from CrossBuy, exactly as the runtime-compiled view
    //     assembly is. So `Every_row_binds_dynamically_from_a_foreign_assembly` puts the real page
    //     model through the real binder across a real assembly boundary — the production failure,
    //     reproduced without rendering HTML.
    // ==========================================================================================
    public class CalendarDayGridViewModelTests
    {
        private static DateTime LocalDay(int day) => new(2026, 6, day, 0, 0, 0, DateTimeKind.Local);

        // ------------------------------------------------------------------------------------------
        // 1. THE BINDER, ACROSS A REAL ASSEMBLY BOUNDARY.
        // ------------------------------------------------------------------------------------------

        [Fact]
        public async Task Every_timeline_row_binds_dynamically_from_a_foreign_assembly()
        {
            using var f = new SchedulingFixture();
            await f.SeedPeopleAsync(10, 11);
            var day = LocalDay(3);
            await f.EventAsync("Standup", day.AddHours(9).ToUniversalTime(), TimeSpan.FromMinutes(30), attendees: new[] { 10 });

            var page = await f.Svc.BuildTimelinePageAsync(1, day);

            // Bound the way the view used to bind it. On the anonymous payload this threw
            // RuntimeBinderException here, in this assembly, for the same reason it threw in Razor.
            foreach (dynamic row in page.People)
            {
                int id = row.EmployeeId;
                string name = row.EmployeeName;
                int count = row.Occurrences.Count;
                Assert.True(id > 0);
                Assert.False(string.IsNullOrEmpty(name));
                Assert.True(count >= 0);
            }

            dynamic model = page;
            Assert.Equal(1, (int)model.OccurrenceCount);
        }

        [Fact]
        public async Task Every_resource_row_binds_dynamically_from_a_foreign_assembly()
        {
            using var f = new SchedulingFixture();
            var room = await f.ResourceAsync("Room A");
            var day = LocalDay(3);
            await f.EventAsync("Review", day.AddHours(10).ToUniversalTime(), TimeSpan.FromHours(1), resources: new[] { room });

            var page = await f.Svc.BuildResourcePageAsync(1, day);

            foreach (dynamic row in page.Resources)
            {
                int id = row.ResourceId;
                string name = row.Name;
                string kind = row.Kind;
                Assert.True(id > 0);
                Assert.False(string.IsNullOrEmpty(name));
                Assert.False(string.IsNullOrEmpty(kind));
                Assert.NotNull(row.Occurrences);
            }
        }

        [Fact]
        public async Task Every_type_the_page_model_exposes_is_public_and_not_compiler_generated()
        {
            using var f = new SchedulingFixture();
            await f.SeedPeopleAsync(10);
            var room = await f.ResourceAsync("Room A");
            var day = LocalDay(3);
            await f.EventAsync("Review", day.AddHours(10).ToUniversalTime(), TimeSpan.FromHours(1),
                attendees: new[] { 10 }, resources: new[] { room });

            var timeline = await f.Svc.BuildTimelinePageAsync(1, day);
            var resources = await f.Svc.BuildResourcePageAsync(1, day);

            var payload = new List<object> { timeline, resources };
            payload.AddRange(timeline.People);
            payload.AddRange(timeline.People.SelectMany(p => p.Occurrences));
            payload.AddRange(resources.Resources);
            payload.AddRange(resources.Resources.SelectMany(r => r.Occurrences));

            Assert.NotEmpty(timeline.People);
            Assert.NotEmpty(resources.Resources);

            foreach (var item in payload)
            {
                var t = item.GetType();
                // IsPublic is precisely what the runtime binder consults from a foreign assembly.
                Assert.True(t.IsPublic, $"{t.FullName} is not public — a view compiled into its own " +
                                        "assembly cannot bind it, which is the RuntimeBinderException this test exists for.");
                Assert.DoesNotContain("AnonymousType", t.Name, StringComparison.Ordinal);
            }
        }

        /// The guard above only means something if the property it checks is one anonymous types lack.
        /// It is: the compiler emits them as internal, which is the whole defect in one line.
        [Fact]
        public void Anonymous_types_are_not_public()
        {
            var anon = new { ID = 1, FullName = "Emp 1" };
            Assert.False(anon.GetType().IsPublic);
            Assert.Contains("AnonymousType", anon.GetType().Name, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------------------------------
        // 2. THE BEHAVIOUR THE OLD VIEW COMPUTED IN RAZOR, NOW ASSERTABLE.
        //
        //    The join used to live in the .cshtml (`attendees.Where(a => a.EmployeeId == empId)`),
        //    where nothing could test it. These pin it.
        // ------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_person_row_carries_only_the_occurrences_that_person_attends()
        {
            using var f = new SchedulingFixture();
            await f.SeedPeopleAsync(10, 11);
            var day = LocalDay(3);
            await f.EventAsync("Ten only", day.AddHours(9).ToUniversalTime(), TimeSpan.FromHours(1), attendees: new[] { 10 });
            await f.EventAsync("Eleven only", day.AddHours(11).ToUniversalTime(), TimeSpan.FromHours(1), attendees: new[] { 11 });
            await f.EventAsync("Both", day.AddHours(14).ToUniversalTime(), TimeSpan.FromHours(1), attendees: new[] { 10, 11 });

            var page = await f.Svc.BuildTimelinePageAsync(1, day);

            var ten = Assert.Single(page.People.Where(p => p.EmployeeId == 10));
            var eleven = Assert.Single(page.People.Where(p => p.EmployeeId == 11));

            Assert.Equal(new[] { "Both", "Ten only" }, ten.Occurrences.Select(o => o.Title).OrderBy(t => t).ToArray());
            Assert.Equal(new[] { "Both", "Eleven only" }, eleven.Occurrences.Select(o => o.Title).OrderBy(t => t).ToArray());

            // The header count is every occurrence in the window, not the sum of the rows.
            Assert.Equal(3, page.OccurrenceCount);
        }

        [Fact]
        public async Task A_resource_row_carries_only_the_occurrences_booked_onto_it()
        {
            using var f = new SchedulingFixture();
            var room = await f.ResourceAsync("Room A");
            var van = await f.ResourceAsync("Van 1");
            var day = LocalDay(3);
            await f.EventAsync("In the room", day.AddHours(9).ToUniversalTime(), TimeSpan.FromHours(1), resources: new[] { room });
            await f.EventAsync("In the van", day.AddHours(9).ToUniversalTime(), TimeSpan.FromHours(1), resources: new[] { van });

            var page = await f.Svc.BuildResourcePageAsync(1, day);

            var roomRow = Assert.Single(page.Resources.Where(r => r.ResourceId == room));
            var vanRow = Assert.Single(page.Resources.Where(r => r.ResourceId == van));
            Assert.Equal("In the room", Assert.Single(roomRow.Occurrences).Title);
            Assert.Equal("In the van", Assert.Single(vanRow.Occurrences).Title);
        }

        /// The double booking is the reason the resource view exists, so it must reach the view as TWO
        /// occurrences on one row — not as the first one with the second quietly dropped.
        [Fact]
        public async Task A_double_booked_resource_reaches_the_view_with_both_occurrences()
        {
            using var f = new SchedulingFixture();
            var room = await f.ResourceAsync("Room A");
            var day = LocalDay(3);
            await f.EventAsync("First", day.AddHours(9).ToUniversalTime(), TimeSpan.FromHours(1), resources: new[] { room });
            await f.EventAsync("Second", day.AddHours(9).ToUniversalTime(), TimeSpan.FromHours(1), resources: new[] { room });

            var page = await f.Svc.BuildResourcePageAsync(1, day);

            Assert.Equal(2, Assert.Single(page.Resources).Occurrences.Count);
        }

        /// A recurring series is one row and many occurrences. The grid must show the repeat that lands
        /// on the day being viewed, not only the day the series started.
        [Fact]
        public async Task A_repeating_series_reaches_the_grid_on_a_later_week()
        {
            using var f = new SchedulingFixture();
            await f.SeedPeopleAsync(10);
            var first = LocalDay(1);
            var eventId = await f.EventAsync("Weekly standup", first.AddHours(9).ToUniversalTime(),
                TimeSpan.FromMinutes(30), attendees: new[] { 10 });

            var (ok, error) = await f.Svc.SaveScheduleAsync(1, new Models.Context.Calendar.CalendarEventSchedule
            {
                EventId = eventId, TimeZoneId = TimeZoneInfo.Local.Id,
                RecurrenceKind = Models.Context.Calendar.RecurrenceKinds.Weekly,
                Interval = 1, OccurrenceCount = 8
            }, actorEmployeeId: 10);
            Assert.True(ok, error);

            var laterWeek = first.AddDays(7);
            var page = await f.Svc.BuildTimelinePageAsync(1, laterWeek);

            var row = Assert.Single(page.People.Where(p => p.EmployeeId == 10));
            var occurrence = Assert.Single(row.Occurrences);
            Assert.True(occurrence.IsRepeat, "the occurrence on the following week must be marked as a repeat (the ↻ the grid draws)");
        }

        // ------------------------------------------------------------------------------------------
        // 3. COMPANY ISOLATION. The attendee table carries no CompanyID, and the old read had no Where
        //    at all — it pulled every company's attendee rows into memory and relied on a later
        //    intersection inside Razor to hide them.
        // ------------------------------------------------------------------------------------------

        [Fact]
        public async Task Another_companys_event_never_reaches_the_grid()
        {
            using var f = new SchedulingFixture();
            await f.SeedPeopleAsync(10);
            var day = LocalDay(3);
            // Same employee id invited to a company-2 event. Company 1's grid must not show it.
            await f.EventAsync("Other company", day.AddHours(9).ToUniversalTime(), TimeSpan.FromHours(1),
                attendees: new[] { 10 }, companyId: 2);

            var page = await f.Svc.BuildTimelinePageAsync(1, day);

            Assert.Equal(0, page.OccurrenceCount);
            Assert.All(page.People, p => Assert.Empty(p.Occurrences));
        }

        [Fact]
        public async Task An_unresolved_company_reads_nothing()
        {
            using var f = new SchedulingFixture();
            await f.SeedPeopleAsync(10);
            await f.ResourceAsync("Room A");
            var day = LocalDay(3);
            await f.EventAsync("Standup", day.AddHours(9).ToUniversalTime(), TimeSpan.FromHours(1), attendees: new[] { 10 });

            // Fail closed — never default to a company.
            var timeline = await f.Svc.BuildTimelinePageAsync(0, day);
            var resources = await f.Svc.BuildResourcePageAsync(0, day);

            Assert.Empty(timeline.People);
            Assert.Equal(0, timeline.OccurrenceCount);
            Assert.Empty(resources.Resources);
        }

        [Fact]
        public async Task An_inactive_employee_is_not_a_row_and_an_inactive_resource_is_not_a_row()
        {
            using var f = new SchedulingFixture();
            await f.SeedPeopleAsync(10);
            f.Db.Employee.Single(e => e.ID == 10).IsActive = false;
            var res = new Models.Context.Calendar.CalendarResource
            { CompanyId = 1, Name = "Retired room", Kind = "Room", IsActive = false, CreatedAt = DateTime.UtcNow };
            f.Db.CalendarResources.Add(res);
            await f.Db.SaveChangesAsync();

            Assert.Empty((await f.Svc.BuildTimelinePageAsync(1, LocalDay(3))).People);
            Assert.Empty((await f.Svc.BuildResourcePageAsync(1, LocalDay(3))).Resources);
        }

        // ------------------------------------------------------------------------------------------
        // 4. SOURCE GUARDS. These fail on the old source directly — they name the constructs it was
        //    made of. Nothing here renders HTML, so they run in every configuration and stay fast.
        // ------------------------------------------------------------------------------------------

        private static string ProjectRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "CrossBuy", "Views", "Calendar")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "CrossBuy");
        }

        /// Comments are stripped before matching, for the reason TasksCalendarVisualConformanceTests
        /// already gives: a construct's name may legitimately appear in a comment explaining why it was
        /// removed, and forbidding that would forbid the code from explaining itself.
        private static string WithoutComments(string source) =>
            Regex.Replace(
                Regex.Replace(source, @"@\*.*?\*@", " ", RegexOptions.Singleline),   // Razor @* … *@
                @"^[ \t]*//.*$", " ", RegexOptions.Multiline);                        // C# line comments

        public static IEnumerable<object[]> DayGridViews() => new List<object[]>
        {
            new object[] { Path.Combine("Calendar", "Timeline.cshtml"), "CalendarTimelinePage" },
            new object[] { Path.Combine("Calendar", "ResourceView.cshtml"), "CalendarResourcePage" },
        };

        [Theory]
        [MemberData(nameof(DayGridViews))]
        public void Each_day_grid_view_declares_a_typed_model(string relative, string expectedModel)
        {
            var view = File.ReadAllText(Path.Combine(ProjectRoot(), "Views", relative));
            Assert.Matches(new Regex($@"^@model\s+{Regex.Escape(expectedModel)}\s*$", RegexOptions.Multiline), view);
        }

        [Theory]
        [MemberData(nameof(DayGridViews))]
        public void No_day_grid_view_binds_a_ViewBag_payload_dynamically(string relative, string _)
        {
            var view = WithoutComments(File.ReadAllText(Path.Combine(ProjectRoot(), "Views", relative)));

            // The exact casts the two views used. `dynamic` over a payload the view does not own is
            // a compile-time success and a runtime coin toss.
            Assert.DoesNotMatch(new Regex(@"IEnumerable\s*<\s*dynamic\s*>", RegexOptions.CultureInvariant), view);
            Assert.DoesNotMatch(new Regex(@"List\s*<\s*dynamic\s*>", RegexOptions.CultureInvariant), view);
        }

        [Fact]
        public void The_two_day_grid_actions_put_no_anonymous_projection_in_ViewBag()
        {
            var controller = WithoutComments(File.ReadAllText(Path.Combine(ProjectRoot(), "Controllers", "CalendarController.cs")));

            // The body of each action, up to the next action attribute or the end of the file.
            foreach (var action in new[] { "Timeline", "ResourceView" })
            {
                var start = controller.IndexOf($"IActionResult> {action}(", StringComparison.Ordinal);
                Assert.True(start > 0, $"CalendarController no longer declares an action named {action}");
                var next = controller.IndexOf("[HttpGet]", start, StringComparison.Ordinal);
                var post = controller.IndexOf("[HttpPost]", start, StringComparison.Ordinal);
                if (post > 0 && (next < 0 || post < next)) next = post;
                var body = next > start ? controller[start..next] : controller[start..];

                Assert.DoesNotMatch(
                    new Regex(@"ViewBag\.\w+\s*=[^;]*\bnew\s*\{", RegexOptions.Singleline | RegexOptions.CultureInvariant),
                    body);
            }
        }
    }
}
