using CrossBuy.Models.Platform;

namespace CrossBuy.BL.TasksCalendar
{
	// ==========================================================================================
	// TASKS & CALENDAR — INTEGRATION CONTRACTS  (Phases 3, 4, 5, 6)
	//
	// This file is DECLARATIVE. It defines:
	//   * the exact EntityRegistry onboarding request for Task and CalendarEvent (Phases 3-4), and
	//   * the versioned business-event contracts for both (Phases 5-6).
	//
	// It REGISTERS NOTHING and RAISES NOTHING.
	//
	// WHY NOT REGISTER
	//   BL/Platform/EntityRegistry.cs is Platform Kernel property. This tab may not edit it, so the
	//   onboarding request is expressed as data of the kernel's OWN type (EntityDefinition) — which
	//   means the kernel can adopt it by moving a value, not by re-deriving a design, and a test can
	//   assert the request's shape today.
	//
	// WHY NOT RAISE
	//   RecordAsync must run inside the caller's ambient transaction immediately before commit with no
	//   swallowing catch, and it THROWS without one. Raising an event therefore makes the event kernel a
	//   hard runtime dependency of task creation. An event name is also validated against the registry —
	//   and neither code is registered yet. So: contracts now, raising after onboarding.
	//
	// FAIL CLOSED
	//   Every capability below is opt-IN. Nothing is public, nothing is external-principal accessible,
	//   and an unresolved access decision denies.
	// ==========================================================================================

	/// The entity codes this tab is requesting. Constants so a test, an event name and a future
	/// registration cannot disagree about spelling.
	public static class TaskCalendarEntityCodes
	{
		public const string Task = "Task";
		public const string CalendarEvent = "CalendarEvent";
	}

	/// Where a capability may be turned on only after an access proof exists.
	public enum CapabilityReadiness
	{
		/// Safe to enable at onboarding.
		Enabled = 0,

		/// Requested, but must stay OFF until the named access resolver is implemented and tested.
		PendingAccessProof = 1,

		/// Must not be enabled in this increment.
		Forbidden = 2
	}

	/// The onboarding request for one entity: the kernel's own definition plus the policy metadata the
	/// kernel type does not carry (privacy ceiling, retention/audit placeholders, readiness per capability).
	public sealed class RegistryOnboardingRequest
	{
		public required EntityDefinition Definition { get; init; }

		/// The id type of the aggregate. Both entities use int today.
		public required string IdType { get; init; }

		/// What a read-access decision consults. Named so the kernel does not have to guess.
		public required string ReadAccessResolver { get; init; }

		/// The permission target the resolver is given.
		public required string PermissionTarget { get; init; }

		/// The MOST permissive visibility this entity may ever reach. A ceiling, not a default.
		public required string PrivacyCeiling { get; init; }

		public required CapabilityReadiness Comments { get; init; }
		public required CapabilityReadiness Mentions { get; init; }
		public required CapabilityReadiness Attachments { get; init; }
		public required CapabilityReadiness Timeline { get; init; }

		/// Explicitly forbidden in this increment, and stated rather than implied.
		public CapabilityReadiness PublicVisibility => CapabilityReadiness.Forbidden;
		public CapabilityReadiness ExternalPrincipalAccess => CapabilityReadiness.Forbidden;

		/// Placeholders: the policy is not decided by this tab, and a null would read as "no policy needed".
		public required string RetentionPolicyPlaceholder { get; init; }
		public required string AuditPolicyPlaceholder { get; init; }

		/// Who must action this request.
		public required string ImplementationOwner { get; init; }

		/// Set while the owning tab has not yet adopted it.
		public bool ExternallyPending { get; init; } = true;

		public required string Rationale { get; init; }
	}

	public static class TaskCalendarRegistryOnboarding
	{
		public const string PrivacyInternalOnly = "InternalOnly";

