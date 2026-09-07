# 18 — Direct Coupling Map

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


**One assembly, one `DbContext`.** Every "module boundary" in this estate is a folder convention.
Nothing prevents any service from querying any table, so the couplings below are the ones that were
*taken*, not the ones that are *possible*.

| Source | Target | Direct call | Shared DbContext | Event | HTTP | Risk |
|---|---|---|---|---|---|---|
| Inventory (`StockService`) | Accounting | **yes** — `AccountingPeriodStatuses.BlocksPosting` (`StockService.cs:200`) | yes | no | no | **LOW** — a shared invariant, correctly expressed as one function with three callers |
| POS | Inventory | yes | yes | no | no | MEDIUM |
| POS | Accounting | yes — cash accounts pinned to company 1 | yes | no | no | MEDIUM |
| Manufacturing | Inventory | yes — lives inside `StockService` and the Inventory view folder | yes | partial (`ManufWorkOrder.*` declared) | no | MEDIUM |
| CRM | Notifications | **yes** — `CrmService`, `CrmAutomationService`, `CrmReminderHostedService` | yes | no | no | MEDIUM |
| HR | Notifications | yes — `LeaveWorkflowService`, `AppraisalService`, `EmployeeRequestService`, `HrDocumentService` | yes | no | no | MEDIUM |
| Accounting | Notifications | yes — `ReceivableService`, `PayableService`, `ProcurementService` | yes | no | no | MEDIUM |
| Inventory | Notifications | yes — `InventoryApprovalService` | yes | no | no | MEDIUM |
| Documents | Tasks | **direct table write** — `DocumentExpiryProjection.cs:196` | yes | no task event | no | **HIGH** |
| Tasks (generator) | Tasks | **direct table write** — `TaskGeneratorService.cs:139` | yes | no task event | no | **HIGH** |
| Orchestration | Tasks | **direct table write** — `TaskOrchestrationService.cs:168` | yes | no task event | no | **HIGH** |
| Documents | Orchestration | **event** — `DocumentVerificationRule` via outbox | — | **yes** | no | **LOW** — the only correct instance |
| Workspace | Tasks + Calendar + Notifications + Mentions + Approvals | yes, five services | yes | no | no | MEDIUM — a legitimate composition root, but it means Workspace breaks when any of five changes |
| AI Insights | Tasks | yes — `InsightActionsController` → `ITaskService` | yes | via TaskService | no | LOW *(out of scope per brief)* |

## Who injects `ITaskService`

Only: `TaskService` itself, `TaskChecklistAndTemplateService`, `WorkspaceAgendaService`,
`Workspace/WorkspaceService`, `Orchestration/TaskOrchestrationService`, `InsightActionsController`,
`TasksController`, and the two seeders.

**No business module injects `ITaskService`.** Accounting, Inventory, CRM, POS, Manufacturing, HR and
Projects create no tasks. That is *good* — modules are not reaching into Tasks — but it also means the
only module→work bridge in the estate is the single Document orchestration rule.

## Highest-risk couplings

1. **Three direct `TaskItems` writes** that skip the canonical writer and its event.
2. **`INotificationService` injected by ~18 services.** Notification is effectively a synchronous
   cross-cutting call, so a module cannot be deployed, tested or reasoned about without it, and no
   record of the notification reaches the outbox.
3. **One `DbContext`.** The write guard and query filters cover 12 entities; the other ~94 are guarded
   only by hand-written predicates.
