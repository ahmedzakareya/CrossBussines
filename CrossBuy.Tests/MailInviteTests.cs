using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.Models.Context.Calendar;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // READING AN INVITATION OUT OF A MESSAGE.
    //
    // The parser runs while someone's mailbox is rendering, so the question is not only "does it get
    // the right answer" but "what does it do with a file it cannot understand". A malformed .ics must
    // mean NO CARD, never an error page over the mail.
    // =============================================================================================
    public class MailInviteParsingTests
    {
        private const string Sample =
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//CrossBuy//Calendar//EN\r\nMETHOD:REQUEST\r\n" +
            "BEGIN:VEVENT\r\nUID:abc@crossbuy\r\nDTSTAMP:20260803T074700Z\r\n" +
            "DTSTART:20260803T074700Z\r\nDTEND:20260803T090000Z\r\n" +
            "SUMMARY:Quarterly review\r\nDESCRIPTION:Bring the numbers\r\nLOCATION:Room 3\r\n" +
            "ORGANIZER;CN=Ahmed Zakareya:mailto:info@alprimecap.com\r\n" +
            "ATTENDEE;CUTYPE=INDIVIDUAL;CN=Sara Ali;RSVP=TRUE:mailto:sara.ali@alprimecap.com\r\n" +
            "ATTENDEE;CUTYPE=INDIVIDUAL;RSVP=TRUE:mailto:khaled@alprimecap.com\r\n" +
            "STATUS:CONFIRMED\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        [Fact]
        public void It_reads_what_the_card_shows()
        {
            var inv = MailInvite.TryParse(Sample);

            Assert.NotNull(inv);
            Assert.Equal("Quarterly review", inv!.Title);
            Assert.Equal("Room 3", inv.Location);
            Assert.Equal("Bring the numbers", inv.Description);
            Assert.Equal("Ahmed Zakareya", inv.OrganizerName);
            Assert.Equal("info@alprimecap.com", inv.OrganizerEmail);

            // CN when the sender gave one, the address when they did not.
            Assert.Equal(new[] { "Sara Ali", "khaled@alprimecap.com" }, inv.Attendees);

            // The Z suffix is UTC and must be read as UTC - taking it as local would move every
            // meeting by the machine's offset, which is the kind of wrong that looks plausible.
            Assert.Equal(DateTimeKind.Utc, inv.StartsAt!.Value.Kind);
            Assert.Equal(new DateTime(2026, 8, 3, 7, 47, 0, DateTimeKind.Utc), inv.StartsAt);
            Assert.Equal(new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc), inv.EndsAt);
            Assert.False(inv.AllDay);
        }

        // AN ALL-DAY DTEND IS EXCLUSIVE in iCalendar: a one-day event ends the NEXT day. Showing the
        // stored value would put every all-day meeting a day later than it is.
        [Fact]
        public void An_all_day_event_ends_on_the_day_it_ends_on()
        {
            var ics =
                "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\n" +
                "DTSTART;VALUE=DATE:20260803\r\nDTEND;VALUE=DATE:20260804\r\n" +
                "SUMMARY:Company day\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

            var inv = MailInvite.TryParse(ics);

            Assert.True(inv!.AllDay);
            Assert.Equal(new DateTime(2026, 8, 3), inv.StartsAt);
            Assert.Equal(new DateTime(2026, 8, 3), inv.EndsAt);   // not the 4th
        }

        // iCalendar folds long lines with a leading space. Unfolding has to happen before parsing or
        // a long subject arrives as three properties and the title is a fragment.
        [Fact]
        public void A_folded_line_is_one_property()
        {
            var ics =
                "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART:20260803T074700Z\r\n" +
                "SUMMARY:A title long enough that the writer\r\n  wrapped it onto a second line\r\n" +
                "END:VEVENT\r\nEND:VCALENDAR\r\n";

            Assert.Equal("A title long enough that the writer wrapped it onto a second line",
                MailInvite.TryParse(ics)!.Title);
        }

        [Fact]
        public void Escaped_commas_and_semicolons_come_back_as_themselves()
        {
            var ics =
                "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART:20260803T074700Z\r\n" +
                "SUMMARY:Budget\\, scope\\; and risk\r\nLOCATION:Floor 2\\, Room 9\r\n" +
                "END:VEVENT\r\nEND:VCALENDAR\r\n";

            var inv = MailInvite.TryParse(ics);
            Assert.Equal("Budget, scope; and risk", inv!.Title);
            Assert.Equal("Floor 2, Room 9", inv.Location);
        }

        [Fact]
        public void A_cancellation_says_so_rather_than_posing_as_an_invitation()
        {
            var ics = Sample.Replace("METHOD:REQUEST", "METHOD:CANCEL");
            Assert.True(MailInvite.TryParse(ics)!.IsCancellation);
            Assert.False(MailInvite.TryParse(Sample)!.IsCancellation);
        }

        // NO CARD, NEVER A THROW. Each of these is something a real mailbox receives.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("this is not a calendar at all")]
        [InlineData("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n")]                       // no event
        [InlineData("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:No when\r\nEND:VEVENT\r\n")]
        [InlineData("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART:20260803T074700Z\r\nEND:VEVENT\r\n")]
        [InlineData("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART:not-a-date\r\nSUMMARY:x\r\nEND:VEVENT\r\n")]
        [InlineData("BEGIN:VEVENT\r\nDTSTART:20260803")]                            // truncated
        public void Anything_it_cannot_understand_is_no_card(string? ics)
        {
            Assert.Null(MailInvite.TryParse(ics));
        }

        // A VEVENT with everything optional missing still renders, because a card only needs to be
        // able to say WHAT and WHEN.
        [Fact]
        public void The_minimum_that_is_still_worth_drawing()
        {
            var inv = MailInvite.TryParse(
                "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART:20260803T074700Z\r\n" +
                "SUMMARY:Just this\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

            Assert.True(inv!.IsRenderable);
            Assert.Null(inv.Location);
            Assert.Null(inv.OrganizerEmail);
            Assert.Empty(inv.Attendees);
        }
    }

    // =============================================================================================
    // ANSWERING, AND PUTTING IT ON THE CALENDAR.
    //
    // These run against a schema built from the EF model, so the response columns exist here while
    // the deployed database is still waiting for deploy/sql/calendar.sql. That is deliberate: it is
    // the only way to prove the behaviour before the slice runs, and the LAST test asserts what
    // happens on a database that has not had it.
    // =============================================================================================
    public class MailInviteResponseTests
    {
        private const string Ics =
            "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\n" +
            "DTSTART:20260803T074700Z\r\nDTEND:20260803T090000Z\r\n" +
            "SUMMARY:Quarterly review\r\nLOCATION:Room 3\r\n" +
            "ORGANIZER;CN=Ahmed:mailto:info@alprimecap.com\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        private const int Me = 5;
        private const int Someone = 99;

        /// The event as the organiser's own screen would have created it: local time, matching title.
        private static int SeedEvent(MailFixture f, int companyId, params int[] attendees)
        {
            var ev = new CalendarEvent
            {
                CompanyID = companyId, Title = "Quarterly review", Scope = "Personal",
                OwnerEmpId = Me,
                StartAt = new DateTime(2026, 8, 3, 7, 47, 0, DateTimeKind.Utc).ToLocalTime(),
                EndAt = new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc).ToLocalTime(),
            };
            f.Db.CalendarEvents.Add(ev);
            f.Db.SaveChanges();

            foreach (var a in attendees)
                f.Db.CalendarEventAttendees.Add(new CalendarEventAttendee { EventId = ev.Id, EmployeeId = a });
            f.Db.SaveChanges();
            return ev.Id;
        }

        [Fact]
        public async Task An_invited_person_can_answer_and_the_answer_is_stored()
        {
            using var f = new MailFixture();
            int mail = f.Mail(MailFixture.CompanyA, "Invitation: Quarterly review", Ics);
            int ev = SeedEvent(f, MailFixture.CompanyA, Me);

            Assert.Null(await f.Invites.CurrentAsync(MailFixture.CompanyA, Me, mail));
            Assert.True(await f.Invites.IsAddressedToAsync(MailFixture.CompanyA, Me, mail));

            var (ok, error, status) = await f.Invites.RespondAsync(
                MailFixture.CompanyA, Me, mail, CalendarResponses.Accepted);

            Assert.True(ok, error);
            Assert.Equal(CalendarResponses.Accepted, status);

            var row = f.Db.CalendarEventAttendees.AsNoTracking()
                .Single(a => a.EventId == ev && a.EmployeeId == Me);
            Assert.Equal(CalendarResponses.Accepted, row.ResponseStatus);
            Assert.NotNull(row.RespondedAtUtc);
        }

        [Fact]
        public async Task Changing_the_answer_replaces_it()
        {
            using var f = new MailFixture();
            int mail = f.Mail(MailFixture.CompanyA, "Invitation", Ics);
            SeedEvent(f, MailFixture.CompanyA, Me);

            await f.Invites.RespondAsync(MailFixture.CompanyA, Me, mail, CalendarResponses.Accepted);
            await f.Invites.RespondAsync(MailFixture.CompanyA, Me, mail, CalendarResponses.Declined);

            Assert.Equal(CalendarResponses.Declined,
                await f.Invites.CurrentAsync(MailFixture.CompanyA, Me, mail));
        }

        // THE SAME ANSWER TWICE IS NOT A NEW FACT. An organiser reading "replied at" wants when they
        // decided, not when they last opened the mail.
        [Fact]
        public async Task Answering_the_same_way_twice_does_not_move_the_timestamp()
        {
            using var f = new MailFixture();
            int mail = f.Mail(MailFixture.CompanyA, "Invitation", Ics);
            int ev = SeedEvent(f, MailFixture.CompanyA, Me);

            await f.Invites.RespondAsync(MailFixture.CompanyA, Me, mail, CalendarResponses.Accepted);
            var first = f.Db.CalendarEventAttendees.AsNoTracking()
                .Single(a => a.EventId == ev && a.EmployeeId == Me).RespondedAtUtc;

            await Task.Delay(15);
            await f.Invites.RespondAsync(MailFixture.CompanyA, Me, mail, CalendarResponses.Accepted);

            Assert.Equal(first, f.Db.CalendarEventAttendees.AsNoTracking()
                .Single(a => a.EventId == ev && a.EmployeeId == Me).RespondedAtUtc);
        }

        // REPLYING MUST NOT ADD YOU TO A MEETING. A forwarded invitation is readable and unanswerable.
        [Fact]
        public async Task Someone_who_was_not_invited_cannot_answer_and_is_not_added()
        {
            using var f = new MailFixture();
            int mail = f.Mail(MailFixture.CompanyA, "Invitation", Ics);
            int ev = SeedEvent(f, MailFixture.CompanyA, Me);

            Assert.False(await f.Invites.IsAddressedToAsync(MailFixture.CompanyA, Someone, mail));

            var (ok, _, _) = await f.Invites.RespondAsync(
                MailFixture.CompanyA, Someone, mail, CalendarResponses.Accepted);

            Assert.False(ok);
            Assert.Empty(f.Db.CalendarEventAttendees.AsNoTracking()
                .Where(a => a.EventId == ev && a.EmployeeId == Someone));
        }

        [Fact]
        public async Task An_unknown_answer_is_refused()
        {
            using var f = new MailFixture();
            int mail = f.Mail(MailFixture.CompanyA, "Invitation", Ics);
            SeedEvent(f, MailFixture.CompanyA, Me);

            foreach (var bad in new[] { "YES", "accepted", "", "MAYBE" })
                Assert.False((await f.Invites.RespondAsync(MailFixture.CompanyA, Me, mail, bad)).ok);

            Assert.Null(await f.Invites.CurrentAsync(MailFixture.CompanyA, Me, mail));
        }

        [Fact]
        public async Task Another_company_cannot_reach_the_invitation_at_all()
        {
            using var f = new MailFixture();
            int mail = f.Mail(MailFixture.CompanyA, "Invitation", Ics);
            SeedEvent(f, MailFixture.CompanyA, Me);

            Assert.False(await f.Invites.IsAddressedToAsync(MailFixture.CompanyB, Me, mail));
            Assert.False((await f.Invites.RespondAsync(
                MailFixture.CompanyB, Me, mail, CalendarResponses.Accepted)).ok);
        }

        // TWO MEETINGS AT THE SAME MOMENT WITH THE SAME NAME cannot be told apart by the matching
        // rule, so the answer is refused. A wrong attendance is acted on; a missing one is chased.
        [Fact]
        public async Task An_ambiguous_match_refuses_rather_than_guessing()
        {
            using var f = new MailFixture();
            int mail = f.Mail(MailFixture.CompanyA, "Invitation", Ics);
            SeedEvent(f, MailFixture.CompanyA, Me);
            SeedEvent(f, MailFixture.CompanyA, Me);          // a duplicate, same title and instant

            Assert.False(await f.Invites.IsAddressedToAsync(MailFixture.CompanyA, Me, mail));
        }

        // ---- ADDING IT TO THIS PRODUCT'S CALENDAR ------------------------------------------

        [Fact]
        public async Task Adding_creates_the_event_once_and_says_so_the_second_time()
        {
            using var f = new MailFixture();
            int mail = f.Mail(MailFixture.CompanyA, "Invitation", Ics);

            var first = await f.Invites.AddToCalendarAsync(MailFixture.CompanyA, Me, mail);
            Assert.True(first.ok);
            Assert.True(first.added);

            var ev = f.Db.CalendarEvents.AsNoTracking().Single();
            Assert.Equal("Quarterly review", ev.Title);
            Assert.Equal("Room 3", ev.Location);
            Assert.Equal("Personal", ev.Scope);               // never published to the company
            Assert.Equal(Me, ev.OwnerEmpId);
            Assert.Single(f.Db.CalendarEventAttendees.AsNoTracking().Where(a => a.EmployeeId == Me));

            // CLICKING AGAIN MUST NOT MAKE A SECOND MEETING.
            var second = await f.Invites.AddToCalendarAsync(MailFixture.CompanyA, Me, mail);
            Assert.True(second.ok);
            Assert.False(second.added);
            Assert.Single(f.Db.CalendarEvents.AsNoTracking());

            // ...AND THE WAY BACK TO IT IS THE SAME BOTH TIMES. The screen turns this answer into a
            // link; if "already there" came back without coordinates, the second click would land on
            // the calendar's default month instead of the meeting - which is the shape of the bug
            // this reports against.
            Assert.Equal(first.eventId, second.eventId);
            Assert.Equal(first.day, second.day);
        }

        // THE COORDINATES MUST POINT AT THE MEETING, not merely be present. The calendar's deep link
        // takes a day and an id; a day off by one, or an id from some other row, opens an empty grid
        // and is indistinguishable to the reader from the button doing nothing.
        //
        // Both are compared against the stored event rather than against a literal date, so the test
        // states the RELATIONSHIP - the link goes where the meeting is - and does not have to be
        // edited whenever the fixture's invitation moves.
        [Fact]
        public async Task Adding_returns_the_day_and_id_the_calendar_deep_link_needs()
        {
            using var f = new MailFixture();
            int mail = f.Mail(MailFixture.CompanyA, "Invitation", Ics);

            var r = await f.Invites.AddToCalendarAsync(MailFixture.CompanyA, Me, mail);
            var ev = f.Db.CalendarEvents.AsNoTracking().Single();

            Assert.True(r.ok);
            Assert.Equal(ev.Id, r.eventId);
            Assert.Equal(ev.StartAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.day);
        }

        // ---- KNOWING BEFORE THE CLICK ------------------------------------------------------
        //
        // The screen asks this while rendering, and shows a statement instead of a button when it is
        // true. So a wrong answer here is not a cosmetic one: a false negative puts back the button
        // the reader complained about, and a false positive offers a link to a meeting they are not
        // on.

        [Fact]
        public async Task A_meeting_already_on_the_readers_calendar_is_reported_before_any_click()
        {
            using var f = new MailFixture();
            int mail = f.Mail(MailFixture.CompanyA, "Invitation", Ics);

            // Exactly what the organiser's send does: the event, with the reader on it.
            await f.Invites.AddToCalendarAsync(MailFixture.CompanyA, Me, mail);
            var ev = f.Db.CalendarEvents.AsNoTracking().Single();

            var at = await f.Invites.LocateAsync(MailFixture.CompanyA, Me, mail);

            Assert.True(at.present);
            Assert.Equal(ev.Id, at.eventId);
            Assert.Equal(ev.StartAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), at.day);
        }

        // THE MEETING EXISTS BUT THE READER IS NOT ON IT. Saying "in your calendar" here would send
        // them to an entry they do not appear in; the button is still the right control, because
        // pressing it genuinely does something - it adds their attendee row.
        [Fact]
        public async Task A_meeting_the_reader_is_not_on_is_not_reported_as_theirs()
        {
            using var f = new MailFixture();
            int mail = f.Mail(MailFixture.CompanyA, "Invitation", Ics);
            SeedEvent(f, MailFixture.CompanyA);              // exists, nobody on it

            var at = await f.Invites.LocateAsync(MailFixture.CompanyA, Me, mail);
            Assert.False(at.present);

            // ...and the control it leaves on the screen is one that does something.
            var r = await f.Invites.AddToCalendarAsync(MailFixture.CompanyA, Me, mail);
            Assert.True(r.added);
            Assert.True((await f.Invites.LocateAsync(MailFixture.CompanyA, Me, mail)).present);
        }

        [Fact]
        public async Task A_message_with_no_meeting_is_nowhere()
        {
            using var f = new MailFixture();
            int plain = f.Mail(MailFixture.CompanyA, "just mail");

            var at = await f.Invites.LocateAsync(MailFixture.CompanyA, Me, plain);

            Assert.False(at.present);
            Assert.Equal(0, at.eventId);
            Assert.Null(at.day);
        }

        // TWO EVENTS MATCHING THE SAME INVITATION CANNOT BOTH BE "THE" ONE. A link to a guess is
        // worse than a button, so ambiguity answers false.
        [Fact]
        public async Task An_ambiguous_match_is_not_reported_as_located()
        {
            using var f = new MailFixture();
            int mail = f.Mail(MailFixture.CompanyA, "Invitation", Ics);
            int ev = SeedEvent(f, MailFixture.CompanyA, Me);
            SeedEvent(f, MailFixture.CompanyA, Me);          // same title, same instant

            Assert.False((await f.Invites.LocateAsync(MailFixture.CompanyA, Me, mail)).present);
            Assert.True(ev > 0);
        }

        // ANOTHER TENANT'S READER IS NOWHERE, even though the meeting exists under company A.
        [Fact]
        public async Task Locating_does_not_reach_across_companies()
        {
            using var f = new MailFixture();
            int mail = f.Mail(MailFixture.CompanyA, "Invitation", Ics);
            await f.Invites.AddToCalendarAsync(MailFixture.CompanyA, Me, mail);

            Assert.True((await f.Invites.LocateAsync(MailFixture.CompanyA, Me, mail)).present);
            Assert.False((await f.Invites.LocateAsync(MailFixture.CompanyB, Me, mail)).present);
        }

        // A REFUSAL CARRIES NO COORDINATES. An eventId left at some stale value would be built into a
        // url by the controller and offered as a way in to a meeting that was never added.
        [Fact]
        public async Task A_refused_add_returns_no_destination()
        {
            using var f = new MailFixture();
            int plain = f.Mail(MailFixture.CompanyA, "just mail");

            var r = await f.Invites.AddToCalendarAsync(MailFixture.CompanyA, Me, plain);

            Assert.False(r.ok);
            Assert.Equal(0, r.eventId);
            Assert.Null(r.day);
        }

        // The organiser already created the meeting when they sent it, so the common path is "it is
        // here but you are not on it" - and the fix is an attendee row, not a second meeting.
        [Fact]
        public async Task An_existing_meeting_gains_the_reader_instead_of_being_duplicated()
        {
            using var f = new MailFixture();
            int mail = f.Mail(MailFixture.CompanyA, "Invitation", Ics);
            int ev = SeedEvent(f, MailFixture.CompanyA);      // exists, nobody on it

            var r = await f.Invites.AddToCalendarAsync(MailFixture.CompanyA, Me, mail);

            Assert.True(r.ok);
            Assert.True(r.added);
            Assert.Single(f.Db.CalendarEvents.AsNoTracking());
            Assert.Single(f.Db.CalendarEventAttendees.AsNoTracking()
                .Where(a => a.EventId == ev && a.EmployeeId == Me));
        }

        [Fact]
        public async Task A_message_with_no_meeting_adds_nothing()
        {
            using var f = new MailFixture();
            int plain = f.Mail(MailFixture.CompanyA, "just mail");

            var r = await f.Invites.AddToCalendarAsync(MailFixture.CompanyA, Me, plain);

            Assert.False(r.ok);
            Assert.Empty(f.Db.CalendarEvents.AsNoTracking());
        }

        [Fact]
        public async Task An_unresolved_caller_adds_nothing()
        {
            using var f = new MailFixture();
            int mail = f.Mail(MailFixture.CompanyA, "Invitation", Ics);

            Assert.False((await f.Invites.AddToCalendarAsync(0, Me, mail)).ok);
            Assert.False((await f.Invites.AddToCalendarAsync(MailFixture.CompanyA, 0, mail)).ok);
            Assert.Empty(f.Db.CalendarEvents.AsNoTracking());
        }
    }
}
