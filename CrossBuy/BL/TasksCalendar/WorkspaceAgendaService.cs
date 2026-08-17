using CrossBuy.Models.Context;

namespace CrossBuy.BL.TasksCalendar
{
	// ==========================================================================================
	// WORKSPACE AGENDA  (Phase 8)
	//
	// A READ-ONLY composition over two modules. Owner decisions 4, 5, 6, 10, 11 and 12 are the design:
	//
	//   * Workspace owns NO task or calendar business data — this service returns a projection and
	//     stores nothing.
	//   * Task due dates appear as a READ UNION.
	//   * A task due date is NEVER materialised as a CalendarEvent row. There is no write path in this
	//     file at all — no DbSet is added, no Save is called, and `_db` is used ONLY to resolve the
	//     signed-in employee's own company/identity.
	//   * A calendar change never touches a task, and a task change never touches an event.
	//
	// WHY IT CALLS THE TWO SERVICES AND NOT THE DbContext
	//   Every access rule already lives in the owning service: TaskService applies task scope, and
	//   CalendarService.Visible() applies organiser/company-scope/attendee. Querying TaskItems or
	//   CalendarEvents directly here would re-implement both rules in a third place, and the copy would
	//   drift the first time either changed. So this service is a COMPOSER: it asks, it does not query.
	//
	// TIME
	//   Every boundary value goes through TaskCalendarTime. An all-day event stays a local DATE and is
	//   never converted; a timed item is UTC internally and carries an explicit offset outward. Ordering
	//   is by a UTC sort key, so two items from two modules cannot interleave wrongly.
	// ==========================================================================================

	public enum AgendaItemType
	{
		Task = 0,
		CalendarEvent = 1
	}

	/// One row of the unified agenda. Display-safe by construction: it carries no task description, no
	/// event description and no attendee list.
	public sealed class WorkspaceAgendaItem
	{
		public required AgendaItemType ItemType { get; init; }
		public required int SourceId { get; init; }
		public required string Title { get; init; }

		/// Start (events) or due (tasks). Carries either a UTC instant + offset, or an all-day local date.
		public required AgendaInstant Start { get; init; }
		public AgendaInstant? End { get; init; }
		public required bool IsAllDay { get; init; }

		public string? Status { get; init; }
		public string? Priority { get; init; }
		public int? OwnerEmployeeId { get; init; }
		public string? OwnerName { get; init; }

		public required string SourceModule { get; init; }
		public required string EntityCode { get; init; }
		public required string DeepLink { get; init; }

		public required bool IsOverdue { get; init; }
		public required bool IsCompleted { get; init; }

		/// True when the caller may see the item exists but not its detail. The title is replaced; the
		/// slot is still shown, because "busy at 14:00" is the useful half and is not private.
		public required bool IsRedacted { get; init; }

		public string? TimeZoneId { get; init; }

		/// Deterministic ordering key. Exposed so a caller cannot invent a different one and get a
		/// different order for the same data.
		public required DateTime SortKeyUtc { get; init; }
	}

	public sealed class WorkspaceAgendaResult
	{
		public required IReadOnlyList<WorkspaceAgendaItem> Items { get; init; }
		public required int TotalMatched { get; init; }
		public required int Page { get; init; }
		public required int PageSize { get; init; }
		public required DateOnly FromLocalDate { get; init; }
		public required DateOnly ToLocalDate { get; init; }
		public required string TimeZoneId { get; init; }

		/// A source that could not be read is reported, never silently dropped — an agenda missing half
		/// its rows without saying so is worse than an error.
		public required IReadOnlyList<string> DegradedSources { get; init; }

		/// The earliest local date overdue work was gathered from, or null when overdue was not requested.
		/// Exposed so a caller can label the overdue band honestly instead of implying "everything ever late".
		public DateOnly? OverdueFromLocalDate { get; init; }
	}

