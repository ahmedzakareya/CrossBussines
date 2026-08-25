using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.TasksCalendar
{
	// ==========================================================================================
	// TASKS & CALENDAR — BUSINESS EVENT PUBLISHER  (Phase 3)
	//
	// Turns the contracts defined in TaskCalendarIntegrationContracts.cs into actual kernel events.
	//
	// THE TRANSACTION RULE, and why every method here is only half the story
	//
	//   RecordAsync must run INSIDE the caller's ambient transaction, immediately before commit, with no
	//   swallowing catch — and it throws when there is no ambient transaction. So this publisher does NOT
	//   open a transaction of its own: the CALLER opens one, mutates state, calls the publisher, and only
	//   then commits. That is what makes "no event on rollback" true by construction rather than by
	//   convention — the event row and the state change are the same commit.
	//
	//   Every call site here is inside a ScopedTx opened by TaskService or CalendarService.
	//
	// PAYLOAD DISCIPLINE
	//
	//   Payloads are built field by field from the classification in the contracts. NOTHING is serialised
	//   wholesale: passing the entity itself would leak Description, BillRate, CustomerId and the attendee
	//   list into a payload a timeline renders to more people than the record does. The
	//   No_event_payload_* tests assert the contract; this file is where the contract is honoured.
	//
	// NAME VALIDATION
	//
	//   BusinessEventTypes.Validate refuses a name whose prefix is not the entity code. That is why the
	//   attendee and reminder events are CalendarEvent.AttendeeAdded / .ReminderTriggered rather than
	//   CalendarAttendee.Added — the earlier contract shape would have been rejected at publish time.
	// ==========================================================================================

	public interface ITaskCalendarEventPublisher
	{
		Task<long> TaskCreatedAsync(TaskItem task, int? actorId, Guid correlationId, CancellationToken ct = default);
		Task<long> TaskAssignedAsync(TaskItem task, int? actorId, Guid correlationId, CancellationToken ct = default);
		Task<long> TaskReassignedAsync(TaskItem task, int previousAssigneeId, int? actorId, Guid correlationId, CancellationToken ct = default);
		Task<long> TaskStatusChangedAsync(TaskItem task, string previousStatus, int? actorId, Guid correlationId, CancellationToken ct = default);
		Task<long> TaskDueDateChangedAsync(TaskItem task, DateTime? previousDueUtc, int? actorId, Guid correlationId, CancellationToken ct = default);
		Task<long> TaskCompletedAsync(TaskItem task, string previousStatus, int? actorId, Guid correlationId, CancellationToken ct = default);
		Task<long> TaskReopenedAsync(TaskItem task, string previousStatus, int? actorId, Guid correlationId, CancellationToken ct = default);
		Task<long> TaskBecameOverdueAsync(TaskItem task, DateTime dueUtc, CancellationToken ct = default);

		// Raised when a task is STILL not Done after its due date plus the escalation grace, and the assignee's
		// direct manager has been told. `managerEmployeeId` is the manager who was notified — never a fallback
		// and never a foreign-company employee; when no manager resolves, no event is raised at all.
		Task<long> TaskEscalatedAsync(TaskItem task, DateTime dueUtc, int managerEmployeeId, int overdueHours,
			CancellationToken ct = default);

		Task<long> CalendarCreatedAsync(int companyId, int eventId, int organizerId, string title,
			DateTime? startUtc, DateTime? endUtc, DateOnly? startLocalDate, DateOnly? endLocalDate,
			bool isAllDay, string? timeZoneId, string scope, int attendeeCount,
			int? actorId, Guid correlationId, CancellationToken ct = default);
		Task<long> CalendarUpdatedAsync(int companyId, int eventId, int organizerId, IReadOnlyList<string> changedFields,
			int? actorId, Guid correlationId, CancellationToken ct = default);
		Task<long> CalendarRescheduledAsync(int companyId, int eventId, int organizerId,
			DateTime? previousStartUtc, DateTime? startUtc, DateTime? previousEndUtc, DateTime? endUtc,
			bool isAllDay, string? timeZoneId, int? actorId, Guid correlationId, CancellationToken ct = default);
		Task<long> CalendarCancelledAsync(int companyId, int eventId, int organizerId, string? reason,
			int? actorId, Guid correlationId, CancellationToken ct = default);
		Task<long> CalendarAttendeeAddedAsync(int companyId, int eventId, int organizerId, int attendeeEmployeeId,
			int? actorId, Guid correlationId, CancellationToken ct = default);
		Task<long> CalendarAttendeeRemovedAsync(int companyId, int eventId, int organizerId, int attendeeEmployeeId,
			int? actorId, Guid correlationId, CancellationToken ct = default);
	}

	public sealed class TaskCalendarEventPublisher : ITaskCalendarEventPublisher
	{
		private const string TaskModule = "Tasks";
		private const string CalendarModule = "Calendar";

		private readonly IBusinessEventService _events;
		public TaskCalendarEventPublisher(IBusinessEventService events) { _events = events; }

		// ---- Task ------------------------------------------------------------------------------
		public Task<long> TaskCreatedAsync(TaskItem t, int? actorId, Guid correlationId, CancellationToken ct = default) =>
			RecordTask(t, "Task.Created", actorId, correlationId, "created", new
			{
				title = t.Title,                       // title only — never Description
				newAssigneeId = Nullable(t.AssigneeEmployeeId),
				newStatus = t.Status,
				priority = t.Priority,
				newDueAt = Utc(t.DueDate),
				linkedEntityCode = t.EntityType,
				linkedEntityId = t.EntityId,
				sourceModule = TaskModule
			}, ct);

		public Task<long> TaskAssignedAsync(TaskItem t, int? actorId, Guid correlationId, CancellationToken ct = default) =>
			RecordTask(t, "Task.Assigned", actorId, correlationId, $"assigned:{t.AssigneeEmployeeId}", new
			{
				newAssigneeId = t.AssigneeEmployeeId,
				newDueAt = Utc(t.DueDate),
				sourceModule = TaskModule
			}, ct);

		public Task<long> TaskReassignedAsync(TaskItem t, int previousAssigneeId, int? actorId, Guid correlationId, CancellationToken ct = default) =>
			RecordTask(t, "Task.Reassigned", actorId, correlationId, $"reassigned:{previousAssigneeId}->{t.AssigneeEmployeeId}", new
			{
				previousAssigneeId,
				newAssigneeId = t.AssigneeEmployeeId,
				sourceModule = TaskModule
			}, ct);

		public Task<long> TaskStatusChangedAsync(TaskItem t, string previousStatus, int? actorId, Guid correlationId, CancellationToken ct = default) =>
			RecordTask(t, "Task.StatusChanged", actorId, correlationId, $"status:{previousStatus}->{t.Status}", new
			{
				previousStatus,
				newStatus = t.Status,
				sourceModule = TaskModule
			}, ct);

		public Task<long> TaskDueDateChangedAsync(TaskItem t, DateTime? previousDueUtc, int? actorId, Guid correlationId, CancellationToken ct = default) =>
			RecordTask(t, "Task.DueDateChanged", actorId, correlationId,
				$"due:{Stamp(previousDueUtc)}->{Stamp(t.DueDate)}", new
				{
					previousDueAt = Utc(previousDueUtc),
					newDueAt = Utc(t.DueDate),
					sourceModule = TaskModule
				}, ct);

		public Task<long> TaskCompletedAsync(TaskItem t, string previousStatus, int? actorId, Guid correlationId, CancellationToken ct = default) =>
			RecordTask(t, "Task.Completed", actorId, correlationId, "completed", new
			{
				previousStatus,
				newStatus = t.Status,
				completedAtUtc = Utc(t.CompletedAt) ?? TaskCalendarTime.UtcNow(),
				sourceModule = TaskModule
			}, ct);

		public Task<long> TaskReopenedAsync(TaskItem t, string previousStatus, int? actorId, Guid correlationId, CancellationToken ct = default) =>
			RecordTask(t, "Task.Reopened", actorId, correlationId, $"reopened:{previousStatus}->{t.Status}", new
			{
				previousStatus,
				newStatus = t.Status,
				sourceModule = TaskModule
			}, ct);

		/// System-actored. The dedup key is derived from the DUE DATE, so a worker that sweeps every
		/// fifteen minutes records ONE event for a given missed date.
		public Task<long> TaskBecameOverdueAsync(TaskItem t, DateTime dueUtc, CancellationToken ct = default) =>
			RecordTask(t, "Task.BecameOverdue", null,
				TaskNotificationService.DeterministicOverdueCorrelation(t.ID, dueUtc),
				$"overdue:{Stamp(dueUtc)}", new
				{
					newDueAt = TaskCalendarTime.AssumeUtc(dueUtc),
					newAssigneeId = Nullable(t.AssigneeEmployeeId),
					sourceModule = TaskModule
				}, ct);

		// The OCCURRENCE is (due date, manager), not the sweep time. That is what makes a repeated sweep, a
		// process restart, or two workers racing produce ONE event: the kernel's DedupKey collapses them.
		public Task<long> TaskEscalatedAsync(TaskItem t, DateTime dueUtc, int managerEmployeeId, int overdueHours,
			CancellationToken ct = default) =>
			RecordTask(t, "Task.Escalated", null,
				TaskNotificationService.DeterministicEscalationCorrelation(t.ID, dueUtc, managerEmployeeId),
				$"escalated:{Stamp(dueUtc)}:{managerEmployeeId}", new
				{
					newDueAt = TaskCalendarTime.AssumeUtc(dueUtc),
					newAssigneeId = Nullable(t.AssigneeEmployeeId),
					managerEmployeeId = Nullable(managerEmployeeId),
					overdueHours,
					sourceModule = TaskModule
				}, ct);

		// ---- Calendar --------------------------------------------------------------------------
		public Task<long> CalendarCreatedAsync(int companyId, int eventId, int organizerId, string title,
			DateTime? startUtc, DateTime? endUtc, DateOnly? startLocalDate, DateOnly? endLocalDate,
			bool isAllDay, string? timeZoneId, string scope, int attendeeCount,
			int? actorId, Guid correlationId, CancellationToken ct = default) =>
			RecordCalendar(companyId, eventId, "CalendarEvent.Created", actorId, correlationId, "created", new
			{
				organizerId,
				title,                                   // title only — never Description
				startUtc = Utc(startUtc),
				endUtc = Utc(endUtc),
				startLocalDate = startLocalDate?.ToString("yyyy-MM-dd"),
				endLocalDate = endLocalDate?.ToString("yyyy-MM-dd"),
				isAllDay,
				timeZoneId,
				scope,
				attendeeCount,                           // a COUNT, never the attendee list
				sourceModule = CalendarModule
			}, ct);

		public Task<long> CalendarUpdatedAsync(int companyId, int eventId, int organizerId,
			IReadOnlyList<string> changedFields, int? actorId, Guid correlationId, CancellationToken ct = default) =>
			RecordCalendar(companyId, eventId, "CalendarEvent.Updated", actorId, correlationId,
				$"updated:{string.Join(",", changedFields)}", new
				{
					organizerId,
					changedFields,                       // field NAMES only, never their values
					sourceModule = CalendarModule
				}, ct);

		public Task<long> CalendarRescheduledAsync(int companyId, int eventId, int organizerId,
			DateTime? previousStartUtc, DateTime? startUtc, DateTime? previousEndUtc, DateTime? endUtc,
			bool isAllDay, string? timeZoneId, int? actorId, Guid correlationId, CancellationToken ct = default) =>
			RecordCalendar(companyId, eventId, "CalendarEvent.Rescheduled", actorId, correlationId,
				$"resched:{Stamp(previousStartUtc)}->{Stamp(startUtc)}", new
				{
					organizerId,
					previousStartUtc = Utc(previousStartUtc),
					startUtc = Utc(startUtc),
					previousEndUtc = Utc(previousEndUtc),
					endUtc = Utc(endUtc),
					isAllDay,
					timeZoneId,
					sourceModule = CalendarModule
				}, ct);

		public Task<long> CalendarCancelledAsync(int companyId, int eventId, int organizerId, string? reason,
			int? actorId, Guid correlationId, CancellationToken ct = default) =>
			RecordCalendar(companyId, eventId, "CalendarEvent.Cancelled", actorId, correlationId, "cancelled", new
			{
				organizerId,
				reason,
				sourceModule = CalendarModule
			}, ct);

		public Task<long> CalendarAttendeeAddedAsync(int companyId, int eventId, int organizerId,
			int attendeeEmployeeId, int? actorId, Guid correlationId, CancellationToken ct = default) =>
			RecordCalendar(companyId, eventId, "CalendarEvent.AttendeeAdded", actorId, correlationId,
				$"att+:{attendeeEmployeeId}", new
				{
					organizerId,
					attendeeEmployeeId,                  // the platform employee id — never an email address
					sourceModule = CalendarModule
				}, ct);

		public Task<long> CalendarAttendeeRemovedAsync(int companyId, int eventId, int organizerId,
			int attendeeEmployeeId, int? actorId, Guid correlationId, CancellationToken ct = default) =>
			RecordCalendar(companyId, eventId, "CalendarEvent.AttendeeRemoved", actorId, correlationId,
				$"att-:{attendeeEmployeeId}", new
				{
					organizerId,
					attendeeEmployeeId,
					sourceModule = CalendarModule
				}, ct);

		// ---- the single record path ------------------------------------------------------------
		private Task<long> RecordTask(TaskItem t, string eventType, int? actorId, Guid correlationId,
			string occurrence, object payload, CancellationToken ct) =>
			_events.RecordAsync(new BusinessEventRecord
			{
				EntityCode = TaskCalendarEntityCodes.Task,
				EntityId = t.ID,
				EventType = eventType,
				Payload = payload,
				PayloadVersion = 1,
				Visibility = BusinessEventVisibility.Internal,
				CorrelationId = correlationId,
				CompanyIdOverride = t.CompanyId,
				ActorEmployeeIdOverride = actorId,
				OccurredAt = TaskCalendarTime.UtcNow(),
				// Deterministic per (entity, event, occurrence): a retried save records ONE event.
				DedupKey = $"{eventType}:{t.CompanyId}:{t.ID}:{occurrence}"
			}, ct);

		private Task<long> RecordCalendar(int companyId, int eventId, string eventType, int? actorId,
			Guid correlationId, string occurrence, object payload, CancellationToken ct) =>
			_events.RecordAsync(new BusinessEventRecord
			{
				EntityCode = TaskCalendarEntityCodes.CalendarEvent,
				EntityId = eventId,
				EventType = eventType,
				Payload = payload,
				PayloadVersion = 1,
				Visibility = BusinessEventVisibility.Internal,
				CorrelationId = correlationId,
				CompanyIdOverride = companyId,
				ActorEmployeeIdOverride = actorId,
				OccurredAt = TaskCalendarTime.UtcNow(),
				DedupKey = $"{eventType}:{companyId}:{eventId}:{occurrence}"
			}, ct);

		private static DateTime? Utc(DateTime? v) => v.HasValue ? TaskCalendarTime.AssumeUtc(v.Value) : null;
		private static int? Nullable(int v) => v > 0 ? v : null;
		private static string Stamp(DateTime? v) => v.HasValue
			? TaskCalendarTime.AssumeUtc(v.Value).ToString("yyyyMMddTHHmmss", System.Globalization.CultureInfo.InvariantCulture)
			: "none";
	}
}
