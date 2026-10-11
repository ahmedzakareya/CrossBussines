using System.Globalization;
using System.Text.Json.Nodes;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform.Orchestration
{
    // =============================================================================================
    // PROJECT BILLING APPROVAL -> PLATFORM WORK. The first approval flow to cross this line, and
    // deliberately the smallest one that is true.
    //
    // WHY THIS FLOW AND NOT ANOTHER. Of the approval families in the estate, ProgressBilling is the
    // only one that already had every piece except this edge: a five-state machine with a legal-moves
    // table, an authoritative consequence, ScopedTx on every transition, company isolation, and
    // Business Events emitted INSIDE the transaction and before its commit. Nothing in the producing
    // module changes to enable this rule; it consumes a contract that already existed.
    //
    // TWO OF THE FOUR EVENTS ARE WORK, AND TWO ARE NOT. That asymmetry is the whole rule:
    //
    //   Submitted  ACTION_REQUIRED. The billing has left the preparer's hands and cannot be posted
    //              until somebody approves it. Until they do, nothing else tracks that they have not
    //              - which is the definition of a task rather than a notification.
    //
    //   Approved   TASK_COMPLETION. The approver's job is over: the billing is authorized to post and
    //              locked against editing. It completes the task THIS rule opened and opens nothing.
    //
    //   Posted     NOT the same work. Posting is a separate call, with its own actor, its own
    //              authorization and its own GL and receipt consequences. A person who approves is
    //              PERMITTED to post but is not REQUIRED to, and no evidence in the module says who
    //              must. Projecting it would invent an obligation, so it is answered Ignore.
    //
    //   Returned   A real decision, and still an unanswered product question: whether it ends the
    //              approver's task, starts a preparer's, or leaves the work open until the billing
    //              comes back. The source does not settle it, so this rule does not either.
    //              PROJECT-BILLING-RETURNED-TASK-SEMANTICS stays open, and Ignore is the honest
    //              answer meanwhile - see the note on it below.
    //
    // NOTHING IS INVENTED HERE, and each of these is an absence in the domain rather than an
    // oversight:
    //
    //   THE ASSIGNEE IS NOBODY. Approving a billing is a ROLE right (billing-approve) plus a
    //   RECORD-level rule that the approver is not the preparer, so any role-holder who did not
    //   prepare it may approve. The header carries CreatedBy, SubmittedBy and ApprovedBy, and
    //   ApprovedBy is written BY the approval - none of the three names an approver in advance.
    //   So the intent contributes 0, the platform's unassigned convention. Not a manager walk, not
    //   the first role-holder, not the submitter, not employee 1. A deployment that DOES know who
    //   should see these routes them through TaskAutoRule.DefaultAssigneeEmployeeId, which is the
    //   mechanism that already exists for exactly that and needs no help from this rule.
    //   PROJECT-BILLING-APPROVER-NOT-DETERMINISTIC records the gap.
    //
    //   THERE IS NO DEADLINE. ProgressBilling carries no due date, no SLA and no approval window;
    //   DueDatePolicy.None says so out loud. It is a real answer with a real consequence - this work
    //   can never be overdue and can never escalate - and that is better than a "now + 3" that would
    //   look authoritative and mean nothing. The escalation cadence that exists is technical timing,
    //   not a business promise, and is not evidence of one.
    //
    //   THERE IS NO PRIORITY, and TaskIntent has no field for one precisely so a rule cannot invent
    //   it: the work policy answers that question once, for every rule, and answers "Normal" here.
    //   Mapping NetDue onto urgency would be this rule deciding a finance department's policy.
    //
    // THE SELF-APPROVAL RULE IS NOT HERE, and must never move here. ProgressBillingService refuses
    // an approver who prepared the billing, inside the transaction, on the record. This rule creates
    // VISIBILITY of work; it grants nothing and gates nothing. An unassigned task is not permission.
    // =============================================================================================
    public sealed class ProjectBillingApprovalRule : ITaskOrchestrationRule
    {
        public const string Rule = "ProjectBillingApproval";

        public string RuleType => Rule;

        /// See the header. The billing carries no deadline of any kind, so None is the answer the
        /// source supports - and it is stated rather than defaulted so that adding one later has to
        /// be a decision somebody takes.
        public DueDatePolicy DefaultDueDatePolicy => DueDatePolicy.None;

        // -----------------------------------------------------------------------------------------
        // IDENTITY. The billing is the correlation and the event is the cycle.
        //
        // The BILLING ID is the domain half, and it is the prefix, because the event that CLOSES this
        // work is a different event from the one that opened it and knows only which billing it is
        // about. The EVENT UID is the cycle half - the same convention DocumentVerification
        // established - and it earns its place on the failure it prevents: a billing that is
        // Returned, edited and Submitted again is a SECOND request for approval, and a key built on
        // the billing alone would fold that second cycle into the first one's ledger row and create
        // nothing at all. A silent no-op that looks like success is worse than a duplicate.
        //
        // (Under today's producer that second Submitted event does not currently reach here at all -
        // the dedup key is "projectbilling:{id}:Submitted", so a re-submit returns the original event
        // rather than recording a new one. That is a PRODUCER contract question and this rule does
        // not answer it: PROJECT-BILLING-RESUBMISSION-EVENT-IDENTITY-GAP. The key is built to be
        // correct on the day it is closed rather than to encode the gap.)
        // -----------------------------------------------------------------------------------------
        // THE ROUND IS PART OF THE ADDRESS, not just part of the payload.
        //
        // It used to be billing + event uid, and completion searched the billing prefix for the
        // NEWEST projection. That was safe while only one event could ever close the work. It stops
        // being safe the moment a billing has two open rounds: a cycle-1 decision arriving late -
        // a redelivered dispatch row, a retry, an out-of-order delivery - would find round 2's task
        // and close work nobody had done. The cycle in the prefix is what makes a decision reach its
        // OWN round and no other.
        //
        // The trailing colon is load-bearing: without it "cycle:1:" would also prefix-match
        // "cycle:11:...", and round 11's task would answer to round 1's decision.
        public static string KeyPrefixFor(int billingId, int cycle)
            => $"TaskProjection:{Rule}:{EntityRegistry.ProjectBilling}:{billingId}:cycle:{cycle}:";

        // A SEPARATE PARTITION FOR EVENTS THAT NAME NO ROUND, rather than a fabricated round number.
        //
        // Events recorded before the approval cycle existed carry no cycle, and there is no honest
        // way to infer one - the billing's CURRENT cycle is not the cycle that event belonged to.
        // Putting them under their own prefix means a legacy decision can only ever close legacy
        // work, and a cycle-scoped decision can only ever close cycle-scoped work. Neither can reach
        // the other, which is the only arrangement that is safe in both directions.
        //
        // It is not a dead branch: under the old producer a billing could have at most ONE Submitted
        // fact, so a legacy partition holds at most one task and completing it is unambiguous.
        public static string LegacyKeyPrefixFor(int billingId)
            => $"TaskProjection:{Rule}:{EntityRegistry.ProjectBilling}:{billingId}:legacy:";

        /// The prefix a decision must search: its own round, or the legacy partition when the event
        /// names no round. Pure, and the ONE place the choice is made - open and close both use it.
        public static string PrefixFor(int billingId, int? approvalCycle)
            => approvalCycle is int cycle ? KeyPrefixFor(billingId, cycle) : LegacyKeyPrefixFor(billingId);

        public static string KeyFor(int billingId, int? approvalCycle, Guid eventUid)
            => PrefixFor(billingId, approvalCycle) + eventUid.ToString("N");

        public TaskProjectionDecision Decide(BusinessEventEnvelope envelope)
        {
            ArgumentNullException.ThrowIfNull(envelope);

            // EXACT ENTITY CODE, ordinal. Not a prefix and not a wildcard: "Project.*", "Progress.*"
            // and anything else that merely reads like billing must fall straight through, and an
            // entity that one day grows its own "Approved" must not be answered by this rule.
            if (!string.Equals(envelope.Entity.Code, EntityRegistry.ProjectBilling, StringComparison.Ordinal))
                return TaskProjectionDecision.Ignore;

            int billingId = envelope.Entity.Id;
            if (billingId <= 0) return TaskProjectionDecision.Ignore;   // no identity, no work

            switch (BusinessEventTypes.ActionOf(envelope.EventType))
            {
                // ---- TASK_COMPLETION: THE TWO WAYS AN APPROVER FINISHES ------------------------
                //
                // A submitted billing asks one question - approve it or send it back - and the state
                // machine says so: LegalNext[Submitted] is exactly { Approved, Returned }. Both are
                // decisions, so both END the review the Submitted fact asked for. They differ in the
                // BUSINESS fact they leave behind, not in whether the human work is finished.
                //
                // RETURNED IS NOT AN APPROVAL AND Done DOES NOT SAY IT IS. The task records that the
                // requested review happened; the billing records what was decided. ProgressBilling
                // stays Returned, editable, and out of the approval inbox - which lists only
                // Submitted and Approved, and is itself the repository's existing statement that a
                // returned billing is no longer anybody's approval work.
                //
                // THIS IS NOT A NEW IDEA IN THIS CODEBASE. The platform's own pilot rule already
                // decided it for documents: DocumentVerificationRule completes the reviewer's task on
                // Rejected as well as Verified, because "a rejection is a decision, not a failure to
                // decide, and the task asked for a decision". Returned is to a billing what Rejected
                // is to a document.
                //
                // Each closes ITS OWN ROUND. When nothing was projected for that round - a decision
                // replayed from before this rule existed, or delivered out of order - the
                // orchestration service answers "nothing was projected for this source" and creates
                // NOTHING. A phantom task fabricated only to be marked Done would be a record of work
                // nobody did.
                //
                // NEITHER OPENS ANYTHING. The next round's task comes from the next Submitted fact,
                // and only from a real resubmission - see the note on Returned below.
                case ProgressBillingEvents.Approved:
                case ProgressBillingEvents.Returned:
                    return TaskProjectionDecision.Complete(PrefixFor(billingId, CycleOf(envelope.Payload)));

                // ---- ACTION_REQUIRED ------------------------------------------------------------
                case ProgressBillingEvents.Submitted:
                    return TaskProjectionDecision.Create(new TaskIntent
                    {
                        CompanyId = envelope.Context.CompanyId,
                        RuleType = Rule,
                        RuleKey = KeyFor(billingId, CycleOf(envelope.Payload), envelope.EventUid),

                        TitleAr = "اعتماد مستخلص" + NumberSuffix(envelope.Payload),
                        TitleEn = "Approve project billing" + NumberSuffix(envelope.Payload),
                        Description = NetDueLine(envelope.Payload),

                        // Nobody. See the header - this is the domain's answer, not a placeholder.
                        AssigneeEmployeeId = 0,

                        // No date, from a fact that carries none. DefaultDueDatePolicy.None means the
                        // policy will not substitute an offset either.
                        SourceDueDate = null,

                        EntityType = EntityRegistry.ProjectBilling,
                        EntityId = billingId,
                    });

                // ---- POSTED, AND EVERYTHING ELSE ------------------------------------------------
                //
                // Posting is a SEPARATE piece of work with its own actor, its own authorization and
                // its own GL and receipt consequences. A person who approves is permitted to post but
                // is not required to, and nothing in the module says who must - so projecting it
                // would invent an obligation. It neither opens approval work nor closes it.
                //
                // Listed rather than left to the default, so a later reader can see it was CONSIDERED
                // and answered rather than simply unhandled.
                case ProgressBillingEvents.Posted:
                default:
                    return TaskProjectionDecision.Ignore;
            }
        }

        // ---- payload readers ---------------------------------------------------------------------
        //
        // PAYLOAD ONLY, and tolerant. The rule performs no database access to decorate a title: a
        // richer title is not worth a query per event, and a query here would also make Decide impure,
        // which the contract forbids. An older producer that omits a field must degrade the TEXT,
        // never throw - a throwing rule fails its dispatch row forever over a cosmetic absence.

        private static string NumberSuffix(JsonNode? payload)
        {
            int? no = IntOf(payload, "billingNo");
            return no is > 0 ? " رقم " + no.Value.ToString(CultureInfo.InvariantCulture) : "";
        }

        /// The one figure an approver needs before opening the document: what this billing is worth to
        /// the customer. Invariant formatting because a task row is not a report and this is not a
        /// localized money presentation - the screen owns that.
        private static string? NetDueLine(JsonNode? payload)
        {
            decimal? net = DecimalOf(payload, "netDue");
            return net == null ? null : "Net due: " + net.Value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// The round this fact belongs to, or null when the producer never recorded one. Null is a
        /// real answer with real consequences here - see LegacyKeyPrefixFor - and is never replaced
        /// by the billing's current cycle, which is a different round than the one this event was about.
        private static int? CycleOf(JsonNode? payload)
        {
            int? cycle = IntOf(payload, "approvalCycle");
            // A zero or negative round is malformed, not "round zero": treat it as unrecorded rather
            // than minting a prefix nothing else will ever match.
            return cycle is > 0 ? cycle : null;
        }

        private static int? IntOf(JsonNode? payload, string property)
        {
            var node = payload?[property];
            if (node == null) return null;
            try { return node.GetValue<int>(); }
            catch (FormatException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private static decimal? DecimalOf(JsonNode? payload, string property)
        {
            var node = payload?[property];
            if (node == null) return null;
            try { return node.GetValue<decimal>(); }
            catch (FormatException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
    }
}
