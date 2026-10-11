using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform.Orchestration
{
    // =============================================================================================
    // HR EMPLOYEE SERVICE REQUEST APPROVAL -> PLATFORM WORK. Four facts, ONE TASK PER APPROVAL LEVEL.
    //
    // THIS IS THE LEAVE SHAPE, DELIBERATELY AND ALMOST EXACTLY. An employee request (an HR letter, or an
    // hourly permission) climbs the same hierarchical chain as a leave request, level by level, to the
    // head of the nearest org unit; EmployeeRequestStep is LeaveApprovalStep with a different foreign
    // key. Where two domains have one lifecycle, a second projection shape would be a difference with
    // nothing behind it — and the day the two disagreed about what a level means, one of them would be
    // wrong in a way nobody had decided. So the level is the unit of work here too: each level gets its
    // own task, opened when the level becomes owed and closed when it is decided. There is no reassign
    // verb in TaskProjectionDecision, deliberately, because a task that silently changes owner erases
    // who was actually asked.
    //
    // FOUR FACTS, THREE SHAPES:
    //
    //   Submitted  ACTION_REQUIRED. Level 1 is owed by a named manager.
    //
    //   Advanced   BOTH AT ONCE: it closes the level that decided AND opens the level that now owes.
    //              TaskProjectionDecision carries one action, so this rule answers Create for the new
    //              level and EmployeeRequestApprovalAdvanceCloseRule below closes the old one from the
    //              same envelope. Two rules rather than one method because the orchestration service
    //              asks EVERY registered rule once per envelope and applies each answer independently —
    //              the existing contract, used exactly as it is, rather than a new platform verb.
    //
    //   Approved   TASK_COMPLETION for the level that decided. Nothing further is owed.
    //   Rejected   TASK_COMPLETION TOO. The business outcomes are opposites but the WORK is the same
    //              work and it is equally finished: the manager was asked to decide and they decided.
    //              A rejection ends the whole request, so no next level is opened. It is emphatically
    //              not a Cancel — the task lifecycle has no Cancelled state and this rule invents none.
    //
    // THE ASSIGNEE IS A NAMED PERSON THE DOMAIN ALREADY CHOSE. The chain was resolved when the request
    // was created and the approver for every level is STORED. This rule consumes that answer and never
    // recomputes it: it does not read the org tree, does not walk a hierarchy, holds no opinion about
    // who manages whom, and reads no session. Re-deriving the manager here would be a second
    // implementation of the approval chain, and the day the two disagreed the one that granted MORE
    // would be a disclosure.
    //
    // THE TITLE SAYS NOTHING ABOUT THE PERSON OR THE REQUEST. Company-breadth task readers can see
    // titles without holding HR entitlement. So no name, no request type, no letter type, no addressee,
    // no dates, no reason — the title says that an approval is owed, and whoever opens it resolves the
    // detail through HR authorization. RequestType is in the payload and is deliberately NOT here:
    // "Letter" plus a salary certificate is a fact about pay.
    //
    // TENANCY COMES FROM THE ENVELOPE AND FROM NOWHERE ELSE. Not from the payload, which carries no
    // company on purpose; not from the requester's employee row; not from a session; and never from the
    // literal 1. The orchestration service refuses an intent whose company differs from the envelope's,
    // so this is checked as well as stated.
    //
    // NOTHING PRODUCES THESE EVENTS YET. EmployeeRequestService is untouched by this slice. This rule
    // is the platform half, ready for the producer that comes later.
    // =============================================================================================
    public sealed class EmployeeRequestApprovalRule : ITaskOrchestrationRule
    {
        public const string Rule = "EmployeeRequestApproval";

        public string RuleType => Rule;

        /// The domain carries no approval deadline: no SLA, no due column, no configured window. None is
        /// the honest answer, stated rather than defaulted so that adding one later has to be a decision
        /// somebody takes. It costs something real — this work can never be overdue and can never
        /// escalate — and that belongs in the open rather than behind an invented `now + 1`. Escalation
        /// would also collide with the approval chain, because both climb the same org tree.
        public DueDatePolicy DefaultDueDatePolicy => DueDatePolicy.None;

        // -----------------------------------------------------------------------------------------
        // IDENTITY. The (request, level) pair is the work, so both are in the key.
        //
        // A key on the request alone would make level 2's task a duplicate of level 1's and the second
        // one would never be created. The trailing colon matters: CompleteAsync matches by PREFIX, so
        // without it a decision on level 1 would also match level 11, and request 5 would match 50.
        //
        // THE `TaskProjection:{Rule}:{EntityCode}:` STEM IS THE HOUSE CONVENTION and all nine rules in
        // this folder use it. It is what keeps one ledger table's keys from colliding across rules, so
        // the stem is not decoration: EmployeeRequestApproval and LeaveApproval could otherwise both
        // produce ":7:level:1:" and each would complete the other's work.
        // -----------------------------------------------------------------------------------------
        public static string KeyFor(int employeeRequestId, int level)
            => $"TaskProjection:{Rule}:{EntityRegistry.EmployeeRequest}:{employeeRequestId}:level:{level}:";

        public TaskProjectionDecision Decide(BusinessEventEnvelope envelope)
        {
            ArgumentNullException.ThrowIfNull(envelope);

            // EXACT ENTITY CODE, ordinal, and the CONSTANT rather than a literal. Not a prefix and not a
            // wildcard: an entity that one day grows its own "Approved" must not be answered by this
            // rule. The constant is load-bearing — a literal here would still compile after the registry
            // renamed the code, and the rule would then quietly answer nothing for ever.
            if (!string.Equals(envelope.Entity.Code, EntityRegistry.EmployeeRequest, StringComparison.Ordinal))
                return TaskProjectionDecision.Ignore;

            int requestId = envelope.Entity.Id;
            if (requestId <= 0) return TaskProjectionDecision.Ignore;   // no identity, no work

            switch (envelope.EventType)
            {
                // ---- ACTION_REQUIRED: level 1 becomes owed -------------------------------------
                case EmployeeRequestEvents.Submitted:
                {
                    int level = LevelOf(envelope.Payload);
                    // LEVEL 0 IS THE EMPTY-CHAIN AUTO-APPROVAL. Nobody was ever asked, so there is no
                    // work: a task raised for an approver who does not exist is work nobody can do, and
                    // one raised only to be completed in the same breath records work nobody did.
                    if (level <= 0) return TaskProjectionDecision.Ignore;

                    return Open(envelope, requestId, level, ApproverOf(envelope.Payload));
                }

                // ---- BOTH: close the level that decided, open the level that now owes ----------
                //
                // The decision returned here CREATES the next level, from the NEXT fields the event
                // already carries. It does not resolve a manager, re-walk the hierarchy or read a
                // session: the payload is the authoritative projection of a decision that has already
                // been committed.
                case EmployeeRequestEvents.Advanced:
                {
                    int next = NextLevelOf(envelope.Payload);
                    if (next <= 0) return TaskProjectionDecision.Ignore;

                    return Open(envelope, requestId, next, NextApproverOf(envelope.Payload));
                }

                // ---- TASK_COMPLETION: the climb is over, either way ----------------------------
                //
                // When nothing was projected — a decision replayed from before this rule existed, or an
                // event delivered out of order — the orchestration service answers "nothing was
                // projected for this source" and creates NOTHING.
                case EmployeeRequestEvents.Approved:
                case EmployeeRequestEvents.Rejected:
                {
                    int level = LevelOf(envelope.Payload);
                    if (level <= 0) return TaskProjectionDecision.Ignore;   // auto-approved: no work to close

                    return TaskProjectionDecision.Complete(KeyFor(requestId, level));
                }

                // AN UNKNOWN EmployeeRequest EVENT IS INERT, NOT AN ERROR. Throwing because the entity
                // matched would fail that dispatch row for ever and retry it for ever after; and a rule
                // is not a state machine over the event vocabulary. Ignore is a successful outcome.
                default:
                    return TaskProjectionDecision.Ignore;
            }
        }

        /// The work one approval level owes.
        ///
        /// REFUSES TO NAME THE REQUESTER. The chain is built so a requester is never their own approver,
        /// and this is the projection layer declining to be the place where that stops being true: work
        /// that would let somebody approve their own request is not created at all. It is defence in
        /// depth and NOT the primary control — the primary control is the authoritative service, which
        /// refuses a decision by the requester. Task assignment is projection, not domain authorization,
        /// and this refusal decides no product policy about self-approval.
        private static TaskProjectionDecision Open(
            BusinessEventEnvelope envelope, int requestId, int level, int? approver)
        {
            if (approver is not > 0) return TaskProjectionDecision.Ignore;   // no named person, no work
            if (approver == RequesterOf(envelope.Payload)) return TaskProjectionDecision.Ignore;

            return TaskProjectionDecision.Create(new TaskIntent
            {
                // THE ONE TENANT AUTHORITY. The payload has no company by design, so there is no second
                // candidate to choose between and no way for this line to drift.
                CompanyId = envelope.Context.CompanyId,
                RuleType = Rule,
                RuleKey = KeyFor(requestId, level),

                // NEUTRAL BY DESIGN — see the header. The id is the only identifying thing here, and it
                // resolves to "#id" through a registry arm that is itself HR-scoped.
                TitleAr = "طلب موظف بانتظار اعتمادك",
                TitleEn = "Employee request approval",
                Description = null,

                // The domain already decided who. This rule does not re-derive it.
                AssigneeEmployeeId = approver.Value,

                // No date, from a fact that carries none.
                SourceDueDate = null,

                EntityType = EntityRegistry.EmployeeRequest,
                EntityId = requestId,
            });
        }

        internal static int LevelOf(System.Text.Json.Nodes.JsonNode? payload) => IntOf(payload, "level") ?? 0;
        internal static int NextLevelOf(System.Text.Json.Nodes.JsonNode? payload) => IntOf(payload, "nextLevel") ?? 0;
        private static int? ApproverOf(System.Text.Json.Nodes.JsonNode? payload) => IntOf(payload, "approverEmployeeId");
        private static int? NextApproverOf(System.Text.Json.Nodes.JsonNode? payload) => IntOf(payload, "nextApproverEmployeeId");
        private static int? RequesterOf(System.Text.Json.Nodes.JsonNode? payload) => IntOf(payload, "requesterEmployeeId");

        /// Tolerant by design: an older producer that omits a field must degrade the DECISION to Ignore,
        /// never throw — a throwing rule fails its dispatch row forever and retries it for ever after.
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
    // Registered alongside EmployeeRequestApprovalRule and answering only `Advanced`: it completes the
    // level that just decided, while the rule above opens the level that now owes. Splitting it this way
    // keeps the platform contract untouched — no reassign, no second action on one decision — and keeps
    // each answer independently replayable, because completing an already-completed level is a no-op and
    // creating an already-created level is suppressed by the ledger.
    //
    // It deliberately shares the OTHER rule's KeyFor rather than owning a key of its own. The key names
    // the WORK, not the rule that last touched it; a private copy here would be a second spelling of one
    // identity, and the day they drifted an advance would close nothing and say it had.
    // =============================================================================================
    public sealed class EmployeeRequestApprovalAdvanceCloseRule : ITaskOrchestrationRule
    {
        public const string Rule = "EmployeeRequestApprovalAdvanceClose";

        public string RuleType => Rule;

        public DueDatePolicy DefaultDueDatePolicy => DueDatePolicy.None;

        public TaskProjectionDecision Decide(BusinessEventEnvelope envelope)
        {
            ArgumentNullException.ThrowIfNull(envelope);

            if (!string.Equals(envelope.Entity.Code, EntityRegistry.EmployeeRequest, StringComparison.Ordinal))
                return TaskProjectionDecision.Ignore;
            if (!string.Equals(envelope.EventType, EmployeeRequestEvents.Advanced, StringComparison.Ordinal))
                return TaskProjectionDecision.Ignore;

            int requestId = envelope.Entity.Id;
            if (requestId <= 0) return TaskProjectionDecision.Ignore;

            int decided = EmployeeRequestApprovalRule.LevelOf(envelope.Payload);
            if (decided <= 0) return TaskProjectionDecision.Ignore;

            return TaskProjectionDecision.Complete(EmployeeRequestApprovalRule.KeyFor(requestId, decided));
        }
    }
}
