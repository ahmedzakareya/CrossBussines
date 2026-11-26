using System.Text.RegularExpressions;
using CrossBuy.BL.TasksCalendar;
using Xunit;

namespace CrossBuy.Tests
{
    /// D-15 / D-08 — THE FOUNDATION OF THE CALENDAR TIMESTAMP FIX, GUARDED BEFORE THE FIX.
    ///
    /// CalendarService.SaveAsync stores the browser's wall clock unconverted and publishes it as `startUtc`
    /// with `timeZoneId: null`. Repairing that needs a schema column, a per-company zone decision and a
    /// one-way conversion of existing rows — none of which is authorized here, and none of which is safe
    /// unless ONE property holds: that the platform's time primitive REFUSES to invent a zone.
    ///
    /// TaskCalendarTime.ResolveZone throws TimeZoneUnresolvedException when no zone is supplied, and its own
    /// header says why: "there is no server-local fallback, because that is the bug this whole type exists
    /// to prevent." That refusal is the entire safety margin. The obvious, well-meant "fix" for a thrown
    /// exception is to default to TimeZoneInfo.Local — and the moment anyone does that, every ambiguous row
    /// silently acquires the SERVER's zone as though it were the user's, the conversion becomes
    /// unverifiable, and the defect stops being visible. These tests exist so that change cannot pass
    /// unnoticed.
    ///
    /// NO DATABASE. Every assertion here is either a pure static call or a read of committed source text, so
    /// this suite runs under the owner's no-new-database policy. It opens no connection of any kind.
    ///
    /// These guards PIN WHAT IS ALREADY TRUE. They are not a fix and they do not pretend the defect is
    /// closed; they protect the one property the eventual fix depends on.
    public sealed class CalendarTimestampSafetyGuardTests
    {
        /// Comments are stripped before any source assertion: every file involved DISCUSSES the hazard in
        /// prose, so matching bare text would fail on a correct explanation rather than on real code.
        private static string CodeOnly(string source)
        {
            var code = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            return Regex.Replace(code, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);
        }

