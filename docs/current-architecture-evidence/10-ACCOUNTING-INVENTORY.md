# 10 — Accounting and Inventory

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


## Accounting

| Area | Evidence |
|---|---|
| Journal entries | `BL/JournalEntryService.cs` |
| Period control | `BL/AccountingPeriodControlService.cs` |
| Period statuses | `Models/Context/Accounting/AccountingFoundation.cs:112` — `BlocksPosting` |
| Receivables / payables | `BL/ReceivableService.cs`, `BL/PayableService.cs` |
| Procurement | `BL/ProcurementService.cs` |
| Progress billing | `BL/ProgressBillingService.cs` (raises events) |
| Access | `BL/AccountingAccessService.cs` — consumes `IBootstrapAccessPolicyReader` |
| Datasets | `BL/Reporting/AccountingDatasets.cs` |
| Views | 66 |

### Period close authority — confirmed

`AccountingPeriodStatuses.BlocksPosting(status)` is **the** authority, and it is consulted at three
independent posting sites:

| Caller | Line |
|---|---|
| `AccountingPeriodControlService.cs` | 418 |
| `JournalEntryService.cs` | 268 |
| `StockService.cs` | 200 |

`JournalEntryService.cs:264` records why it is a function and not a literal: *"BlocksPosting, not a
literal: SoftClosed was accepted by FiscalPeriodService.SetStatusAsync"* — i.e. a literal comparison
missed a status the setter allowed. `TaskLifecycle.cs:7` names this as its own model: *"One definition
of what a task's state means; many enforcement points, exactly as
AccountingPeriodStatuses.BlocksPosting is for the ledger."*

**Stock posting respects the accounting period** (`StockService.cs:200`) — a real cross-module
invariant, enforced by shared call rather than by event.

### Work items that are not Tasks

Accounting exception flows that plainly represent work, and which create no `TaskItem`:

- a period that cannot close because entries are unposted;
- an unreconciled or failed posting;
- an overdue receivable or payable.

No Accounting service injects `ITaskService`. Recorded as **F-08**.

## Inventory

| Area | Evidence |
|---|---|
| Items / warehouses / stock | `BL/StockService.cs`, `Models/Context/Inventory/Inventory.cs` |
| Approvals | `Models/Context/Inventory/Inventory.cs:558` `InventoryApproval`, `BL/InventoryApprovalService.cs` |
| Quotations | `Quotation` — a company-filtered pilot entity (`CompanyQueryFilters.cs:131`) |
| Datasets | `BL/Reporting/InventoryDatasets.cs` |
| Views | 79 — the largest surface, includes manufacturing screens |
| API | `Controllers/Api/InventoryApiController.cs` — carries `private const int CompanyId = 1` (line 17) |

### InventoryApproval company isolation

`InventoryApproval` is **not** in the 12-entity company-filter pilot. Isolation is by hand-written
predicate. `CrossBuy.Tests/ApprovalCompanyIsolationTests.cs` exists specifically to pin this and its
header records the original defect: visibility was gated on "does this employee hold InventoryManager"
with **no company predicate**, so a manager saw every company's pending approvals and a role granted in
one company unlocked the inbox in all of them. That is now covered by tests.

### Pilot entities (global query filter + write guard)

`BL/Platform/CompanyQueryFilters.cs` — 12 entities: `JournalEntry`, `SalesInvoice`, `PurchaseInvoice`,
`Customer`, `Item`, `Warehouse`, `Quotation`, `Lead`, `Opportunity`, `CrmAccount`, `BusinessEvent`,
`Notification`. Everything else relies on hand-written predicates.
