using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform.Orchestration
{
    // =============================================================================================
    // HR LEAVE APPROVAL -> PLATFORM WORK. One rule, four facts, ONE TASK PER APPROVAL LEVEL.
    //
    // WHY THE LEVEL IS THE UNIT OF WORK AND THE REQUEST IS NOT. This is the platform's first
    // multi-level approval. An inventory approval is one decision by one population; a leave request
    // climbs from the direct manager to the head of the nearest org unit, and at every rung a
    // DIFFERENT named person owes a DIFFERENT decision. Modelling the request as one long-lived task
    // would mean one task changing hands - and there is no reassign verb in TaskProjectionDecision,
    // deliberately, because a task that silently changes owner erases who was actually asked. So each
    // level gets its own task, opened when the level becomes owed and closed when it is decided. The
    // cost is honest and stated: a three-level request leaves three task rows, which is three records
    // of three real decisions rather than one record that quietly rewrote itself twice.
    //
    // FOUR FACTS, THREE SHAPES:
    //
    //   Submitted  ACTION_REQUIRED. Level 1 is owed by a named manager.
    //
    //   Advanced   BOTH AT ONCE, and this is the shape the other rules do not have: it closes the
    //              level that decided AND opens the level that now owes. TaskProjectionDecision
    //              carries one action, so the rule answers Create for the new level and the OLD
    //              level's task is closed by the same envelope on a second pass - see DecideOne.
    //
    //   Approved   TASK_COMPLETION for the level that decided. Nothing further is owed.
    //   Rejected   TASK_COMPLETION TOO. The business outcomes are opposites but the WORK is the same
    //              work and it is equally finished: the manager was asked to decide and they decided.
    //              A rejection ends the whole request, so no next level is opened.
    //
    // THE ASSIGNEE IS A NAMED PERSON, AND THAT IS THE DOMAIN'S ANSWER RATHER THAN A GUESS. Inventory
    // approval is a ROLE right, so its task is unassigned by design. Leave is the opposite: the chain
    // was resolved when the request was created and the approver for every level is STORED. This rule
    // consumes that answer and never recomputes it - it does not read the org tree, does not walk a
    // hierarchy, and holds no opinion about who manages whom. Re-deriving the manager here would be a
    // second implementation of the approval chain, and the day the two disagreed the one that granted
    // MORE would be a disclosure.
    //
    // THE TITLE SAYS NOTHING ABOUT THE PERSON OR THE LEAVE. Company-breadth task readers can see
    // titles without holding HR entitlement, and a leave request is medical-adjacent data about a
    // named employee. So no name, no leave type, no dates, no reason - the title says that an approval
    // is owed, and whoever opens it resolves the detail through HR authorization.
    // =============================================================================================
    public sealed class LeaveApprovalRule : ITaskOrchestrationRule
    {
        public const string Rule = "LeaveApproval";

        public string RuleType => Rule;

        /// The domain carries no approval deadline: no SLA, no due column, no configured window, and
        /// nothing in the repository confirms one. None is the honest answer, stated rather than
        /// defaulted so that adding one later has to be a decision somebody takes. It costs something
        /// real - this work can never be overdue and can never escalate - and that belongs in the open
        /// rather than behind an invented `now + 1`. Escalation would also collide with the approval
        /// chain here, because both climb the same org tree.
        public DueDatePolicy DefaultDueDatePolicy => DueDatePolicy.None;

        // -----------------------------------------------------------------------------------------
        // IDENTITY. The (request, level) pair is the work, so both are in the key.
        //
        // A key on the request alone would make level 2's task a duplicate of level 1's and the second
        // one would never be created. The trailing colon matters: CompleteAsync matches by PREFIX, so
        // without it a decision on level 1 would also match level 11, and request 5 would match 50.
        // -----------------------------------------------------------------------------------------
        public static string KeyFor(int leaveRequestId, int level)
            => $"TaskProjection:{Rule}:{EntityRegistry.LeaveRequest}:{leaveRequestId}:level:{level}:";

        public TaskProjectionDecision Decide(BusinessEventEnvelope envelope)
        {
            ArgumentNullException.ThrowIfNull(envelope);

            // EXACT ENTITY CODE, ordinal. Not a prefix and not a wildcard: an entity that one day grows
            // its own "Approved" must not be answered by this rule.
            if (!string.Equals(envelope.Entity.Code, EntityRegistry.LeaveRequest, StringComparison.Ordinal))
                return TaskProjectionDecision.Ignore;

            int requestId = envelope.Entity.Id;
            if (requestId <= 0) return TaskProjectionDecision.Ignore;   // no identity, no work

            switch (envelope.EventType)
            {
                // ---- ACTION_REQUIRED: level 1 becomes owed -------------------------------------
                case LeaveRequestEvents.Submitted:
                {
                    int level = LevelOf(envelope.Payload);
                    // LEVEL 0 IS THE TOP-OF-TREE AUTO-APPROVAL. Nobody was ever asked, so there is no
                    // work: a task raised for an approver who does not exist is work nobody can do, and
                    // one raised only to be completed in the same breath records work nobody did.
                    if (level <= 0) return TaskProjectionDecision.Ignore;

                    return Open(envelope, requestId, level, ApproverOf(envelope.Payload));
                }

                // ---- BOTH: close the level that decided, open the level that now owes ----------
                //
                // The decision returned here CREATES the next level. The completion of the level that
                // just decided is the orchestration service's second pass over the same envelope,
                // driven by the SAME rule instance - see the note on DecideOne below.
                case LeaveRequestEvents.Advanced:
                {
                    int next = NextLevelOf(envelope.Payload);
                    if (next <= 0) return TaskProjectionDecision.Ignore;

                    return Open(envelope, requestId, next, NextApproverOf(envelope.Payload));
                }

                // ---- TASK_COMPLETION: the climb is over, either way ----------------------------
                //
                // When nothing was projected - a decision replayed from before this rule existed, or an
                // event delivered out of order - the orchestration service answers "nothing was
                // projected for this source" and creates NOTHING.
                case LeaveRequestEvents.Approved:
                case LeaveRequestEvents.Rejected:
                {
                    int level = LevelOf(envelope.Payload);
                    if (level <= 0) return TaskProjectionDecision.Ignore;   // auto-approved: no work to close

                    return TaskProjectionDecision.Complete(KeyFor(requestId, level));
                }

                default:
                    return TaskProjectionDecision.Ignore;
            }
        }

        /// The work one approval level owes.
        ///
        /// REFUSES TO NAME THE REQUESTER. The chain is built so a requester is never their own approver,
        /// and this is the projection layer declining to be the place where that stops being true: work
        /// that would let somebody approve their own leave is not created at all. It is defence in
        /// depth, not the primary control - the primary control is HrAccessService, which refuses
        /// `leave-approve` when subject == caller.
        private static TaskProjectionDecision Open(
            BusinessEventEnvelope envelope, int requestId, int level, int? approver)
        {
            if (approver is not > 0) return TaskProjectionDecision.Ignore;   // no named person, no work
            if (approver == RequesterOf(envelope.Payload)) return TaskProjectionDecision.Ignore;

            return TaskProjectionDecision.Create(new TaskIntent
            {
                CompanyId = envelope.Context.CompanyId,
                RuleType = Rule,
                RuleKey = KeyFor(requestId, level),

                // NEUTRAL BY DESIGN - see the header. The id is the only identifying thing here, and it
                // resolves to "#id" through a registry arm that is itself HR-scoped.
                TitleAr = "طلب إجازة بانتظار اعتمادك",
                TitleEn = "Leave request awaiting your approval",
                Description = null,

                // The domain already decided who. This rule does not re-derive it.
                AssigneeEmployeeId = approver.Value,

                // No date, from a fact that carries none.
                SourceDueDate = null,

                EntityType = EntityRegistry.LeaveRequest,
                EntityId = requestId,
            });
        }

        // -----------------------------------------------------------------------------------------
        // THE SECOND HALF OF `Advanced`.
        //
        // TaskProjectionDecision carries ONE action, and an advance genuinely does two things. Rather
        // than invent a verb - which would mean changing a platform contract this rule is not permitted
        // to touch, and which every other rule would then have to understand - the closing half is
        // expressed as a SECOND rule registration over the same event. `Decide` above opens the new
        // level; this one closes the old.
        //
        // Two rules rather than one method because the orchestration service asks each registered rule
        // once per envelope and applies each answer independently. That is the existing contract, used
        // exactly as it is.
        // -----------------------------------------------------------------------------------------
        internal static int LevelOf(System.Text.Json.Nodes.JsonNode? payload) => IntOf(payload, "level") ?? 0;
        internal static int NextLevelOf(System.Text.Json.Nodes.JsonNode? payload) => IntOf(payload, "nextLevel") ?? 0;
        private static int? ApproverOf(System.Text.Json.Nodes.JsonNode? payload) => IntOf(payload, "approverEmployeeId");
        private static int? NextApproverOf(System.Text.Json.Nodes.JsonNode? payload) => IntOf(payload, "nextApproverEmployeeId");
        private static int? RequesterOf(System.Text.Json.Nodes.JsonNode? payload) => IntOf(payload, "requesterEmployeeId");

        /// Tolerant by design: an older producer that omits a field must degrade the DECISION to Ignore,
        /// never throw - a throwing rule fails its dispatch row forever and retries it for ever after.
        private static int? IntOf(System.Text.Json.Nodes.JsonNode? payload, string name)
        {
            var node = payload?[name];
            if (node == null) return null;
            try { return node.GetValue<int>(); }
            catch (InvalidOperationException) { return null; }
            catch (FormatException) { return null; }
            catch (OverflowException) { return null; }
        }
    }

    // =============================================================================================
    // THE CLOSING HALF OF AN ADVANCE.
    //
    // Registered alongside LeaveApprovalRule and answering only `Advanced`: it completes the level that
    // just decided, while the rule above opens the level that now owes. Splitting it this way keeps the
    // platform contract untouched - no reassign, no second action on one decision - and keeps each
    // answer independently replayable, because completing an already-completed level is a no-op and
    // creating an already-created level is suppressed by the ledger.
    // =============================================================================================
    public sealed class LeaveApprovalAdvanceCloseRule : ITaskOrchestrationRule
    {
        public const string Rule = "LeaveApprovalAdvanceClose";

        public string RuleType => Rule;

        public DueDatePolicy DefaultDueDatePolicy => DueDatePolicy.None;

        public TaskProjectionDecision Decide(BusinessEventEnvelope envelope)
        {
            ArgumentNullException.ThrowIfNull(envelope);

            if (!string.Equals(envelope.Entity.Code, EntityRegistry.LeaveRequest, StringComparison.Ordinal))
                return TaskProjectionDecision.Ignore;
            if (!string.Equals(envelope.EventType, LeaveRequestEvents.Advanced, StringComparison.Ordinal))
                return TaskProjectionDecision.Ignore;

            int requestId = envelope.Entity.Id;
            if (requestId <= 0) return TaskProjectionDecision.Ignore;

            int decided = LeaveApprovalRule.LevelOf(envelope.Payload);
            if (decided <= 0) return TaskProjectionDecision.Ignore;

            return TaskProjectionDecision.Complete(LeaveApprovalRule.KeyFor(requestId, decided));
        }
    }
}