	public sealed class WorkspaceAgendaQuery
	{
		public required int CompanyId { get; init; }
		public required int EmployeeId { get; init; }
		public required DateOnly FromLocalDate { get; init; }
		public required DateOnly ToLocalDate { get; init; }

		/// Explicit. There is no server-local fallback; an unresolved zone fails (owner decision 7/11).
		public string? TimeZoneId { get; init; }
		public string? CompanyDefaultTimeZoneId { get; init; }

		public bool IncludeTasks { get; init; } = true;
		public bool IncludeCalendar { get; init; } = true;

		/// Restrict tasks to another employee. Honoured ONLY for a caller allowed to see them; this
		/// service does not widen task scope by itself.
		public int? AssigneeEmployeeId { get; init; }

		public bool IncludeCompletedTasks { get; init; }

		/// Include work whose due date has already passed, ABOVE the window rather than inside it.
		///
		/// OPT-IN, default false. The window is what the caller asked for; pulling in dates before it is a
		/// product decision, so the caller states it. The Workspace switches it on because a dashboard that
		/// hides late work is worse than no dashboard — a caller that wants a literal date range still gets one.
		public bool IncludeOverdue { get; init; }

		/// How far back overdue work is gathered, in days, counted from the window start. Bounded on purpose:
		/// an open-ended sweep grows without limit as the system ages, and the oldest rows are the least
		/// actionable. Clamped to [0, MaxOverdueLookbackDays]; 0 means "no overdue".
		public int OverdueLookbackDays { get; init; } = WorkspaceAgendaService.DefaultOverdueLookbackDays;

		public int Page { get; init; } = 1;
		public int PageSize { get; init; } = 50;
	}

	public interface IWorkspaceAgendaService
	{
		Task<WorkspaceAgendaResult> GetAgendaAsync(WorkspaceAgendaQuery query, CancellationToken ct = default);
	}

	public sealed class WorkspaceAgendaService : IWorkspaceAgendaService
	{
		private const int MaxPageSize = 200;

		// ---- candidate-fetch bounds (UAT defect 1) ----------------------------------------------
		//
		// ITaskService.GetTasksAsync CLAMPS SILENTLY: `if (pageSize < 1 || pageSize > 200) pageSize = 25`.
		// This service used to ask for MaxPageSize * 4 = 800, which is > 200, so every request was rewritten
		// to 25 — and the rewrite is silent, no exception, no signal. Worse, the default sort is
		// `CreatedAt DESC`, NOT due date, so the 25 rows that arrived were the most recently CREATED tasks
		// and bore no relation to the agenda window. Employee 5 had 18 tasks due inside 7 days and the
		// agenda showed 4: whichever of the newest 25 happened to land in range.
		//
		// The fix is to PAGE, at the page size the task service actually honours, rather than to raise
		// anybody's global cap. Task pagination safety is TAB-4's contract and stays exactly as it is.
		private const int TaskFetchPageSize = 200;

		/// Hard stop on the candidate scan: 25 × 200 = 5,000 tasks per agenda request. Reaching it is
		/// reported through DegradedSources, never silently truncated — a short agenda that does not admit
		/// it is the defect this whole file exists to avoid.
		private const int MaxTaskPages = 25;

		// ---- overdue bounds (UAT defect 2) ------------------------------------------------------
		//
		// The domain already defines overdue in TaskOverdueSweepService: DueDate < now, Status != "Done",
		// assigned — bounded by COUNT (MaxPerSweep = 500), oldest first, and "the bound is stated, not
		// silent". This mirrors that shape: oldest first, count-capped, stated. It adds a DATE bound too,
		// because the sweep is a per-tick worker while an agenda is rendered on every page load.
		internal const int DefaultOverdueLookbackDays = 90;
		internal const int MaxOverdueLookbackDays = 365;
		private const int MaxOverdueItems = 200;

		private readonly ITaskService _tasks;
		private readonly ICalendarService _calendar;

		public WorkspaceAgendaService(ITaskService tasks, ICalendarService calendar)
		{
			_tasks = tasks; _calendar = calendar;
		}

