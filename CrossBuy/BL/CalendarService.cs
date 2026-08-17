using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Calendar;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
    // FullCalendar event shape (+ extended props consumed by the view).
    public record CalEventDto(int id, string title, string start, string? end, bool allDay,
        string className, string scope, string? description, string? location,
        string ownerName, List<string> attendees, List<int> attendeeIds, bool canEdit);

    public class CalEventInput
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public string? Location { get; set; }
        public bool AllDay { get; set; }
        public DateTime StartAt { get; set; }
        public DateTime? EndAt { get; set; }
        public string Scope { get; set; } = "Personal";
        public List<int> Attendees { get; set; } = new();
    }

    public interface ICalendarService
    {
        Task<List<CalEventDto>> ListAsync(int companyId, int empId, DateTime? from, DateTime? to);
        Task<CalEventDto?> GetAsync(int companyId, int empId, int id);
        Task<int> SaveAsync(int companyId, int empId, CalEventInput input);
        Task<bool> DeleteAsync(int companyId, int empId, int id);
    }

    // ==========================================================================================
    // BUSINESS EVENTS  (UAT DEFECT 6 — the publisher existed with zero call sites)
    //
    // ITaskCalendarEventPublisher has carried CalendarCreated / Updated / Rescheduled / Cancelled /
    // AttendeeAdded / AttendeeRemoved since TAB 4 Phase 3, and EntityRegistry onboarded `CalendarEvent`
    // specifically so they could be validated ("registration is what makes the already-defined Task.* /
    // CalendarEvent.* contracts publishable at all"). Nothing ever called them: a grep for the six methods
    // returned exactly one file — their own definition. So every calendar change was invisible to
    // /BusinessEventMonitor, to Platform.BusinessEventLog and to the entity timeline, while task changes
    // were fully visible. The two modules disagreed about whether calendar activity was a business fact.
    //
    // WHERE THE PUBLISH GOES, and why here rather than in the controller.
    //   TaskService publishes its own events from inside its own ScopedTx. CalendarService is the matching
    //   writer, so it publishes its own. Putting it in CalendarController would leave the seeder, and any
    //   future caller of SaveAsync, silently eventless — the exact shape of the defect being closed.
    //
    // THE TRANSACTION RULE IS THE KERNEL'S, NOT A CHOICE (ADR-001).
    //   RecordAsync must run inside the caller's ambient transaction, immediately before commit, with no
    //   swallowing catch — and it THROWS when there is no ambient transaction. SaveAsync previously called
    //   SaveChangesAsync twice with no transaction at all, so the event and the row could not have committed
    //   together. Both writes now sit in ONE ScopedTx with the events recorded before CommitAsync: the event
    //   and the fact share one fate, and a rollback leaves neither.
    //
    // WHICH EVENTS, AND ONLY THOSE THE CONTRACT DEFINES.
    //   Created · Updated (changed FIELD NAMES only) · Rescheduled (start/end moved) · Cancelled (the
    //   soft delete) · AttendeeAdded / AttendeeRemoved (the diff). Nothing is invented: there is no
    //   "resource assigned" event in the contract, so resource bookings raise none — CalendarEventResource
    //   is written by no service today, and adding an event type would be a new vocabulary, which the brief
    //   forbids and which would fail BusinessEventTypes.Validate anyway.
    //
    // IDEMPOTENT ON RETRY. Every DedupKey is derived by the publisher from (event type, company, event id,
    // occurrence) — e.g. `att+:{employeeId}` — so a retried save records ONE event per real change.
    // ==========================================================================================
    public class CalendarService : ICalendarService
    {
        private readonly CrossDbContext _db;
        private readonly CrossBuy.BL.TasksCalendar.ITaskCalendarEventPublisher? _events;

        // The publisher is OPTIONAL so a host that has not registered it (the older calendar unit tests
        // construct this service with a DbContext alone) keeps working. It is registered in Program.cs for
        // every real run, so production always publishes; a null publisher writes the row and records nothing
        // rather than throwing, and that degradation is visible in the tests that opt out of it.
        public CalendarService(CrossDbContext db,
            CrossBuy.BL.TasksCalendar.ITaskCalendarEventPublisher? events = null)
        { _db = db; _events = events; }

        // visible to me = I own it OR it's company-wide OR I'm an attendee
        private IQueryable<CalendarEvent> Visible(int companyId, int empId)
        {
            var mine = _db.CalendarEventAttendees.AsNoTracking().Where(a => a.EmployeeId == empId).Select(a => a.EventId);
            return _db.CalendarEvents.AsNoTracking()
                .Where(e => e.CompanyID == companyId && e.DeletedAt == null
                    && (e.OwnerEmpId == empId || e.Scope == "Company" || mine.Contains(e.Id)));
        }

        public async Task<List<CalEventDto>> ListAsync(int companyId, int empId, DateTime? from, DateTime? to)
        {
            var q = Visible(companyId, empId);
            if (from.HasValue) q = q.Where(e => (e.EndAt ?? e.StartAt) >= from.Value);
            if (to.HasValue) q = q.Where(e => e.StartAt <= to.Value);
            var rows = await q.OrderBy(e => e.StartAt).Take(2000).ToListAsync();
            var ids = rows.Select(r => r.Id).ToList();

            // owner + attendee names in one round-trip each
            var owners = await _db.Employee.AsNoTracking()
                .Where(e => rows.Select(r => r.OwnerEmpId).Contains(e.ID))
                .Select(e => new { e.ID, e.FullName }).ToListAsync();
            var atts = await _db.CalendarEventAttendees.AsNoTracking().Where(a => ids.Contains(a.EventId))
                .Join(_db.Employee.AsNoTracking(), a => a.EmployeeId, e => e.ID,
                    (a, e) => new { a.EventId, a.EmployeeId, e.FullName }).ToListAsync();

            return rows.Select(r => new CalEventDto(
                r.Id, r.Title, Iso(r.StartAt), r.EndAt.HasValue ? Iso(r.EndAt.Value) : null, r.AllDay,
                r.Scope == "Company" ? "cbev-company" : "cbev-personal", r.Scope,
                r.Description, r.Location,
                owners.FirstOrDefault(o => o.ID == r.OwnerEmpId)?.FullName ?? "",
                atts.Where(a => a.EventId == r.Id).Select(a => a.FullName).ToList(),
                atts.Where(a => a.EventId == r.Id).Select(a => a.EmployeeId).ToList(),
                r.OwnerEmpId == empId)).ToList();
        }

        public async Task<CalEventDto?> GetAsync(int companyId, int empId, int id)
        {
            var r = await Visible(companyId, empId).FirstOrDefaultAsync(e => e.Id == id);
            if (r == null) return null;
            var ownerName = await _db.Employee.AsNoTracking().Where(e => e.ID == r.OwnerEmpId).Select(e => e.FullName).FirstOrDefaultAsync() ?? "";
            var atts = await _db.CalendarEventAttendees.AsNoTracking().Where(a => a.EventId == r.Id)
                .Join(_db.Employee.AsNoTracking(), a => a.EmployeeId, e => e.ID, (a, e) => new { a.EmployeeId, e.FullName }).ToListAsync();
            return new CalEventDto(r.Id, r.Title, Iso(r.StartAt), r.EndAt.HasValue ? Iso(r.EndAt.Value) : null, r.AllDay,
                r.Scope == "Company" ? "cbev-company" : "cbev-personal", r.Scope, r.Description, r.Location, ownerName,
                atts.Select(a => a.FullName).ToList(), atts.Select(a => a.EmployeeId).ToList(), r.OwnerEmpId == empId);
        }

        public async Task<int> SaveAsync(int companyId, int empId, CalEventInput input)
        {
            CalendarEvent ev;
            bool isNew;

            // Captured BEFORE mutating. A reschedule is defined by the PREVIOUS start/end, and after the
            // assignments below they are gone — the same reason TaskService captures previousAssignee first.
            DateTime? previousStart = null, previousEnd = null;
            var changed = new List<string>();

            if (input.Id > 0)
            {
                ev = await _db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == input.Id && e.CompanyID == companyId && e.DeletedAt == null)
                     ?? throw new InvalidOperationException("not found");
                if (ev.OwnerEmpId != empId) throw new UnauthorizedAccessException();  // only owner edits
                ev.updatedBy = empId; ev.UpdatedAt = DateTime.Now;
                isNew = false;

                previousStart = ev.StartAt;
                previousEnd = ev.EndAt;

                // FIELD NAMES ONLY — never their values. CalendarEvent.Updated's payload carries changedFields
                // and the publisher's own comment is explicit that a value there would leak a private subject
                // or location to everyone the timeline renders for.
                string newScope = input.Scope == "Company" ? "Company" : "Personal";
                if (ev.Title != (input.Title ?? "").Trim()) changed.Add(nameof(ev.Title));
                if (ev.Description != input.Description) changed.Add(nameof(ev.Description));
                if (ev.Location != input.Location) changed.Add(nameof(ev.Location));
                if (ev.AllDay != input.AllDay) changed.Add(nameof(ev.AllDay));
                if (ev.Scope != newScope) changed.Add(nameof(ev.Scope));
            }
            else
            {
                ev = new CalendarEvent { CompanyID = companyId, OwnerEmpId = empId, CreatedBy = empId, CreatedAt = DateTime.Now };
                _db.CalendarEvents.Add(ev);
                isNew = true;
            }
            ev.Title = (input.Title ?? "").Trim();
            ev.Description = input.Description;
            ev.Location = input.Location;
            ev.AllDay = input.AllDay;
            ev.StartAt = input.StartAt;
            ev.EndAt = input.EndAt;
            ev.Scope = input.Scope == "Company" ? "Company" : "Personal";

            var wanted = (input.Attendees ?? new()).Distinct().Where(x => x > 0).ToList();
            var correlation = Guid.NewGuid();

            // ONE transaction for the row, its attendees and the events. Two bare SaveChangesAsync calls used
            // to sit here with nothing joining them, so a failure between them left an event with no attendees.
            await using (var tx = await ScopedTx.BeginOrJoinAsync(_db))
            {
                await _db.SaveChangesAsync();   // saved first so a new event has a real Id for the event row

                // The attendee DIFF, computed against what is stored rather than assumed: the previous code
                // deleted every row and re-inserted, which would make every save look like "everyone removed
                // and everyone re-added" to the event log even when the list never changed.
                var current = await _db.CalendarEventAttendees
                    .Where(a => a.EventId == ev.Id).ToListAsync();
                var currentIds = current.Select(a => a.EmployeeId).ToHashSet();

                var added = wanted.Where(id => !currentIds.Contains(id)).ToList();
                var removed = current.Where(a => !wanted.Contains(a.EmployeeId)).ToList();

                if (removed.Count > 0) _db.CalendarEventAttendees.RemoveRange(removed);
                foreach (var aid in added)
                    _db.CalendarEventAttendees.Add(new CalendarEventAttendee { EventId = ev.Id, EmployeeId = aid });

                if (added.Count > 0 || removed.Count > 0) await _db.SaveChangesAsync();

                if (_events != null)
                {
                    if (isNew)
                    {
                        await _events.CalendarCreatedAsync(companyId, ev.Id, ev.OwnerEmpId, ev.Title,
                            startUtc: ev.AllDay ? null : ev.StartAt,
                            endUtc: ev.AllDay ? null : ev.EndAt,
                            startLocalDate: ev.AllDay ? DateOnly.FromDateTime(ev.StartAt) : null,
                            endLocalDate: ev.AllDay && ev.EndAt.HasValue ? DateOnly.FromDateTime(ev.EndAt.Value) : null,
                            isAllDay: ev.AllDay, timeZoneId: null, scope: ev.Scope,
                            attendeeCount: wanted.Count, actorId: empId, correlationId: correlation);
                    }
                    else
                    {
                        // Rescheduled and Updated are SEPARATE facts and both are recorded when both happened:
                        // "it moved" is what an attendee acts on, "the location changed" is not, and collapsing
                        // them would make a consumer parse a field list to find a move.
                        bool moved = previousStart != ev.StartAt || previousEnd != ev.EndAt;
                        if (moved)
                            await _events.CalendarRescheduledAsync(companyId, ev.Id, ev.OwnerEmpId,
                                previousStartUtc: previousStart, startUtc: ev.StartAt,
                                previousEndUtc: previousEnd, endUtc: ev.EndAt,
                                isAllDay: ev.AllDay, timeZoneId: null, actorId: empId, correlationId: correlation);

                        if (changed.Count > 0)
                            await _events.CalendarUpdatedAsync(companyId, ev.Id, ev.OwnerEmpId, changed,
                                actorId: empId, correlationId: correlation);
                    }

                    foreach (var aid in added)
                        await _events.CalendarAttendeeAddedAsync(companyId, ev.Id, ev.OwnerEmpId, aid, empId, correlation);
                    foreach (var a in removed)
                        await _events.CalendarAttendeeRemovedAsync(companyId, ev.Id, ev.OwnerEmpId, a.EmployeeId, empId, correlation);
                }

                await tx.CommitAsync();
            }

            return ev.Id;
        }

        public async Task<bool> DeleteAsync(int companyId, int empId, int id)
        {
            var ev = await _db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id && e.CompanyID == companyId && e.DeletedAt == null);
            if (ev == null) return false;
            if (ev.OwnerEmpId != empId) return false;   // only owner deletes

            ev.DeletedAt = DateTime.Now; ev.updatedBy = empId; ev.UpdatedAt = DateTime.Now;

            // CANCELLED, not deleted: the contract has CalendarEvent.Cancelled and the row is soft-deleted, so
            // the event name matches what actually happened to the record.
            await using (var tx = await ScopedTx.BeginOrJoinAsync(_db))
            {
                await _db.SaveChangesAsync();
                if (_events != null)
                    await _events.CalendarCancelledAsync(companyId, ev.Id, ev.OwnerEmpId,
                        reason: null, actorId: empId, correlationId: Guid.NewGuid());
                await tx.CommitAsync();
            }
            return true;
        }

        // local time, no zone suffix → FullCalendar reads it as-is (matches the browser-entered value)
        private static string Iso(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss");
    }
}
