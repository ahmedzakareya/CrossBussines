using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform.Orchestration
{
    // =============================================================================================
    // BUSINESS EVENT -> TASK ORCHESTRATION — the contract.
    //
    // ONE AUTHORITY, NOT A CALL FROM EVERY MODULE. A module that wanted follow-up work could call
    // ITaskService directly, and the two production paths that already create system work do exactly
    // that. Phase 3 does not add a third: a module commits its own transaction and records its own
    // Business Event, and the outbox hands that COMMITTED event to one consumer, which asks one
    // service, which asks the rules. The producing module keeps knowing nothing about tasks.
    //
    // That is not architecture for its own sake. It is what buys retryability (the dispatch row is
    // the retry), idempotency (a redelivered row must not duplicate work), auditability (the event
    // is the reason the task exists), and company isolation (the event carries its own company, so
    // no background consumer has to infer one).
    //
    // STRONGLY TYPED, NOT A RULE DSL. A generic condition language would be a second product to
    // maintain and a second thing to get wrong, and nothing in this repository asks for one yet. A
    // rule is a class that reads an envelope and returns a decision; when a deployment needs to turn
    // one off or route it, it uses TaskAutoRule, which already exists for exactly that.
    // =============================================================================================

    /// What a rule decided to do about one event. Deliberately four outcomes and no fifth: there is
    /// no Cancel, because the task lifecycle has no Cancelled state and Phase 3 does not invent one.
    public enum TaskProjectionAction
    {
        /// The overwhelmingly common answer, and a SUCCESSFUL one. Most events are not work.
        Ignore = 0,
        Create,
        /// Move an already-projected task to Done, through the canonical transition — never by
        /// assigning Status.
        Complete,
        /// WAVE 2. Update an already-projected task's PROGRESS and nothing else.
        ///
        /// Added rather than worked around, and deliberately narrow. The closed set above was closed
        /// against CANCEL, because the lifecycle has no Cancelled state — that reasoning is about
        /// STATE, and this action touches no state: a task stays New or InProgress and only its
        /// ProgressPct moves. Without it a partial-production event could only be ignored or made to
        /// open a second task, and the second is exactly the spam this wave exists to prevent.
        ///
        /// It carries no title, assignee, due date or priority on purpose. A person may have edited
        /// those, and a progress fact has no business overwriting a human's routing decision.
        SyncProgress,
    }

    /// The canonical shape of work a rule wants to exist. It carries only fields TaskItem already
    /// has; Phase 3 adds no column to satisfy a wish list.
    public sealed class TaskIntent
    {
        public required int CompanyId { get; init; }

        /// The rule that asked for it, and the key that makes asking twice harmless.
        public required string RuleType { get; init; }
        public required string RuleKey { get; init; }

        public required string TitleAr { get; init; }
        public string? TitleEn { get; init; }
        public string? Description { get; init; }

        /// 0 means UNASSIGNED, for a manager to route. That is the convention both existing system
        /// creators already use, not an absence of an answer — and it is emphatically not a licence
        /// to guess a manager. Escalation resolves managers; assignment does not.
        public int AssigneeEmployeeId { get; init; }

        /// A deadline the BUSINESS FACT carries — an expiry, a milestone, a promised date. Null
        /// when the source names none, in which case the work policy supplies the offset. A rule
        /// never computes `now + N` of its own: that is a configurable decision with one home.
        public DateTime? SourceDueDate { get; init; }

        /// SOURCE LINKAGE, through the fields TaskItem already carries. Entity identity, never title
        /// matching, so "why does this task exist?" is answerable by the link resolver and the
        /// permission model that already understand these two fields.
        public required string EntityType { get; init; }
        public required int EntityId { get; init; }
    }

    public sealed class TaskProjectionDecision
    {
        public required TaskProjectionAction Action { get; init; }

        /// Set when Action is Create.
        public TaskIntent? Intent { get; init; }

        /// Set when Action is Complete: the prefix identifying which earlier projection to close.
        /// A prefix rather than an exact key because the closing event is a DIFFERENT event from the
        /// one that opened the work, and cannot know its identity.
        public string? CompleteRuleKeyPrefix { get; init; }

        /// Set when Action is SyncProgress: 0..100, derived from authoritative quantities the event
        /// payload actually carries. Never a guess, and never an hours figure.
        public int? ProgressPct { get; init; }

        public static readonly TaskProjectionDecision Ignore =
            new() { Action = TaskProjectionAction.Ignore };

        public static TaskProjectionDecision Create(TaskIntent intent) =>
            new() { Action = TaskProjectionAction.Create, Intent = intent };

        public static TaskProjectionDecision Complete(string ruleKeyPrefix) =>
            new() { Action = TaskProjectionAction.Complete, CompleteRuleKeyPrefix = ruleKeyPrefix };

        /// Progress on work that already exists. The prefix locates it the same way Complete does.
        public static TaskProjectionDecision SyncProgress(string ruleKeyPrefix, int progressPct) =>
            new()
            {
                Action = TaskProjectionAction.SyncProgress,
                CompleteRuleKeyPrefix = ruleKeyPrefix,
                ProgressPct = progressPct,
            };
    }

    /// One rule. A rule decides; it never writes. Everything that touches the database — the
    /// assignee check, the ledger, the transition — happens in the one service, so a new rule cannot
    /// introduce a new way to get company isolation or idempotency wrong.
    public interface ITaskOrchestrationRule
    {
        /// Matches a TaskAutoRule.RuleType, so a deployment can disable or route this rule with the
        /// mechanism it already has.
        string RuleType { get; }

        /// WHETHER THIS KIND OF WORK HAS A DEADLINE AT ALL — a question about the business fact, so
        /// the rule answers it. HOW LONG is configurable, so the policy answers that.
        ///
        /// `None` is a real answer and not an omission: work with no deadline can never be overdue
        /// and can never escalate. Five of the platform's own generator rules already work that
        /// way, and saying so out loud is what stops a deadline being invented for them later.
        DueDatePolicy DefaultDueDatePolicy { get; }

        /// Pure: no I/O, no clock beyond the envelope's own OccurredAt.
        /// Pure: no I/O and no clock. The rule reads the business fact and says what KIND of fact
        /// it is; the work policy says what should happen about it.
        TaskProjectionDecision Decide(BusinessEventEnvelope envelope);
    }

    public sealed class TaskProjectionResult
    {
        public required TaskProjectionAction Action { get; init; }
        public int TaskId { get; init; }

        /// EVERY matching rule runs, not just the first. One business fact can legitimately close
        /// one piece of work and open another — a rejected document ends the reviewer's job and
        /// starts the filer's — and a first-match loop would silently drop the second.
        public IReadOnlyList<TaskProjectionResult> Also { get; init; } = Array.Empty<TaskProjectionResult>();

        /// True when the work already existed: a redelivered dispatch row, a retry, a second worker.
        /// Not a failure — the idempotent path, and the common one.
        public bool AlreadyProjected { get; init; }

        public string? Reason { get; init; }

        public static TaskProjectionResult Ignored(string reason) =>
            new() { Action = TaskProjectionAction.Ignore, Reason = reason };
    }

    public interface ITaskOrchestrationService
    {
        /// Project one COMMITTED business event into managed work. Idempotent: the same event twice
        /// produces one task.
        Task<TaskProjectionResult> ProjectAsync(
            BusinessEventEnvelope envelope, CancellationToken ct = default);
    }
}