		public async Task<WorkspaceAgendaResult> GetAgendaAsync(WorkspaceAgendaQuery q, CancellationToken ct = default)
		{
			if (q.CompanyId <= 0)
				throw new InvalidOperationException(
					"Workspace agenda refused: the company is unresolved. There is no company fallback.");
			if (q.EmployeeId <= 0)
				throw new InvalidOperationException(
					"Workspace agenda refused: the employee is unresolved. An agenda is always somebody's agenda.");
			if (q.ToLocalDate < q.FromLocalDate)
				throw new ArgumentException("Workspace agenda refused: the range ends before it starts.", nameof(q));

			// Explicit resolution — throws TimeZoneUnresolvedException when neither is usable.
			var zone = TaskCalendarTime.ResolveZone(q.TimeZoneId, q.CompanyDefaultTimeZoneId);
			var (fromUtc, toUtcExclusive) = TaskCalendarTime.LocalRangeToUtcWindow(q.FromLocalDate, q.ToLocalDate, zone);

			// The overdue floor is derived from the window START, so it does not move when the caller widens
			// the forward range: "7 days out" and "30 days out" agree about how far BACK late work reaches.
			int lookback = Math.Clamp(q.OverdueLookbackDays, 0, MaxOverdueLookbackDays);
			DateOnly? overdueFrom = q.IncludeOverdue && lookback > 0
				? q.FromLocalDate.AddDays(-lookback)
				: null;
			DateTime? overdueFloorUtc = overdueFrom is DateOnly of
				? TaskCalendarTime.LocalRangeToUtcWindow(of, of, zone).fromUtc
				: null;

			var items = new List<WorkspaceAgendaItem>();
			var degraded = new List<string>();

			if (q.IncludeTasks)
			{
				try
				{
					var (taskItems, truncation) = await TaskItemsAsync(q, zone, fromUtc, toUtcExclusive, overdueFloorUtc);
					items.AddRange(taskItems);
					if (truncation != null) degraded.Add(truncation);
				}
				catch (Exception ex) { degraded.Add($"Tasks: {ex.GetType().Name}"); }
			}

			if (q.IncludeCalendar)
			{
				try { items.AddRange(await CalendarItemsAsync(q, zone, fromUtc, toUtcExclusive)); }
				catch (Exception ex) { degraded.Add($"Calendar: {ex.GetType().Name}"); }
			}

			// Deterministic ordering, and every tie is broken — otherwise page 2 could repeat a row from
			// page 1 for two items sharing an instant.
			var ordered = items
				.OrderBy(i => i.SortKeyUtc)
				.ThenBy(i => i.ItemType)
				.ThenBy(i => i.SourceId)
				.ToList();

			int page = q.Page < 1 ? 1 : q.Page;
			int size = q.PageSize < 1 ? 1 : Math.Min(q.PageSize, MaxPageSize);

			return new WorkspaceAgendaResult
			{
				Items = ordered.Skip((page - 1) * size).Take(size).ToList(),
				TotalMatched = ordered.Count,
				Page = page,
				PageSize = size,
				FromLocalDate = q.FromLocalDate,
				ToLocalDate = q.ToLocalDate,
				TimeZoneId = zone.Id,
				DegradedSources = degraded,
				OverdueFromLocalDate = overdueFrom
			};
		}

