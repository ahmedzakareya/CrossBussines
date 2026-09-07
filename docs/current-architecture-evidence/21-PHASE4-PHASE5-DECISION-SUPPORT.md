# 21 — Phase-4 / Phase-5 Decision Support

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


## Q1. Is the planned Phase-4 Work Policy + Management Control architecture aligned with existing code?

**PARTIALLY — and the reason is unusual: much of it is already built.**

`tasks/phase4` (7c6fcea) already contains `TaskWorkPolicy`, `TaskWorkPolicyResolver`,
`TaskEscalationTiming` and `TaskManagementQueryService`, all DI-registered. The design is not awaiting
implementation; it is awaiting **integration and a UI**.

Aligned:
- Work policy sits behind `ITaskWorkPolicyResolver` and the orchestration pilot already consumes it,
  with commit `a56f255` stating the projection "behaves identically" — so the policy was introduced
  without changing behaviour, which is the correct way to land it.
- Escalation timing is addressable (platform default + per-company override) without inventing policy.
- Management queries are derived from the canonical lifecycle and refuse to invent capacity metrics.

Not aligned:
- `TaskManagementQueryService` has **no consumer** — no controller, view or report (F-10).
- Management control's most valuable questions ("which module creates most work", "which came from
  automation") cannot be answered because automated tasks raise no events (F-02) and `TaskItem`
  carries no source discriminator.
- None of it is on `master` (F-01).

## Q2. Should Phase 4 continue as designed, or be revised?

**REVISE — and the revision is about sequencing, not design.**

The design is sound and largely built. What is missing is the part that makes it usable and safe:

1. **Merge before extending.** 37 unmerged commits is the dominant risk in this repository. Every
   additional Phase-4 commit widens a branch that no other tab can see, against a `master` that six
   tabs are actively changing.
2. **Close F-02 before building management control on top of it.** A management layer that aggregates
   work while a third of the creation doors are invisible to the event backbone will produce numbers
   that are quietly wrong — the worst possible failure mode for a control surface.
3. **Then** wire `TaskManagementQueryService` to a surface.

## Q3. What should Phase 4 REUSE rather than recreate?

| Reuse | Why |
|---|---|
| `TaskLifecycle` | already the single definition of states, transitions and overdue |
| `TaskScopeQuery` | the one place an `AccessScope` becomes a task predicate; already refuses Branch and CrossCompany correctly |
| `IOrgHierarchy` | cycle-guarded, hop-bounded, already trusted by four modules |
| `BusinessEvent` + `BusinessEventDispatch` | a real per-consumer outbox with retry — do not add a second bus |
| `IWorkerGate` + `WorkerCompanyRunner` | the estate's worker coordination; a second one would be a defect |
| `IBootstrapAccessPolicyReader` | the canonical "module not configured yet" answer |
| `TaskAutoLog` + `RuleKey` | working idempotency for automated work |
| Report Studio (`ReportDatasetRegistry`) | add a dataset, do not build a reporting stack |
| `CommThread` `(CompanyID, EntityType, EntityId, …)` | any entity can carry a thread with no schema change |
| `AccountingPeriodStatuses.BlocksPosting` | the pattern `TaskLifecycle` already copies |

## Q4. What would Phase 4 accidentally duplicate?

| Risk | Existing thing |
|---|---|
| A new "work item" table | `TaskItem` |
| A new overdue definition | `TaskLifecycle.IsOverdue` / `OverdueAt` — the seven-copy problem was just fixed |
| A new escalation cadence store | `TaskEscalationTiming` (config, two layers) |
| A new notification path | `INotificationService` + `NotificationProjectionConsumer` |
| A new event bus or dispatcher | `BusinessEventDispatchWorker` |
| A new worker coordinator | `IWorkerGate` |
| A new reporting engine | 42 files under `BL/Reporting/` |
| A new approval framework | three already exist; adding a fourth is the trap |
| A new manager-resolution walk | `IOrgHierarchy.DirectManagerAsync` |
| A "MyWork" service | `TaskScopeQuery.WithinScope(Own)` |