		// ---- Phase 3 — Task ------------------------------------------------------------------
		public static RegistryOnboardingRequest TaskRequest() => new()
		{
			Definition = new EntityDefinition
			{
				Code = TaskCalendarEntityCodes.Task,
				DisplayNameAr = "مهمة",
				DisplayNameEn = "Task",
				Module = "Tasks",
				Icon = "ki-outline ki-check-square",
				Color = "primary",
				// Tasks has a list screen and no per-id detail screen today, exactly like PosOrder, which the
				// registry already carries as reference-only. A detail route replaces this when one exists.
				RouteTemplate = "/Tasks/Index",
				SupportsSearch = true,
				SupportsTimeline = true,
				SupportsComments = true,
				SupportsFiles = true,
				SupportsFollowers = true,
				// NOT ScopeNone. TasksAccessService already exists with 8 actions and 3 roles; registering
				// with no scope would make the registry the loosest door into a module that already has a lock.
				PermissionScope = "Tasks",
				ListedInRecordPicker = true
			},
			IdType = "int",
			ReadAccessResolver = "ITasksAccessService.CanAsync(context, TasksActions.Read, PermissionTarget.ForTask(id))",
			PermissionTarget = "TaskId",
			PrivacyCeiling = PrivacyInternalOnly,
			Comments = CapabilityReadiness.PendingAccessProof,
			Mentions = CapabilityReadiness.Enabled,
			Attachments = CapabilityReadiness.Enabled,
			Timeline = CapabilityReadiness.Enabled,
			RetentionPolicyPlaceholder = "TBD — task history retention is an owner decision (TCI-D-07)",
			AuditPolicyPlaceholder = "TBD — task field-level audit is not implemented (see gap TC-G-13)",
			ImplementationOwner = "Platform Kernel (EntityRegistry)",
			Rationale =
				"Tasks already CONSUMES the registry through TaskLinkResolver but is absent FROM it, so nothing " +
				"can link to a task and a task appears on no timeline. Comments stay PendingAccessProof until " +
				"a per-task read resolver is wired, because a comment surface is a read surface."
		};

		// ---- Phase 4 — CalendarEvent ---------------------------------------------------------
		public static RegistryOnboardingRequest CalendarEventRequest() => new()
		{
			Definition = new EntityDefinition
			{
				Code = TaskCalendarEntityCodes.CalendarEvent,
				DisplayNameAr = "حدث",
				DisplayNameEn = "Calendar event",
				Module = "Calendar",
				Icon = "ki-outline ki-calendar",
				Color = "info",
				RouteTemplate = "/Calendar/Index",
				SupportsSearch = true,
				SupportsTimeline = true,
				SupportsComments = true,
				SupportsFiles = true,
				// Attendees ARE the follower set. A second follower concept would diverge from the attendee
				// list the moment either changed.
				SupportsFollowers = false,
				// Calendar has NO access service. Its record-level rule is organiser/attendee/company-scope,
				// already implemented in CalendarService.Visible(). Introducing a module scope here would
				// invent an authorization surface nobody owns.
				PermissionScope = "None",
				ListedInRecordPicker = true
			},
			IdType = "int",
			ReadAccessResolver =
				"CalendarService.Visible(companyId, employeeId) — organiser OR Scope=Company OR attendee",
			PermissionTarget = "CalendarEventId",
			PrivacyCeiling = PrivacyInternalOnly,
			// A comment surface on an event is a read surface on that event. Until the organiser/attendee
			// check is proven against the registry's own path, comments stay off.
			Comments = CapabilityReadiness.PendingAccessProof,
			Mentions = CapabilityReadiness.Enabled,
			Attachments = CapabilityReadiness.PendingAccessProof,
			Timeline = CapabilityReadiness.Enabled,
			RetentionPolicyPlaceholder = "TBD — calendar history retention is an owner decision (TCI-D-07)",
			AuditPolicyPlaceholder = "TBD — calendar has no field-level audit today",
			ImplementationOwner = "Platform Kernel (EntityRegistry), after the Calendar handover decision (TCI-D-02)",
			Rationale =
				"An attendee EMAIL is not an authenticated platform principal, so external attendees are " +
				"deferred pending an ExternalPrincipalContext. PermissionScope is None because the real rule " +
				"is record-level (organiser/attendee) and already lives in CalendarService."
		};

		public static IReadOnlyList<RegistryOnboardingRequest> All() =>
			new[] { TaskRequest(), CalendarEventRequest() };
	}

	// ==========================================================================================
	// Phases 5 & 6 — BUSINESS EVENT CONTRACTS.  Defined, versioned, NOT raised.
	// ==========================================================================================

	/// How a payload field must be treated by any consumer.
	public enum EventDataClass
	{
		/// Safe for a general timeline.
		Operational = 0,

