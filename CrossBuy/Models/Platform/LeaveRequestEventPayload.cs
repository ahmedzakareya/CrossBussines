namespace CrossBuy.Models.Platform
{
    // =============================================================================================
    // HR LEAVE REQUEST — the payload for the four canonical leave facts (PayloadVersion 1).
    //
    // WHAT THIS IS FOR. One consumer today: the orchestration rule that turns an approval level into
    // a Task. Everything here exists because that rule cannot do its job without it, and nothing here
    // exists because it might be interesting later.
    //
    // THE LEVEL IS THE POINT. Leave is not one decision, it is a CHAIN of them: level 1 is the direct
    // manager, and each approval hands the work upward to the next level until the unit head. So the
    // unit of work is not the request, it is the (request, level) pair - which is why every fact here
    // names its level, and why the rule keys its Task on the pair rather than on the request.
    //
    // NO LOCALIZED TEXT, NO NOTIFICATION CONTENT, NO UI STRINGS, NO DEEP LINKS. Leave is HR data about
    // a named person, and this payload is readable by anyone who may read the event. A reason, a leave
    // type or an employee name frozen into the permanent log would disclose to that whole audience what
    // the HR authorization layer exists to keep narrow. The rule builds a NEUTRAL title from nothing
    // but the id, and whoever needs the detail resolves it through HR authorization instead.
    //
    // NO COMPANY. The envelope carries it - see BusinessEventService, which takes company from the
    // ambient BusinessContext and never from the record - and a second copy is a second answer waiting
    // to disagree. That matters more here than elsewhere: LeaveRequest carries no CompanyID column at
    // all, so a company written into this payload would be derived, and a derived copy that drifts from
    // the envelope is worse than no copy.
    //
    // NO DATES, NO DAY COUNT, NO BALANCE, NO LEAVE TYPE. The rule needs none of them, and every one of
    // them is a fact about a person's absence.
    // =============================================================================================
    public sealed class LeaveRequestEventPayload
    {
        public const int Version = 1;

        public int LeaveRequestId { get; init; }

        /// The person the leave is FOR. Carried because the rule uses it as a refusal test rather than
        /// as decoration: work whose approver is the requester is work that would let somebody approve
        /// their own leave, and the rule declines to create it. The chain is built so that cannot
        /// happen; this is the projection layer refusing to be the place where it starts happening.
        public int RequesterEmployeeId { get; init; }

        /// The level this fact is ABOUT.
        ///   Submitted → the level now awaiting a decision (1 when a chain exists).
        ///   Advanced  → the level that was just decided; Next* carries the one now owed.
        ///   Approved / Rejected → the level that took the terminal decision.
        ///
        /// ZERO MEANS THERE IS NO CHAIN. That is the top-of-tree requester the domain auto-approves:
        /// a real, existing behaviour where nobody was ever asked to decide. The rule reads 0 and
        /// creates NOTHING, because a Task raised for an approver who does not exist is work nobody
        /// can do, and a Task raised only to be completed in the same breath is a record of work
        /// nobody did.
        public int Level { get; init; }

        /// Who owes the decision at Level. Null when Level is 0.
        public int? ApproverEmployeeId { get; init; }

        /// Set on Advanced ONLY - the level the chain moved to, and who owes it. Null everywhere else,
        /// including on both terminal facts, because a decided request owes nobody anything.
        public int? NextLevel { get; init; }
        public int? NextApproverEmployeeId { get; init; }

        /// Who decided. Null on Submitted, because at that moment nobody has.
        public int? DecidedByEmployeeId { get; init; }
    }
}
