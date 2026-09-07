# 02 — Task Management, Current State

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


## Files

### On `master` (deployed reality)

| File | Role |
|---|---|
| `Models/Context/Tasks/TaskItem.cs` | the entity |
| `Models/Context/Tasks/TaskEcosystem.cs` | checklist, dependency, template, comment entities |
| `Models/Context/Tasks/TaskAutomation.cs` | `TaskAutoRule`, `TaskAutoLog` |
| `Models/Context/Tasks/TaskMatchSuggestion.cs` | schedule-match suggestions |
| `Models/Context/Tasks/TimesheetEntry.cs` | time capture |
| `BL/TaskService.cs` | **the canonical writer** |
| `BL/TaskGeneratorService.cs` + `TaskGeneratorHostedService.cs` | recurring generation |
| `BL/TaskScheduleMatcher.cs` + `TaskScheduleMatchHostedService.cs` | scheduled-task matching |
| `BL/TasksAccessService.cs` | module access service |
| `BL/Platform/TaskScopeQuery.cs` | the one place an `AccessScope` becomes a task predicate |
| `BL/TaskLinkResolver.cs` | entity back-links |
| `BL/TaskCostService.cs`, `TaskBillingService.cs`, `TaskReportService.cs` | cost / billing / reports |
| `BL/TasksCalendar/*` (11 files) | escalation, overdue sweep, notifications, calendar publishing, agenda |
| `Controllers/TasksController.cs` | UI |
| `Views/Tasks/*` (8) | Index, Board, Detail, AutoRules, Templates, MatchSuggestions, Reports, HoursReport |

### Added by `tasks/phase4` (NOT on master)

| File | Role |
|---|---|
| `Models/Context/Tasks/TaskLifecycle.cs` | **the canonical lifecycle** — states, transitions, overdue |
| `BL/Platform/Orchestration/TaskOrchestrationService.cs` | event → task projection |
| `BL/Platform/Orchestration/TaskOrchestrationConsumer.cs` | outbox consumer |
| `BL/Platform/Orchestration/TaskOrchestrationContracts.cs` | `TaskIntent`, decisions, rule interface |
| `BL/Platform/Orchestration/DocumentVerificationRule.cs` | **the only rule** |
| `BL/Platform/Orchestration/TaskWorkPolicy.cs` | Phase-4 policy + resolver |
| `BL/Platform/Orchestration/TaskEscalationTiming.cs` | grace / interval / max level, addressable |
| `BL/Platform/Orchestration/TaskManagementQueryService.cs` | the management questions |

`TaskLifecycle.cs` header states plainly that it is an **extraction, not a redesign**: the states were
private statics inside `TaskService` and the overdue rule "was written out by hand in seven places".

## Current flow (phase4)

```
SOURCES
  manual (TasksController)  ──► ITaskService.SaveAsync ──┐
  template/checklist        ──► ITaskService.SaveAsync ──┤
  recurring generator       ──► TaskItems.Add ───────────┤
  document expiry           ──► TaskItems.Add ───────────┤   ALL land in the same table
  event orchestration       ──► TaskItems.Add ───────────┘
                                        │
                                        ▼
                                    TaskItem row
                                        │
        ┌───────────────────────────────┼───────────────────────────────┐
        ▼                               ▼                               ▼
  lifecycle (New/InProgress/Done)  assignment              DueDate (nullable)
        │                               │                               │
        ▼                               ▼                               ▼
  ┌─────────────────────────── read surfaces ───────────────────────────┐
  │  MyWork          TaskScopeQuery.WithinScope(Own)                    │
  │  Team/Workspace  TaskScopeQuery.WithinScope(Team|Company)           │
  │  Agenda/Calendar WorkspaceAgendaService — Task + CalendarEvent only │
  │  Management      TaskManagementQueryService  ← NOT WIRED to any UI  │
  └─────────────────────────────────────────────────────────────────────┘
                                        │
                                        ▼
                        overdue = DueDate < now AND Status != Done   (derived, never stored)
                                        │
                                        ▼
                       TaskEscalationHostedService (grace 24h)
                                        │
                                        ▼
                        OrgHierarchy.DirectManagerAsync  (walk up H_Type==5)
                                        │
                          ┌─────────────┴─────────────┐
                          ▼                           ▼
                  Notification              Task.Escalated business event
```

**The gap in that diagram:** only the two `SaveAsync` sources emit `Task.Created`. The three direct-write
doors emit nothing — see `06-SYSTEM-WORK-CREATION-PATHS.md`.
