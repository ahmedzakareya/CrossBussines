namespace CrossBuy.Models.Platform
{
    // Platform Kernel — the payload shapes the TIMELINE reads for the Task and Calendar event families.
    //
    // WHY THESE EXIST. TaskCalendarEventPublisher writes its payloads as anonymous objects, so nothing on the
    // read side could bind them: TimelineEventPresenter.TryPresent had no case for Task.* or CalendarEvent.*
    // and answered "no timeline presentation registered". EntityRegistry marks both families
    // SupportsTimeline = true, so TimelineProjectionConsumer took the strict path and failed every one of
    // them — 512 dispatch rows at Attempts = 5 — while NotificationProjection for the same events succeeded.
    //
    // These are READ-SIDE CONTRACTS, deliberately: they mirror the fields the publisher already emits rather
    // than becoming a second source of truth. Every property is optional because a payload is written per
    // event type and carries only the fields that transition needs — Task.StatusChanged has no title,
    // Task.Assigned has no previousStatus. Binding is case-insensitive (JsonSerializerDefaults.Web), which is
    // what lets these PascalCase properties read the publisher's camelCase JSON.
    //
    // PayloadVersion is 1 for both families (TaskCalendarEventPublisher.RecordTask/RecordCalendar), and the
    // presenter's shared version gate refuses anything higher rather than rendering a payload it may not
    // understand.

    public sealed class TaskEventPayload
    {
        public const int Version = 1;

        // Title only. The publisher deliberately never emits Description ("title only — never Description"),
        // so the timeline cannot show it either — the read side inherits that decision instead of restating it.
        public string? Title { get; init; }

        public string? PreviousStatus { get; init; }
        public string? NewStatus { get; init; }
        public string? Priority { get; init; }

        public DateTime? PreviousDueAt { get; init; }
        public DateTime? NewDueAt { get; init; }
        public DateTime? CompletedAtUtc { get; init; }

        // Employee ids are carried so a future renderer could resolve them to names. The presenter does NOT
        // put them in the description: a bare id is not information to a human reader, and printing it adds
        // an identifier to a row without adding meaning.
        public int? PreviousAssigneeId { get; init; }
        public int? NewAssigneeId { get; init; }

        // What the task hangs off, when it was created from another record.
        public string? LinkedEntityCode { get; init; }
        public int? LinkedEntityId { get; init; }

        public string? SourceModule { get; init; }
    }

    public sealed class CalendarEventPayload
    {
        public const int Version = 1;

        public int? OrganizerId { get; init; }

        // Title only, same rule as Task.
        public string? Title { get; init; }

        public DateTime? PreviousStartUtc { get; init; }
        public DateTime? StartUtc { get; init; }
        public DateTime? PreviousEndUtc { get; init; }
        public DateTime? EndUtc { get; init; }

        public string? StartLocalDate { get; init; }
        public string? EndLocalDate { get; init; }

        public bool? IsAllDay { get; init; }
        public string? TimeZoneId { get; init; }
        public string? Scope { get; init; }

        // A COUNT, never the attendee list — the publisher's own constraint.
        public int? AttendeeCount { get; init; }

        // Field NAMES only, never their values. Safe to show: it says WHAT changed without saying what to.
        public IReadOnlyList<string>? ChangedFields { get; init; }

        // The platform employee id of a single added/removed attendee, never an email address. Bound so the
        // shape is complete; NOT rendered, for the same reason as the Task assignee ids.
        public int? AttendeeEmployeeId { get; init; }

        // Free text a user typed when cancelling. Bound but deliberately NOT rendered — see PresentCalendarEvent.
        public string? Reason { get; init; }

        public string? SourceModule { get; init; }
    }
}
