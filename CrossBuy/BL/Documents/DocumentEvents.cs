using System;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Documents
{
    // =============================================================================================
    // THE ONE PLACE A DOCUMENT BECOMES AN EVENT.
    //
    // Two producers now announce document facts: PlatformDocumentService, for the transitions a person
    // causes (submitted, verified, rejected), and DocumentExpiryProjection, for the two the CALENDAR
    // causes (expiring soon, expired). They must agree about three things, and the only way to be sure
    // they agree is for there to be one copy:
    //
    //   * WHAT MAY BE SAID. The payload exclusion list is a security rule, not a formatting choice. If
    //     the projection grew its own payload, the first field somebody added in a hurry would be the
    //     file name, and nothing would have failed.
    //   * HOW LOUDLY. Visibility comes from the document's confidentiality. A projection that defaulted
    //     to Internal would announce a restricted medical document to everyone who may read the
    //     employee - which is the exact leak the confidentiality tier exists to prevent, arriving
    //     through the back door of a worker.
    //   * WHO IT IS ABOUT. The event is addressed to the OWNING ENTITY, never to the document.
    //
    // WHY THIS IS NOT A SECOND EVENT ENGINE. It records nothing itself. It builds a
    // BusinessEventRecord and hands it to the kernel's IBusinessEventService, which owns validation,
    // the ambient-transaction rule and deduplication. This is a payload policy, not a bus.
    // =============================================================================================
    public static class DocumentEvents
    {
        // ---- the action vocabulary ---------------------------------------------------------------
        //
        // "Document" prefixes each because the event is addressed to the employee: "Employee.Submitted"
        // would be a fact about the person, not about their file.
        public const string Submitted = "DocumentSubmitted";
        public const string Verified = "DocumentVerified";
        public const string Rejected = "DocumentRejected";

        /// The two the calendar causes. There is deliberately NO DocumentRenewalSubmitted and NO
        /// DocumentRenewed: a renewal submission IS a DocumentSubmitted and a verified renewal IS a
        /// DocumentVerified - the same moment, already announced. A second name for one transition
        /// would force every consumer to handle both spellings and would double-count anything that
        /// counted them. A consumer that needs to tell a renewal from a first filing reads `versionNo`.
        public const string ExpiringSoon = "DocumentExpiringSoon";
        public const string Expired = "DocumentExpired";

        /// Event visibility from document confidentiality.
        ///
        /// The two vocabularies coincide word for word, and this is still written out rather than cast,
        /// because a silent coincidence between an ACCESS classification and an EVENT classification is
        /// not something to bet a restricted medical document on. Anything unrecognised becomes
        /// Restricted - the narrowest, not the default.
        public static string VisibilityFor(string? confidentiality) => confidentiality switch
        {
            DocumentConfidentiality.Internal => BusinessEventVisibility.Internal,
            DocumentConfidentiality.Confidential => BusinessEventVisibility.Confidential,
            DocumentConfidentiality.Restricted => BusinessEventVisibility.Restricted,
            DocumentConfidentiality.System => BusinessEventVisibility.System,
            _ => BusinessEventVisibility.Restricted,
        };

        /// The dedup key, which is what stops a polling worker announcing the same fact every tick.
        ///
        /// A lifecycle transition is keyed per document, per VERSION, per action: a resubmission is a
        /// new fact because the version changed, while a retried commit of the same write is not.
        ///
        /// An expiry transition is keyed per document, per EXPIRY DATE, per action - never by the tick.
        /// That is the whole idempotency argument: the same document crossing the same warning
        /// threshold is one fact however many times a worker looks at it, and a RENEWED document
        /// carries a different expiry date, so it is correctly allowed to warn again. The precedent is
        /// the task platform's own overdue sweep, whose key comes from the due date rather than the run.
        public static string LifecycleKey(long documentId, long? versionId, string action)
            => $"platformdoc:{documentId}:v{versionId ?? 0}:{action}";

        public static string ExpiryKey(long documentId, DateTime expiry, string action)
            => $"platformdoc:{documentId}:exp{expiry:yyyyMMdd}:{action}";

        /// Builds the record. NOTHING is recorded here - the caller hands this to the kernel inside its
        /// own transaction, because an event that outlived a rolled-back write would be a lie.
        ///
        /// WHAT THE PAYLOAD MAY SAY, and the reasoning behind every exclusion. The kernel's own rule is
        /// that a payload never carries files, secrets or unrestricted employee data, and a document is
        /// made almost entirely of things that fail that test:
        ///
        ///   StorageKey  - EXCLUDED. The one value that must never travel: a subscriber that learned it
        ///                 would hold a handle the access resolver never issued.
        ///   path / bytes / file name - EXCLUDED. A payload is not a delivery channel, and a name like
        ///                 "termination-letter.pdf" leaks the content it names.
        ///   DocumentNumber - EXCLUDED. This is the passport or civil-ID number itself: the most
        ///                 sensitive field on the row, with no business in an event log.
        ///   DecisionNote - EXCLUDED. A rejection reason is free text a human typed about a person. The
        ///                 event says a decision happened; the document says what it was.
        ///
        /// What is left is the SHAPE of the fact: which document, of which type, in which state, with
        /// which dates. Enough to react to, and enough to come back through the authorized read path
        /// for the rest - which is exactly the amount of trust an event deserves.
        public static BusinessEventRecord Record(
            PlatformDocument doc,
            PlatformDocumentType? type,
            string action,
            string dedupKey,
            int? versionNo = null,
            int? daysRemaining = null)
            => new()
            {
                EntityCode = doc.EntityType,
                EntityId = doc.EntityId,
                EventType = BusinessEventTypes.Build(doc.EntityType, action),
                Visibility = VisibilityFor(doc.Confidentiality),
                DedupKey = dedupKey,
                Payload = new
                {
                    documentId = doc.Id,
                    documentTypeId = doc.DocumentTypeId,
                    // The CODE, so a consumer can recognise a passport without hardcoding one - it
                    // matches against the code it was configured with.
                    documentTypeCode = type?.Code,
                    status = doc.Status,
                    issueDate = doc.IssueDate,
                    expiryDate = doc.ExpiryDate,
                    decidedBy = doc.DecidedBy,
                    hasDecisionNote = !string.IsNullOrWhiteSpace(doc.DecisionNote),
                    // How a consumer tells a renewal from a first filing, without a second event type.
                    versionNo,
                    daysRemaining,
                },
            };

        /// Convenience for a producer that has an optional event service.
        public static Task RaiseAsync(
            IBusinessEventService? events,
            PlatformDocument doc,
            PlatformDocumentType? type,
            string action,
            string dedupKey,
            CancellationToken ct,
            int? versionNo = null,
            int? daysRemaining = null)
            => events == null
                ? Task.CompletedTask
                : events.RecordAsync(Record(doc, type, action, dedupKey, versionNo, daysRemaining), ct);
    }
}