		// -------------------------------------------------------------------------------------
		// Tasks — through ITaskService. No task rule is re-implemented here.
		// -------------------------------------------------------------------------------------
		private async Task<(List<WorkspaceAgendaItem> Items, string? Truncation)> TaskItemsAsync(
			WorkspaceAgendaQuery q, TimeZoneInfo zone, DateTime fromUtc, DateTime toUtcExclusive,
			DateTime? overdueFloorUtc)
		{
			// "mine" unless an explicit assignee is requested — this service never widens task scope.
			string scope = q.AssigneeEmployeeId.HasValue ? "all" : "mine";

			var (candidates, capped) = await FetchCandidatesAsync(q, scope);

			var nowUtc = TaskCalendarTime.UtcNow();
			var windowed = new List<WorkspaceAgendaItem>();
			var overdue = new List<WorkspaceAgendaItem>();

			foreach (var t in candidates)
			{
				if (t.DueDate is not DateTime due) continue;          // no due date = not on an agenda
				bool completed = string.Equals(t.Status, "Done", StringComparison.OrdinalIgnoreCase);
				if (completed && !q.IncludeCompletedTasks) continue;

				// A task due date is stored without an offset today. Treating it as UTC here is the seam's
				// stated rule; the legacy re-stamp is a separate measured migration (Time-Model doc §6).
				var dueUtc = TaskCalendarTime.AssumeUtc(due);

				bool inWindow = dueUtc >= fromUtc && dueUtc < toUtcExclusive;
				bool isLate = !completed && dueUtc < nowUtc;

				// `!inWindow` is what makes this EXCLUSIVE. A task due earlier TODAY is both inside the
				// window and already late; it belongs to the window bucket once, and still carries
				// IsOverdue = true so the UI badges it. Without that guard it would be emitted twice.
				bool lateAndOutsideWindow =
					overdueFloorUtc is DateTime floor && isLate && !inWindow && dueUtc >= floor;

				if (!inWindow && !lateAndOutsideWindow) continue;

				var start = TaskCalendarTime.Timed(dueUtc, zone);
				var item = new WorkspaceAgendaItem
				{
					ItemType = AgendaItemType.Task,
					SourceId = t.Id,
					Title = t.Title,
					Start = start,
					End = null,
					IsAllDay = false,
					Status = t.Status,
					Priority = t.Priority,
					OwnerEmployeeId = t.AssigneeEmployeeId,
					OwnerName = t.AssigneeName,
					SourceModule = "Tasks",
					EntityCode = TaskCalendarEntityCodes.Task,
					DeepLink = TaskNotificationService.DeepLink(t.Id),

					// The REAL due date is preserved everywhere — Start, SortKeyUtc and this flag. Nothing is
					// restamped to "today": a late task sorts under its own past date and says it is late.
					IsOverdue = isLate,
					IsCompleted = completed,
					IsRedacted = false,
					TimeZoneId = zone.Id,
					SortKeyUtc = start.SortKeyUtc(zone)
				};

				if (inWindow) windowed.Add(item); else overdue.Add(item);
			}

			var notes = new List<string>();
			if (capped)
				notes.Add($"Tasks: candidate scan stopped at its {MaxTaskPages * TaskFetchPageSize:N0}-task bound");

			// Oldest first, then capped — the same order TaskOverdueSweepService uses, for the same reason:
			// if something must be dropped, drop the least recent, and say so.
			if (overdue.Count > MaxOverdueItems)
			{
				notes.Add($"Tasks: showing the {MaxOverdueItems} oldest of {overdue.Count} overdue items");
				overdue = overdue.OrderBy(i => i.SortKeyUtc).ThenBy(i => i.SourceId).Take(MaxOverdueItems).ToList();
			}

			windowed.AddRange(overdue);
			return (windowed, notes.Count == 0 ? null : string.Join(" · ", notes));
		}