        private static string Source(params string[] relative)
        {
            var rel = Path.Combine(relative);
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, rel)))
                dir = Directory.GetParent(dir)?.FullName;
            Assert.True(dir is not null, $"Could not locate {rel} by walking up from {AppContext.BaseDirectory}.");
            return File.ReadAllText(Path.Combine(dir!, rel));
        }

        /// THE REFUSAL ITSELF. Both absent must throw, not fall back. If this ever returns a zone, the
        /// migration plan is unsafe and the ambiguity becomes invisible instead of loud.
        [Fact]
        public void An_unresolvable_zone_is_refused_and_never_defaulted()
        {
            Assert.Throws<TimeZoneUnresolvedException>(() => TaskCalendarTime.ResolveZone(null, null));
            Assert.Throws<TimeZoneUnresolvedException>(() => TaskCalendarTime.ResolveZone("", "   "));

            // And an unknown id is refused rather than quietly swapped for something that does resolve.
            Assert.Throws<TimeZoneUnresolvedException>(
                () => TaskCalendarTime.ResolveZone("Not/A/Real/Zone", null));
        }

        /// THE FALLBACK ORDER. An explicit zone wins; the company default is used only when none is given.
        /// Both halves matter: the per-company strategy depends on the second, and a user-level override
        /// depends on the first.
        [Fact]
        public void An_explicit_zone_wins_and_the_company_default_is_the_only_fallback()
        {
            Assert.Equal("Arab Standard Time",
                TaskCalendarTime.ResolveZone("Arab Standard Time", "UTC").Id);
            Assert.Equal("UTC", TaskCalendarTime.ResolveZone(null, "UTC").Id);
            // The Try- form REPORTS the refusal rather than throwing, and it reports it as false with a null
            // zone. It does NOT quietly hand back a usable zone, which is the thing that would make a caller
            // believe it had resolved one.
            Assert.False(TaskCalendarTime.TryResolveZone(null, null, out var none));
            Assert.Null(none);
        }

        /// NO SERVER ZONE, ANYWHERE IN THE PRIMITIVE. Asserted as CALL FORMS, not words: this file's own
        /// header discusses DateTime.Now and the server's local zone in prose, so matching the bare words
        /// would fail on a correct explanation.
        [Theory]
        [InlineData("TimeZoneInfo.Local")]
        [InlineData("DateTime.Now")]
        [InlineData(".ToLocalTime()")]
        public void The_time_primitive_never_consults_the_servers_own_zone(string forbidden)
        {
            var source = Source("CrossBuy", "BL", "TasksCalendar", "TaskCalendarTime.cs");

            // Strip line comments and block comments before asserting, so prose that NAMES the hazard is not
            // mistaken for code that uses it.
            var code = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            code = Regex.Replace(code, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);

            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
        }

        /// AN UNSPECIFIED VALUE IS LABELLED, NOT SHIFTED. This is the difference between recording what a
        /// row already means and silently moving it by the server's offset — the single most damaging thing
        /// a conversion can get wrong, and the reason the conversion must come before SaveAsync changes.
        [Fact]
        public void An_unspecified_instant_is_labelled_utc_rather_than_shifted()
        {
            var naive = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Unspecified);
            var labelled = TaskCalendarTime.AssumeUtc(naive);

            Assert.Equal(DateTimeKind.Utc, labelled.Kind);
            Assert.Equal(naive.TimeOfDay, labelled.TimeOfDay);   // the clock reading is untouched
            Assert.Equal(naive.Date, labelled.Date);

            // An already-UTC value passes through unchanged.
            var utc = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
            Assert.Equal(utc, TaskCalendarTime.AssumeUtc(utc));
        }

        /// THE CONVERSION THE FIX WILL USE, pinned now so the migration can be trusted when it runs.
        /// A wall clock plus a zone is a real instant; the same wall clock in a different zone is a
        /// different instant. If these ever agree, the conversion is a no-op and the defect survives it.
        [Fact]
        public void A_wall_clock_becomes_a_real_instant_only_once_a_zone_is_named()
        {
            var wall = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Unspecified);

            var riyadh = TaskCalendarTime.LocalWallClockToUtc(wall, TaskCalendarTime.ResolveZone("Arab Standard Time"));
            var utcZone = TaskCalendarTime.LocalWallClockToUtc(wall, TaskCalendarTime.ResolveZone("UTC"));

            Assert.Equal(DateTimeKind.Utc, riyadh.Kind);
            Assert.NotEqual(riyadh, utcZone);
            Assert.Equal(TimeSpan.FromHours(3), utcZone - riyadh);   // UTC+3, so the instant is 3h earlier
        }

        /// THE RATCHET. Eight places read the server's own clock or convert an instant to it, and until the
        /// migration lands they cannot be removed — the stored convention IS server-local, deliberately, and
        /// changing one site in isolation would write a second format alongside the first.
        ///
        /// So this does not forbid them. It FIXES THE INVENTORY, in the same shape as the authorization
        /// baseline: the count may only ever go DOWN. A ninth site is a new ambiguous write path and fails
        /// here; a site removed by the migration lowers the number deliberately, with the test updated in the
        /// same change. The three files are named because a FOURTH file acquiring calendar-time conversion
        /// is the thing nobody would notice.
        [Theory]
        [InlineData("CrossBuy/BL/CalendarService.cs", "DateTime.Now", 4)]
        [InlineData("CrossBuy/BL/MailInviteResponse.cs", ".ToLocalTime()", 4)]
        [InlineData("CrossBuy/Controllers/CalendarController.cs", "DateTime.Now", 1)]
        public void The_server_local_clock_sites_are_inventoried_and_may_only_shrink(
            string relative, string form, int expected)
        {
            var code = CodeOnly(Source(relative.Split('/')));
            var actual = Regex.Matches(code, Regex.Escape(form)).Count;

            Assert.True(actual <= expected,
                $"{relative} now has {actual} '{form}' call sites, up from {expected}. A NEW server-local "
                + "write is a new ambiguous timestamp path and must not be added while the Calendar "
                + "timezone migration is pending. If the migration REMOVED one, lower the expected count "
                + "in the same change.");
            Assert.True(actual == expected,
                $"{relative} now has {actual} '{form}' call sites, down from {expected}. If that was the "
                + "migration, update this inventory in the same change so the ratchet keeps its teeth.");
        }

        /// AND THE DEFECT'S OWN SITE MUST NOT ACQUIRE A SERVER-ZONE SHORTCUT EITHER. CalendarService is
        /// where the fix lands; if it grows a TimeZoneInfo.Local default instead of taking a resolved zone,
        /// the fix will look done and will have relabelled every row with the server's offset.
        [Fact]
        public void The_calendar_service_does_not_reach_for_the_server_zone()
        {
            var source = Source("CrossBuy", "BL", "CalendarService.cs");
            var code = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            code = Regex.Replace(code, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);

            Assert.DoesNotContain("TimeZoneInfo.Local", code, StringComparison.Ordinal);
            Assert.DoesNotContain(".ToLocalTime()", code, StringComparison.Ordinal);
        }
    }
}