		/// Identifies a person. Fine internally; not for an external surface.
		Personal = 1,

		/// Commercial or private content. Must NOT appear in a general timeline payload.
		Confidential = 2
	}

	public sealed class EventFieldContract
	{
		public required string Name { get; init; }
		public required string Type { get; init; }
		public required bool Required { get; init; }
		public required EventDataClass Classification { get; init; }
		public string? Note { get; init; }
	}

	public sealed class BusinessEventContract
	{
		public required string EventName { get; init; }
		public required int EventVersion { get; init; }
		public required string EntityCode { get; init; }
		public required string SourceModule { get; init; }
		public required string Trigger { get; init; }
		public required IReadOnlyList<EventFieldContract> Fields { get; init; }

		/// Fields explicitly excluded, and why. Recorded because an omission that is not stated reads
		/// as an oversight the next person "fixes".
		public required IReadOnlyList<string> DeliberatelyExcluded { get; init; }

		public string FullName => $"{EventName} v{EventVersion}";
	}

	public static class TaskCalendarBusinessEvents
	{
		// Every task event carries this envelope. Declared once so a field cannot be forgotten on one event.
		private static IReadOnlyList<EventFieldContract> Envelope(string idField) => new List<EventFieldContract>
		{
			new() { Name = "CompanyID",      Type = "int",      Required = true,  Classification = EventDataClass.Operational },
			new() { Name = idField,          Type = "int",      Required = true,  Classification = EventDataClass.Operational },
			new() { Name = "ActorID",        Type = "int?",     Required = false, Classification = EventDataClass.Personal,
					Note = "null = system (a hosted worker), and that is meaningful, not missing" },
			new() { Name = "OccurredAtUtc",  Type = "DateTime", Required = true,  Classification = EventDataClass.Operational,
					Note = "UTC. Never server-local." },
			new() { Name = "CorrelationID",  Type = "Guid",     Required = true,  Classification = EventDataClass.Operational,
					Note = "one user action stays recognisable across every event and notification it produced" },
			new() { Name = "SourceModule",   Type = "string",   Required = true,  Classification = EventDataClass.Operational },
		};

		private static List<EventFieldContract> With(string idField, params EventFieldContract[] extra)
		{
			var list = Envelope(idField).ToList();
			list.AddRange(extra);
			return list;
		}

		private static EventFieldContract F(string name, string type, bool required, EventDataClass cls, string? note = null)
			=> new() { Name = name, Type = type, Required = required, Classification = cls, Note = note };

		// The same exclusion list applies to every task event, and it is the point of the classification:
		// a timeline is read by more people than a task screen.
		private static readonly string[] TaskExclusions =
		{
			"Description — unrestricted free text may contain confidential detail",
			"Attachments / attachment content",
			"BillRate, CustomerId — commercial data (TM-5)",
			"Timesheet cost rates"
		};

		private static readonly string[] CalendarExclusions =
		{
			"Description / agenda body — meeting content is confidential",
			"Location free text when the event scope is Personal",
			"Attendee email addresses — an attendee email is not an authenticated principal",
			"Attachments / minutes"
		};

