using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Calendar;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CrossBuy.BL.TasksCalendar
{
	// ==========================================================================================
	// CALENDAR SCHEDULING — recurrence expansion, availability, conflict detection.
	//
	// The one non-obvious rule, and the reason this is a service rather than a query:
	//
	//   A RECURRING EVENT REPEATS IN LOCAL WALL-CLOCK TIME, NOT EVERY N HOURS.
	//
	// A 09:00 Sunday standup is at 09:00 the week after the clocks move, which is a DIFFERENT number
	// of hours later. So the expansion walks the LOCAL calendar and converts each occurrence to UTC
	// individually. Adding 7*24 hours to a UTC instant would silently drift the meeting by an hour
	// twice a year — and nobody would report it as a bug, they would just start arriving late.
	//
	// Time conversion is delegated to TaskCalendarTime, which already refuses an unresolvable zone
	// rather than guessing UTC, and resolves DST gaps and ambiguities deterministically.
	// ==========================================================================================

	public sealed class CalendarOccurrence
	{
		public required int EventId { get; init; }
		public required string Title { get; init; }
		public required DateTime StartUtc { get; init; }
		public required DateTime? EndUtc { get; init; }
		public required bool AllDay { get; init; }
		/// The local date this occurrence falls on — what the user sees, and what an exception names.
		public required DateOnly LocalDate { get; init; }
		/// False for the first occurrence, true for every repeat. The UI marks a series member.
		public required bool IsRepeat { get; init; }
	}

	public sealed class CalendarConflict
	{
		public required int EventId { get; init; }
		public required string Title { get; init; }
		public required DateTime StartUtc { get; init; }
		public required DateTime? EndUtc { get; init; }
		/// Who or what is double-booked: an employee id, or a resource id.
		public required int? EmployeeId { get; init; }
		public required int? ResourceId { get; init; }
		public required string SubjectName { get; init; }
	}

	public sealed class AvailabilitySlot
	{
		public required DateTime StartUtc { get; init; }
		public required DateTime EndUtc { get; init; }
	}

	// ==========================================================================================
	// THE TWO DAY-GRID PAGE MODELS — people (Timeline) and resources (ResourceView).
	//
	// WHY THESE EXIST AS NAMED PUBLIC TYPES, AND WHY THAT IS NOT A STYLE PREFERENCE.
	//
	// Both screens used to be handed `ViewBag.Employees = … Select(e => new { e.ID, e.FullName })`
	// and read back as `IEnumerable<dynamic>`. An anonymous type is INTERNAL to the assembly that
	// declares it — here CrossBuy.dll. In Development the application calls
	// AddRazorRuntimeCompilation(), so a view is compiled into its OWN dynamic assembly; the C#
	// runtime binder then resolves `emp.ID` against a type it is not permitted to see and reports
	//
	//     RuntimeBinderException: 'object' does not contain a definition for 'ID'
	//
	// which is a real HTTP 500 on /Calendar/Timeline, not a warning. ResourceView carried the
	// IDENTICAL defect on `b.ResourceId` and merely looked healthy because CalendarResources was
	// empty — its `foreach` never ran, so the binder was never asked. The first row a company adds
	// would have turned that 200 into a 500.
	//
	// A named PUBLIC type crosses the assembly boundary by construction, so the failure cannot come
	// back. The join that used to sit in Razor (`attendees.Where(a => a.EmployeeId == empId)`) is
	// done here instead: a view that has to reconstruct a relationship is a view that can get it
	// wrong, and it cannot be tested without rendering HTML.
	// ==========================================================================================

	/// One row of the people timeline: an employee, and only the occurrences that employee attends.
	public sealed class CalendarTimelineRow
	{
		public required int EmployeeId { get; init; }
		public required string EmployeeName { get; init; }
		public required IReadOnlyList<CalendarOccurrence> Occurrences { get; init; }
	}

	/// One row of the resource view: a bookable thing, and the occurrences booked onto it.
	public sealed class CalendarResourceRow
	{
		public required int ResourceId { get; init; }
		public required string Name { get; init; }
		public required string Kind { get; init; }
		public required int? Capacity { get; init; }
		public required IReadOnlyList<CalendarOccurrence> Occurrences { get; init; }
	}

	public sealed class CalendarTimelinePage
	{
		/// The local day the grid spans — the value the date picker and the prev/next links carry.
		public required DateTime Day { get; init; }
		public required IReadOnlyList<CalendarTimelineRow> People { get; init; }
		/// Every occurrence in the window, including ones nobody in this company attends. This is the
		/// count the card header has always shown, so it is carried rather than recomputed from rows.
		public required int OccurrenceCount { get; init; }
	}

	public sealed class CalendarResourcePage
	{
		public required DateTime Day { get; init; }
		public required IReadOnlyList<CalendarResourceRow> Resources { get; init; }
	}

	public interface ICalendarSchedulingService
	{
		Task<CalendarEventSchedule?> GetScheduleAsync(int companyId, int eventId, CancellationToken ct = default);

		Task<(bool ok, string? error)> SaveScheduleAsync(
			int companyId, CalendarEventSchedule input, int? actorEmployeeId, CancellationToken ct = default);

		/// Every occurrence of one event that starts inside [windowStartUtc, windowEndUtc).
		Task<List<CalendarOccurrence>> ExpandAsync(
			int companyId, int eventId, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken ct = default);

		/// Every occurrence of every event visible in the window — what the timeline and resource views draw.
		Task<List<CalendarOccurrence>> ExpandWindowAsync(
			int companyId, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken ct = default);

		/// Who/what is already busy during a proposed slot. `excludeEventId` lets an event be moved
		/// without conflicting with itself.
		Task<List<CalendarConflict>> DetectConflictsAsync(
			int companyId, DateTime startUtc, DateTime endUtc,
			IReadOnlyCollection<int> employeeIds, IReadOnlyCollection<int> resourceIds,
			int? excludeEventId = null, CancellationToken ct = default);

		/// Gaps in which EVERY listed employee and resource is free, at least minimumMinutes long.
		Task<List<AvailabilitySlot>> FindFreeSlotsAsync(
			int companyId, DateTime windowStartUtc, DateTime windowEndUtc,
			IReadOnlyCollection<int> employeeIds, IReadOnlyCollection<int> resourceIds,
			int minimumMinutes, CancellationToken ct = default);

		/// The people day-grid, already joined: one row per active employee of THIS company, carrying
		/// the occurrences that employee attends. Returns named public types, never a projection the
		/// view would have to bind dynamically.
		Task<CalendarTimelinePage> BuildTimelinePageAsync(int companyId, DateTime localDay, CancellationToken ct = default);

		/// The resource day-grid, on the same terms.
		Task<CalendarResourcePage> BuildResourcePageAsync(int companyId, DateTime localDay, CancellationToken ct = default);
	}

	public sealed class CalendarSchedulingService : ICalendarSchedulingService
	{
		/// A hard stop on expansion. A series with a bad interval, or a window a caller opened wider
		/// than they meant to, must not become an unbounded loop inside a web request.
		public const int MaxOccurrencesPerEvent = 1000;

		private readonly CrossDbContext _db;
		private readonly IConfiguration? _config;

		public CalendarSchedulingService(CrossDbContext db, IConfiguration? config = null)
		{ _db = db; _config = config; }

		// ==========================================================================================
		// THE DEFAULT ZONE FOR A SCHEDULE THAT NAMES NONE  (UAT DEFECT 3 — null TimeZoneId was a 500)
		//
		// CalendarEventSchedule.TimeZoneId documents null as "the company default, resolved at read time —
		// never silently treated as UTC", and SaveScheduleAsync accepts null (it validates a zone only when one
		// is supplied). Expand then called the ONE-argument ResolveZone, which throws when its only candidate is
		// null — so a perfectly valid saved schedule made /Calendar/Timeline and /Calendar/ResourceView a hard
		// HTTP 500 for the WHOLE company, because ExpandWindowAsync expands every event in one pass and has no
		// catch. One null row took the screen down for every other row with it.
		//
		// WHAT THE DEFAULT HONESTLY IS. There is no Companies.TimeZoneId column and no approved company-zone
		// setting in this product yet — I checked before inventing one. What DOES exist is the calendar's
		// storage convention: CalendarService writes the posted wall-clock value as-is and emits ISO with NO
		// offset (BL/CalendarService.cs:129), so every CalendarEvent.StartAt already IS a local wall-clock in
		// the server's zone. A schedule that names no zone therefore means "the same zone this event's own
		// StartAt is already expressed in" — and that is the server zone, not UTC and not a guess.
		//
		// So the default is: `Calendar:DefaultTimeZoneId` when a deployment configures one, otherwise the host
		// zone. That is a documented resolution with a single source, and it is NOT the "silent server-local
		// fallback" TaskCalendarTime forbids — TaskCalendarTime refuses to GUESS a zone for a value whose zone
		// is unknown; here the zone is known from the storage convention and is being stated explicitly.
		//
		// WHEN A REAL COMPANY ZONE ARRIVES, this property is the one place that changes: give it the company id
		// and read the column. Nothing else in the file reads a zone.
		//
		// NO WRITER IS FORCED TO PERSIST A ZONE. That was the other candidate fix and the brief rules it out
		// for good reason: it would make every existing null row permanently broken and push a read-time
		// concern onto every writer, including the seeder.
		public string DefaultZoneId =>
			_config?["Calendar:DefaultTimeZoneId"] is { Length: > 0 } configured ? configured : TimeZoneInfo.Local.Id;

		public Task<CalendarEventSchedule?> GetScheduleAsync(int companyId, int eventId, CancellationToken ct = default) =>
			_db.CalendarEventSchedules.AsNoTracking()
				.FirstOrDefaultAsync(s => s.CompanyId == companyId && s.EventId == eventId, ct);

		public async Task<(bool ok, string? error)> SaveScheduleAsync(
			int companyId, CalendarEventSchedule input, int? actorEmployeeId, CancellationToken ct = default)
		{
			if (companyId <= 0) return (false, "الشركة غير محددة (unresolved company)");
			if (input.EventId <= 0) return (false, "الحدث غير محدد (no event)");
			if (!RecurrenceKinds.IsKnown(input.RecurrenceKind)) return (false, "نوع تكرار غير معروف (unknown recurrence kind)");

			// Interval 0 would make "the next occurrence" the same occurrence, forever.
			if (input.RecurrenceKind != RecurrenceKinds.None && input.Interval < 1)
				return (false, "الفاصل الزمني يجب أن يكون 1 على الأقل (the interval must be at least 1)");

			// A repeating series must END. Without an end there is no last occurrence, so every expansion
			// would be silently truncated by a limit the user never chose.
			if (input.RecurrenceKind != RecurrenceKinds.None
				&& input.UntilLocalDate == null && (input.OccurrenceCount == null || input.OccurrenceCount <= 0))
				return (false, "يجب تحديد نهاية للتكرار: تاريخ أو عدد مرات (a repeating series needs an end: a date or a count)");

			if (input.UntilLocalDate != null && input.OccurrenceCount != null)
				return (false, "حدّد نهاية واحدة فقط: تاريخ أو عدد (choose one end: a date or a count, not both)");

			if (!string.IsNullOrWhiteSpace(input.TimeZoneId))
			{
				// Fail here, where the user can fix it, rather than at every later read.
				try { TaskCalendarTime.ResolveZone(input.TimeZoneId); }
				catch (TimeZoneUnresolvedException) { return (false, $"منطقة زمنية غير معروفة: {input.TimeZoneId} (unknown time zone)"); }
			}

			var ev = await _db.CalendarEvents.AsNoTracking()
				.FirstOrDefaultAsync(e => e.Id == input.EventId && e.CompanyID == companyId && e.DeletedAt == null, ct);
			if (ev == null) return (false, "الحدث غير موجود في هذه الشركة (event not found in this company)");

			var existing = await _db.CalendarEventSchedules
				.FirstOrDefaultAsync(s => s.CompanyId == companyId && s.EventId == input.EventId, ct);

			if (existing == null)
			{
				input.CompanyId = companyId;
				input.CreatedAt = TaskCalendarTime.UtcNow();
				input.CreatedByEmployeeId = actorEmployeeId;
				_db.CalendarEventSchedules.Add(input);
			}
			else
			{
				// Update in place, keeping the row's identity: the schedule is the same schedule, edited.
				existing.TimeZoneId = input.TimeZoneId;
				existing.RecurrenceKind = input.RecurrenceKind;
				existing.Interval = input.Interval;
				existing.ByWeekdays = input.ByWeekdays;
				existing.UntilLocalDate = input.UntilLocalDate;
				existing.OccurrenceCount = input.OccurrenceCount;
				existing.ExceptionDates = input.ExceptionDates;
				existing.UpdatedAt = TaskCalendarTime.UtcNow();
				existing.UpdatedByEmployeeId = actorEmployeeId;
			}

			await _db.SaveChangesAsync(ct);
			return (true, null);
		}

		public async Task<List<CalendarOccurrence>> ExpandAsync(
			int companyId, int eventId, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken ct = default)
		{
			var ev = await _db.CalendarEvents.AsNoTracking()
				.FirstOrDefaultAsync(e => e.Id == eventId && e.CompanyID == companyId && e.DeletedAt == null, ct);
			if (ev == null) return new();

			var schedule = await GetScheduleAsync(companyId, eventId, ct);
			return Expand(ev, schedule, windowStartUtc, windowEndUtc, DefaultZoneId);
		}

		public async Task<List<CalendarOccurrence>> ExpandWindowAsync(
			int companyId, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken ct = default)
		{
			if (companyId <= 0) return new();

			// A recurring event can start LONG before the window and still occur inside it, so the
			// candidate set cannot be filtered by StartAt >= windowStart. Non-recurring events can be,
			// and that is where the volume is.
			var scheduledIds = await _db.CalendarEventSchedules.AsNoTracking()
				.Where(s => s.CompanyId == companyId && s.RecurrenceKind != RecurrenceKinds.None)
				.Select(s => s.EventId).ToListAsync(ct);

			var events = await _db.CalendarEvents.AsNoTracking()
				.Where(e => e.CompanyID == companyId && e.DeletedAt == null
							&& (scheduledIds.Contains(e.Id)
								|| (e.StartAt < windowEndUtc && (e.EndAt == null || e.EndAt > windowStartUtc))))
				.ToListAsync(ct);

			var schedules = await _db.CalendarEventSchedules.AsNoTracking()
				.Where(s => s.CompanyId == companyId)
				.ToDictionaryAsync(s => s.EventId, ct);

			var all = new List<CalendarOccurrence>();
			foreach (var ev in events)
			{
				schedules.TryGetValue(ev.Id, out var sched);
				// ONE resolved default for the whole pass, and every event gets it. This is the call that turned
				// a single null-zone row into a 500 for the entire company (Timeline + ResourceView both land
				// here), so it is the call that most needed the default.
				all.AddRange(Expand(ev, sched, windowStartUtc, windowEndUtc, DefaultZoneId));
			}
			return all.OrderBy(o => o.StartUtc).ToList();
		}

		// ------------------------------------------------------------------------------------------
		// The expansion itself. Pure, given the event and its schedule — which is what makes it
		// testable without a database.
		// ------------------------------------------------------------------------------------------
		/// `defaultTimeZoneId` is the zone to use when the SCHEDULE names none — see DefaultZoneId for what it
		/// is and why. It is a PARAMETER rather than a read of TimeZoneInfo.Local inside this method: that keeps
		/// the method pure and testable (a DST test must be able to name its own zone), and it keeps the "no
		/// hidden server-zone read" rule this file is built on literally true.
		///
		/// Both null still throws, deliberately: a caller that supplies neither a schedule zone nor a default
		/// has told us nothing about the zone, and guessing there is the original defect. Every PRODUCTION
		/// caller supplies DefaultZoneId, so the production path cannot reach that throw.
		public static List<CalendarOccurrence> Expand(
			CalendarEvent ev, CalendarEventSchedule? schedule, DateTime windowStartUtc, DateTime windowEndUtc,
			string? defaultTimeZoneId = null)
		{
			var result = new List<CalendarOccurrence>();
			var duration = ev.EndAt.HasValue ? ev.EndAt.Value - ev.StartAt : TimeSpan.Zero;

			// No schedule, or an explicit "None": the event is exactly what it has always been.
			if (schedule == null || schedule.RecurrenceKind == RecurrenceKinds.None)
			{
				if (ev.StartAt < windowEndUtc && (ev.EndAt ?? ev.StartAt) >= windowStartUtc)
				{
					result.Add(new CalendarOccurrence
					{
						EventId = ev.Id, Title = ev.Title, StartUtc = ev.StartAt, EndUtc = ev.EndAt,
						AllDay = ev.AllDay, LocalDate = DateOnly.FromDateTime(ev.StartAt), IsRepeat = false
					});
				}
				return result;
			}

			// The TWO-argument overload. The one-argument call that used to be here is the whole of defect 3:
			// it passed a nullable as the only candidate and threw when it was null.
			var zone = TaskCalendarTime.ResolveZone(schedule.TimeZoneId, defaultTimeZoneId);

			// Walk the LOCAL calendar, not the UTC clock. This is the whole point of the method.
			var firstLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(ev.StartAt, DateTimeKind.Utc), zone);
			var localTimeOfDay = firstLocal.TimeOfDay;
			var cursor = DateOnly.FromDateTime(firstLocal);

			var exceptions = ParseExceptions(schedule.ExceptionDates);
			var weekdays = ParseWeekdays(schedule.ByWeekdays, firstLocal.DayOfWeek);
			int interval = Math.Max(1, schedule.Interval);
			int emitted = 0;
			int guard = 0;

			while (guard++ < MaxOccurrencesPerEvent * 8)
			{
				if (schedule.UntilLocalDate.HasValue && cursor > DateOnly.FromDateTime(schedule.UntilLocalDate.Value)) break;
				if (schedule.OccurrenceCount.HasValue && emitted >= schedule.OccurrenceCount.Value) break;
				if (result.Count >= MaxOccurrencesPerEvent) break;

				bool matches = schedule.RecurrenceKind switch
				{
					RecurrenceKinds.Daily => DayIndex(cursor, firstLocal) % interval == 0,
					RecurrenceKinds.Weekly => weekdays.Contains(cursor.DayOfWeek)
											  && WeekIndex(cursor, DateOnly.FromDateTime(firstLocal)) % interval == 0,
					RecurrenceKinds.Monthly => cursor.Day == firstLocal.Day
											   && MonthIndex(cursor, firstLocal) % interval == 0,
					RecurrenceKinds.Yearly => cursor.Day == firstLocal.Day && cursor.Month == firstLocal.Month
											  && (cursor.Year - firstLocal.Year) % interval == 0,
					_ => false
				};

				if (matches && !exceptions.Contains(cursor))
				{
					// Each occurrence converts on its OWN local date, so a DST change moves the UTC
					// instant and leaves the local wall-clock time where the user put it.
					var localStart = cursor.ToDateTime(TimeOnly.FromTimeSpan(localTimeOfDay));
					var startUtc = TaskCalendarTime.LocalWallClockToUtc(localStart, zone);
					emitted++;

					if (startUtc < windowEndUtc && (startUtc + duration) >= windowStartUtc)
					{
						result.Add(new CalendarOccurrence
						{
							EventId = ev.Id, Title = ev.Title,
							StartUtc = startUtc,
							EndUtc = ev.EndAt.HasValue ? startUtc + duration : null,
							AllDay = ev.AllDay, LocalDate = cursor,
							IsRepeat = startUtc != ev.StartAt
						});
					}

					// Past the window and no longer growing: stop rather than expanding to the series end.
					if (startUtc >= windowEndUtc) break;
				}

				cursor = cursor.AddDays(1);
			}

			return result;
		}

		private static int DayIndex(DateOnly cursor, DateTime first) =>
			cursor.DayNumber - DateOnly.FromDateTime(first).DayNumber;

		private static int WeekIndex(DateOnly cursor, DateOnly first)
		{
			// Weeks counted from the START of the first occurrence's week, so "every 2 weeks" means the
			// same pair of weeks for every weekday in the series, not a different pair per weekday.
			var firstWeekStart = first.AddDays(-(int)first.DayOfWeek);
			var cursorWeekStart = cursor.AddDays(-(int)cursor.DayOfWeek);
			return (cursorWeekStart.DayNumber - firstWeekStart.DayNumber) / 7;
		}

		private static int MonthIndex(DateOnly cursor, DateTime first) =>
			(cursor.Year - first.Year) * 12 + (cursor.Month - first.Month);

		private static HashSet<DateOnly> ParseExceptions(string? raw)
		{
			var set = new HashSet<DateOnly>();
			if (string.IsNullOrWhiteSpace(raw)) return set;
			foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				if (DateOnly.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out var d)) set.Add(d);
			return set;
		}

		private static HashSet<DayOfWeek> ParseWeekdays(string? raw, DayOfWeek fallback)
		{
			var set = new HashSet<DayOfWeek>();
			if (!string.IsNullOrWhiteSpace(raw))
			{
				foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
					if (Enum.TryParse<DayOfWeek>(part, ignoreCase: true, out var d)) set.Add(d);
			}
			// An empty or unparseable list means "the day the series starts on" — never "no days",
			// which would produce a series with no occurrences and no explanation.
			if (set.Count == 0) set.Add(fallback);
			return set;
		}

		// ------------------------------------------------------------------------------------------
		// CONFLICTS
		// ------------------------------------------------------------------------------------------
		public async Task<List<CalendarConflict>> DetectConflictsAsync(
			int companyId, DateTime startUtc, DateTime endUtc,
			IReadOnlyCollection<int> employeeIds, IReadOnlyCollection<int> resourceIds,
			int? excludeEventId = null, CancellationToken ct = default)
		{
			var conflicts = new List<CalendarConflict>();
			if (companyId <= 0) return conflicts;
			// A zero-length or inverted slot cannot overlap anything. Returning "no conflicts" for it
			// would be a true answer to a question the caller did not mean to ask, so refuse quietly.
			if (endUtc <= startUtc) return conflicts;
			if (employeeIds.Count == 0 && resourceIds.Count == 0) return conflicts;

			// Occurrences, not raw rows: a weekly meeting conflicts on the week it actually lands in.
			var occurrences = await ExpandWindowAsync(companyId, startUtc, endUtc, ct);
			var candidateIds = occurrences.Select(o => o.EventId).Distinct().ToList();
			if (excludeEventId.HasValue) candidateIds.Remove(excludeEventId.Value);
			if (candidateIds.Count == 0) return conflicts;

			var attendeeRows = employeeIds.Count == 0 ? new() : await _db.CalendarEventAttendees.AsNoTracking()
				.Where(a => candidateIds.Contains(a.EventId) && employeeIds.Contains(a.EmployeeId))
				.Select(a => new { a.EventId, a.EmployeeId }).ToListAsync(ct);

			var resourceRows = resourceIds.Count == 0 ? new() : await _db.CalendarEventResources.AsNoTracking()
				.Where(r => r.CompanyId == companyId && candidateIds.Contains(r.EventId) && resourceIds.Contains(r.ResourceId))
				.Select(r => new { r.EventId, r.ResourceId }).ToListAsync(ct);

			var employeeNames = attendeeRows.Count == 0 ? new() : await _db.Employee.AsNoTracking()
				.Where(e => attendeeRows.Select(a => a.EmployeeId).Contains(e.ID))
				.Select(e => new { e.ID, e.FullName }).ToDictionaryAsync(e => e.ID, e => e.FullName ?? $"#{e.ID}", ct);

			var resourceNames = resourceRows.Count == 0 ? new() : await _db.CalendarResources.AsNoTracking()
				.Where(r => r.CompanyId == companyId && resourceRows.Select(x => x.ResourceId).Contains(r.ID))
				.ToDictionaryAsync(r => r.ID, r => r.Name, ct);

			foreach (var occ in occurrences)
			{
				if (excludeEventId.HasValue && occ.EventId == excludeEventId.Value) continue;
				var occEnd = occ.EndUtc ?? occ.StartUtc;
				// Touching intervals do not overlap: a meeting ending at 10:00 does not conflict with one
				// starting at 10:00.
				if (occ.StartUtc >= endUtc || occEnd <= startUtc) continue;

				foreach (var a in attendeeRows.Where(a => a.EventId == occ.EventId))
					conflicts.Add(new CalendarConflict
					{
						EventId = occ.EventId, Title = occ.Title, StartUtc = occ.StartUtc, EndUtc = occ.EndUtc,
						EmployeeId = a.EmployeeId, ResourceId = null,
						SubjectName = employeeNames.TryGetValue(a.EmployeeId, out var n) ? n : $"#{a.EmployeeId}"
					});

				foreach (var r in resourceRows.Where(r => r.EventId == occ.EventId))
					conflicts.Add(new CalendarConflict
					{
						EventId = occ.EventId, Title = occ.Title, StartUtc = occ.StartUtc, EndUtc = occ.EndUtc,
						EmployeeId = null, ResourceId = r.ResourceId,
						SubjectName = resourceNames.TryGetValue(r.ResourceId, out var n) ? n : $"#{r.ResourceId}"
					});
			}

			return conflicts;
		}

		// ------------------------------------------------------------------------------------------
		// AVAILABILITY — the gaps left after removing every busy interval from the window.
		// ------------------------------------------------------------------------------------------
		public async Task<List<AvailabilitySlot>> FindFreeSlotsAsync(
			int companyId, DateTime windowStartUtc, DateTime windowEndUtc,
			IReadOnlyCollection<int> employeeIds, IReadOnlyCollection<int> resourceIds,
			int minimumMinutes, CancellationToken ct = default)
		{
			var slots = new List<AvailabilitySlot>();
			if (companyId <= 0 || windowEndUtc <= windowStartUtc) return slots;
			if (minimumMinutes <= 0) minimumMinutes = 1;

			var busy = await DetectConflictsAsync(companyId, windowStartUtc, windowEndUtc,
				employeeIds, resourceIds, excludeEventId: null, ct);

			// Merge the busy intervals first. Two overlapping meetings are ONE busy stretch; subtracting
			// them one at a time would invent free gaps between them.
			var intervals = busy
				.Select(b => (Start: b.StartUtc, End: b.EndUtc ?? b.StartUtc))
				.Where(i => i.End > i.Start)
				.OrderBy(i => i.Start)
				.ToList();

			var merged = new List<(DateTime Start, DateTime End)>();
			foreach (var i in intervals)
			{
				if (merged.Count > 0 && i.Start <= merged[^1].End)
					merged[^1] = (merged[^1].Start, i.End > merged[^1].End ? i.End : merged[^1].End);
				else
					merged.Add(i);
			}

			var min = TimeSpan.FromMinutes(minimumMinutes);
			var cursor = windowStartUtc;
			foreach (var m in merged)
			{
				if (m.Start > cursor && m.Start - cursor >= min)
					slots.Add(new AvailabilitySlot { StartUtc = cursor, EndUtc = m.Start });
				if (m.End > cursor) cursor = m.End;
			}
			if (windowEndUtc > cursor && windowEndUtc - cursor >= min)
				slots.Add(new AvailabilitySlot { StartUtc = cursor, EndUtc = windowEndUtc });

			return slots;
		}

		// ------------------------------------------------------------------------------------------
		// THE DAY GRIDS. Both build the SAME window the screens have always drawn — the local day,
		// converted to UTC exactly as the controller did — and both return named public types.
		// ------------------------------------------------------------------------------------------

		public async Task<CalendarTimelinePage> BuildTimelinePageAsync(
			int companyId, DateTime localDay, CancellationToken ct = default)
		{
			var day = localDay.Date;
			var occurrences = companyId <= 0
				? new List<CalendarOccurrence>()
				: await ExpandWindowAsync(companyId, day.ToUniversalTime(), day.AddDays(1).ToUniversalTime(), ct);

			if (companyId <= 0)
				return new CalendarTimelinePage { Day = day, People = new List<CalendarTimelineRow>(), OccurrenceCount = 0 };

			// OrderBy(ID) only so two identical requests draw the rows in the same order — SQL Server
			// gives no order without one, and an unordered grid is a UI test that flakes for no reason.
			var employees = await _db.Employee.AsNoTracking()
				.Where(e => e.EmpCompanyID == companyId && e.IsActive)
				.OrderBy(e => e.ID)
				.Select(e => new { e.ID, e.FullName })
				.ToListAsync(ct);

			// COMPANY ISOLATION: CalendarEventAttendee carries no CompanyID, so it is scoped through the
			// events it belongs to — and those came from ExpandWindowAsync, which is already filtered to
			// this company. The previous read had no Where at all and pulled every company's attendee
			// rows into memory, relying on a later intersection in Razor to hide them.
			var eventIds = occurrences.Select(o => o.EventId).Distinct().ToList();
			var attendees = eventIds.Count == 0
				? new List<CalendarEventAttendeeKey>()
				: (await _db.CalendarEventAttendees.AsNoTracking()
					.Where(a => eventIds.Contains(a.EventId))
					.Select(a => new { a.EventId, a.EmployeeId })
					.ToListAsync(ct))
				  .Select(a => new CalendarEventAttendeeKey { EventId = a.EventId, EmployeeId = a.EmployeeId })
				  .ToList();

			var eventsByEmployee = attendees
				.GroupBy(a => a.EmployeeId)
				.ToDictionary(g => g.Key, g => g.Select(a => a.EventId).ToHashSet());

			var people = employees.Select(e => new CalendarTimelineRow
			{
				EmployeeId = e.ID,
				EmployeeName = e.FullName ?? $"#{e.ID}",
				// Always a List, never Array.Empty: a caller must not have to discover which runtime
				// type it was handed for an empty row versus a full one.
				Occurrences = eventsByEmployee.TryGetValue(e.ID, out var mine)
					? occurrences.Where(o => mine.Contains(o.EventId)).ToList()
					: new List<CalendarOccurrence>()
			}).ToList();

			return new CalendarTimelinePage { Day = day, People = people, OccurrenceCount = occurrences.Count };
		}

		public async Task<CalendarResourcePage> BuildResourcePageAsync(
			int companyId, DateTime localDay, CancellationToken ct = default)
		{
			var day = localDay.Date;
			if (companyId <= 0)
				return new CalendarResourcePage { Day = day, Resources = new List<CalendarResourceRow>() };

			var occurrences = await ExpandWindowAsync(companyId, day.ToUniversalTime(), day.AddDays(1).ToUniversalTime(), ct);

			var resources = await _db.CalendarResources.AsNoTracking()
				.Where(r => r.CompanyId == companyId && r.IsActive)
				.OrderBy(r => r.Name)
				.ToListAsync(ct);

			var bookings = await _db.CalendarEventResources.AsNoTracking()
				.Where(r => r.CompanyId == companyId)
				.Select(r => new { r.EventId, r.ResourceId })
				.ToListAsync(ct);

			var eventsByResource = bookings
				.GroupBy(b => b.ResourceId)
				.ToDictionary(g => g.Key, g => g.Select(b => b.EventId).ToHashSet());

			var rows = resources.Select(r => new CalendarResourceRow
			{
				ResourceId = r.ID,
				Name = r.Name,
				Kind = r.Kind,
				Capacity = r.Capacity,
				Occurrences = eventsByResource.TryGetValue(r.ID, out var booked)
					? occurrences.Where(o => booked.Contains(o.EventId)).ToList()
					: new List<CalendarOccurrence>()
			}).ToList();

			return new CalendarResourcePage { Day = day, Resources = rows };
		}

		/// The (event, employee) pair, named rather than anonymous so it can be grouped, asserted and
		/// passed across an assembly boundary. See the page-model note above for why that matters.
		private sealed class CalendarEventAttendeeKey
		{
			public required int EventId { get; init; }
			public required int EmployeeId { get; init; }
		}
	}
}
