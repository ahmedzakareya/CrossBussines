# 16 — Background Workers

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


**Seven hosted services.** Identical set on `master` and `tasks/phase4` — Phase 3–4 added a *consumer*,
not a worker.

| # | Worker | Module | Writes | Company scope | Notes |
|---|---|---|---|---|---|
| 1 | `Platform/BusinessEventDispatchWorker` | Platform | `BusinessEventDispatch` | per-company | drains the outbox; per-consumer dispatch state gives per-consumer retry |
| 2 | `TasksCalendar/TaskEscalationHostedService` | Tasks | notifications, `Task.Escalated` events | assignee's `EmpCompanyID` | grace 24h, max level 2, 500/sweep |
| 3 | `TaskGeneratorHostedService` | Tasks | **`TaskItems`** | company predicate | recurring rules; **raises no task event** |
| 4 | `TaskScheduleMatchHostedService` | Tasks | `TaskMatchSuggestion` | company predicate | matches scheduled tasks to documents |
| 5 | `Documents/DocumentExpiryHostedService` | Documents | **`TaskItems`** | company predicate | **raises no task event**; no assignee company check |
| 6 | `CrmReminderHostedService` | CRM | notifications | `WorkerCompanyRunner` per company | repaired from `CompanyId = 1`; `IWorkerGate` prevents duplicate dispatch on multi-instance |
| 7 | `IntegrityCheckHostedService` | Platform | integrity findings, events | per-company | |

## Coordination

`IWorkerGate` is the estate's single worker-coordination mechanism —
`CrmReminderHostedService` uses it with the comment *"this worker is NOT safe to run in two processes
at once — it would duplicate the records it creates. Only the worker-primary instance proceeds."*

`WorkerCompanyRunner.ForEachCompanyAsync` + `WorkerScope.ForCompany` is the canonical per-company
iteration, so query filters read a resolved company rather than nothing.

**Not every worker demonstrably uses both.** `IWorkerGate` adoption was verified for
`CrmReminderHostedService`; the other six were not individually confirmed in this pack. Recorded as
**F-09** — an audit item, not an asserted defect.

## Overlap and duplication

| Observation | Detail |
|---|---|
| **Two workers create tasks** | #3 generator and #5 document expiry both write `TaskItems` directly, by different rules, with different validation |
| **Three overdue-adjacent paths** | `TaskEscalationHostedService`, `TaskOverdueSweepService` and the agenda's own overdue computation. Phase 4's `TaskLifecycle` unifies the *rule*; the *sweeps* remain separate |
| **No unowned workers found** | all seven map to a module folder |
| **No comm delivery worker** | `CommNotificationDispatcher` is a drain with no driver — see file 13 |
