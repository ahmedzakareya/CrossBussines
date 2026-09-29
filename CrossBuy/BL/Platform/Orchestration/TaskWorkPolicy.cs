using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CrossBuy.BL.Platform.Orchestration
{
    // =============================================================================================
    // WORK POLICY — what SHOULD happen, kept separate from the Task, which records what work EXISTS.
    //
    // Phase 3 proved a business event can become managed work. Phase 4 asks the question that comes
    // immediately after: on whose authority, to whom, by when, and how urgently — for every rule,
    // in every company, without those answers being written out again inside each rule.
    //
    // WHY THIS IS NOT A RULE ENGINE. A policy is resolved data, not a script: nothing here evaluates
    // conditions, and nothing chooses WHETHER an event is work — that stays with the rule, which can
    // read the business fact. A policy answers only the configurable half, so the same four questions
    // are answered the same way whoever asks them.
    //
    // NOTHING IS COPIED ONTO THE TASK. The task keeps the fields it always had; the policy is asked
    // at projection time and its lineage stays in the rule key, exactly as Phase 3 established.
    // =============================================================================================

    /// §11 — the anti-spam vocabulary. Only ACTION_REQUIRED normally becomes new work.
    ///
    /// It is an explicit classification rather than a default because the failure it prevents is
    /// silent: an estate where every committed fact becomes somebody's job stops being a task list
    /// and becomes a second event log that people have to close by hand.
    public enum WorkEventClass
    {
        /// Not about work at all, and not a fact this platform reasons about.
        NotApplicable = 0,
        /// Worth telling somebody; not worth opening work. Notifications already carry these.
        Informational,
        /// Kept for the record and for reporting. Nobody acts on it.
        AuditOnly,
        /// One system telling another that state moved. No human step exists.
        StateSync,
        /// The source says work that already exists is finished.
        TaskCompletion,
        /// A human must do something, and until they do, nothing tracks that they have not.
        ActionRequired,
    }

    /// Where an assignee comes from. Each value must name a source that actually exists in the
    /// repository — there is deliberately no `Manager`, and deliberately no `Anybody`.
    public enum AssignmentStrategy
    {
        /// Nobody is named; the work is created unassigned for a manager to route. The convention
        /// every system creator already uses, and an honest answer rather than a failure.
        Unassigned = 0,
        /// The deployment's TaskAutoRule.DefaultAssigneeEmployeeId for this rule and company.
        ConfiguredDefault,
        /// A person named by the business fact itself — a document owner, an approval reviewer.
        /// Only usable by a rule that can actually read one.
        ExplicitFromSource,
    }

    /// Where a deadline comes from. `Now + N` is a strategy with a name and one place to configure
    /// it, not something written out per rule.
    public enum DueDatePolicy
    {
        /// No deadline. Legitimate — five of the platform's own generator rules work this way — but
        /// it means the task can never be overdue and can never escalate, which is a decision.
        None = 0,
        /// A date carried by the business fact: an expiry, a milestone, a promised date.
        FromSource,
        /// The configured offset from when the fact occurred.
        OffsetFromEvent,
    }

    /// §28 — why a policy did not produce work. Never a silent skip and never a malformed task.
    public enum PolicyOutcome
    {
        Applicable = 0,
        /// The rule is switched off for this company.
        Disabled,
        /// The company on the event cannot be used.
        CompanyInvalid,
        /// A policy names a strategy whose configuration is absent.
        ConfigurationMissing,
        /// A named assignee exists but is not this company's employee.
        AssigneeInvalid,
    }

    /// The resolved answer for one (company, rule). Immutable, and cheap enough to resolve per event.
    public sealed class TaskWorkPolicy
    {
        public required int CompanyId { get; init; }
        public required string RuleType { get; init; }
        public required bool Enabled { get; init; }

        public AssignmentStrategy Assignment { get; init; } = AssignmentStrategy.Unassigned;
        /// Only meaningful for ConfiguredDefault; already company-checked when non-zero.
        public int ConfiguredAssigneeEmployeeId { get; init; }

        public DueDatePolicy DueDate { get; init; } = DueDatePolicy.OffsetFromEvent;
        public int DueOffsetDays { get; init; }

        public string Priority { get; init; } = "Normal";

        /// Escalation timing is NOT copied per rule. It is resolved from one place, so a company that
        /// changes its cadence changes it for every kind of work at once — which is the current
        /// product rule, and departing from it must be a decision rather than a drift.
        public required TaskEscalationTiming Timing { get; init; }
    }

    public sealed class TaskWorkPolicyResolution
    {
        public required PolicyOutcome Outcome { get; init; }
        public TaskWorkPolicy? Policy { get; init; }
        public string? Reason { get; init; }

        public bool IsApplicable => Outcome == PolicyOutcome.Applicable && Policy != null;

        public static TaskWorkPolicyResolution Refused(PolicyOutcome outcome, string reason) =>
            new() { Outcome = outcome, Reason = reason };

        public static TaskWorkPolicyResolution Ok(TaskWorkPolicy policy) =>
            new() { Outcome = PolicyOutcome.Applicable, Policy = policy };
    }

    public interface ITaskWorkPolicyResolver
    {
        Task<TaskWorkPolicyResolution> ResolveAsync(
            int companyId, string ruleType, CancellationToken ct = default,
            DueDatePolicy dueDateKind = DueDatePolicy.OffsetFromEvent);
    }

    // ---------------------------------------------------------------------------------------------
    public sealed class TaskWorkPolicyResolver : ITaskWorkPolicyResolver
    {
        /// The offset used when a rule has no date of its own. ONE place, so no rule writes its own.
        public const int DefaultDueOffsetDays = 3;

        private readonly CrossDbContext _db;
        private readonly IConfiguration? _config;

        public TaskWorkPolicyResolver(CrossDbContext db, IConfiguration? config = null)
        { _db = db; _config = config; }

        public async Task<TaskWorkPolicyResolution> ResolveAsync(
            int companyId, string ruleType, CancellationToken ct = default,
            DueDatePolicy dueDateKind = DueDatePolicy.OffsetFromEvent)
        {
            if (companyId <= 0)
                return TaskWorkPolicyResolution.Refused(
                    PolicyOutcome.CompanyInvalid, "no company: a policy is never resolved for 'any company'");

            if (string.IsNullOrWhiteSpace(ruleType))
                return TaskWorkPolicyResolution.Refused(
                    PolicyOutcome.ConfigurationMissing, "no rule type");

            // ABSENT MEANS ON; INACTIVE MEANS OFF. Preserved verbatim from DocumentExpiryProjection,
            // which established it: a deployment that has never heard of a rule still gets its work,
            // and one that switched it off stays switched off.
            var row = await _db.TaskAutoRules.AsNoTracking()
                .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.RuleType == ruleType, ct);

            if (row != null && !row.IsActive)
                return TaskWorkPolicyResolution.Refused(
                    PolicyOutcome.Disabled, $"rule {ruleType} is switched off for company {companyId}");

            int configured = row?.DefaultAssigneeEmployeeId ?? 0;
            var assignment = configured > 0
                ? AssignmentStrategy.ConfiguredDefault
                : AssignmentStrategy.Unassigned;

            // A CONFIGURED ASSIGNEE FROM ANOTHER COMPANY IS NOT A ROUTING DECISION, IT IS A LEAK.
            // Checked against the Employee row, deliberately not the entity registry, which does not
            // company-filter Employee (TM-2). The policy then degrades to Unassigned rather than
            // refusing outright: the work is real and somebody should route it, and a task nobody
            // owns is visible while a task that never existed is not.
            if (assignment == AssignmentStrategy.ConfiguredDefault)
            {
                bool ours = await _db.Employee.AsNoTracking()
                    .AnyAsync(e => e.ID == configured && e.EmpCompanyID == companyId, ct);
                if (!ours)
                    return TaskWorkPolicyResolution.Ok(Build(
                        companyId, ruleType, AssignmentStrategy.Unassigned, 0, dueDateKind));
            }

            return TaskWorkPolicyResolution.Ok(Build(companyId, ruleType, assignment, configured, dueDateKind));
        }

        private TaskWorkPolicy Build(int companyId, string ruleType, AssignmentStrategy assignment,
            int assignee, DueDatePolicy dueDateKind) =>
            new()
            {
                CompanyId = companyId,
                RuleType = ruleType,
                Enabled = true,
                Assignment = assignment,
                ConfiguredAssigneeEmployeeId = assignment == AssignmentStrategy.ConfiguredDefault ? assignee : 0,
                DueDate = dueDateKind,
                DueOffsetDays = OffsetDays,
                Priority = "Normal",
                Timing = TaskEscalationTiming.Resolve(_config, companyId),
            };

        private int OffsetDays =>
            int.TryParse(_config?["Tasks:Orchestration:DefaultDueOffsetDays"], out var d) && d >= 0
                ? d : DefaultDueOffsetDays;
    }
}
