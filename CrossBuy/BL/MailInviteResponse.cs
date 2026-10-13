using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Calendar;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL
{
    /// The three answers an invitation admits, in iCalendar's own vocabulary (PARTSTAT). The sender
    /// already writes these words into the .ics, so using anything else here would create a second
    /// spelling of the same fact and a mapping table to keep in step with it.
    public static class CalendarResponses
    {
        public const string Accepted = "ACCEPTED";
        public const string Tentative = "TENTATIVE";
        public const string Declined = "DECLINED";

        public static bool IsValid(string? v) =>
            v == Accepted || v == Tentative || v == Declined;
    }

    public interface IMailInviteResponder
    {
        /// Record this employee's answer to the meeting carried by this message.
        Task<(bool ok, string? error, string? status)> RespondAsync(
            int companyId, int employeeId, int messageId, string response,
            CancellationToken cancellationToken = default);

        /// What they answered already, or null. Drives which button is shown as chosen.
        Task<string?> CurrentAsync(int companyId, int employeeId, int messageId,
            CancellationToken cancellationToken = default);

        /// Is this meeting addressed to them at all? The screen asks before offering the buttons, so
        /// it never shows a control the action would refuse.
        Task<bool> IsAddressedToAsync(int companyId, int employeeId, int messageId,
            CancellationToken cancellationToken = default);

        /// Is this meeting ALREADY on this employee's calendar, and if so where. Asked while the
        /// message is being rendered, so the screen can state the fact instead of offering an action
        /// whose only outcome is to discover it. Writes nothing.
        Task<(bool present, int eventId, string? day)> LocateAsync(
            int companyId, int employeeId, int messageId, CancellationToken cancellationToken = default);

        /// Put this meeting in THIS product's calendar for this employee, rather than handing them a
        /// file for somebody else's. `added` is false when it was already there.
        ///
        /// WHICH event, and on WHAT DAY, are part of the answer. "It is on the calendar" is not a
        /// useful place to stop: the calendar opens on today, and a meeting is routinely weeks away
        /// in either direction, so without these the reader lands on the wrong month and concludes
        /// nothing happened. They are exactly what the calendar's own deep link takes (?day=&eventId=).
        Task<(bool ok, bool added, string? error, int eventId, string? day)> AddToCalendarAsync(
            int companyId, int employeeId, int messageId, CancellationToken cancellationToken = default);
    }

    // =============================================================================================
    // ANSWERING AN INVITATION.
    //
    // THE HARD PART IS NOT STORING THE ANSWER, IT IS FINDING THE MEETING. A mail message carries an
    // .ics file; the attendee row that holds the answer belongs to a CalendarEvent row. Nothing links
    // the two directly - the message knows a file, the event knows a company and a title - so the
    // event is resolved by MATCHING, and the matching rule is deliberately strict:
    //
    //   the same company, the same start instant, and the same title.
    //
    // Loose matching here would attach somebody's "yes" to a different meeting, which is worse than
    // refusing: a wrong attendance is acted on, a missing one is chased.
    //
    // NO ROW IS INVENTED FOR SOMEONE WHO WAS NOT INVITED. If this employee has no attendee row on
    // that event, the answer is refused rather than creating membership - replying to a forwarded
    // invitation must not add you to the meeting.
    // =============================================================================================
    public sealed class MailInviteResponder : IMailInviteResponder
    {
        private readonly CrossDbContext _db;
        private readonly ICommService _comm;
        private readonly ILogger<MailInviteResponder> _log;

        public MailInviteResponder(CrossDbContext db, ICommService comm,
            ILogger<MailInviteResponder> log)
        { _db = db; _comm = comm; _log = log; }

        public async Task<string?> CurrentAsync(int companyId, int employeeId, int messageId,
            CancellationToken cancellationToken = default)
        {
            var row = await AttendeeAsync(companyId, employeeId, messageId, cancellationToken);
            return row?.ResponseStatus;
        }

        public async Task<bool> IsAddressedToAsync(int companyId, int employeeId, int messageId,
            CancellationToken cancellationToken = default)
            => await AttendeeAsync(companyId, employeeId, messageId, cancellationToken) is not null;

        // ---- IS IT ALREADY THERE? ASKED BEFORE ANYTHING IS OFFERED ---------------------------
        //
        // THE COMMON CASE IS "YES". The organiser's own send already created the event and put the
        // invitees on it, so for most readers the button labelled "add to calendar" had nothing to
        // add - it existed only to report, after a click and a round trip, that there was nothing to
        // do. That is the wrong shape: a screen should say what is true while it is being read, and
        // keep its buttons for things that change something.
        //
        // PRESENT MEANS PRESENT FOR YOU - the meeting exists AND you are on its attendee list. An
        // event somebody else is attending is not on your calendar, and saying so would send you to
        // an entry you do not appear in. In that case this answers false and the button is offered,
        // which is exactly what AddToCalendarAsync would then do: add the attendee row.
        //
        // AMBIGUITY ANSWERS FALSE. Two events matching the same invitation cannot both be "the" one,
        // and a link to a guess is worse than a button.
        public async Task<(bool present, int eventId, string? day)> LocateAsync(
            int companyId, int employeeId, int messageId, CancellationToken cancellationToken = default)
        {
            if (companyId <= 0 || employeeId <= 0) return (false, 0, null);

            var mail = await _comm.GetAsync(companyId, messageId);
            var invite = mail?.Invite;
            if (invite?.StartsAt is not { } startsAt || string.IsNullOrWhiteSpace(invite.Title))
                return (false, 0, null);

            var local = startsAt.Kind == DateTimeKind.Utc ? startsAt.ToLocalTime() : startsAt;
            var title = invite.Title!.Trim();

            var ids = await _db.CalendarEvents.AsNoTracking()
                .Where(e => e.CompanyID == companyId && e.DeletedAt == null
                         && e.StartAt == local && e.Title == title)
                .Select(e => e.Id)
                .Take(2)
                .ToListAsync(cancellationToken);

            if (ids.Count != 1) return (false, 0, null);

            bool mine = await _db.CalendarEventAttendees.AsNoTracking()
                .AnyAsync(a => a.EventId == ids[0] && a.EmployeeId == employeeId, cancellationToken);

            return mine
                ? (true, ids[0], local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                : (false, 0, null);
        }

        // ---- PUT IT IN OUR OWN CALENDAR ------------------------------------------------------
        //
        // The button used to hand over the .ics file, which is what you do when the recipient's
        // calendar lives somewhere else. This product HAS a calendar, and the meeting is already in
        // it whenever the organiser is a colleague - so the honest behaviour is to make sure the
        // reader can see it there, not to export it.
        //
        // IDEMPOTENT, and that is most of the work. Clicking twice must not produce two meetings,
        // and the invitation that created the event in the first place must not be duplicated by the
        // recipient opening their mail. So: find the event by the same strict match the responder
        // uses, and only create one when there is genuinely none.
        //
        // PERSONAL SCOPE for anything created here. An event a recipient adds from their mailbox is
        // THEIRS; writing it as Company scope would publish a meeting to the whole organisation on
        // one person's click.
        public async Task<(bool ok, bool added, string? error, int eventId, string? day)> AddToCalendarAsync(
            int companyId, int employeeId, int messageId, CancellationToken cancellationToken = default)
        {
            if (companyId <= 0 || employeeId <= 0) return (false, false, "Unresolved caller", 0, null);

            var mail = await _comm.GetAsync(companyId, messageId);
            var invite = mail?.Invite;
            if (invite?.StartsAt is not { } startsAt || string.IsNullOrWhiteSpace(invite.Title))
                return (false, false, "This message carries no meeting", 0, null);

            var local = startsAt.Kind == DateTimeKind.Utc ? startsAt.ToLocalTime() : startsAt;
            var title = invite.Title!.Trim();

            // The day the calendar should open on, in the shape its deep link parses. Formatted from
            // the LOCAL instant and with the invariant culture, because a calendar day is a position
            // in the grid, not a date for a person to read - an Arabic or French culture here would
            // produce digits FullCalendar's initialDate rejects, and it would silently open on today.
            var day = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var existing = await _db.CalendarEvents.AsNoTracking()
                .Where(e => e.CompanyID == companyId && e.DeletedAt == null
                         && e.StartAt == local && e.Title == title)
                .Select(e => e.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing > 0)
            {
                // The meeting is on the calendar. Make sure this reader is on ITS attendee list, so
                // it shows up as theirs - without that, an invitation can be in the system and absent
                // from the only agenda the recipient looks at.
                bool onIt = await _db.CalendarEventAttendees
                    .AnyAsync(a => a.EventId == existing && a.EmployeeId == employeeId, cancellationToken);

                if (onIt) return (true, false, null, existing, day);

                _db.CalendarEventAttendees.Add(new CalendarEventAttendee
                { EventId = existing, EmployeeId = employeeId });
                await _db.SaveChangesAsync(cancellationToken);
                return (true, true, null, existing, day);
            }

            var ev = new CalendarEvent
            {
                CompanyID = companyId,
                Title = title,
                Description = invite.Description,
                Location = invite.Location,
                AllDay = invite.AllDay,
                StartAt = local,
                EndAt = invite.EndsAt is { } e2
                    ? (e2.Kind == DateTimeKind.Utc ? e2.ToLocalTime() : e2)
                    : null,
                Scope = "Personal",
                OwnerEmpId = employeeId,
            };
            _db.CalendarEvents.Add(ev);
            await _db.SaveChangesAsync(cancellationToken);

            _db.CalendarEventAttendees.Add(new CalendarEventAttendee
            { EventId = ev.Id, EmployeeId = employeeId });
            await _db.SaveChangesAsync(cancellationToken);

            return (true, true, null, ev.Id, day);
        }

        public async Task<(bool ok, string? error, string? status)> RespondAsync(
            int companyId, int employeeId, int messageId, string response,
            CancellationToken cancellationToken = default)
        {
            if (!CalendarResponses.IsValid(response))
                return (false, "Unknown response", null);

            var row = await AttendeeAsync(companyId, employeeId, messageId, cancellationToken);
            if (row == null)
                return (false, "This invitation is not addressed to you", null);

            // The SAME answer twice is not a new fact, and must not move the timestamp: an organiser
            // reading "replied at" wants when they decided, not when they last clicked.
            if (string.Equals(row.ResponseStatus, response, StringComparison.Ordinal))
                return (true, null, response);

            row.ResponseStatus = response;
            row.RespondedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return (true, null, response);
        }

        /// This employee's attendee row on the meeting this message carries, or null.
        private async Task<CalendarEventAttendee?> AttendeeAsync(
            int companyId, int employeeId, int messageId, CancellationToken cancellationToken)
        {
            if (companyId <= 0 || employeeId <= 0 || messageId <= 0) return null;

            // The message is read through the ordinary service, so its company predicate applies and
            // a message id from another tenant resolves to nothing.
            var mail = await _comm.GetAsync(companyId, messageId);
            var invite = mail?.Invite;
            if (invite?.StartsAt is not { } startsAt || string.IsNullOrWhiteSpace(invite.Title))
                return null;

            // Stored local, compared local. The .ics carries a UTC instant; CalendarEvent.StartAt is
            // what the organiser typed, so the comparison is made on the same footing rather than
            // across a conversion that would miss by the offset.
            var local = startsAt.Kind == DateTimeKind.Utc ? startsAt.ToLocalTime() : startsAt;
            var title = invite.Title!.Trim();

            var eventIds = await _db.CalendarEvents.AsNoTracking()
                .Where(e => e.CompanyID == companyId
                         && e.DeletedAt == null
                         && e.StartAt == local
                         && e.Title == title)
                .Select(e => e.Id)
                .ToListAsync(cancellationToken);

            if (eventIds.Count != 1) return null;   // ambiguous or absent: refuse rather than guess

            // ---- THE COLUMNS MAY NOT BE DEPLOYED YET, AND THAT MUST NOT BREAK READING MAIL ----
            //
            // ResponseStatus and RespondedAtUtc arrive in deploy/sql/calendar.sql, which is authored
            // and applied separately from the code. Between the two a database is a version behind,
            // and EF - which selects every mapped column - fails the whole query with "Invalid column
            // name". That turned OPENING an invitation into a 500: a feature that is merely
            // unavailable took the message with it, which is a far worse failure than the one it was
            // meant to add.
            //
            // So a missing column means "no answer recorded" and the buttons are simply not offered.
            // The mailbox keeps working, and the moment the slice runs the feature appears with no
            // code change. The catch is narrow - a schema mismatch on THIS query - and it is logged
            // rather than swallowed, so a column missing in production is visible rather than quiet.
            try
            {
                return await _db.CalendarEventAttendees
                    .FirstOrDefaultAsync(a => a.EventId == eventIds[0] && a.EmployeeId == employeeId,
                        cancellationToken);
            }
            catch (DbException ex)
            {
                _log.LogWarning(ex,
                    "Meeting responses are unavailable: CalendarEventAttendees is missing the response "
                    + "columns. Apply deploy/sql/calendar.sql. The invitation still renders without "
                    + "Accept/Tentative/Decline.");
                return null;
            }
        }
    }
}
