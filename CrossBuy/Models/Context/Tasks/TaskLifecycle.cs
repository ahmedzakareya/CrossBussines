using System.Linq.Expressions;

namespace CrossBuy.Models.Context.Tasks
{
    // =============================================================================================
    // THE CANONICAL TASK LIFECYCLE. One definition of what a task's state means; many enforcement
    // points, exactly as AccountingPeriodStatuses.BlocksPosting is for the ledger.
    //
    // NOTHING HERE IS NEW BEHAVIOUR. Every rule below was already in the codebase — the states and
    // the transition matrix were private statics inside TaskService, and the overdue rule was
    // written out by hand in seven places. This file is the extraction, not a redesign: the states
    // keep their existing names, the matrix keeps its existing edges, and the overdue predicate
    // keeps its existing meaning.
    //
    // WHY EXTRACT IT AT ALL. The rule being correct in seven places is not the same as its being one
    // rule. WorkspaceAgendaService already carried a comment saying "the domain already defines
    // overdue in TaskOverdueSweepService" — somebody noticed the duplication and documented it
    // rather than being able to call it. A rule that cannot be called cannot be mutation-tested
    // either: change one copy and six tests still pass.
    //
    // OVERDUE IS DERIVED, NEVER STORED. TaskItem.Status's own comment has said so since the entity
    // was written. It is a temporal condition over (DueDate, Status, now), so a completed task whose
    // due date has passed is NOT overdue — it is finished, and escalating it in Phase 2 would be
    // chasing work that is already done.
    // =============================================================================================
    public static class TaskStatuses
    {
        public const string New = "New";
        public const string InProgress = "InProgress";
        public const string Done = "Done";

        /// The three states this product actually has. `Task.Cancelled` exists in the event
        /// vocabulary with no state behind it; that is recorded as debt rather than invented here.
        public static readonly IReadOnlyList<string> All = new[] { New, InProgress, Done };

        public static bool IsKnown(string? status) =>
            status != null && All.Contains(status, StringComparer.Ordinal);

        /// Work that is still somebody's to do. The Phase-2 escalation predicate reads this, which is
        /// why it is stated here rather than inferred from "not Done" at each call site.
        public static bool IsActionable(string? status) =>
            status == New || status == InProgress;

        /// TERMINAL FOR THE WORK, not for the record. Done ends the doing; the matrix still allows
        /// Done -> InProgress, because reopening is a decision somebody takes rather than a state the
        /// task drifts back into. Both halves matter: a terminal task is never actionable, and it is
        /// never overdue, but it is not frozen.
        public static bool IsTerminal(string? status) => status == Done;

        // ----------------------------------------------------------------------------------------
        // THE TRANSITION MATRIX — lifted verbatim from TaskService, where it was private.
        //
        //   New        -> InProgress | Done      (start, or finish something trivial outright)
        //   InProgress -> New        | Done      (put back, or finish)
        //   Done       -> InProgress             (reopen)
        //
        // Done -> New is deliberately absent and always was: reopening resumes work, it does not
        // pretend the task was never started.
        // ----------------------------------------------------------------------------------------
        private static readonly Dictionary<string, string[]> Matrix = new(StringComparer.Ordinal)
        {
            [New] = new[] { InProgress, Done },
            [InProgress] = new[] { New, Done },
            [Done] = new[] { InProgress },
        };

        public static IReadOnlyList<string> AllowedFrom(string? from) =>
            from != null && Matrix.TryGetValue(from, out var allowed) ? allowed : Array.Empty<string>();

        /// The one question every status-changing path must ask. An unknown state on either side is
        /// refused rather than treated as a fresh start — a status nobody declared is a data fault,
        /// and letting it transition would launder it into a legitimate one.
        public static bool CanTransition(string? from, string? to) =>
            IsKnown(from) && IsKnown(to) && AllowedFrom(from).Contains(to, StringComparer.Ordinal);

        // ----------------------------------------------------------------------------------------
        // OVERDUE
        //
        // TWO FORMS, ONE MEANING, AND THE HONEST NOTE ABOUT WHY. EF cannot translate a method call
        // into SQL, so a query needs an expression and an in-memory caller needs a predicate. They
        // are written out twice here — and TaskLifecycleTests drives both across a matrix of due
        // dates and states and asserts they agree on every cell, because two hand-written twins are
        // exactly the thing this file exists to stop being invisible.
        // ----------------------------------------------------------------------------------------

