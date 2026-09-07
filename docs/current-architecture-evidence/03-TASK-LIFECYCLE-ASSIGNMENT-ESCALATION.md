# 03 — Lifecycle, Assignment, Escalation

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


## Lifecycle — `Models/Context/Tasks/TaskLifecycle.cs` (phase4)

| Property | Value | Evidence |
|---|---|---|
| States | `New`, `InProgress`, `Done` | `TaskStatuses.All`, line 34 |
| Terminal | `Done` **only** | `IsTerminal`, line 48 |
| Actionable | `New`, `InProgress` | `IsActionable`, line 41 |
| Transitions | `AllowedFrom` / `CanTransition` | lines 67, 73 |
| Overdue | `DueDate != null && DueDate < now && Status != Done` | lines 88–96 |
| Overdue storage | **derived, never stored** | header comment, and `OverdueAt` expression line 92 |

On `master` these rules do not exist as a callable unit — they are private statics in `TaskService`
plus seven hand-written copies of the overdue predicate. The extraction is phase4-only.

## `Task.Cancelled` — resolved

Four pieces of evidence, and they disagree in a specific way:

| Artefact | Present? | Evidence |
|---|---|---|
| Event type constant | **YES** | `BusinessEventTypes.cs:188` → `"Task.Cancelled"` |
| Contract registration | **YES** | `TaskCalendarIntegrationContracts.cs:341` |
| Notification kind | **YES** | `TaskNotificationService.cs:57` `task_cancelled`, `TaskCancelledAsync` at 97/183 |
| **Lifecycle state** | **NO** | absent from `TaskStatuses.All` |
| **Producer** | **NO** | no `TaskCancelled…` call in `TaskCalendarEventPublisher.cs` |

The contract registration itself carries the note: *"TaskItem.Status has no Cancelled value today
(New|InProgress|Done)"*. `TaskOverdueSweepService.cs:115` carries a forward-looking comment: *"when a
Cancelled status is added, it must be excluded here too"*. `TaskOrchestrationContracts.cs:26` says
*"no Cancel, because the task lifecycle has no Cancelled state and Phase 3 does not invent one."*

**Verdict: a deliberately reserved contract.** Not dead code (three artefacts are wired and one is
consumed by the notification kind list), not a partial state machine (no state was ever added), and
not an accident — three separate files document the reservation. Adding the state is a product
decision; the plumbing to carry it already exists.

## Assignment

| Mechanism | Where | Who chooses | Company verified? |
|---|---|---|---|
| Explicit assignee | `TaskService.SaveAsync` | caller | **yes** — `EmpCompanyID == companyId` |
| Template/checklist | `TaskChecklistAndTemplateService.cs:333` | template | yes (via SaveAsync) |
| Generator | `TaskGeneratorService.cs` | `TaskAutoRule` | yes — 1 assignee company check |
| Document expiry | `DocumentExpiryProjection.cs` | `DefaultAssigneeEmployeeId` | **NO assignee company check found** |
| Orchestration | `TaskOrchestrationService.cs:124` | work policy | **yes** — `e.ID == assignee && e.EmpCompanyID == companyId` |
| Work policy | `TaskWorkPolicyResolver.cs:175` | configured assignee | yes |

`AssignmentStrategy` (phase4, `TaskWorkPolicy.cs:47`) enumerates the strategies the policy can express;
`Unassigned` is the default. Tasks **can** be unassigned — `TaskManagementQueryService.UnassignedAsync`
exists precisely because "work nobody owns yet" is a real state.

**No evidence found** of an employee *active-state* check on any assignment path. Only company
membership is verified.

## Escalation

`BL/TasksCalendar/TaskEscalationHostedService.cs` → `TaskEscalationService.cs`.

| Aspect | Value | Origin |
|---|---|---|
| Grace | **24h** | `DefaultGraceHours`, configurable `Tasks:Escalation:GraceHours` |
| Level advance | **24h** | `DefaultLevelAdvanceHours` |
| Max level | **2** | `DefaultMaxLevel` |
| Batch cap | 500 per sweep | `MaxPerSweep`, `TaskEscalationService.cs:105` |
| Manager lookup | `OrgHierarchy.DirectManagerAsync` | walks to first `H_Type == 5` ancestor |
| Hard traversal cap | **64 hops** | `OrgHierarchy.cs:168` `MaxHops` |
| Cycle guard | `visited` set | `OrgHierarchy.cs:158/173` |
| Notification | via `ITaskNotificationService` | kind `Escalated` |
| Business event | `Task.Escalated` | `TaskCalendarEventPublisher.cs:162` |
| Company scope | assignee's own `Employee.EmpCompanyID` | `TaskEscalationService.cs:216` |

### Policy vs default — stated by the code itself

`TaskEscalationTiming.cs:30` is unambiguous:

> *"The Phase-2 defaults, unchanged. They are still **PRODUCT POLICY DEBT**: nothing in the repository
> confirms 24/24/2 as a business rule, and this class does not promote them into one — it only makes
> them addressable."*

So **24 / 24 / 2 are current technical defaults**, introduced in Phase 2, made configurable
(platform default + `Tasks:Escalation:Company:{id}:GraceHours` override) in Phase 4. **No confirmed
business policy exists.** A rule-level override is deliberately absent and the file explains why.