		// ---- Phase 5 — Task events -----------------------------------------------------------
		public static IReadOnlyList<BusinessEventContract> TaskEvents() => new List<BusinessEventContract>
		{
			New("Task.Created", TaskCalendarEntityCodes.Task, "Tasks", "a task row is committed",
				With("TaskID",
					F("Title", "string", true, EventDataClass.Operational, "title only — never Description"),
					F("NewAssigneeID", "int?", false, EventDataClass.Personal),
					F("NewStatus", "string", true, EventDataClass.Operational),
					F("Priority", "string", true, EventDataClass.Operational),
					F("NewDueAt", "DateTime?", false, EventDataClass.Operational, "UTC"),
					F("LinkedEntityCode", "string?", false, EventDataClass.Operational),
					F("LinkedEntityId", "int?", false, EventDataClass.Operational)),
				TaskExclusions),

			New("Task.Assigned", TaskCalendarEntityCodes.Task, "Tasks", "a task gains an assignee it did not have",
				With("TaskID",
					F("NewAssigneeID", "int", true, EventDataClass.Personal),
					F("NewDueAt", "DateTime?", false, EventDataClass.Operational, "UTC")),
				TaskExclusions),

			New("Task.Reassigned", TaskCalendarEntityCodes.Task, "Tasks", "the assignee changes from one person to another",
				With("TaskID",
					F("PreviousAssigneeID", "int", true, EventDataClass.Personal),
					F("NewAssigneeID", "int", true, EventDataClass.Personal)),
				TaskExclusions),

			New("Task.StatusChanged", TaskCalendarEntityCodes.Task, "Tasks", "status transitions",
				With("TaskID",
					F("PreviousStatus", "string", true, EventDataClass.Operational),
					F("NewStatus", "string", true, EventDataClass.Operational)),
				TaskExclusions),

			New("Task.DueDateChanged", TaskCalendarEntityCodes.Task, "Tasks", "the due date is set, moved or cleared",
				With("TaskID",
					F("PreviousDueAt", "DateTime?", false, EventDataClass.Operational, "UTC"),
					F("NewDueAt", "DateTime?", false, EventDataClass.Operational, "UTC")),
				TaskExclusions),

			New("Task.BecameOverdue", TaskCalendarEntityCodes.Task, "Tasks",
				"a due date passes while the task is not Done — detected by ONE owner (Phase 9)",
				With("TaskID",
					F("NewDueAt", "DateTime?", true, EventDataClass.Operational, "UTC — the date that was missed"),
					F("NewAssigneeID", "int?", false, EventDataClass.Personal)),
				TaskExclusions),

			New("Task.Completed", TaskCalendarEntityCodes.Task, "Tasks", "status becomes Done",
				With("TaskID",
					F("PreviousStatus", "string", true, EventDataClass.Operational),
					F("NewStatus", "string", true, EventDataClass.Operational),
					F("CompletedAtUtc", "DateTime", true, EventDataClass.Operational)),
				TaskExclusions),

			New("Task.Reopened", TaskCalendarEntityCodes.Task, "Tasks", "status leaves Done",
				With("TaskID",
					F("PreviousStatus", "string", true, EventDataClass.Operational),
					F("NewStatus", "string", true, EventDataClass.Operational)),
				TaskExclusions),

			New("Task.Escalated", TaskCalendarEntityCodes.Task, "Tasks",
				"a task is still not Done after its due date plus the escalation grace, so the assignee's DIRECT " +
				"MANAGER is told. Raised once per missed due date per manager — the occurrence is keyed on the due " +
				"date, not on the sweep time, so a worker that runs repeatedly (or restarts) does not re-escalate.",
				With("TaskID",
					F("NewDueAt", "DateTime?", true, EventDataClass.Operational, "UTC — the date that was missed"),
					F("NewAssigneeID", "int?", true, EventDataClass.Personal, "who was supposed to do it"),
					F("ManagerEmployeeID", "int?", true, EventDataClass.Personal, "the direct manager who was told"),
					F("OverdueHours", "int?", false, EventDataClass.Operational, "how long it had been overdue when escalated")),
				TaskExclusions),

			New("Task.Cancelled", TaskCalendarEntityCodes.Task, "Tasks",
				"a task is cancelled — NOTE: TaskItem.Status has no Cancelled value today (New|InProgress|Done). " +
				"The contract is defined; raising it requires the status vocabulary to gain the value first.",
				With("TaskID",
					F("PreviousStatus", "string", true, EventDataClass.Operational),
					F("NewStatus", "string", true, EventDataClass.Operational),
					F("Reason", "string?", false, EventDataClass.Operational)),
				TaskExclusions),
		};

