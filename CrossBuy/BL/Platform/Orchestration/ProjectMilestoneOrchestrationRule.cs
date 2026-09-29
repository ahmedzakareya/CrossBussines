using System.Text.Json.Nodes;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform.Orchestration
{
    // =============================================================================================
    // P0 — a project milestone is a dated COMMITMENT, and a commitment somebody has to meet is work.
    //
    // ONE TASK PER MILESTONE, ACROSS THE WHOLE LIFECYCLE. Projects emits three milestone facts —
    // Created, StatusChanged and Deleted — and a naive rule would open a task on Created and another
    // on every StatusChanged, so a milestone that moved Planned → InProgress → Completed would leave
    // three work items for one commitment. Instead ONE task is opened at Created and closed by
    // whichever fact settles the commitment, all found through a single rule key on the MILESTONE.
    //
    // THIS IS PROJECTION, NOT SYNCHRONISATION. The rule writes a task into existence and later marks
    // it finished. It does not mirror the milestone's title, description, assignee or progress, and
    // it does not reopen. A person may have edited the task; a projection has no business overwriting
    // a human's routing decision, and P0 deliberately owns neither direction of a two-way edit.
    //
    // WHY THIS RULE EARNS A REAL DEADLINE. ProjectMilestone.TargetDate is REQUIRED by the model
    // ("a commitment with no date is not a milestone, it is a wish"), so this rule can hand the
    // platform a date the BUSINESS named. Nothing here invents an SLA, an offset or a default, and
    // when the payload carries no date — an event persisted before the field existed — the rule
    // supplies none and lets the work policy answer instead.
    //
    // ── WHAT THIS RULE DELIBERATELY DOES NOT DO ─────────────────────────────────────────────────
    //
    // IT NAMES NOBODY. The milestone model carries no assignee, deliberately: a milestone is a
    // commitment, not an assignment. ProjectMembers cannot supply one either — its unique index is
    // on (ProjectId, EmployeeId) and not on the role, so two active Managers on one project is a
    // legal state and any pick would be arbitrary while reading as though somebody had decided.
    // Unassigned is the honest answer and the convention every other system creator already uses.
    //
    // IT DOES NOT SYNC PROGRESS. A milestone has a STATUS, not a percentage. Mapping four statuses
    // onto 0..100 would invent a completion measure the product has never defined — and one already
    // exists elsewhere, in ProjectProgress against the BOQ, which would then disagree with it.
    //
    // IT DOES NOT REOPEN. ProjectMilestoneStatuses allows Completed → InProgress, but TaskProjection
    // has no Reopen action and TaskStatuses has no system transition back out of Done. A reopened
    // milestone therefore leaves its old task closed; wanting the task back is a product decision
    // with its own concern rather than a back door here.
    // =============================================================================================

    public sealed class ProjectMilestoneOrchestrationRule : ITaskOrchestrationRule
    {
        public const string Rule = "ProjectMilestone";

        /// Matched on the ACTION half of the event type, the same way the generic contract expects.
        public const string CreatedAction = "Created";
        public const string StatusChangedAction = "StatusChanged";
        public const string DeletedAction = "Deleted";

        public string RuleType => Rule;

        /// The milestone's own TargetDate. See the header: a date the business named, never an offset
        /// this rule chose. This is what makes milestone work escalation-eligible on the EXISTING
        /// overdue/escalation engine without that engine changing at all.
        public DueDatePolicy DefaultDueDatePolicy => DueDatePolicy.FromSource;

        /// The rule key identifying THIS milestone's work. Entity identity only — never the name,
        /// which a user can edit and which is not unique.
        ///
        /// THE TRAILING "Created" NAMES THE PROJECTION, NOT A STATUS. It records which fact opened
        /// the work; the milestone's own status is deliberately NOT part of the key, because the
        /// generic service locates earlier work by PREFIX and a status-bearing key would stop
        /// matching the moment the milestone moved.
        public static string KeyFor(int milestoneId)
            => $"{KeyPrefixFor(milestoneId)}{CreatedAction}";

        /// The prefix the settling facts search by.
        ///
        /// THE TRAILING COLON IS LOAD-BEARING AND NOT COSMETIC. The generic CompleteAsync matches
        /// with RuleKey.StartsWith(prefix), so a prefix of "…:ProjectMilestone:7" would also match
        /// milestone 77's ledger row and settle the wrong commitment's work. The delimiter is what
        /// makes "7" unable to prefix "77".
        public static string KeyPrefixFor(int milestoneId)
            => $"TaskProjection:{Rule}:{milestoneId}:";

        public TaskProjectionDecision Decide(BusinessEventEnvelope envelope)
        {
            ArgumentNullException.ThrowIfNull(envelope);

            // ONLY the milestone entity. A rule that matched on the action alone would answer for
            // every entity that happens to have a "Created" or "Deleted" action — and several do.
            //
            // This also closes the Task.* loop by construction rather than by trusting the generic
            // guard: a Task event carries Entity.Code "Task", never "ProjectMilestone", so it cannot
            // reach the switch below even if the platform guard were removed.
            if (!string.Equals(envelope.Entity.Code, EntityRegistry.ProjectMilestone, StringComparison.Ordinal))
                return TaskProjectionDecision.Ignore;
            if (envelope.Entity.Id <= 0) return TaskProjectionDecision.Ignore;

            string action = BusinessEventTypes.ActionOf(envelope.EventType);
            int milestoneId = envelope.Entity.Id;

            switch (action)
            {
                // ---- ACTION_REQUIRED: open the work, once ----------------------------------------
                case CreatedAction:
                    return TaskProjectionDecision.Create(new TaskIntent
                    {
                        // THE EVENT'S COMPANY, AND ONLY IT. Never an ambient employee's company,
                        // never a caller-supplied one, never a constant. The generic service then
                        // refuses any intent whose company differs from the envelope's, so this line
                        // and that check have to agree for a row to be written at all.
                        CompanyId = envelope.Context.CompanyId,
                        RuleType = Rule,
                        RuleKey = KeyFor(milestoneId),

                        TitleAr = "أنجز المعلم" + Reference(envelope),
                        TitleEn = "Deliver milestone" + Reference(envelope),
                        Description = null,

                        // See the header: the fact names nobody, and this rule refuses to guess.
                        AssigneeEmployeeId = 0,

                        // THE COMMITTED DATE. Null only for an event persisted before the payload
                        // carried it, in which case the work policy supplies the offset rather than
                        // this rule inventing one.
                        SourceDueDate = DateOf(envelope.Payload, "targetDate"),

                        EntityType = EntityRegistry.ProjectMilestone,
                        EntityId = milestoneId,
                    });

                // ---- TASK_COMPLETION: the commitment is settled ----------------------------------
                //
                // Completed and Cancelled are different BUSINESS outcomes and the same WORK outcome:
                // in both, there is nothing left for anyone to do. The distinction is preserved where
                // it belongs — on the milestone row and in its event stream — rather than faked here,
                // because TaskStatuses has no Cancelled state and the generic contract deliberately
                // did not invent one. Done means "this task is finished", not "achieved".
                case StatusChangedAction:
                {
                    string? status = StringOf(envelope.Payload, "status");
                    return IsSettled(status)
                        ? TaskProjectionDecision.Complete(KeyPrefixFor(milestoneId))
                        // Planned and InProgress are the commitment still being worked. The task
                        // already exists and stays open; creating a second one here is exactly the
                        // spam this rule's single-key design exists to prevent.
                        : TaskProjectionDecision.Ignore;
                }

                // ---- TASK_COMPLETION: the commitment was withdrawn -------------------------------
                //
                // Only a Planned milestone can be deleted at all, so this is a promise retracted
                // before it started. The task must not outlive the commitment it was raised for.
                case DeletedAction:
                    return TaskProjectionDecision.Complete(KeyPrefixFor(milestoneId));

                default:
                    return TaskProjectionDecision.Ignore;
            }
        }

        /// Settled = nothing left for anyone to do.
        ///
        /// ASKED OF THE MODEL'S OWN VOCABULARY, NOT RESTATED. ProjectMilestoneStatuses.IsOpen is the
        /// single definition of "still awaiting delivery" and IsKnown is the single definition of the
        /// vocabulary itself.
        ///
        /// AN UNRECOGNISED STATUS IS TREATED AS OPEN, WHICH IS THE FAIL-SAFE DIRECTION. Settling work
        /// on a status nobody defined would silently retire a live commitment; leaving it open is
        /// visible and recoverable. Note the order matters: IsKnown is asked FIRST, because IsOpen
        /// returns false for an unknown string just as it does for a terminal one, and testing only
        /// IsOpen would make every typo a completion.
        private static bool IsSettled(string? status) =>
            !string.IsNullOrEmpty(status)
            && CrossBuy.Models.Context.Accounting.ProjectMilestoneStatuses.IsKnown(status)
            && !CrossBuy.Models.Context.Accounting.ProjectMilestoneStatuses.IsOpen(status);

        /// " Foundation pour", or empty when the payload names nothing. Never used for identity —
        /// only for something a human can read in a task list.
        ///
        /// The milestone payload deliberately carries no Name (a consumer that needs the milestone
        /// fetches it through its own authorization), so this is empty today and stays correct if a
        /// name is ever added additively.
        private static string Reference(BusinessEventEnvelope envelope)
        {
            var name = StringOf(envelope.Payload, "name");
            return string.IsNullOrWhiteSpace(name) ? "" : " " + name;
        }

        private static string? StringOf(JsonNode? payload, string name)
        {
            var node = payload?[name];
            if (node == null) return null;
            try { return node.GetValue<string>(); }
            catch (Exception) { return null; }
        }

        private static DateTime? DateOf(JsonNode? payload, string name)
        {
            var node = payload?[name];
            if (node == null) return null;
            try { return node.GetValue<DateTime>(); }
            catch (Exception) { return null; }
        }
    }
}