## Q5. Product-owner decisions required before Phase 5

1. **Escalation cadence.** Confirm or replace 24h grace / 24h advance / max level 2. Currently
   unowned technical defaults (F-16).
2. **`Task.Cancelled`.** Add the state, or formally retire the reserved contract (F-15).
3. **SLA model.** Is there a task SLA distinct from escalation? Nothing exists today (question 11).
4. **Capacity.** `WorkloadAsync` deliberately refuses to compute utilisation because no hours budget
   or availability model exists. Is one wanted?
5. **Are approvals work?** If a pending approval should appear in MyWork, that is a product decision
   with real architectural consequence (F-13).
6. **POS catalog tenancy.** Company-1 catalog with branches as companies 65–79 is deliberate; confirm
   how POS-originated work should be attributed.
7. **Unassigned work.** `UnassignedAsync` exists because work can be ownerless. What is the policy —
   is unassigned acceptable, or must everything have an owner?

## Q6. Modules technically ready for Task rollout now

| Module | Ready? | Why |
|---|---|---|
| **Documents** | **YES** | already produces events and already has the only orchestration rule |
| **Projects** | **YES** | no competing task concept (F-19), has an access service; needs an event family |
| **Calendar** | YES | produces 9 event types, already in the agenda |
| **Tasks** | n/a | is the subject |
| Communication | partial | produces events; delivery worker missing (F-06) |
| HR | partial | attendance produces events; leave/requests do not |
| CRM | **NO** | no events, no entity codes (F-14) |
| Accounting | **NO** | producers not proven (F-07) |
| Inventory | **NO** | producers not proven (F-07) |
| POS | **NO** | one producer; company-1 catalog complicates attribution |
| Manufacturing | **NO** | six event types declared, producer not proven (F-07) |

## Q7. Modules blocked because they do not publish a committed Business Event

**CRM** (no family at all), **Approvals** (no family at all), **HR leave/requests** (notify directly),
and — pending verification of F-07 — **Accounting**, **Inventory** and **Manufacturing**, whose
families are declared but whose producers were not located.

## Q8. Modules that already have their own task/work concept

| Module | Own concept? |
|---|---|
| Projects | **No** — only `ProjectActivityType`, an accounting dimension |
| CRM | **Yes, partially** — `Activity` (Call/Meeting/Email/Task) with `DueDate`, `Done`, `ReminderAt` and its own reminder worker. This is a genuine parallel work concept and the one real collision in the estate |
| HR | **Yes, partially** — `LeaveApprovalStep` and employee requests are work items with their own state |
| Inventory | **Yes, partially** — `InventoryApproval` is a pending work item |
| POS | No |
| Manufacturing | work orders are production objects, not assignable work |

CRM `Activity` is the collision to plan for: it duplicates due-date, done-state and reminder semantics
that `TaskItem` also has, and it has its own hosted service.

## Q9. Is Reporting ready to consume Task/management data without schema changes?

**NO.** Report Studio has five dataset providers and none for Tasks, and
`TaskManagementQueryService` has no consumer. Both are additive code in existing frameworks — **no DDL
required** for the six answerable questions. Two questions ("which module creates most work", "which
came from which module") **do** require a source discriminator on `TaskItem`.

## Q10. The single most important architectural gap to close next

**F-02 — automated task creation is invisible to the event backbone.**

Not F-01, which is bigger operationally but is a merge, not an architecture decision. F-02 is the one
that silently corrupts everything Phase 4 and Phase 5 are for: three of four creation doors write
`TaskItem` rows that never produce a `Task.Created` event, so the timeline is incomplete, no creation
notification fires, and any management or reporting layer built on the outbox under-counts exactly the
automated work that automation was introduced to make visible.

The direction — not the implementation — is that **the event should be a property of the row being
written, not of the door that writes it**. Whether that is achieved by routing the three doors through
`TaskService.SaveAsync`, or by moving event emission to a save interceptor, is an implementation
choice this pack does not make.
