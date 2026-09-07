# 06 — Business Object Catalog

## Scope
Every significant business object in the system — **58 catalogued** — not only the 9 currently registered in
`IEntityRegistry`. Each row records what actually exists: table, keys, isolation fields, real status model, real
transitions, route, controller, service and permission scope.

## Evidence
`evidence/Business-Object-Capabilities.csv` (complete, 58 rows, machine-generated) ·
`evidence/Entity-Inventory.csv` (261 classes) · `evidence/DbSet-Inventory.csv` (218 DbSets) ·
`BL/Platform/EntityRegistry.cs` · per-entity service inspection.

## Registered vs catalogued

`IEntityRegistry` currently freezes **9** codes:
`SalesInvoice` · `PurchaseInvoice` · `Customer` · `Supplier` · `ManufWorkOrder` · `PosOrder` · `Employee` ·
`Project` · `Item`.

That is **9 of 58** — 15%. The catalogue below proposes canonical codes for the rest so the vocabulary can be
extended without re-litigating names.

## Catalogue

Legend: **Reg** = in `IEntityRegistry` · **TL** = `_DocEventTimeline` wired to a real screen ·
**Cm** = `_DocTimeline` (comments) wired · **Ev** = has BusinessEvent producers.

### Accounting / AR / AP

| Proposed code | Entity | PK | Company | Branch | Status model | Real transitions | Route | Service | Reg | TL | Cm | Ev |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `SalesInvoice` | `SalesInvoice` | ID | CompanyID | — | `Status` str: Draft/Posted/Cancelled | **Created→Posted at insert; Updated (reverse+repost)**. No approve. No cancel producer | `/Accounting/SalesInvoiceDetail?id=` | `ReceivableService` | ✅ | ✅ | ✅ | 2 |
| `PurchaseInvoice` | `PurchaseInvoice` | ID | CompanyID | — | `Status` str: Draft/Posted/Cancelled | **same shape as sales** | `/Accounting/PurchaseInvoiceDetail?id=` | `PayableService` | ✅ | ✅ | ✅ | 2 |
| `Customer` | `Customer` | ID | CompanyID | — | `IsActive` bool (**no Status column**) | Created; Updated (upsert). **No activate/deactivate op** | `/Accounting/CustomerStatement?id=` | `ReceivableService` | ✅ | ✅ | — | 2 |
| `Supplier` | `Vendor` | ID | CompanyID | — | `IsActive` bool | Create/Save | (none — list only) | `PayableService` | ✅ | — | — | — |
| `SalesReturn` | `SalesReturn` | ID | CompanyID | — | `Status` str Posted | Create; Edit (reverse+repost) | list only | `ReceivableService` | ❌ | — | — | — |
| `PurchaseReturn` | `PurchaseReturn` | ID | CompanyID | — | `Status` str Posted | Create; Edit | list only | `PayableService` | ❌ | — | — | — |
| `Receipt` | `Receipt` | ID | CompanyID | — | `Status` str Posted | Create (+allocations) | list only | `ReceivableService` | ❌ | — | — | — |
| `Payment` | `Payment` | ID | CompanyID | — | `Status` str Posted | Create (+WHT, allocations) | list only | `PayableService` | ❌ | — | — | — |
| `JournalEntry` | `JournalEntry` | ID | CompanyID | — | `Status` str: Draft/Posted/**Reversed** | **Create→Post; Reverse (mirror entry, sets Reversed + ReversedByEntryId)** | `/Accounting/Journals` | `JournalEntryService` | ❌ | — | — | — |
| `Account` | `Account` | ID | CompanyID | — | `IsActive` | CRUD | `/Accounting/ChartOfAccounts` | `ChartOfAccountsService` | ❌ | — | — | — |
| `CostCenter` | `CostCenter` | ID | CompanyID | — | `IsActive` | CRUD | setup screen | `CostCenterService` | ❌ | — | — | — |
| `FiscalPeriod` | `FiscalPeriod` | ID | **(none)** | — | `IsClosed` | Open/Close | setup | `FiscalPeriodService` | ❌ | — | — | — |
| `FixedAsset` | `FixedAsset` | ID | CompanyID | — | `Status` | Capitalize; Depreciate; Dispose | `/Accounting/FixedAssets` | `FixedAssetService` | ❌ | — | — | — |

### Inventory / Warehouses

| Proposed code | Entity | PK | Company | Branch | Status model | Real transitions | Route | Service | Reg | TL |
|---|---|---|---|---|---|---|---|---|---|---|
| `Item` | `Item` | ID | CompanyID | — | `IsActive`, `ItemType` | CRUD | `/Inventory/EditItem?id=` | `ItemService` | ✅ | — |
| `Warehouse` | `Warehouse` | ID | CompanyID | BranchID | `IsActive`, `AllowNegativeStock` | CRUD | `/Inventory/Warehouses` | `WarehouseService` | ❌ | — |
| `StockMovement` | `StockMovement` | ID | CompanyID | — | `Direction` ±1 (immutable ledger) | **Post only — never updated** | (no screen; ledger) | `StockService` | ❌ | — |
| `PurchaseOrder` | `PurchaseOrder` | ID | CompanyID | — | `Status` str | Create; **approval-gated by amount**; Convert→invoice | `/Inventory/PurchaseOrders` | `ProcurementService` | ❌ | — |
| `SalesOrder` | `SalesOrder` | ID | CompanyID | — | `Status` str | Create; Convert→invoice | `/Inventory/SalesOrders` | `SellingService` | ❌ | — |
| `GoodsReceipt` | `GoodsReceipt` | ID | CompanyID | — | `Status` str | Receive (→stock in) | `/Inventory/GoodsReceipts` | `ProcurementService` | ❌ | — |
| `Delivery` | `DeliveryNote` | ID | CompanyID | — | `Status` str | Post (→stock out) | `/Inventory/Deliveries` | `SellingService` | ❌ | — |
| `Quotation` | `Quotation` | ID | CompanyID | — | `Status` str | Create; Convert | `/Inventory/QuotationDetails?id=` | `SellingService` | ❌ | — |
| `StockCount` | `StockCount` | ID | CompanyID | — | `Status` str | Count; **approval-gated**; Post adjustment | `/Inventory/StockCounts` | `StockService` | ❌ | — |
| `StockTransfer` | `StockTransfer` | ID | CompanyID | — | `Status` str | Transfer; **approval-gated** | `/Inventory/Transfers` | `StockService` | ❌ | — |

**`Quotation` is comments-wired (`_DocTimeline` on `QuotationDetails.cshtml`) but is NOT in `IEntityRegistry`** —
it therefore stores a **free-text `EntityType`** in `DocComments`. This is the one surviving instance of the
exact problem ADR-002 exists to prevent. **Finding — Inconsistency, see 19.**

### Manufacturing

| Proposed code | Entity | PK | Company | Status model | Real transitions | Route | Service | Reg | TL | Ev |
|---|---|---|---|---|---|---|---|---|---|---|---|
| `ManufWorkOrder` | `ManufWorkOrder` | ID | CompanyID | `Status`: **Draft\|Released\|Completed\|Cancelled** | **the richest real lifecycle in the system** (see 09) | `/Inventory/WorkOrderDetails?id=` | `ManufService` | ✅ | ✅ | 6 |
| `ManufPlan` | `ManufPlan` | ID | CompanyID | `Status`: Draft/Generated | Create; RunMrp; GenerateWorkOrders | `/Inventory/ManufPlans` | `ManufService` | ❌ | — | — |
| `ManufWorkCenter` | `ManufWorkCenter` | ID | CompanyID | — | CRUD | setup | `ManufService` | ❌ | — | — |
| `ManufRoutingOp` | `ManufRoutingOp` | ID | CompanyID | — | CRUD | setup | `ManufService` | ❌ | — | — |

### POS

| Proposed code | Entity | PK | Company | Branch | Status model | Real transitions | Service | Reg |
|---|---|---|---|---|---|---|---|---|
| `PosOrder` | `PosOrder` | ID | **CompanyId** (lowercase d) | BranchId | `Status`, `KdsStatus` | Open→Send kitchen→Settle→Void/Refund | `PosOrderService` | ✅ |
| `PosShift` | `PosShift` | ID | CompanyId | — | `Status`: Open/Closed | Open; Close (+variance JE) | `PosSetupService` | ❌ |
| `PosTerminal` | `PosTerminal` | ID | CompanyId | BranchId | `IsActive` | CRUD | `PosSetupService` | ❌ |

**Casing inconsistency:** POS entities use `CompanyId`, accounting/inventory use `CompanyID`. Any generic
company filter must handle both (`EntityRegistry.SearchAsync` already special-cases `PosOrder`).

### HR

| Proposed code | Entity | PK | Company | Status model | Real transitions | Route | Service | Reg |
|---|---|---|---|---|---|---|---|---|
| `Employee` | `Employee` | ID | **EmpCompanyID** (int, non-null) | `IsActive` | CRUD + auto Identity user on hire | `/Admin/EmployeesList` (list only) | `EmployeeService` | ✅ |
| `LeaveRequest` | `LeaveRequest` | ID | **(none)** | **`Status` int 0/1/2** | **Submit→(chain)→Approve/Reject** — silo 1 | `/People/Leaves` | `LeaveWorkflowService` | ❌ |
| `EmployeeRequest` | `EmployeeRequest` | ID | (see CSV) | **`Status` int 0/1/2** | **Submit→(chain)→Approve/Reject** — silo 2 | `/People/Requests` | `EmployeeRequestService` | ❌ |
| `Payslip` | `Payslip` | ID | (see CSV) | `Status` | Generate; Disburse (→JE) | `/Admin/Payroll` | `EmployeeService`/payroll | ❌ |
| `Appraisal` | `Appraisal` | ID | — | `Status` | Submit; Acknowledge | `/Admin/Appraisals` | `AppraisalService` | ❌ |
| `JobApplication` | `JobApplication` | ID | CompanyID | `Stage` (Kanban) | Stage moves; **Hire → creates Employee + Identity login** | `/Admin/Applications` | `RecruitmentService` | ❌ |
| `TrainingCourse` | `TrainingCourse` | ID | CompanyID | `Status` | CRUD; enroll | `/Admin/TrainingCourses` | `TrainingService` | ❌ |
| `AttendanceRecord` | `AttendanceRecord` | ID | (see CSV) | — | Check-in/out | `/People/Attendance` | `AttendanceService` | ❌ |
| `EmploymentContract` | `EmploymentContract` | ID | (see CSV) | `Status` | CRUD; expiry alerts | `/Admin/Contracts` | `EmployeeService` | ❌ |
| `FinalSettlement` | `FinalSettlement` | ID | (see CSV) | `Status` | Compute; Post (→JE) | `/Admin/FinalSettlement` | `FinalSettlementService` | ❌ |

**`LeaveRequest` has no `CompanyID`.** Company is reached only through `Employee.EmpCompanyID`. Same for several
HR entities — see 08 isolation report.

### Projects · Tasks · CRM · Communication · Platform

| Proposed code | Entity | Company | Status model | Real transitions | Service | Reg |
|---|---|---|---|---|---|---|
| `Project` | `Project` | CompanyID | `Status` | CRUD; budget; progress | `ProjectService` | ✅ |
| `ProjectContract` | `Contract`* | CompanyID | `Status` | Create; advance/retention | `ContractService` | ❌ |
| `ProgressBilling` | `ProgressBilling` | CompanyID | `Status` | Create→Post (→AR + JE) | `ProgressBillingService` | ❌ |
| `Subcontract` | `Subcontract` | CompanyID | `Status` | Create; bill; retention release | `SubcontractBillingService` | ❌ |
| `VariationOrder` | `VariationOrder` | CompanyID | `Status` | Create; approve-in-place | `VariationOrderService` | ❌ |
| `TaskItem` | `TaskItem` | CompanyID | `Status`: New/InProgress/Done | **Create; Edit; ChangeStatus (guarded); timer; billing** — polymorphic `EntityType`+`EntityId` link | `TaskService` | ❌ |
| `TimesheetEntry` | `TimesheetEntry` | CompanyID | — | Add/Delete; post to WO | `TimesheetService` | ❌ |
| `CrmLead` | `Lead` | CompanyID | `Status`/`Stage` | Create; score; **Convert→Opportunity** | `CrmService` | ❌ |
| `CrmOpportunity` | `Opportunity` | CompanyID | `Stage` | Stage change (fires automation rules); **Convert→Quotation** | `CrmService` | ❌ |
| `CrmAccount` | `CrmAccount` | CompanyID | — | CRUD; link to `Customer` | `CrmService` | ❌ |
| `CrmTicket` | `Ticket` | CompanyID | `Status` | Open→Resolve | `CrmService` | ❌ |
| `Activity` | `Activity` | CompanyID | `Done` bool | Create; complete; reminder | `CrmService` | ❌ |
| `Notification` | `Notification` | CompanyID? | `IsRead` | Create; read; mute | `NotificationService` | ❌ |
| `Conversation` / `ChatMessage` | Chat.* | CompanyID | soft-delete | Send; edit; delete; react; read | `ChatService` | ❌ |
| `CommMessage` | `CommMessage` | CompanyID | `Status`: Queued/Sent/Failed | Queue→Send (SMTP)→Failed+retry | `CommService` | ❌ |
| `Announcement` | `Announcement` | CompanyID | — | Publish; mark read | `AnnouncementService` | ❌ |
| `CalendarEvent` | `CalendarEvent` | CompanyID | `Scope` | CRUD; attendees | `CalendarService` | ❌ |
| `LibraryItem` | `LibraryItem` | CompanyID | soft-delete | Folder/file CRUD; upload | `FileManagerService` | ❌ |
| `DocComment` | `DocComment` | CompanyID | soft-delete | Add; delete; mention | `DocCommentService` | ❌ |
| `BusinessEvent` | `BusinessEvent` | CompanyID | `CompletedAt` | **append-only** | `BusinessEventService` | (kernel) |

\* `Contract` class name per `evidence/Entity-Inventory.csv`; the DbSet is `ProjectContracts`.

## Aliases and conflicting names

| Concept | Names in use | Where |
|---|---|---|
| Supplier | `Vendor` (entity/DbSet), `Supplier` (registry code, TM-2 party type) | `EntityRegistry.Supplier` → `_db.Vendors` |
| Purchase invoice | `PurchaseInvoice` (entity/code), `purchase_invoice` (notification type), `PurchaseInvoice` (JE SourceType), `PurchaseInvoiceEdit` (stock SourceType) | 4 vocabularies, 3 casings |
| Work order | `ManufWorkOrder` (entity/code), `WorkOrder` (JE SourceType), `WO-#####` (document number) | `SourceType="WorkOrder"` |
| Company field | `CompanyID` · `CompanyId` · `EmpCompanyID` | Accounting/Inventory · POS/Tasks · Employee |
| Timeline | `_DocTimeline`=comments, `_DocEventTimeline`=events, `_Timeline`=CRM activities | 3 components |
| Status type | `string` (Accounting/Inventory/POS) vs `int` 0/1/2 (Leave, EmployeeRequest, approval steps) | mixed |

## Status-model inconsistency (Finding)

**107 of 261 entity classes carry a status field**, in at least four different shapes:
1. `string Status` with a documented value list (accounting, inventory, POS, manufacturing) — dominant.
2. `int Status` 0/1/2 (`LeaveRequest`, `EmployeeRequest`, `LeaveApprovalStep`, `EmployeeRequestStep`).
3. `bool IsActive` (master data: `Customer`, `Vendor`, `Item`, `Account`, `Employee`).
4. `bool Done` (`Activity`), `bool IsClosed` (`FiscalPeriod`), `Stage` (`JobApplication`, `Opportunity`).

A generic workflow engine must handle all four, or the objects must be normalised first. **This is the single
biggest input to the Workflow Engine design** — see 14 and 21.

## Gaps
- 49 of 58 objects have no canonical registry code, so nothing generic can address them.
- No object has Files, Followers, Relations, Tags or AI Context (see 07).
- `FiscalPeriod`, `LeaveRequest` and 76 other classes lack a company field (see 08).

## Risks
- Free-text `EntityType` is still reachable (`Quotation` in `DocComments`, `TaskItem` links, `Activity` links).
- Four status shapes make cross-module status reporting impossible without per-entity code.

## Dependencies
07 (capabilities), 08 (isolation), 09 (registry), 14 (status models feed the engine).

## Recommendations
1. Register `Quotation` next — it already stores comments under a free-text type.
2. Freeze canonical codes for the remaining 49 objects in one pass (names only; no behaviour change).
3. Decide the status-normalisation policy **before** the Workflow Engine, not during it.
