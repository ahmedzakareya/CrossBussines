namespace CrossBuy.Models.Platform
{
    // =============================================================================================
    // HR EMPLOYEE SERVICE REQUEST — the payload for the four canonical employee-request facts
    // (PayloadVersion 1).
    //
    // WHAT THIS IS FOR. One consumer today: the orchestration rule that turns an approval level into a
    // Task. Everything here exists because that rule cannot do its job without it, and nothing here
    // exists because it might be interesting later.
    //
    // THE LEVEL IS THE POINT. An employee request is not one decision, it is a CHAIN of them: level 1
    // is the direct manager, and each approval hands the work upward until the unit head. So the unit
    // of work is not the request, it is the (request, level) pair — which is why every fact here names
    // its level, and why the rule keys its Task on the pair rather than on the request.
    //
    // NO COMPANY, AND THIS ONE WAS PROPOSED AND THEN REFUSED. An earlier draft of this slice carried a
    // CompanyId here; it is deliberately absent, and the governance register records that as SUPERSEDED
    // BEFORE IMPLEMENTATION. The envelope already carries authoritative tenant identity — see
    // BusinessEventService, which takes company from the ambient BusinessContext and never from the
    // record — and a second copy is a second answer waiting to disagree with the first. On a TENANT
    // boundary a divergent second copy is not a redundancy, it is the defect: the rule would have two
    // candidate companies and no rule for which wins. There is exactly ONE tenant authority for this
    // family and it is envelope.Context.CompanyId.
    //
    // WHY THAT IS WORTH SAYING TWICE HERE. Unlike LeaveRequest, EmployeeRequest DOES carry its own
    // CompanyID column, so a payload copy would have looked authoritative rather than derived. That
    // makes the temptation stronger and the disagreement quieter: a request row moved between companies,
    // or an event replayed after one, would produce a payload company that is simply a different answer
    // from the envelope's. The registry resolver reads the row's column to decide EXISTENCE in a
    // company; the projection reads the envelope to decide WHERE THE WORK GOES. Those are two questions
    // and they are answered in two places, each by the thing that owns it.
    //
    // NO LOCALIZED TEXT, NO NOTIFICATION CONTENT, NO UI STRINGS, NO DEEP LINKS, AND NO REQUEST CONTENT.
    // No DecisionNote, no Reason, no LetterType, no Addressee, no PermissionDate, no times, no
    // attachment, no file path, no identifier and no salary figure. This payload is readable by anyone
    // who may read the event, and an HR letter request can be a salary certificate — freezing any of
    // that into the permanent log would disclose to that whole audience exactly what the HR
    // authorization layer exists to keep narrow. The rule builds a NEUTRAL title from nothing but the
    // id, and whoever needs the detail resolves it through HR authorization instead.
    // =============================================================================================
    public sealed class EmployeeRequestEventPayload
    {
        public const int Version = 1;

        public int EmployeeRequestId { get; init; }

        /// The person the request is FOR. Carried because the rule uses it as a refusal test rather than
        /// as decoration: work whose approver is the requester is work that would let somebody approve
        /// their own request, and the rule declines to create it. The chain is built so that cannot
        /// happen; this is the projection layer refusing to be the place where it starts happening.
        public int RequesterEmployeeId { get; init; }

        /// WHICH KIND OF REQUEST — "Letter" or "Permission", the domain's own discriminator.
        ///
        /// IT IS ROUTING METADATA AND NOT DISPLAY TEXT, and the distinction is enforced rather than
        /// hoped for: the rule NEVER puts this in a Task title or description, which is asserted by its
        /// own test at two values including a deliberately sensitive-looking one. It is here because a
        /// consumer that one day routes letters differently from permissions must be able to tell them
        /// apart from the fact itself, and re-reading the row to find out would be a second source of
        /// truth about an event that already happened.
        ///
        /// WHAT IT IS NOT: LetterType. "Letter" says a letter was asked for; "Salary" would say what the
        /// letter is about, which is a fact about pay and stays out of the log entirely.
        public string? RequestType { get; init; }

        /// The level this fact is ABOUT.
        ///   Submitted → the level now awaiting a decision (1 when a chain exists).
        ///   Advanced  → the level that was just decided; Next* carries the one now owed.
        ///   Approved / Rejected → the level that took the terminal decision.
        ///
        /// ZERO MEANS THERE IS NO CHAIN. That is the request the domain auto-approves because nobody was
        /// ever asked — the empty-chain case CreateAsync already has. The rule reads 0 and creates
        /// NOTHING, because a Task raised for an approver who does not exist is work nobody can do, and a
        /// Task raised only to be completed in the same breath is a record of work nobody did.
        public int Level { get; init; }

        /// Who owes the decision at Level. Null when Level is 0.
        public int? ApproverEmployeeId { get; init; }

        /// Set on Advanced ONLY — the level the chain moved to, and who owes it. Null everywhere else,
        /// including on both terminal facts, because a decided request owes nobody anything.
        public int? NextLevel { get; init; }
        public int? NextApproverEmployeeId { get; init; }

        /// Who decided. Null on Submitted, because at that moment nobody has.
        public int? DecidedByEmployeeId { get; init; }
    }
}
