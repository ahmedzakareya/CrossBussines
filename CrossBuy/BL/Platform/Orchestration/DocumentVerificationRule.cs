using System;
using System.Text.Json.Nodes;
using CrossBuy.BL.Documents;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform.Orchestration
{
    // =============================================================================================
    // A SUBMITTED DOCUMENT IS WORK SOMEBODY OWES AN ANSWER TO.
    //
    // WHY THIS RULE IS LEGITIMATE HERE, stated first because "the donor had it" is not a reason.
    //
    //   SUBMIT IS NOT UPLOAD, and PlatformDocumentService says so in those words. UploadAsync is an HR
    //   act authorized by employee-manage: whoever performs it has authority over the record, so what
    //   they attach IS the record - it lands Status="Active" and emits NOTHING. SubmitAsync is a
    //   person handing in their own evidence, authorized by employee-request, self-only; it always
    //   lands Status="Submitted" and emits DocumentSubmitted. So this event already carries
    //   VERIFICATION INTENT. It is not a notification that a file appeared.
    //
    //   AND THE INTENT HAS NOWHERE ELSE TO GO. The only exits from "Submitted" are VerifyAsync and
    //   RejectAsync, both of which require employee-manage - an authority the submitter provably does
    //   not hold, which is what makes "verified" mean anything. There is no auto-verify path. So every
    //   submitted document is, by construction, waiting on a human decision, and that waiting is
    //   currently invisible: nothing on this line opens work for it.
    //
    // WHAT THIS RULE DOES NOT DECIDE, and deliberately leaves to the parts that already own it:
    //
    //   WHO VERIFIES. The verifier population is "whoever holds HR employee-manage for that employee",
    //   which is a POPULATION and not a person - no reviewer, approver or assignee column exists on a
    //   document or its type. So the rule names NOBODY and the task is created UNASSIGNED, for the
    //   deployment to route through TaskAutoRule.DefaultAssigneeEmployeeId. That is not a shrug: it is
    //   exactly what DocumentExpiryProjection - a LIVE producer, in this same document family, on this
    //   same line - already does (`rule?.DefaultAssigneeEmployeeId ?? 0`), and what the platform's own
    //   TaskIntent calls "the convention both existing system creators already use".
    //
    //   IT MUST NEVER REACH FOR THE SUBMITTER'S MANAGER. That is escalation's hierarchy. Using it here
    //   would make every unrouted document somebody's job by accident, and the task platform keeps
    //   those two ideas apart on purpose: escalation resolves managers, assignment does not.
    //
    //   WHEN IT IS DUE. DueDatePolicy.None, and that is an answer rather than an omission. A submitted
    //   document carries no deadline of its own - the expiry date in the payload is the DOCUMENT'S,
    //   and verifying a passport that expires in two years is not a job with two years to do it.
    //   Inventing an SLA offset here would publish a target time the business has never agreed to.
    //   None means the task can never be overdue and never escalates, which a deployment can change by
    //   configuring the rule rather than by editing this file.
    //
    // CONFIDENTIALITY LIVES IN THE TITLE RULE, and this is the one place this rule differs from the
    // programme line's version, which names the document type unconditionally. DocumentExpiryProjection
    // established the convention for document-derived task text on this line: name the type ONLY when
    // the document is Internal, otherwise say something generic (see its `Nameable`). A task is read by
    // whoever can see the task, which is not the same population as whoever may see the document - so a
    // Restricted medical or disciplinary filing must not announce its own type on a work board. The
    // rule reads the envelope's Visibility, which DocumentEvents derives from the document's
    // confidentiality, so the decision stays PURE: no lookup, no I/O, no second copy of the policy.
    //
    // NOTHING ELSE OF THE DOCUMENT TRAVELS. No number, no issue or expiry date, no decision note, no
    // file name. Description is null. The task points at the owning entity by identity, which is the
    // subject the link resolver and the permission model both already understand.
    // =============================================================================================
    public sealed class DocumentVerificationRule : ITaskOrchestrationRule
    {
        /// Its own rule type, so a deployment can route or switch off verification work without
        /// touching document EXPIRY work, and vice versa. Matches TaskAutoRule.RuleType.
        public const string Rule = "DocumentVerification";

        public string RuleType => Rule;

        /// See the header: a submitted document carries no deadline of its own, and this rule does not
        /// invent one.
        public DueDatePolicy DefaultDueDatePolicy => DueDatePolicy.None;

        public TaskProjectionDecision Decide(BusinessEventEnvelope envelope)
        {
            if (envelope is null) return TaskProjectionDecision.Ignore;

            string action = BusinessEventTypes.ActionOf(envelope.EventType);

            bool opens = string.Equals(action, DocumentEvents.Submitted, StringComparison.Ordinal);

            // A REJECTION CLOSES THE REVIEW JUST AS A VERIFICATION DOES. Both are decisions, and the
            // decision is the work this task asked for. A rejected document may be submitted again -
            // and when it is, SubmitCoreAsync writes a NEW VERSION and raises a new DocumentSubmitted
            // whose dedup key carries that version, so the next cycle opens its own task rather than
            // reopening a review somebody already finished.
            bool closes = string.Equals(action, DocumentEvents.Verified, StringComparison.Ordinal)
                       || string.Equals(action, DocumentEvents.Rejected, StringComparison.Ordinal);

            if (!opens && !closes) return TaskProjectionDecision.Ignore;

            // THE COMPANY COMES FROM THE EVENT'S OWN CONTEXT and is refused rather than defaulted. The
            // engine checks it again before writing; stated here too because a guard that relies on
            // somebody else having checked is not a guard.
            if (envelope.Context is null || envelope.Context.CompanyId <= 0)
                return TaskProjectionDecision.Ignore;

            if (envelope.Entity is null || string.IsNullOrWhiteSpace(envelope.Entity.Code)
                || envelope.Entity.Id <= 0)
                return TaskProjectionDecision.Ignore;

            long? documentId = DocumentIdOf(envelope.Payload);
            if (documentId is not long docId || docId <= 0) return TaskProjectionDecision.Ignore;

            // The prefix identifies THIS document's verification work, whichever event opened it, and
            // carries the company so one tenant's key can never match another's.
            string prefix =
                $"TaskProjection:{Rule}:{envelope.Context.CompanyId}:{envelope.Entity.Code}:{envelope.Entity.Id}:doc{docId}:";

            if (closes) return TaskProjectionDecision.Complete(prefix);

            var (titleAr, titleEn) = Title(envelope);

            return TaskProjectionDecision.Create(new TaskIntent
            {
                CompanyId = envelope.Context.CompanyId,
                RuleType = Rule,

                // EVENT IDENTITY, not entity identity. A redelivered dispatch row carries the SAME
                // EventUid and is therefore the same key - harmless. A genuine resubmission is a
                // different event with a different uid, so it opens its own review. Entity-only
                // uniqueness would silently swallow the second filing, which is the one that matters.
                RuleKey = prefix + "evt" + envelope.EventUid.ToString("N"),

                TitleAr = titleAr,
                TitleEn = titleEn,

                // Nothing about the document travels in free text. See the header.
                Description = null,

                // UNASSIGNED. The deployment routes it through TaskAutoRule; the rule names nobody.
                AssigneeEmployeeId = 0,

                // The rule names no date; DueDatePolicy.None above is the answer.
                SourceDueDate = null,

                EntityType = envelope.Entity.Code,
                EntityId = envelope.Entity.Id,
            });
        }

        /// The type is named ONLY for an Internal document, matching DocumentExpiryProjection's
        /// `Nameable`. Visibility is derived from the document's confidentiality by DocumentEvents, so
        /// reading it off the envelope keeps this decision pure AND keeps one copy of the policy.
        private static (string Ar, string En) Title(BusinessEventEnvelope envelope)
        {
            bool nameable = string.Equals(envelope.Visibility, BusinessEventVisibility.Internal,
                StringComparison.Ordinal);

            string? typeCode = nameable ? TypeCodeOf(envelope.Payload) : null;

            return string.IsNullOrWhiteSpace(typeCode)
                ? ("مستند مُقدَّم يحتاج إلى مراجعة", "A submitted document needs verification")
                : ($"مراجعة مستند مُقدَّم: {typeCode}", $"Verify a submitted document: {typeCode}");
        }

        private static long? DocumentIdOf(JsonNode? payload)
        {
            var node = payload?["documentId"];
            if (node == null) return null;
            // A malformed payload projects NOTHING, and says so by returning null rather than guessing
            // an id - a task pointing at document 0 would be unanswerable work.
            try { return node.GetValue<long>(); }
            catch (Exception) { return null; }
        }

        private static string? TypeCodeOf(JsonNode? payload)
        {
            var node = payload?["documentTypeCode"];
            if (node == null) return null;
            try { return node.GetValue<string?>(); }
            catch (Exception) { return null; }
        }
    }
}
