namespace CrossBuy.Models.Context.Accounting
{
    // =============================================================================================
    // PROJECTS-3 — the Project lifecycle event vocabulary and its payload.
    //
    // WHY IT LIVES HERE AND NOT IN Models/Platform: the Projects module already set the precedent one
    // file away. ProgressBillingEvents and ProgressBillingEventPayload sit beside their entity in
    // Models/Context/Accounting/ProgressBilling.cs, not in the platform kernel — a module payload in
    // the kernel would make the kernel name a module fact.
    //
    // ACTIONS, NOT FULL EVENT TYPES, for the same reason ProgressBillingEvents holds "Submitted"
    // rather than "ProjectBilling.Submitted": the producer composes the type from
    // EntityRegistry.Project so the prefix can only ever be the code the event is recorded against.
    // BusinessEventTypes.Validate compares the two with ordinal equality, so composing it from the
    // registry constant makes a mismatch impossible rather than merely unlikely.
    //
    // FIVE, AND FIVE PRODUCERS. Created, Updated and StatusChanged are produced by
    // ProjectService.SaveAsync; Closed and Reopened by ProjectCloseoutService. A declared event with
    // no producer is a promise the platform does not keep, so nothing speculative is declared here —
    // no overdue action, and no milestone or deliverable action, whose entity codes the registry does
    // not carry at all.
    // =============================================================================================
    public static class ProjectEvents
    {
        public const string Created = "Created";
        public const string Updated = "Updated";

        /// The stored Status actually MOVED. Writing the same status again is not a transition.
        public const string StatusChanged = "StatusChanged";

        public const string Closed = "Closed";
        public const string Reopened = "Reopened";

        /// Published so a test can assert the vocabulary rather than restate it.
        public static readonly IReadOnlyList<string> All =
            new[] { Created, Updated, StatusChanged, Closed, Reopened };
    }

    // =============================================================================================
    // The wire payload. Identifiers and state only — no EF entity, no navigation graph, no file path,
    // no free text the user typed.
    //
    // THE CLOSE REASON IS DELIBERATELY ABSENT, and the precedent is explicit: the ProgressBilling
    // payload records that "a consumer that needs the document fetches it through its own
    // authorization rather than reading it out of an event it was handed". The reason is stored on
    // the ProjectCloseout row; a consumer holding ProjectId can read it under its own authority.
    //
    // A FIELD THAT CANNOT BE SUPPLIED AUTHORITATIVELY IS NULL, NEVER DEFAULTED. Two of them are:
    //
    //   * PreviousStatus is null on Created, because a project that has just come into existence had
    //     no previous status. Defaulting it to "" or "Draft" would put a transition in a permanent
    //     audit log that never happened.
    //   * ActorEmployeeId is null from SaveAsync, because IProjectService.SaveAsync takes a Project
    //     and no actor — the service genuinely does not know who is calling. Writing 0 would be a
    //     fabricated actor, and the audit log is where a fabrication is hardest to find later. The
    //     closeout producers DO know (BusinessContext.EmployeeId) and set it.
    //
    // The EVENT ROW still carries the true company and actor regardless: BusinessEventService stamps
    // both from the resolved BusinessContext and honours an override only for system callers. These
    // payload fields are the producer's own statement of the same facts, not a substitute for them.
    // =============================================================================================
    public sealed class ProjectEventPayload
    {
        // A later field is added as an ADDITIVE NULLABLE at version 1 rather than a version bump -
        // exactly what ProgressBillingEventPayload records for ApprovalCycle. Bumping tells every
        // consumer to expect a change none of them has to handle.
        public const int Version = 1;

        public int CompanyId { get; init; }
        public int ProjectId { get; init; }

        /// The project's business code. Stable, human-meaningful, and not a name that may be renamed
        /// in either language.
        public string Code { get; init; } = "";

        /// The status the project holds AFTER this event.
        public string Status { get; init; } = "";

        /// The status it held before, ONLY where a real transition occurred. Null on Created.
        public string? PreviousStatus { get; init; }

        /// The acting employee where the producer authoritatively knows one. Null from SaveAsync.
        public int? ActorEmployeeId { get; init; }
    }

    // =============================================================================================
    // PROJECTS-3 - the MILESTONE lifecycle event vocabulary and its payload.
    //
    // IT LIVES BESIDE THE PROJECT ONES, not in Models/Platform, for the reason ProgressBilling
    // already established: a module payload in the platform kernel would make the kernel name a
    // module fact. Same file, separate contract - a milestone is NOT a project.
    //
    // THREE ACTIONS AND NO MORE. There is deliberately no Updated: a rename, a description edit, a
    // target-date change and a reorder are not facts anyone is waiting on, and an event stream that
    // carries every keystroke stops being a record of what happened.
    //
    // AND NO Overdue. It stays DERIVED - ProjectMilestone computes it as a NotMapped expression from
    // TargetDate and today, exactly as TaskItem does for its own. An overdue EVENT would need a
    // scheduler to stay true and would be wrong every midnight until one ran.
    // =============================================================================================
    public static class ProjectMilestoneEvents
    {
        public const string Created = "Created";

        /// The stored Status actually MOVED. Writing the same status again is not a transition.
        public const string StatusChanged = "StatusChanged";

        /// The row was physically removed. DELETION IS NOT CANCELLATION - a cancelled milestone is
        /// still a commitment that was made and withdrawn, and it keeps its row and its status.
        public const string Deleted = "Deleted";

        /// Published so a test can assert the vocabulary rather than restate it.
        public static readonly IReadOnlyList<string> All = new[] { Created, StatusChanged, Deleted };
    }

    // =============================================================================================
    // Identifiers and state only. No Name, no Description, no TargetDate: a consumer that needs the
    // milestone fetches it through its own authorization rather than reading it out of an event it
    // was handed - the rule the ProgressBilling payload states for itself.
    //
    // THE ACTOR IS AUTHORITATIVE HERE, unlike on ProjectEventPayload, and that is measured rather
    // than assumed: ProjectMilestoneService.GateAsync refuses any context whose EmployeeId is not
    // greater than zero, so every write that reaches an emission point already carries a real
    // employee. It is still typed nullable so the shape matches its sibling and so a future caller
    // that genuinely has no actor cannot be forced to invent one.
    // =============================================================================================
    public sealed class ProjectMilestoneEventPayload
    {
        // A later field is added as an ADDITIVE NULLABLE at version 1 rather than a version bump,
        // exactly as ProgressBillingEventPayload records for ApprovalCycle.
        public const int Version = 1;

        public int CompanyId { get; init; }
        public int ProjectId { get; init; }
        public int MilestoneId { get; init; }

        /// The status the milestone holds AFTER this event. On Deleted, the status it held when it
        /// was removed - which the business rule constrains to Planned.
        public string Status { get; init; } = "";

        /// The status it held before, ONLY where a real transition occurred. Null on Created and
        /// null on Deleted: neither is a move from one state to another.
        public string? PreviousStatus { get; init; }

        public int? ActorEmployeeId { get; init; }
    }
}
