# 08 — Workspace, MyWork, Attention, Calendar

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


## Workspace

`Controllers/WorkspaceController.cs` — five actions:

| Action | View | Source |
|---|---|---|
| `Index` | `Workspace/Index.cshtml` | `BL/Workspace/WorkspaceService.cs` |
| `Agenda(days = 7)` | `Workspace/Agenda.cshtml` | `TasksCalendar/WorkspaceAgendaService.cs` |
| `Reports` | `Workspace/Reports.cshtml` | `Reporting/ReportingWorkspaceSource.cs` |
| `Notifications(unreadOnly)` | `Workspace/Notifications.cshtml` | `INotificationService` |
| `Mentions` | `Workspace/Mentions.cshtml` | `Communication/CommMentionService.cs` |

## MyWork — semantics proven from `BL/Platform/TaskScopeQuery.cs`

There is no separate "MyWork" service. It is `TaskScopeQuery.WithinScope(scope, callerEmployeeId)`
with an `Own` breadth.

| Question | Answer | Evidence |
|---|---|---|
| How is "mine" resolved? | `AssigneeEmployeeId == me OR CreatedByEmployeeId == me` | line 74 |
| Employee-scoped? | yes — employee id, not user id | line 72 |
| If the employee cannot resolve? | **empty result, not unfiltered** — `Where(_ => false)` | line 71 |
| Empty scope? | `Where(_ => false)` — "an empty scope yields an empty RESULT, not an unfiltered query" | lines 37–40 |
| Subordinate work? | not in `Own`; that is `Team` breadth (lines 50–64) | |
| Generated tasks? | **yes** — the predicate is over `TaskItems`, regardless of creator door | |
| Completed tasks? | not excluded by scope; status filtering is the caller's | |
| Due/overdue? | derived, `TaskLifecycle.IsOverdue` (phase4) | |

Two refusals are notable and correct: `Branch` breadth **throws** rather than guessing a predicate
(line 86), and `CrossCompany` returns empty (line 100).

## Attention

**Attention is composition, not a store.** No `Attention` entity, table or persistence was found. It
is assembled per request from:

| Source | Service |
|---|---|
| Tasks | `TaskScopeQuery` over `TaskItems` |
| Calendar events | `WorkspaceAgendaService` |
| Notifications | `INotificationService` |
| Mentions | `CommMentionService` |
| Approvals | `Approvals/ApprovalInboxService.cs` (read-only over three mechanisms) |

**Duplication risk:** the same overdue task can surface in MyWork, Agenda and the notification list
simultaneously, each computed independently. Before phase4's `TaskLifecycle`, the overdue rule was
written out by hand in seven places — `TaskLifecycle.cs` header names `WorkspaceAgendaService` as
carrying a comment about that duplication *"rather than being able to call it"*. Phase 4 fixes the
rule's definition; it does not de-duplicate the surfaces.

## Calendar / Agenda data sources

`WorkspaceAgendaService.AgendaItemType` has exactly **two** members:

```csharp
public enum AgendaItemType { Task = 0, CalendarEvent = 1 }
```

| Source | In agenda? |
|---|---|
| Task due dates | ✅ |
| Calendar events / meetings | ✅ |
| Approvals | ❌ |
| Documents / expiry | ❌ |
| Projects | ❌ |

Each item carries `SourceModule`, `EntityCode`, `SourceId` and `DeepLink`, plus `IsOverdue`,
`IsCompleted` and `IsRedacted` — so duplicate prevention and completed/overdue rendering are handled
per item, and redaction is a first-class state rather than an omission.
