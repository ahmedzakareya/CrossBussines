using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CrossBuy.BL
{
    // =============================================================================================
    // A MEETING INVITATION, READ BACK OUT OF THE MESSAGE THAT CARRIED IT.
    //
    // WHY PARSE AT ALL. The platform already SENDS proper invitations: CalendarController writes a
    // METHOD:REQUEST VEVENT and attaches it to the mail. Until now the receiving screen showed that
    // file the way it shows a PDF - a grey chip with a filename - so an invitation arrived looking
    // like paperwork. Every mail client renders it as a card instead, because "when and where" is
    // the whole content of the message and nobody should have to download a file to read it.
    //
    // THE FILE IS THE TRUTH, not the subject line. The subject of these messages already contains a
    // human summary ("دعوة: X ... الوقت: ... المكان: ..."), and reading the card off that text would
    // be guessing: it is composed for a person, it is localized, and it changes whenever somebody
    // edits the wording. The VEVENT is a contract with a grammar, so it is what gets read.
    //
    // TOLERANT, NEVER THROWING. This runs while rendering someone's mailbox. A truncated file, an
    // unknown property, a date in a shape this writer never produced - all of them mean "no card",
    // and the attachment chip the screen already had is still there. An invitation that fails to
    // parse must not take the message with it.
    // =============================================================================================
    public sealed class MailInvite
    {
        public string? Title { get; init; }
        public DateTime? StartsAt { get; init; }
        public DateTime? EndsAt { get; init; }
        public bool AllDay { get; init; }
        public string? Location { get; init; }

        /// The organiser's own words about the meeting. Carried so the CARD can say everything the
        /// message says - once the card is complete, the generated body underneath it is a duplicate
        /// frozen in whatever language it was sent in, and can be folded away.
        public string? Description { get; init; }
        public string? OrganizerName { get; init; }
        public string? OrganizerEmail { get; init; }
        public IReadOnlyList<string> Attendees { get; init; } = Array.Empty<string>();

        /// The method the sender asked for: REQUEST (an invitation), CANCEL (a withdrawal), REPLY.
        /// Carried because a cancellation must not be drawn as an invitation still awaiting an answer.
        public string Method { get; init; } = "REQUEST";

        public bool IsCancellation =>
            string.Equals(Method, "CANCEL", StringComparison.OrdinalIgnoreCase);

        /// A card is only worth drawing when it can say WHAT and WHEN. Everything else is optional.
        public bool IsRenderable => !string.IsNullOrWhiteSpace(Title) && StartsAt.HasValue;

        // -----------------------------------------------------------------------------------------

        public static MailInvite? TryParse(string? ics)
        {
            if (string.IsNullOrWhiteSpace(ics)) return null;
            if (ics.IndexOf("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase) < 0) return null;

            try
            {
                var lines = Unfold(ics);

                string method = "REQUEST";
                string? title = null, location = null, orgName = null, orgEmail = null, description = null;
                DateTime? start = null, end = null;
                bool allDay = false;
                var attendees = new List<string>();
                bool inEvent = false;

                foreach (var line in lines)
                {
                    if (line.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { inEvent = true; continue; }
                    if (line.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase)) break;

                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;

                    var head = line[..colon];
                    var value = line[(colon + 1)..];
                    var semi = head.IndexOf(';');
                    var name = (semi > 0 ? head[..semi] : head).Trim().ToUpperInvariant();
                    var parameters = semi > 0 ? head[(semi + 1)..] : "";

                    if (!inEvent)
                    {
                        // METHOD sits on the VCALENDAR, above the event.
                        if (name == "METHOD") method = value.Trim();
                        continue;
                    }

                    switch (name)
                    {
                        case "SUMMARY": title = Unescape(value); break;
                        case "LOCATION": location = Unescape(value); break;
                        case "DESCRIPTION": description = Unescape(value); break;

                        case "DTSTART":
                            allDay = parameters.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase);
                            start = ParseStamp(value, allDay);
                            break;

                        case "DTEND":
                            end = ParseStamp(value, parameters.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase));
                            break;

                        case "ORGANIZER":
                            orgEmail = MailAddressOf(value);
                            orgName = CnOf(parameters);
                            break;

                        case "ATTENDEE":
                            var who = CnOf(parameters) ?? MailAddressOf(value);
                            if (!string.IsNullOrWhiteSpace(who)) attendees.Add(who!);
                            break;
                    }
                }

                // ALL-DAY DTEND IS EXCLUSIVE in iCalendar - a one-day event ends the NEXT day. Showing
                // that date to a person would move every all-day meeting a day later than it is.
                if (allDay && end.HasValue) end = end.Value.AddDays(-1);

                var invite = new MailInvite
                {
                    Title = title,
                    StartsAt = start,
                    EndsAt = end,
                    AllDay = allDay,
                    Location = location,
                    Description = description,
                    OrganizerName = orgName,
                    OrganizerEmail = orgEmail,
                    Attendees = attendees.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Method = method,
                };

                return invite.IsRenderable ? invite : null;
            }
            catch (Exception)
            {
                // Deliberately broad: see the header. A malformed invitation is "no card", never an
                // error page over somebody's mail.
                return null;
            }
        }

        // ---- the grammar -------------------------------------------------------------------------

        /// iCalendar folds long lines by starting the continuation with a space or tab. Unfolding
        /// FIRST is what makes a 200-character SUMMARY parse as one property rather than three.
        private static List<string> Unfold(string ics)
        {
            var raw = ics.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var outLines = new List<string>(raw.Length);

            foreach (var line in raw)
            {
                if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && outLines.Count > 0)
                    outLines[^1] += line[1..];
                else
                    outLines.Add(line);
            }
            return outLines;
        }

        private static string Unescape(string v) => v
            .Replace("\\n", "\n").Replace("\\N", "\n")
            .Replace("\\,", ",").Replace("\\;", ";")
            .Replace("\\\\", "\\")
            .Trim();

        /// "mailto:someone@example.com" -> "someone@example.com".
        private static string? MailAddressOf(string value)
        {
            var v = value.Trim();
            const string scheme = "mailto:";
            if (v.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) v = v[scheme.Length..];
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        /// CN="Ahmed Zakareya" or CN=Ahmed, out of the property parameters.
        private static string? CnOf(string parameters)
        {
            foreach (var part in parameters.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = part.Trim();
                if (!t.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) continue;
                var v = t[3..].Trim().Trim('"');
                return string.IsNullOrWhiteSpace(v) ? null : Unescape(v);
            }
            return null;
        }

        /// The three shapes this product and its correspondents actually emit: a UTC stamp ending in
        /// Z, a floating local stamp, and a bare date. Anything else returns null, which means "no
        /// card" rather than a wrong time.
        private static DateTime? ParseStamp(string value, bool dateOnly)
        {
            var v = value.Trim();
            if (v.Length == 0) return null;

            if (dateOnly || v.Length == 8)
                return DateTime.TryParseExact(v[..Math.Min(8, v.Length)], "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

            if (v.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
                return DateTime.TryParseExact(v[..^1], "yyyyMMdd'T'HHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var u) ? u : null;

            return DateTime.TryParseExact(v, "yyyyMMdd'T'HHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var l) ? l : null;
        }
    }
}
