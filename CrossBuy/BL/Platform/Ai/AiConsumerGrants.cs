using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — the gate that decides whether a business fact may enter the AI subsystem at all.
    //
    // WHY THE TABLE IS IN CODE AND NOT IN THE DATABASE. A database-backed grant table would let someone
    // make a new module AI-visible with an INSERT — no review, no builder, no field-level decision. The
    // grant is inseparable from a PROJECTION BUILDER, which is code and must be written and reviewed
    // anyway: a grant with no builder can project nothing, and a builder with no grant is never reached.
    // Keeping them together makes "this event is now AI-visible" a reviewable code change with an explicit
    // field list attached, which is exactly the control §4 asks for. IsEnabled still gives operations a
    // kill switch without a schema change.
    //
    // DEFAULT DENY is structural, not a policy setting: the lookup is a whitelist and its miss path
    // returns Deny. There is no wildcard, no prefix match and no "allow the rest".
    public interface IAiConsumerGrants
    {
        AiGrantDecision Evaluate(string consumer, string eventType, string entityType, string visibility);

        // Every grant, for the startup consistency check and for the tests that assert the table itself.
        IReadOnlyList<AiConsumerGrant> All { get; }
    }

    public sealed class AiConsumerGrants : IAiConsumerGrants
    {
        // ------------------------------------------------------------------------------------------
        // THE PILOT GRANTS.
        //
        // Chosen from events the tree ACTUALLY raises (TaskCalendarEventPublisher), not invented for this
        // increment, and deliberately confined to Task lifecycle: it is operationally useful (throughput,
        // ageing, overdue pressure) and carries no financial or personal detail. Accounting events exist
        // and are tempting, but SalesInvoice/JournalEntry facts are exactly the "sensitive financial
        // detail" §9 says not to expose merely to satisfy a pilot.
        //
        // Every grant is Internal-only. Task events are raised as Internal today; pinning it here means a
        // producer that later raises a Confidential variant of the same event type is REFUSED rather than
        // silently admitted.
        // ------------------------------------------------------------------------------------------
        public const string TaskLifecycleProjection = "TaskLifecycle";
        public const int TaskLifecycleVersion = 1;

        // ------------------------------------------------------------------------------------------
        // INCREMENT 2 — the second shape, chosen to prove the architecture is not hardcoded to Tasks.
        //
        // CalendarEvent, from a DIFFERENT module, with a DIFFERENT permission scope: EntityRegistry
        // gives it PermissionScope = ScopeCalendar, whose access service carries a genuine RECORD-level
        // rule (organiser / attendee / company-scope / manager). That is the point — it exercises the
        // read side's per-record permission re-check rather than a module-wide yes.
        //
        // Only three of the nine calendar event types are granted, and the omissions are the reasoning:
        //   * Updated       — its payload is a list of CHANGED FIELD NAMES; useful to a human timeline,
        //                     but a shape whose content is "which fields moved" invites reconstructing
        //                     the record over time.
        //   * AttendeeAdded/Removed — attendee employee ids over time is people-tracking (who meets
        //                     whom, how often). Scheduling LOAD is the analytic value here; the
        //                     attendee ROSTER is not, so only a count is ever exposed.
        //   * ReminderTriggered / Started / Completed — machine/lifecycle noise with no analytic value
        //                     that Created + Rescheduled + Cancelled do not already carry.
        // ------------------------------------------------------------------------------------------
        public const string CalendarSchedulingProjection = "CalendarScheduling";
        public const int CalendarSchedulingVersion = 1;

        // EntityRegistry.Task rather than the literal "Task": the registry is the canonical vocabulary, and
        // the permission gate resolves the same code. TaskEvents.* likewise — an event type that stopped
        // existing would then fail to compile instead of silently never matching.
        private static readonly AiConsumerGrant[] Grants =
        {
            Grant(TaskEvents.Created,       EntityRegistry.Task, TaskLifecycleProjection, TaskLifecycleVersion),
            Grant(TaskEvents.StatusChanged, EntityRegistry.Task, TaskLifecycleProjection, TaskLifecycleVersion),
            Grant(TaskEvents.Completed,     EntityRegistry.Task, TaskLifecycleProjection, TaskLifecycleVersion),
            Grant(TaskEvents.BecameOverdue, EntityRegistry.Task, TaskLifecycleProjection, TaskLifecycleVersion),

            Grant(CalendarEventEvents.Created,     EntityRegistry.CalendarEvent, CalendarSchedulingProjection, CalendarSchedulingVersion),
            Grant(CalendarEventEvents.Rescheduled, EntityRegistry.CalendarEvent, CalendarSchedulingProjection, CalendarSchedulingVersion),
            Grant(CalendarEventEvents.Cancelled,   EntityRegistry.CalendarEvent, CalendarSchedulingProjection, CalendarSchedulingVersion),
        };

        private static AiConsumerGrant Grant(string eventType, string entityType, string projectionType, int version) => new()
        {
            Consumer = BusinessEventConsumers.AiProjection,
            EventType = eventType,
            EntityType = entityType,
            ProjectionType = projectionType,
            ProjectionVersion = version,
            MaxVisibility = BusinessEventVisibility.Internal,
            IsEnabled = true,
        };

        public IReadOnlyList<AiConsumerGrant> All => Grants;

        public AiGrantDecision Evaluate(string consumer, string eventType, string entityType, string visibility)
        {
            // Ordinal comparisons throughout: these are canonical machine identifiers, and a culture-aware
            // comparison is how "Task" stops matching "Task" under a Turkish locale.
            var grant = Array.Find(Grants, g =>
                string.Equals(g.Consumer, consumer, StringComparison.Ordinal) &&
                string.Equals(g.EventType, eventType, StringComparison.Ordinal));

            // ---- default deny: no grant, no projection ----
            if (grant == null)
                return AiGrantDecision.Deny($"no AI grant for event type '{eventType}'");

            if (!grant.IsEnabled)
                return AiGrantDecision.Deny($"AI grant for '{eventType}' is disabled");

            // The entity type must match the grant as well as the event type. "<Entity>.<Action>" is
            // validated at record time, so a mismatch here means the registry changed under us — refuse
            // rather than project a fact against the wrong entity kind.
            if (!string.Equals(grant.EntityType, entityType, StringComparison.Ordinal))
                return AiGrantDecision.Deny(
                    $"event '{eventType}' arrived for entity '{entityType}' but the grant covers '{grant.EntityType}'");

            // ---- classification ceiling ----
            if (!string.Equals(visibility, grant.MaxVisibility, StringComparison.Ordinal))
                return AiGrantDecision.Deny(
                    $"event '{eventType}' has visibility '{visibility}'; the AI grant admits only '{grant.MaxVisibility}'");

            return AiGrantDecision.Allow(grant);
        }
    }
}
