using System.Text.Json.Nodes;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — where DATA MINIMIZATION actually happens.
    //
    // A builder receives the business event envelope and returns an explicit field list. It never
    // serializes an EF entity, never walks a navigation property, never copies "the rest of the payload",
    // and never reaches back into the database for more. If a field is not named here, AI does not get it.
    //
    // The default for an event with no builder is NO PAYLOAD — the consumer refuses rather than falling
    // back to the raw event payload, because a fallback is precisely how unreviewed fields leak.
    public interface IAiProjectionBuilder
    {
        // The shape this builder produces. Must match a grant's ProjectionType.
        string ProjectionType { get; }
        int ProjectionVersion { get; }

        // Returns null when the event cannot be projected (payload missing or malformed). Null means FAIL
        // CLOSED — the consumer records a deterministic skip; it does not invent an empty projection.
        AiProjectionEnvelope? Build(BusinessEventEnvelope envelope);
    }

    // ---------------------------------------------------------------------------------------------
    // Task lifecycle — the pilot shape, covering Task.Created / StatusChanged / Completed / BecameOverdue.
    //
    // WHAT IS EXPOSED, and why it is safe: status, priority, dates, the assignee's EMPLOYEE ID, and the
    // linked entity's code+id. These are structural operational facts. They support the analytics this
    // pilot exists to enable — throughput, ageing, overdue pressure, workload distribution — and none of
    // them is free text.
    //
    // WHAT IS DELIBERATELY EXCLUDED, and why it matters more than what is included:
    //
    //   * title — the producer already refuses to emit Description, and emits title for the human
    //     TIMELINE. A task title is free text written by a user and routinely contains a customer name, a
    //     person's name, an invoice number or a complaint. Free text is where PII enters a corpus, and a
    //     corpus is the one place it cannot be un-seen. Excluded pending an explicit data-classification
    //     decision, which is a business call and not mine to assume.
    //   * description, comments, attachments — never in the event payload to begin with, and never here.
    //   * employee NAMES, emails, photographs — only the integer id crosses the boundary. An id is
    //     re-identifiable by an authorized reader through the normal permission path; a name in a corpus
    //     is not revocable.
    //   * amounts, costs, prices — no financial field is in this shape at all.
    //   * the raw event payload — not passed through under any key.
    // ---------------------------------------------------------------------------------------------
    public sealed class TaskLifecycleProjectionBuilder : IAiProjectionBuilder
    {
        public string ProjectionType => AiConsumerGrants.TaskLifecycleProjection;
        public int ProjectionVersion => AiConsumerGrants.TaskLifecycleVersion;

        public AiProjectionEnvelope? Build(BusinessEventEnvelope envelope)
        {
            // A Task event with no payload is malformed for this shape. Fail closed rather than write a
            // projection that asserts nothing but still claims the event was ingested.
            if (envelope.Payload is not JsonObject payload) return null;

            // Every field is named. There is no loop over payload properties anywhere in this method —
            // that is the point: adding a field to the producer cannot add a field to the AI corpus.
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["taskId"] = envelope.Entity.Id,
                ["action"] = ActionOf(envelope.EventType),
                ["previousStatus"] = Str(payload, "previousStatus"),
                ["newStatus"] = Str(payload, "newStatus"),
                ["priority"] = Str(payload, "priority"),
                ["assigneeEmployeeId"] = Int(payload, "newAssigneeId"),
                ["previousAssigneeEmployeeId"] = Int(payload, "previousAssigneeId"),
                ["dueAtUtc"] = Str(payload, "newDueAt"),
                ["previousDueAtUtc"] = Str(payload, "previousDueAt"),
                ["completedAtUtc"] = Str(payload, "completedAtUtc"),
                ["linkedEntityCode"] = Str(payload, "linkedEntityCode"),
                ["linkedEntityId"] = Int(payload, "linkedEntityId"),
                ["sourceModule"] = Str(payload, "sourceModule"),
            };

            return new AiProjectionEnvelope
            {
                ProjectionType = ProjectionType,
                ProjectionVersion = ProjectionVersion,
                Payload = fields,
            };
        }

        // "Task.StatusChanged" -> "StatusChanged". The entity half is already a column on the projection,
        // so repeating it in the payload would be redundant, not informative.
        private static string ActionOf(string eventType)
        {
            int dot = eventType.IndexOf('.');
            return dot >= 0 && dot < eventType.Length - 1 ? eventType[(dot + 1)..] : eventType;
        }

        // Absent keys become null rather than throwing: these four event types legitimately carry
        // different subsets (BecameOverdue has no previousStatus), and a null is an honest "not part of
        // this fact". A WRONGLY-TYPED value is different — it means the producer changed shape — so it is
        // also surfaced as null rather than coerced into something plausible.
        private static string? Str(JsonObject payload, string key)
            => payload.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s)
                ? s : null;

        private static int? Int(JsonObject payload, string key)
            => payload.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<int>(out var i)
                ? i : null;

        internal static string? Str_(JsonObject p, string k) => Str(p, k);
        internal static int? Int_(JsonObject p, string k) => Int(p, k);
        internal static bool? Bool_(JsonObject p, string k)
            => p.TryGetPropertyValue(k, out var n) && n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        internal static string ActionOf_(string eventType) => ActionOf(eventType);
    }

    // ---------------------------------------------------------------------------------------------
    // Calendar scheduling — the SECOND shape (Increment 2), covering
    // CalendarEvent.Created / Rescheduled / Cancelled.
    //
    // WHAT IS EXPOSED: the organiser's employee id, the time window (and its previous value on a
    // reschedule), all-day flag, time zone, the event's scope, and an attendee COUNT. This supports the
    // analytic the shape exists for — scheduling load, meeting churn, reschedule and cancellation rates
    // — and every field is structural.
    //
    // WHAT IS DELIBERATELY EXCLUDED:
    //
    //   * title — the single most sensitive field in a calendar. Meeting subjects routinely name a
    //     client, a counterparty, a candidate, or the nature of a private matter ("disciplinary review",
    //     "redundancy consultation"). The producer emits it for the human timeline, where the reader is
    //     already authorized to see that specific event; a corpus is not that.
    //   * reason — the free-text cancellation reason, for the same argument.
    //   * the attendee ROSTER — only a count crosses. Attendee ids over time reconstruct who meets whom
    //     and how often, which is people-tracking rather than scheduling analytics.
    //   * changedFields — CalendarEvent.Updated is not granted at all, so this never arises.
    //   * external attendee emails — never in the event payload to begin with, and never here.
    // ---------------------------------------------------------------------------------------------
    public sealed class CalendarSchedulingProjectionBuilder : IAiProjectionBuilder
    {
        public string ProjectionType => AiConsumerGrants.CalendarSchedulingProjection;
        public int ProjectionVersion => AiConsumerGrants.CalendarSchedulingVersion;

        public AiProjectionEnvelope? Build(BusinessEventEnvelope envelope)
        {
            if (envelope.Payload is not JsonObject payload) return null;

            // Named fields only — no loop over the stored payload, exactly as the Task builder.
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["calendarEventId"] = envelope.Entity.Id,
                ["action"] = TaskLifecycleProjectionBuilder.ActionOf_(envelope.EventType),
                ["organizerEmployeeId"] = TaskLifecycleProjectionBuilder.Int_(payload, "organizerId"),
                ["startUtc"] = TaskLifecycleProjectionBuilder.Str_(payload, "startUtc"),
                ["endUtc"] = TaskLifecycleProjectionBuilder.Str_(payload, "endUtc"),
                ["previousStartUtc"] = TaskLifecycleProjectionBuilder.Str_(payload, "previousStartUtc"),
                ["previousEndUtc"] = TaskLifecycleProjectionBuilder.Str_(payload, "previousEndUtc"),
                ["isAllDay"] = TaskLifecycleProjectionBuilder.Bool_(payload, "isAllDay"),
                ["timeZoneId"] = TaskLifecycleProjectionBuilder.Str_(payload, "timeZoneId"),
                ["scope"] = TaskLifecycleProjectionBuilder.Str_(payload, "scope"),
                ["attendeeCount"] = TaskLifecycleProjectionBuilder.Int_(payload, "attendeeCount"),
                ["sourceModule"] = TaskLifecycleProjectionBuilder.Str_(payload, "sourceModule"),
            };

            return new AiProjectionEnvelope
            {
                ProjectionType = ProjectionType,
                ProjectionVersion = ProjectionVersion,
                Payload = fields,
            };
        }
    }
}