        /// In-memory. `now` is a parameter rather than a clock read: one rule has to serve a request,
        /// a background sweep and a test without any of them owning the calendar.
        public static bool IsOverdue(DateTime? dueDate, string? status, DateTime now) =>
            dueDate != null && dueDate < now && !IsTerminal(status);

        /// The same rule, shaped for a database query.
        public static Expression<Func<TaskItem, bool>> OverdueAt(DateTime now) =>
            t => t.DueDate != null && t.DueDate < now && t.Status != Done;

        /// Convenience for callers that already hold the row.
        public static bool IsOverdue(TaskItem task, DateTime now) =>
            IsOverdue(task.DueDate, task.Status, now);

        // ----------------------------------------------------------------------------------------
        // SWEEP ELIGIBILITY — overdue AND not actively on hold (SHF-140, Wave 2B).
        //
        // WHY THIS IS A SEPARATE RULE AND NOT A CHANGE TO THE ONE ABOVE. IsOverdue and OverdueAt keep
        // their present meaning because THIRTEEN call sites read them - the task list, the board, the
        // workspace agenda, the CRM dashboard, three reporting datasets and the task report service
        // among them - and because a task that was ALREADY OVERDUE when it went on hold must still
        // READ as overdue. Pausing stops the SWEEPS from acting; it does not erase a fact that already
        // happened. So hold never enters the overdue rule; it narrows who acts on it.
        //
        // WHY IT LIVES HERE RATHER THAN IN THE TWO SWEEPS. This file exists because the overdue rule
        // had been written out by hand in seven places, and being correct in seven places is not the
        // same as being ONE rule. A hold filter pasted into the overdue sweep and again into the
        // escalation sweep would recreate exactly that duplication - and a rule that cannot be called
        // cannot be mutation-tested either.
        //
        // ACTIVELY ON HOLD IS THE APPROVAL, NOT THE REQUEST. A pending request pauses nothing: the SLA
        // keeps running until somebody with authority says otherwise, which is the whole point of
        // asking. Only HoldApprovedAtUtc stops a sweep.
        // ----------------------------------------------------------------------------------------

        /// Is this task actively on hold - approved, not merely requested?
        public static bool IsOnHold(DateTime? holdApprovedAtUtc) => holdApprovedAtUtc != null;

        /// In-memory. Overdue by the canonical rule, and not paused.
        public static bool IsSweepEligible(DateTime? dueDate, string? status, DateTime? holdApprovedAtUtc, DateTime now) =>
            IsOverdue(dueDate, status, now) && !IsOnHold(holdApprovedAtUtc);

        // ----------------------------------------------------------------------------------------
        // THE DERIVED HOLD STATE. Three values, read off the nullability of two timestamps - there is
        // no stored status and no enum column behind this.
        // ----------------------------------------------------------------------------------------

        public const string HoldStateNormal = "Normal";
        public const string HoldStateRequested = "HoldRequested";
        public const string HoldStateOnHold = "OnHold";

        public static string HoldState(DateTime? requestedAtUtc, DateTime? approvedAtUtc) =>
            approvedAtUtc != null ? HoldStateOnHold
            : requestedAtUtc != null ? HoldStateRequested
            : HoldStateNormal;
        // ----------------------------------------------------------------------------------------
        // STAGE 3 CONVERGENCE SEAM - THREE OVERLOADS ARE DELIBERATELY ABSENT.
        //
        // This file is the programme line's TaskStatuses, imported verbatim except for the three
        // members that read TaskItem.HoldApprovedAtUtc / HoldRequestedAtUtc. Those two columns belong
        // to the Hold feature and do not exist on TaskItem here yet, so the overloads would not
        // compile. Nothing was rewritten to make them fit: each omitted member is a whole method,
        // and the parameter-taking twin of each one is present and byte-identical.
        //
        // OMITTED, AND OWNED BY THE HOLD STAGE:
        //   Expression<Func<TaskItem, bool>> SweepEligibleAt(DateTime)
        //   bool   IsSweepEligible(TaskItem, DateTime)
        //   string HoldState(TaskItem)
        //
        // The Hold stage restores them together with the two columns, and this comment is how it
        // knows they are owed rather than missing by accident.
        // ----------------------------------------------------------------------------------------
    }
}
