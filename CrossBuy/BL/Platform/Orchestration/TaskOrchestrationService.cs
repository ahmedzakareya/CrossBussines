using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform.Orchestration
{
    // =============================================================================================
    // THE ONE ORCHESTRATION AUTHORITY.
    //
    // Rules decide. This writes. Everything a rule could get wrong about isolation, idempotency or
    // the lifecycle is decided here once instead of once per rule.
    //
    // WHAT IT REFUSES, BEFORE IT LOOKS AT ANY RULE:
    //   * an event with no resolvable company. NOT company 1, not the current user's company, not
    //     "all". A background consumer that inferred a company would be inventing the one fact the
    //     event already carries.
    //   * a TASK event. Task.Created must never reach a rule that creates tasks, or the first
    //     projection produces the second and the outbox does the rest. The refusal is explicit and
    //     tested rather than left to the fact that no rule happens to match today.
    //
    // NO NEW TABLE. Idempotency lives in TaskAutoLog, which has existed for exactly this since the
    // generator was written: one row per (company, rule key) already turned into a task, and the
    // task id alongside it. That row is also what lets a later event CLOSE the work it opened.
    // =============================================================================================
    public sealed class TaskOrchestrationService : ITaskOrchestrationService
    {
        /// Events about tasks are never projected into tasks. See the loop argument above.
        public const string TaskEntityPrefix = "Task.";

        private readonly CrossDbContext _db;
        private readonly IEnumerable<ITaskOrchestrationRule> _rules;
        private readonly ITaskService _tasks;
        private readonly ITaskWorkPolicyResolver _policies;
        private readonly TimeProvider _clock;
        private readonly ILogger<TaskOrchestrationService>? _log;

        public TaskOrchestrationService(
            CrossDbContext db,
            IEnumerable<ITaskOrchestrationRule> rules,
            ITaskService tasks,
            ITaskWorkPolicyResolver policies,
            TimeProvider? clock = null,
            ILogger<TaskOrchestrationService>? log = null)
        { _db = db; _rules = rules; _tasks = tasks; _policies = policies; _clock = clock ?? TimeProvider.System; _log = log; }

        public async Task<TaskProjectionResult> ProjectAsync(
            BusinessEventEnvelope envelope, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(envelope);

            int companyId = envelope.Context.CompanyId;
            if (companyId <= 0)
                return TaskProjectionResult.Ignored("no company on the event");

            // LOOP SAFETY, stated as a rule rather than left to luck. A task projection emits
            // Task.Created; if that could reach a rule, one source event would produce an unbounded
            // chain and every retry would widen it.
            if (envelope.EventType.StartsWith(TaskEntityPrefix, StringComparison.Ordinal))
                return TaskProjectionResult.Ignored("task events are never projected into tasks");

            // EVERY MATCHING RULE RUNS. One business fact can legitimately close one piece of work
            // and open another - a rejected document ends the reviewer's job and starts the filer's -
            // and a first-match loop would have silently dropped the second. Rules stay independent:
            // none of them can see what another decided.
            var applied = new List<TaskProjectionResult>();

            foreach (var rule in _rules)
            {
                var decision = rule.Decide(envelope);
                if (decision.Action == TaskProjectionAction.Ignore) continue;

                // THE POLICY IS ASKED ONCE THE RULE HAS SAID THIS FACT IS WORK, and it answers every
                // configurable question at once: enabled, assignee, deadline, priority, cadence. The
                // rule contributes only the KIND of deadline. A refusal is a NAMED outcome rather
                // than a silent skip - see TaskWorkPolicy - and it refuses THAT rule, not the event:
                // one disabled rule must not suppress another rule's work.
                var resolution = await _policies.ResolveAsync(
                    companyId, rule.RuleType, ct, rule.DefaultDueDatePolicy);
                if (!resolution.IsApplicable)
                {
                    applied.Add(TaskProjectionResult.Ignored($"{resolution.Outcome}: {resolution.Reason}"));
                    continue;
                }

                applied.Add(decision.Action switch
                {
                    TaskProjectionAction.Create =>
                        await CreateAsync(companyId, decision.Intent!, resolution.Policy!, envelope, ct),
                    TaskProjectionAction.Complete =>
                        await CompleteAsync(companyId, decision.CompleteRuleKeyPrefix!, ct),
                    TaskProjectionAction.SyncProgress =>
                        await SyncProgressAsync(companyId, decision.CompleteRuleKeyPrefix!, decision.ProgressPct!.Value, ct),
_ => TaskProjectionResult.Ignored("no action"),
                });
            }

            if (applied.Count == 0) return TaskProjectionResult.Ignored("no rule applies");

            var first = applied[0];
            return applied.Count == 1
                ? first
                : new TaskProjectionResult
                {
                    Action = first.Action,
                    TaskId = first.TaskId,
                    AlreadyProjected = first.AlreadyProjected,
                    Reason = first.Reason,
                    Also = applied.Skip(1).ToList(),
                };
        }

        // -----------------------------------------------------------------------------------------
        private async Task<TaskProjectionResult> CreateAsync(
            int companyId, TaskIntent intent, TaskWorkPolicy policy,
            BusinessEventEnvelope envelope, CancellationToken ct)
        {
            // The rule may not project across a company boundary, however it was built.
            if (intent.CompanyId != companyId)
                return TaskProjectionResult.Ignored("rule produced a foreign company");

            // ALREADY DONE? The ledger is the evidence, read before any write, so the common path on
            // a redelivered dispatch row costs one query.
            var existing = await _db.TaskAutoLogs.AsNoTracking()
                .FirstOrDefaultAsync(l => l.CompanyId == companyId && l.RuleKey == intent.RuleKey, ct);
            if (existing != null)
                return new TaskProjectionResult
                {
                    Action = TaskProjectionAction.Create,
                    TaskId = existing.TaskId,
                    AlreadyProjected = true,
                };

            // THE ASSIGNEE, IN ORDER OF AUTHORITY: a person the RULE read out of the business fact,
            // then the POLICY's configured default, then unassigned. Never a manager, never the
            // first employee, never employee 1. The policy has already refused a configured default
            // that is not this company's employee; a source-named person is checked below for the
            // same reason, against the Employee row rather than the entity registry, which does not
            // company-filter Employee (TM-2).
            int assignee = intent.AssigneeEmployeeId > 0
                ? intent.AssigneeEmployeeId
                : policy.ConfiguredAssigneeEmployeeId;

            if (assignee > 0)
            {
                bool ours = await _db.Employee.AsNoTracking()
                    .AnyAsync(e => e.ID == assignee && e.EmpCompanyID == companyId, ct);
                if (!ours)
                {
                    _log?.LogWarning(
                        "Task orchestration: assignee {Assignee} for rule {Rule} does not belong to company " +
                        "{Company}; the task is created UNASSIGNED rather than routed across a tenant.",
                        assignee, intent.RuleType, companyId);
                    assignee = 0;
                }
            }

            // THE DEADLINE IS THE POLICY'S ANSWER, not the rule's. A source that carries its own
            // date wins; otherwise the configured offset from when the fact occurred; and None is a
            // real answer — work with no deadline never becomes overdue and never escalates, which is
            // a decision somebody makes rather than an omission.
            DateTime? due = policy.DueDate == DueDatePolicy.None
                ? null
                : intent.SourceDueDate ?? envelope.OccurredAt.Date.AddDays(policy.DueOffsetDays);

            var now = _clock.GetUtcNow().UtcDateTime;
            var task = new TaskItem
            {
                CompanyId = companyId,
                Title = intent.TitleAr,
                TitleEn = intent.TitleEn,
                Description = intent.Description,
                AssigneeEmployeeId = assignee,
                // 0 = the system created this, the same convention the expiry projection uses. The
                // actor is not impersonated as a person who did not press anything.
                CreatedByEmployeeId = 0,
                Priority = policy.Priority,
                DueDate = due,
                Status = TaskStatuses.New,
                ActualHours = 0m,
                EntityType = intent.EntityType,
                EntityId = intent.EntityId,
                CreatedAt = now,
            };

            // ONE TRANSACTION FOR THE TASK AND ITS LEDGER ROW. A task without its ledger row would be
            // created again on the next redelivery; a ledger row without its task would suppress the
            // work forever. They are one fact.
            await using (var tx = await ScopedTx.BeginOrJoinAsync(_db))
            {
                _db.TaskItems.Add(task);
                await _db.SaveChangesAsync(ct);

                _db.TaskAutoLogs.Add(new TaskAutoLog
                {
                    CompanyId = companyId,
                    RuleKey = intent.RuleKey,
                    TaskId = task.ID,
                    CreatedAt = now,
                });
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync();
            }

            return new TaskProjectionResult { Action = TaskProjectionAction.Create, TaskId = task.ID };
        }

        // -----------------------------------------------------------------------------------------
        private async Task<TaskProjectionResult> CompleteAsync(
            int companyId, string ruleKeyPrefix, CancellationToken ct)
        {
            // The closing event is a different event from the opening one and cannot know its
            // identity, so the work is found by the prefix the rule owns — scoped to this company.
            var opened = await _db.TaskAutoLogs.AsNoTracking()
                .Where(l => l.CompanyId == companyId && l.RuleKey.StartsWith(ruleKeyPrefix))
                .OrderByDescending(l => l.ID)
                .FirstOrDefaultAsync(ct);

            if (opened == null)
                return TaskProjectionResult.Ignored("nothing was projected for this source");

            // THROUGH THE LIFECYCLE, NEVER AROUND IT. This is system authority, not a licence: the
            // transition is refused if the matrix refuses it, the company is checked by the service
            // itself, and the actor recorded is 0 — the system — rather than a person who did not
            // press anything.
            var (ok, error) = await _tasks.ChangeStatusAsync(
                companyId, opened.TaskId, TaskStatuses.Done, currentEmployeeId: 0);

            if (!ok)
            {
                _log?.LogInformation(
                    "Task orchestration could not complete task {TaskId} for company {Company}: {Error}",
                    opened.TaskId, companyId, error);
                return new TaskProjectionResult
                {
                    Action = TaskProjectionAction.Complete,
                    TaskId = opened.TaskId,
                    Reason = error,
                };
            }

            return new TaskProjectionResult
            {
                Action = TaskProjectionAction.Complete,
                TaskId = opened.TaskId,
            };
        }

        // -----------------------------------------------------------------------------------------
        // WAVE 2 — progress on work that already exists.
        //
        // IT WRITES EXACTLY ONE COLUMN. ProgressPct and nothing else: not the title, description,
        // assignee, due date or priority, because a person may have edited those and a progress fact
        // has no business overwriting a human's routing decision. Not ActualHours either — that is a
        // timesheet total, not progress.
        //
        // IT NEVER TOUCHES STATUS. A task at 60% is still New or InProgress; only a real completion
        // event moves it to Done, and only through the lifecycle service. So this action cannot
        // become a back door around the status matrix.
        //
        // IT IS IDEMPOTENT BY NATURE rather than by ledger. Re-delivering the same production event
        // writes the same percentage, and it deliberately adds NO ledger row: the ledger records that
        // work was PROJECTED, and no new work was. Writing one per receipt would also make the
        // Complete lookup — which takes the newest row for the prefix — point at a progress row
        // instead of the opening projection.
        //
        // A TASK ALREADY DONE IS LEFT ALONE. Moving its percentage back below 100 would contradict
        // the completion, and a late-arriving production event is not authority to reopen work.
        private async Task<TaskProjectionResult> SyncProgressAsync(
            int companyId, string ruleKeyPrefix, int progressPct, CancellationToken ct)
        {
            var opened = await _db.TaskAutoLogs.AsNoTracking()
                .Where(l => l.CompanyId == companyId && l.RuleKey.StartsWith(ruleKeyPrefix))
                .OrderByDescending(l => l.ID)
                .FirstOrDefaultAsync(ct);

            if (opened == null)
                return TaskProjectionResult.Ignored("nothing was projected for this source");

            // Company-scoped read of the task itself: the ledger row is already company-scoped, and
            // this predicate makes the task write scoped too rather than trusting the join.
            var task = await _db.TaskItems
                .FirstOrDefaultAsync(t => t.ID == opened.TaskId && t.CompanyId == companyId, ct);

            if (task == null)
                return TaskProjectionResult.Ignored("the projected task no longer exists");

            if (string.Equals(task.Status, TaskStatuses.Done, StringComparison.Ordinal))
                return TaskProjectionResult.Ignored("the task is already done");

            int clamped = progressPct < 0 ? 0 : (progressPct > 100 ? 100 : progressPct);
            if (task.ProgressPct == clamped)
                return new TaskProjectionResult { Action = TaskProjectionAction.SyncProgress, TaskId = task.ID };

            task.ProgressPct = clamped;
            await _db.SaveChangesAsync(ct);

            return new TaskProjectionResult { Action = TaskProjectionAction.SyncProgress, TaskId = task.ID };
        }
    }
}
