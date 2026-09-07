# 09 — Documents and Approvals

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


## Documents

| Piece | File |
|---|---|
| Service | `BL/Documents/PlatformDocumentService.cs` |
| Events | `BL/Documents/DocumentEvents.cs` |
| Expiry projection | `BL/Documents/DocumentExpiryProjection.cs` |
| Worker | `BL/Documents/DocumentExpiryHostedService.cs` |
| Controller / view | `Controllers/DocumentsController.cs`, **one** view |

Documents is one of the few modules that **produces business events** (`DocumentEvents.cs`,
`PlatformDocumentService.cs`, `DocumentExpiryProjection.cs` all reference `IBusinessEventService`).

### Expiry → task

`DocumentExpiryProjection.cs:196` writes `_db.TaskItems.Add(task)` **directly**. It carries the
strongest idempotency of any door (11 idempotency-shaped references) and uses `TaskAutoLog`, but:

- it does **not** use the lifecycle constants;
- it does **not** company-check the assignee (`DefaultAssigneeEmployeeId` is used unverified);
- it raises **no task business event**.

### Orchestrator or specialised?

**Both, and they are different paths.** Phase 3 added `DocumentVerificationRule` — the only
orchestration rule — which handles *document verification* through the outbox. Document **expiry**
remains a specialised projection with its own worker and its own direct write. They coexist; expiry
was not migrated onto the orchestrator.

## Approvals — three mechanisms, one read-only inbox

| Mechanism | Entity | Service |
|---|---|---|
| Inventory approvals | `Models/Context/Inventory/Inventory.cs:558` `InventoryApproval` | `BL/InventoryApprovalService.cs` |
| Leave approvals | `Models/Context/Admin/LeaveApprovalStep.cs:8` | `BL/LeaveWorkflowService.cs` |
| Employee requests | (entity in Admin context) | `BL/EmployeeRequestService.cs` |
| **Unifying read** | — | `BL/Approvals/ApprovalInboxService.cs` + `ApprovalReadContracts.cs` |

`ApprovalInboxService` is a **read-only** union. There is no shared state machine, no shared approver
resolution, and no shared audit.

| Question | Answer |
|---|---|
| Do approvals produce business events? | **No.** No `Approval.*` family in `BusinessEventTypes.cs` |
| Do approvals create Tasks? | **No.** No approval service injects `ITaskService` |
| More than one framework? | **Yes — three**, plus `AiEgressApproval` which is unrelated governance |
| How is the approver determined? | per mechanism; Inventory by `InventoryUserRole`, Leave by `LeaveApprovalStep` chain |
| What happens after approve/reject? | mechanism-specific; notification via direct `INotificationService` call, no event |

Approvals are therefore **invisible to the event backbone and to Task management**. A pending approval
is work, and nothing in the platform treats it as such.
