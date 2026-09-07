# 05 — Task Orchestration (Phase 3–4)

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


**Present on `tasks/phase3` and `tasks/phase4` only. Absent from `master`.**

## Components

| Piece | File |
|---|---|
| Service | `BL/Platform/Orchestration/TaskOrchestrationService.cs` |
| Consumer | `BL/Platform/Orchestration/TaskOrchestrationConsumer.cs` |
| Contracts | `BL/Platform/Orchestration/TaskOrchestrationContracts.cs` |
| Rule interface | `ITaskOrchestrationRule` |
| **Only rule** | `BL/Platform/Orchestration/DocumentVerificationRule.cs` |
| Work policy | `BL/Platform/Orchestration/TaskWorkPolicy.cs` (+ `TaskWorkPolicyResolver`) |

Registered at `Program.cs:680–685`.

## Which events create tasks?

**One family: document verification**, via `DocumentVerificationRule`. That is the entire pilot.

## Which events are intentionally ignored?

`TaskOrchestrationService.ProjectAsync` refuses in this order:

| Check | Line | Reason returned |
|---|---|---|
| No company on the event | 57 | `"no company on the event"` |
| Event type starts with `Task.` | 63 | `"task events are never projected into tasks"` |
| Rule produced a foreign company | 97 | `"rule produced a foreign company"` |
| No rule matched | 87 | `"no rule applies"` |

**Loop guard: `TaskEntityPrefix = "Task."` (line 32), checked at line 63.** `Task.*` events can never
recursively create tasks. This is a prefix test, not an allow-list, so any future `Task.X` event is
covered automatically.

## Idempotency

`TaskAutoLog` keyed on `RuleKey`:

```csharp
.FirstOrDefaultAsync(l => l.CompanyId == companyId && l.RuleKey == intent.RuleKey, ct)   // line 102
```

A second delivery of the same event finds the log row and returns `AlreadyProjected` instead of
creating a duplicate. `Complete` uses a `RuleKey` **prefix** match (line 192) so a later event can
close what an earlier one opened.

## Assignment validation

```csharp
.AnyAsync(e => e.ID == assignee && e.EmpCompanyID == companyId, ct)   // line 124
```

The assignee must be an employee **of the event's company**. Combined with the line-97 foreign-company
refusal, a rule cannot project work into another tenant.

## Answers

| Question | Answer |
|---|---|
| Which events create tasks? | Document verification only |
| Which are intentionally ignored? | all `Task.*`; events with no company; unmatched types |
| Which pilot is implemented? | `DocumentVerificationRule` |
| Does a producing module call the orchestrator directly? | **No.** No module injects `ITaskOrchestrationService`; the only entry is the outbox consumer |
| Can `Task.*` recursively create tasks? | **No** — prefix guard at line 63 |
| How is replay handled? | Not automatic. `Program.cs:678` — a newly registered consumer gets dispatch rows for **new** events only |

## Work policy (Phase 4)

`TaskWorkPolicy.cs` adds one resolver answering *who, when, how urgently, and how it escalates*:

- `AssignmentStrategy` (default `Unassigned`), `ConfiguredAssigneeEmployeeId`
- `DueDatePolicy` (default `OffsetFromEvent`), `DueOffsetDays` (default **3**, line 133)
- `Priority` (default `Normal`)
- `Timing` → `TaskEscalationTiming`
- `PolicyOutcome` — a policy can refuse, and refusal is a first-class outcome

Backed by a table lookup keyed `(CompanyId, RuleType)` at line 156, with the configured assignee
company-checked at line 175. Phase 4 **did not change orchestration behaviour** — the commit message
for `a56f255` says the projection "takes its configurable decisions from the policy, and behaves
identically".
