using System.Text.Json.Nodes;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform.Orchestration
{
    // =============================================================================================
    // CRM -> PLATFORM WORK. One rule, one fact, and a deliberately empty answer for everything else.
    //
    // CRM now emits fourteen event types. Thirteen of them are things that HAPPENED — a lead was
    // created or edited, a deal moved, was won or lost, an account came into existence. None of them
    // says who must do what by when, and a rule that opened a task for them would be inventing the
    // assignment, the deadline and the instruction all at once. The audit trail already records what
    // happened; a task would only be a second copy of it that someone has to close by hand.
    //
    // THE ONE EXCEPTION IS THE FOLLOW-UP. A CRM Activity of type "Task", carrying an owner and a due
    // date, is the single place this domain states an obligation: THIS PERSON must do THIS by THIS
    // DATE. That sentence is the definition of a Platform Task, so CrmService announces it as
    // Lead.FollowUpScheduled / Opportunity.FollowUpScheduled and this rule turns it into work.
    //
    // NOTHING IS INVENTED HERE, and that is the point of each choice below:
    //
    //   ASSIGNEE — the activity's own OwnerEmployeeId, carried in the payload. Never the person who
    //   pressed save, never a manager, never "the first employee in the company". The orchestration
    //   service independently refuses an assignee who is not this company's employee and creates the
    //   task unassigned instead, so a cross-tenant owner cannot be routed to even if one arrived.
    //
    //   DEADLINE — the activity's own DueDate, and DueDatePolicy.FromSource so it is used as given.
    //   The producer never records a follow-up without one, so this rule never falls back to a
    //   computed offset. `now + N` is a decision with one home and this is not it.
    //
    //   IDENTITY — the ACTIVITY id, not the lead or the deal. A lead may carry ten follow-ups over
    //   its life; a key built on the lead would make the second one vanish into the first one's
    //   ledger row, which is worse than a duplicate because it looks like success.
    //
    // WHAT THIS RULE DOES NOT DO. It does not migrate CRM Activity into Platform Task: calls,
    // meetings and emails stay in CRM, where interaction history belongs, and only outstanding
    // assigned work crosses the line. It does not touch a task it did not open — completion is
    // resolved through this rule's own key prefix, never by searching for tasks that look related.
    // CRM-ACTIVITY-VS-PLATFORM-TASK-AUTHORITY stays open; this narrows it by one well-defined case.
    // =============================================================================================
    public sealed class CrmFollowUpRule : ITaskOrchestrationRule
    {
        public const string Rule = "CrmFollowUp";

        /// Matched on the ACTION half of the event type, the same way every rule beside this does.
        public const string ScheduledAction = "FollowUpScheduled";
        public const string CompletedAction = "FollowUpCompleted";

        public string RuleType => Rule;

        /// The activity's own due date. See the header: the producer never emits a follow-up without
        /// one, so FromSource is always answered by the source.
        public DueDatePolicy DefaultDueDatePolicy => DueDatePolicy.FromSource;

        /// The work opened for ONE follow-up. Entity identity plus the activity that is the
        /// obligation — never subject text, which a user can edit and which is not unique.
        public static string KeyFor(string entityCode, int entityId, int activityId)
            => $"TaskProjection:{Rule}:{entityCode}:{entityId}:{activityId}";

        public TaskProjectionDecision Decide(BusinessEventEnvelope envelope)
        {
            ArgumentNullException.ThrowIfNull(envelope);

            // Only the two CRM records that carry follow-ups. A rule matching on the action alone
            // would answer for any entity that ever grows a "FollowUpScheduled".
            string entity = envelope.Entity.Code;
            if (!string.Equals(entity, EntityRegistry.Lead, StringComparison.Ordinal)
                && !string.Equals(entity, EntityRegistry.Opportunity, StringComparison.Ordinal))
                return TaskProjectionDecision.Ignore;
            if (envelope.Entity.Id <= 0) return TaskProjectionDecision.Ignore;

            string action = BusinessEventTypes.ActionOf(envelope.EventType);
            if (action is not (ScheduledAction or CompletedAction))
                return TaskProjectionDecision.Ignore;   // every other CRM fact is history, not work

            int activityId = IntOf(envelope.Payload, "activityId") ?? 0;
            if (activityId <= 0) return TaskProjectionDecision.Ignore;   // no identity, no work

            string key = KeyFor(entity, envelope.Entity.Id, activityId);

            if (action == CompletedAction)
            {
                // TASK_COMPLETION, and strictly the task THIS rule opened for THIS activity. The
                // prefix is the whole key rather than a shorter stem, so a completion cannot reach a
                // sibling follow-up on the same lead.
                return TaskProjectionDecision.Complete(key);
            }

            // ---- ACTION_REQUIRED ----------------------------------------------------------------
            DateTime? due = DateOf(envelope.Payload, "dueDate");
            if (due == null) return TaskProjectionDecision.Ignore;   // no authoritative deadline, no invented one

            string subject = StringOf(envelope.Payload, "subject") ?? "";
            string record = StringOf(envelope.Payload, "entityName") ?? "";
            bool isLead = string.Equals(entity, EntityRegistry.Lead, StringComparison.Ordinal);

            return TaskProjectionDecision.Create(new TaskIntent
            {
                CompanyId = envelope.Context.CompanyId,
                RuleType = Rule,
                RuleKey = key,

                TitleAr = "متابعة " + (isLead ? "عميل محتمل" : "فرصة بيعية") + Suffix(record),
                TitleEn = (isLead ? "Follow up lead" : "Follow up opportunity") + Suffix(record),
                Description = string.IsNullOrWhiteSpace(subject) ? null : subject,

                // The activity's owner. 0 when the payload names nobody, which is the platform's
                // unassigned convention and not a licence to guess.
                AssigneeEmployeeId = IntOf(envelope.Payload, "ownerEmployeeId") ?? 0,

                SourceDueDate = due,

                EntityType = entity,
                EntityId = envelope.Entity.Id,
            });
        }

        private static string Suffix(string record)
            => string.IsNullOrWhiteSpace(record) ? "" : ": " + record;

        // ---- payload readers. Tolerant by design: a payload from an older producer that omits an
        //      optional field must make the rule IGNORE, never throw — a throwing rule would fail the
        //      dispatch row forever for an event that simply carries less than it used to.
        private static int? IntOf(JsonNode? payload, string property)
        {
            var node = payload?[property];
            if (node == null) return null;
            try { return node.GetValue<int>(); }
            catch (FormatException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private static string? StringOf(JsonNode? payload, string property)
        {
            var node = payload?[property];
            if (node == null) return null;
            try { return node.GetValue<string>(); }
            catch (InvalidOperationException) { return null; }
        }

        private static DateTime? DateOf(JsonNode? payload, string property)
        {
            var node = payload?[property];
            if (node == null) return null;
            try { return node.GetValue<DateTime>(); }
            catch (FormatException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
    }
}
