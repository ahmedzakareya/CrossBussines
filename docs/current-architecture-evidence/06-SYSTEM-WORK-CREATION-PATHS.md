# 06 — System Work: every door that creates a TaskItem

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


## The doors

Excluding `DevSeedController` (dev seeding, 7 sites, not production) and `UatDatasetSeeder`:

| # | Door | File:line | Class |
|---|---|---|---|
| 1 | Canonical writer | `BL/TaskService.cs:338` | **CANONICAL** |
| 2 | Recurring generator | `BL/TaskGeneratorService.cs:139` | GENERATOR |
| 3 | Document expiry | `BL/Documents/DocumentExpiryProjection.cs:196` | DOCUMENT EXPIRY |
| 4 | Event orchestration | `BL/Platform/Orchestration/TaskOrchestrationService.cs:168` | ORCHESTRATION *(phase3/4 only)* |

Door 1 is reached by `TasksController` and by
`TasksCalendar/TaskChecklistAndTemplateService.cs:333` (templates/checklists). Doors 2–4 call
`_db.TaskItems.Add(...)` directly.

On `master` there are **three** doors (1, 2, 3). Phase 3 adds the fourth.

## What each door validates

Measured by grep over each file at `7c6fcea`:

| Door | Lifecycle constants | Assignee company check | Company predicate | `TaskAutoLog` | Business event | Idempotency |
|---|---|---|---|---|---|---|
| **TaskService** | ✅ 7 | ✅ 2 | ✅ 9 | — n/a | ✅ **11** | ✅ 2 |
| **Generator** | ❌ 0 | ✅ 1 | ✅ 11 | ✅ 3 | ❌ **0** | ✅ 3 |
| **DocExpiry** | ❌ 0 | ❌ **0** | ✅ 3 | ✅ 2 | ❌ **0**¹ | ✅ 11 |
| **Orchestration** | ✅ 2 | ✅ 1 | ✅ 3 | ✅ 4 | ❌ **0** | ✅ 8 |

¹ `DocumentExpiryProjection` holds an optional `IBusinessEventService? _events` (lines 81, 85) for
*document* events; it raises no **task** event.

## The finding that matters

`TaskService.cs:345` is the **only** call site of `TaskCreatedAsync` in the repository:

```
CrossBuy/BL/TaskService.cs:345:   await _events.TaskCreatedAsync(nt, currentEmployeeId, newCorrelation);
```

None of the three automated doors injects `ITaskCalendarEventPublisher` or `ITaskNotificationService`
(verified: 0 references in each of the three files).

**Consequence.** A task created by the generator, by document expiry, or by orchestration:

- raises no `Task.Created` business event;
- therefore produces no dispatch rows;
- therefore never reaches `TimelineProjectionConsumer` → **no timeline entry**;
- therefore never reaches `NotificationProjectionConsumer` → **no creation notification**;
- therefore never reaches `AiProjectionConsumer`.

It *does* appear in MyWork, Workspace, Calendar and escalation, because those read `TaskItems`
directly. So automated work is **visible in the UI but absent from the event backbone** — which is
exactly the data a Phase-4 management-control layer would want to aggregate.

## Verdict

**FRAGMENTED.**

Three of four production doors bypass the canonical writer. They are not reckless — each carries
idempotency, most carry a company predicate, and the orchestration door is the strictest of the three.
But they disagree on which invariants they enforce (lifecycle constants: 2 of 4; assignee company
check: 3 of 4; task event: **1 of 4**), and there is no single place that guarantees any of them.

This is *not* "multiple safe specialized doors": safe specialisation would mean each door enforces the
same invariants by a different route. Here the invariant set differs per door, and the difference is
undocumented at the call sites.