		// ---- Phase 6 — Calendar events -------------------------------------------------------
		public static IReadOnlyList<BusinessEventContract> CalendarEvents() => new List<BusinessEventContract>
		{
			New("CalendarEvent.Created", TaskCalendarEntityCodes.CalendarEvent, "Calendar", "an event row is committed",
				With("CalendarEventID",
					F("OrganizerID", "int", true, EventDataClass.Personal),
					F("Title", "string", true, EventDataClass.Operational),
					F("StartUtc", "DateTime?", false, EventDataClass.Operational, "null when IsAllDay"),
					F("EndUtc", "DateTime?", false, EventDataClass.Operational, "null when IsAllDay"),
					F("StartLocalDate", "DateOnly?", false, EventDataClass.Operational, "set ONLY when IsAllDay"),
					F("EndLocalDate", "DateOnly?", false, EventDataClass.Operational, "set ONLY when IsAllDay"),
					F("IsAllDay", "bool", true, EventDataClass.Operational),
					F("TimeZoneID", "string?", false, EventDataClass.Operational, "required when not all-day"),
					F("Scope", "string", true, EventDataClass.Operational, "Personal | Company"),
					F("AttendeeCount", "int", true, EventDataClass.Operational, "a count, never the list")),
				CalendarExclusions),

			New("CalendarEvent.Updated", TaskCalendarEntityCodes.CalendarEvent, "Calendar",
				"a material field changes other than start/end — a title edit is NOT a reschedule",
				With("CalendarEventID",
					F("OrganizerID", "int", true, EventDataClass.Personal),
					F("ChangedFields", "string[]", true, EventDataClass.Operational, "names only, never values")),
				CalendarExclusions),

			New("CalendarEvent.Rescheduled", TaskCalendarEntityCodes.CalendarEvent, "Calendar",
				"start or end moves — separated from Updated because only this one is worth interrupting attendees for",
				With("CalendarEventID",
					F("OrganizerID", "int", true, EventDataClass.Personal),
					F("PreviousStartUtc", "DateTime?", false, EventDataClass.Operational),
					F("StartUtc", "DateTime?", false, EventDataClass.Operational),
					F("PreviousEndUtc", "DateTime?", false, EventDataClass.Operational),
					F("EndUtc", "DateTime?", false, EventDataClass.Operational),
					F("IsAllDay", "bool", true, EventDataClass.Operational),
					F("TimeZoneID", "string?", false, EventDataClass.Operational)),
				CalendarExclusions),

			New("CalendarEvent.Cancelled", TaskCalendarEntityCodes.CalendarEvent, "Calendar",
				"the event is soft-deleted",
				With("CalendarEventID",
					F("OrganizerID", "int", true, EventDataClass.Personal),
					F("Reason", "string?", false, EventDataClass.Operational)),
				CalendarExclusions),

			New("CalendarEvent.AttendeeAdded", TaskCalendarEntityCodes.CalendarEvent, "Calendar", "an attendee is invited",
				With("CalendarEventID",
					F("OrganizerID", "int", true, EventDataClass.Personal),
					F("AttendeeEmployeeID", "int", true, EventDataClass.Personal)),
				CalendarExclusions),

			New("CalendarEvent.AttendeeRemoved", TaskCalendarEntityCodes.CalendarEvent, "Calendar", "an attendee is removed",
				With("CalendarEventID",
					F("OrganizerID", "int", true, EventDataClass.Personal),
					F("AttendeeEmployeeID", "int", true, EventDataClass.Personal)),
				CalendarExclusions),

			New("CalendarEvent.ReminderTriggered", TaskCalendarEntityCodes.CalendarEvent, "Calendar",
				"a reminder lead time is reached — system actor",
				With("CalendarEventID",
					F("StartUtc", "DateTime?", false, EventDataClass.Operational),
					F("MinutesBefore", "int", true, EventDataClass.Operational)),
				CalendarExclusions),

			New("CalendarEvent.Started", TaskCalendarEntityCodes.CalendarEvent, "Calendar",
				"the start instant passes — meaningful only for timed events, never for all-day",
				With("CalendarEventID",
					F("StartUtc", "DateTime", true, EventDataClass.Operational)),
				CalendarExclusions),

			New("CalendarEvent.Completed", TaskCalendarEntityCodes.CalendarEvent, "Calendar",
				"the end instant passes — meaningful only for timed events with an end",
				With("CalendarEventID",
					F("EndUtc", "DateTime", true, EventDataClass.Operational)),
				CalendarExclusions),
		};

		public static IReadOnlyList<BusinessEventContract> All() =>
			TaskEvents().Concat(CalendarEvents()).ToList();

		private static BusinessEventContract New(
			string name, string entityCode, string module, string trigger,
			List<EventFieldContract> fields, string[] excluded) => new()
			{
				EventName = name,
				EventVersion = 1,
				EntityCode = entityCode,
				SourceModule = module,
				Trigger = trigger,
				Fields = fields,
				DeliberatelyExcluded = excluded
			};
	}
}
