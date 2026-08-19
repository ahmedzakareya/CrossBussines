using System.Globalization;
using System.Security.Claims;
using System.Text;
using CrossBuy.BL;
using CrossBuy.Hubs;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
    // Company Calendar (Metronic demo39 calendar — FullCalendar). Events: personal or company-wide + invited attendees.
    [SessionValidation]
    public class CalendarController : Controller
    {
        private readonly ICalendarService _cal;
        private readonly IEmployeeService _employees;
        private readonly ICommService _comm;
        private readonly IWebHostEnvironment _env;
        private readonly IStringLocalizer<SharedResources> _sr;
        private readonly INotificationService _notify;
        private readonly IHubContext<NotificationsHub> _hub;
        private readonly CrossDbContext _db;
        // Recurrence, time zone, resources, conflicts and availability. Read-only over CalendarEvents;
        // it writes only its own satellite tables, so the existing calendar path is untouched.
        private readonly CrossBuy.BL.TasksCalendar.ICalendarSchedulingService _scheduling;
        // The calendar's own permission policy. Declared as ICalendarAccessService, which INHERITS
        // IModuleAccessService — a real authority, not an exemption.
        private readonly ICalendarAccessService _calendarAccess;
        private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _businessContexts;

        public CalendarController(ICalendarService cal, IEmployeeService employees, ICommService comm, IWebHostEnvironment env,
            IStringLocalizer<SharedResources> sr, INotificationService notify, IHubContext<NotificationsHub> hub, CrossDbContext db,
            CrossBuy.BL.TasksCalendar.ICalendarSchedulingService scheduling,
            ICalendarAccessService calendarAccess, CrossBuy.BL.Platform.IBusinessContextAccessor businessContexts)
        { _cal = cal; _employees = employees; _comm = comm; _env = env; _sr = sr; _notify = notify; _hub = hub; _db = db; _scheduling = scheduling; _calendarAccess = calendarAccess; _businessContexts = businessContexts; }

        // Real-time signal: tell every open calendar in the company to refetch (visibility is enforced server-side).
        private Task BroadcastCalendarChangedAsync(int companyId) =>
            _hub.Clients.Group(NotificationsHub.CompanyGroupFor(companyId)).SendAsync("calendarChanged");

        private async Task<(int empId, int companyId)?> MeAsync()
        {
            var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(uid)) return null;
            var emp = await _employees.GetEmployeeByUserIdAsync(uid);
            if (emp == null) return null;
            var companyId = await _db.Employee.AsNoTracking().Where(e => e.ID == emp.ID).Select(e => e.EmpCompanyID).FirstOrDefaultAsync();
            return (emp.ID, companyId);
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var me = await MeAsync(); if (me == null) return RedirectToAction("Login", "Account");
            // Without this the Inventory layout falls back to MainMenu.Inventory() — the wrong menu on a
            // Calendar screen.
            ViewBag.SidebarMenu = CrossBuy.Models.Menu.MainMenu.Calendar();
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> Events(DateTime? from, DateTime? to)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            return Json(await _cal.ListAsync(me.Value.companyId, me.Value.empId, from, to));
        }

        // today's timed events for the app-wide reminder/alarm (approaching toast + forced modal at start time)
        [HttpGet]
        public async Task<IActionResult> Upcoming()
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var now = DateTime.Now;
            var endOfDay = now.Date.AddDays(1).AddSeconds(-1);
            var list = await _cal.ListAsync(me.Value.companyId, me.Value.empId, now.AddHours(-1), endOfDay);
            return Json(list.Where(e => !e.allDay)
                .Select(e => new { e.id, e.title, e.start, e.end, e.location, e.description, e.ownerName }));
        }

        [HttpGet]
        public async Task<IActionResult> Get(int id)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var ev = await _cal.GetAsync(me.Value.companyId, me.Value.empId, id);
            return ev == null ? NotFound() : Json(ev);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(int id, string title, string? description, string? location,
            bool allDay, DateTime start, DateTime? end, string? scope, string? attendees)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            if (string.IsNullOrWhiteSpace(title)) return Json(new { ok = false, error = "title_required" });
            var att = (attendees ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).ToList();
            try
            {
                var newId = await _cal.SaveAsync(me.Value.companyId, me.Value.empId, new CalEventInput
                {
                    Id = id, Title = title, Description = description, Location = location,
                    AllDay = allDay, StartAt = start, EndAt = end,
                    Scope = scope == "Company" ? "Company" : "Personal", Attendees = att
                });
                int invited = 0;
                if (att.Count > 0)
                {
                    // in-app notification (bell + real-time) to each invited attendee, except the owner
                    foreach (var aid in att.Where(a => a != me.Value.empId).Distinct())
                    {
                        try
                        {
                            await _notify.NotifyAsync(aid,
                                title, title,
                                _sr["You've been invited to this event"].Value, "You've been invited to this event",
                                NotificationTypes.CalendarEvent, refId: newId,
                                companyId: me.Value.companyId, actorEmployeeId: me.Value.empId);
                        }
                        catch { /* notification failure must not break the save */ }
                    }
                    try { invited = await SendInvitesAsync(me.Value.companyId, me.Value.empId, newId); }
                    catch { invited = 0; }
                }
                // real-time: refetch on every open calendar in the company
                try { await BroadcastCalendarChangedAsync(me.Value.companyId); } catch { }
                return Json(new { ok = true, id = newId, invited });
            }
            catch (UnauthorizedAccessException) { return Json(new { ok = false, error = "not_owner" }); }
            catch (InvalidOperationException) { return Json(new { ok = false, error = "not_found" }); }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var ok = await _cal.DeleteAsync(me.Value.companyId, me.Value.empId, id);
            if (ok) { try { await BroadcastCalendarChangedAsync(me.Value.companyId); } catch { } }
            return Json(new { ok });
        }

        // attendees people-picker: company employees (id + name + photo) matching a query
        [HttpGet]
        public async Task<IActionResult> Attendees(string? q)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var isAr = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
            var query = _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == me.Value.companyId && e.IsActive);
            var t = (q ?? "").Trim();
            if (t.Length > 0) query = query.Where(e => e.FullName.Contains(t) || (e.FullNameEn != null && e.FullNameEn.Contains(t)));
            var rows = await query.OrderBy(e => e.FullName).Take(8)
                .Select(e => new { e.ID, e.FullName, e.FullNameEn, e.Email, e.ProfileImage }).ToListAsync();
            return Json(rows.Select(e => new { id = e.ID, name = isAr ? e.FullName : (e.FullNameEn ?? e.FullName), email = e.Email, photo = e.ProfileImage }));
        }

        // Email a calendar invite (+ .ics attachment) to each attendee that has an email. Returns count sent to.
        private async Task<int> SendInvitesAsync(int companyId, int empId, int eventId)
        {
            var ev = await _db.CalendarEvents.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == eventId && e.CompanyID == companyId);
            if (ev == null) return 0;
            var attIds = await _db.CalendarEventAttendees.AsNoTracking()
                .Where(a => a.EventId == eventId).Select(a => a.EmployeeId).ToListAsync();
            if (attIds.Count == 0) return 0;
            var emails = await _db.Employee.AsNoTracking()
                .Where(e => attIds.Contains(e.ID) && e.Email != null && e.Email != "")
                .Select(e => e.Email!).Distinct().ToListAsync();
            if (emails.Count == 0) return 0;
            var owner = await _db.Employee.AsNoTracking()
                .Where(e => e.ID == ev.OwnerEmpId).Select(e => new { e.FullName, e.Email }).FirstOrDefaultAsync();
            var ownerName = owner?.FullName ?? "";
            var organizerEmail = string.IsNullOrWhiteSpace(owner?.Email) ? "noreply@crossbuy.local" : owner!.Email!.Trim();

            var end = ev.EndAt ?? ev.StartAt.AddHours(1);

            // .ics (VEVENT, METHOD:REQUEST) — floating local time; all-day uses VALUE=DATE (end exclusive).
            string IcsEsc(string? s) => (s ?? "").Replace("\\", "\\\\").Replace(",", "\\,")
                .Replace(";", "\\;").Replace("\r\n", "\\n").Replace("\n", "\\n");
            var ics = new StringBuilder();
            ics.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//CrossBuy//Calendar//EN\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\n");
            ics.Append("UID:").Append(Guid.NewGuid().ToString("N")).Append("@crossbuy\r\n");
            ics.Append("DTSTAMP:").Append(ev.StartAt.ToUniversalTime().ToString("yyyyMMddTHHmmss")).Append("Z\r\n");
            ics.Append("SEQUENCE:0\r\n");
            if (ev.AllDay)
            {
                ics.Append("DTSTART;VALUE=DATE:").Append(ev.StartAt.ToString("yyyyMMdd")).Append("\r\n");
                // all-day DTEND is exclusive → end date + 1 day (single-day event ends the next day)
                ics.Append("DTEND;VALUE=DATE:").Append((ev.EndAt ?? ev.StartAt).AddDays(1).ToString("yyyyMMdd")).Append("\r\n");
            }
            else
            {
                // UTC (…Z) so Gmail/Outlook resolve the time unambiguously — floating time makes Gmail fail to load the event
                ics.Append("DTSTART:").Append(ev.StartAt.ToUniversalTime().ToString("yyyyMMddTHHmmss")).Append("Z\r\n");
                ics.Append("DTEND:").Append(end.ToUniversalTime().ToString("yyyyMMddTHHmmss")).Append("Z\r\n");
            }
            ics.Append("SUMMARY:").Append(IcsEsc(ev.Title)).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(ev.Description)) ics.Append("DESCRIPTION:").Append(IcsEsc(ev.Description)).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(ev.Location)) ics.Append("LOCATION:").Append(IcsEsc(ev.Location)).Append("\r\n");
            ics.Append("ORGANIZER;CN=").Append(IcsEsc(ownerName)).Append(":mailto:").Append(organizerEmail).Append("\r\n");
            // METHOD:REQUEST requires at least one ATTENDEE (RFC 5546) — without it Gmail shows "couldn't load the event"
            foreach (var em in emails)
                ics.Append("ATTENDEE;CUTYPE=INDIVIDUAL;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:").Append(em.Trim()).Append("\r\n");
            ics.Append("STATUS:CONFIRMED\r\n");
            ics.Append("END:VEVENT\r\nEND:VCALENDAR\r\n");

            var dir = System.IO.Path.Combine(_env.WebRootPath, "uploads", "comm");
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            var stored = Guid.NewGuid().ToString("N") + ".ics";
            var full = System.IO.Path.Combine(dir, stored);
            // no-BOM UTF-8: a BOM before BEGIN:VCALENDAR breaks strict iCalendar parsers
            await System.IO.File.WriteAllTextAsync(full, ics.ToString(), new UTF8Encoding(false));
            var attach = new List<MailFile> { new MailFile("/uploads/comm/" + stored, "invite.ics", new System.IO.FileInfo(full).Length) };

            // HTML invite body (labels from resources).
            string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
            var when = ev.AllDay ? ev.StartAt.ToString("yyyy-MM-dd")
                : ev.StartAt.ToString("yyyy-MM-dd HH:mm") + " — " + end.ToString("HH:mm");
            var body = new StringBuilder();
            body.Append("<div style=\"font-family:Inter,Arial,sans-serif;font-size:14px;color:#181c32\">");
            body.Append("<h3 style=\"margin:0 0 10px\">").Append(Enc(ev.Title)).Append("</h3>");
            body.Append("<p><strong>").Append(Enc(ownerName)).Append("</strong> ")
                .Append(Enc(_sr["invited you to an event"])).Append("</p>");
            body.Append("<p>🕒 <strong>").Append(Enc(_sr["When"])).Append(":</strong> ").Append(Enc(when)).Append("</p>");
            if (!string.IsNullOrWhiteSpace(ev.Location))
                body.Append("<p>📍 <strong>").Append(Enc(_sr["Where"])).Append(":</strong> ").Append(Enc(ev.Location)).Append("</p>");
            if (!string.IsNullOrWhiteSpace(ev.Description))
                body.Append("<p>").Append(Enc(ev.Description)).Append("</p>");
            body.Append("<p style=\"color:#7e8299;font-size:12px;margin-top:16px\">CrossBuy Calendar</p></div>");

            var subject = _sr["Invitation"].Value + ": " + ev.Title;
            var res = await _comm.SendAsync(companyId, empId, string.Join(", ", emails), null, subject, body.ToString(), attach, null, "New");
            return emails.Count;
        }

        // ==========================================================================================
        // SCHEDULING — recurrence, time zone, resources, conflicts, availability.
        //
        // Every read here returns OCCURRENCES, not rows. A weekly meeting is one row and many
        // occurrences; a screen that drew rows would show it once and be wrong for every other week.
        // ==========================================================================================

        // Both day grids take a TYPED page model, not a bag of anonymous projections. An anonymous type
        // is internal to CrossBuy.dll, and in Development the views are compiled at runtime into their
        // own assembly — so `emp.ID` bound dynamically threw RuntimeBinderException ("'object' does not
        // contain a definition for 'ID'") and Timeline was a hard 500. ResourceView had the same bug on
        // `b.ResourceId` and only looked healthy because the company had no CalendarResources rows yet.
        // The join now happens in the service, which is testable without rendering HTML.
        [HttpGet]
        public async Task<IActionResult> Timeline(DateTime? day, CancellationToken ct = default)
        {
            var me = await MeAsync(); if (me == null) return RedirectToAction("Login", "Account");
            ViewBag.SidebarMenu = CrossBuy.Models.Menu.MainMenu.Calendar();
            return View("Timeline", await _scheduling.BuildTimelinePageAsync(me.Value.companyId, (day ?? DateTime.Today).Date, ct));
        }

        [HttpGet]
        public async Task<IActionResult> ResourceView(DateTime? day, CancellationToken ct = default)
        {
            var me = await MeAsync(); if (me == null) return RedirectToAction("Login", "Account");
            ViewBag.SidebarMenu = CrossBuy.Models.Menu.MainMenu.Calendar();
            return View("ResourceView", await _scheduling.BuildResourcePageAsync(me.Value.companyId, (day ?? DateTime.Today).Date, ct));
        }

        [HttpGet]
        public async Task<IActionResult> Schedule(int eventId)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var s = await _scheduling.GetScheduleAsync(me.Value.companyId, eventId);
            if (s == null) return Json(new { exists = false });
            return Json(new
            {
                exists = true, timeZoneId = s.TimeZoneId, kind = s.RecurrenceKind, interval = s.Interval,
                byWeekdays = s.ByWeekdays, until = s.UntilLocalDate, count = s.OccurrenceCount,
                exceptions = s.ExceptionDates
            });
        }

        // Editing a recurrence is editing the event, so it asks the calendar's own policy: the owner,
        // or a manager above the owner, or a calendar administrator. An attendee deliberately may NOT —
        // being invited to a meeting is not permission to move it for everyone else.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveSchedule(int eventId, string? timeZoneId, string kind, int interval,
            string? byWeekdays, DateTime? until, int? count, string? exceptions, CancellationToken ct = default)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();

            var ctx = await _businessContexts.TryGetCurrentAsync(ct);
            if (ctx == null || !await _calendarAccess.CanEventAsync(ctx, CalendarActions.Edit, eventId, ct))
                return Json(new { ok = false, error = _sr["not_authorized"].Value });

            var (ok, err) = await _scheduling.SaveScheduleAsync(me.Value.companyId,
                new CrossBuy.Models.Context.Calendar.CalendarEventSchedule
                {
                    EventId = eventId, TimeZoneId = timeZoneId, RecurrenceKind = kind, Interval = interval,
                    ByWeekdays = byWeekdays, UntilLocalDate = until, OccurrenceCount = count, ExceptionDates = exceptions
                }, me.Value.empId, ct);

            if (ok) await BroadcastCalendarChangedAsync(me.Value.companyId);
            return Json(new { ok, error = err });
        }

        [HttpGet]
        public async Task<IActionResult> Conflicts(DateTime startUtc, DateTime endUtc, string? employeeIds,
            string? resourceIds, int? excludeEventId)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var conflicts = await _scheduling.DetectConflictsAsync(me.Value.companyId, startUtc, endUtc,
                Ids(employeeIds), Ids(resourceIds), excludeEventId);
            return Json(conflicts.Select(c => new
            {
                eventId = c.EventId, title = c.Title, startUtc = c.StartUtc, endUtc = c.EndUtc,
                subject = c.SubjectName, isResource = c.ResourceId != null
            }));
        }

        [HttpGet]
        public async Task<IActionResult> FreeSlots(DateTime windowStartUtc, DateTime windowEndUtc,
            string? employeeIds, string? resourceIds, int minimumMinutes = 30)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var slots = await _scheduling.FindFreeSlotsAsync(me.Value.companyId, windowStartUtc, windowEndUtc,
                Ids(employeeIds), Ids(resourceIds), minimumMinutes);
            return Json(slots.Select(s => new { startUtc = s.StartUtc, endUtc = s.EndUtc }));
        }

        private static List<int> Ids(string? csv) => string.IsNullOrWhiteSpace(csv)
            ? new List<int>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n > 0).Distinct().ToList();
    }
}