		/// Pages ITaskService at the size it actually honours, and CANNOT loop forever: it stops on an empty
		/// page, on a short page (the last one), on a page that contributed no new id, or at MaxTaskPages.
		/// The id set also makes the aggregation duplicate-proof if a concurrent insert shifts the underlying
		/// order between two reads.
		private async Task<(List<TaskRowDto> Rows, bool Capped)> FetchCandidatesAsync(
			WorkspaceAgendaQuery q, string scope)
		{
			var seen = new HashSet<int>();
			var rows = new List<TaskRowDto>();

			for (int page = 1; ; page++)
			{
				var batch = await _tasks.GetTasksAsync(
					companyId: q.CompanyId,
					scope: scope,
					currentEmployeeId: q.EmployeeId,
					status: null, priority: null, q: null,
					page: page, pageSize: TaskFetchPageSize,
					view: null,
					assignee: q.AssigneeEmployeeId,

					// sort: null keeps the service's default `CreatedAt DESC, ID DESC`. That total order has a
					// unique tiebreak, which is what makes Skip/Take across pages safe. "due" would be the
					// intuitive choice and is the WRONG one here: it orders by `DueDate ?? MaxValue` with no
					// tiebreak, so equal due dates could repeat or skip rows between pages.
					sort: null);

				if (batch.Count == 0) break;

				int fresh = 0;
				foreach (var t in batch)
					if (seen.Add(t.Id)) { rows.Add(t); fresh++; }

				if (batch.Count < TaskFetchPageSize) break;   // a short page is the last page
				if (fresh == 0) break;                        // no progress — refuse to spin
				if (page >= MaxTaskPages) return (rows, true);
			}

			return (rows, false);
		}

		// -------------------------------------------------------------------------------------
		// Calendar — through ICalendarService, which applies its own visibility rule.
		// -------------------------------------------------------------------------------------
		private async Task<List<WorkspaceAgendaItem>> CalendarItemsAsync(
			WorkspaceAgendaQuery q, TimeZoneInfo zone, DateTime fromUtc, DateTime toUtcExclusive)
		{
			// CalendarService filters to what THIS employee may see (owner ∪ company-scope ∪ attendee).
			var events = await _calendar.ListAsync(q.CompanyId, q.EmployeeId, fromUtc, toUtcExclusive);

			var result = new List<WorkspaceAgendaItem>();
			foreach (var e in events)
			{
				if (!DateTime.TryParse(e.start, System.Globalization.CultureInfo.InvariantCulture,
						System.Globalization.DateTimeStyles.None, out var startRaw))
					continue;

				DateTime? endRaw = DateTime.TryParse(e.end, System.Globalization.CultureInfo.InvariantCulture,
					System.Globalization.DateTimeStyles.None, out var parsedEnd) ? parsedEnd : null;

				// An all-day value is a LOCAL DATE and is never converted — that conversion is exactly what
				// moves a holiday to the previous evening for anyone west of the server.
				AgendaInstant start = e.allDay
					? TaskCalendarTime.AllDay(startRaw)
					: TaskCalendarTime.Timed(TaskCalendarTime.AssumeUtc(startRaw), zone);

				AgendaInstant? end = endRaw is DateTime en
					? (e.allDay ? TaskCalendarTime.AllDay(en) : TaskCalendarTime.Timed(TaskCalendarTime.AssumeUtc(en), zone))
					: null;

				// A Personal event the caller does not own is visible as a BUSY SLOT with its detail
				// withheld. The caller can only be seeing it because CalendarService let them, but the
				// title of somebody else's personal appointment is not agenda data.
				bool redact = !e.canEdit && string.Equals(e.scope, "Personal", StringComparison.OrdinalIgnoreCase);

				result.Add(new WorkspaceAgendaItem
				{
					ItemType = AgendaItemType.CalendarEvent,
					SourceId = e.id,
					Title = redact ? "مشغول (Busy)" : e.title,
					Start = start,
					End = end,
					IsAllDay = e.allDay,
					Status = null,
					Priority = null,
					OwnerEmployeeId = null,
					OwnerName = redact ? null : e.ownerName,
					SourceModule = "Calendar",
					EntityCode = TaskCalendarEntityCodes.CalendarEvent,
					DeepLink = $"/Calendar/Index?eventId={e.id}",
					IsOverdue = false,
					IsCompleted = false,
					IsRedacted = redact,
					TimeZoneId = e.allDay ? null : zone.Id,
					SortKeyUtc = start.SortKeyUtc(zone)
				});
			}
			return result;
		}
	}
}
