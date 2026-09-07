# Stage-Construction-01 — Live Repository Inventory

**Method:** read the live implementation, not the class names. For every service below, the *write path* was read —
what it posts, through which writer, in what transaction, with what guard. Counts in this document were produced by
command and are reproduced in `Stage-Construction-Final-Delivery-Report.md` §Verification.

Root inspected: `c:\Users\Lenovo\Desktop\CrossBuy\CrossBuy` (branch `master`).
Note on paths: application code lives under `CrossBuy/`; **construction SQL lives in the repository-root
`deploy/sql/`**, not in `CrossBuy/deploy/sql/` (which holds 60 other scripts).

---

## 1. Entities and DbSets

### 1.1 Construction-relevant entities that exist

| Entity | File | Notable |
|---|---|---|
| `Project` | `CrossBuy/Models/Context/Accounting/Dimensions.cs:23` | An **accounting dimension**. Base: Code, Name, NameEn, IsActive, StartDate, EndDate, Budget, CostCenterId. Contracting additions (all nullable): CustomerId, Location, ContractValue, Status, ActivityTypeId, AdvancePercent, RetentionPercent. **No** ParentId, SiteId, BranchID, CurrencyId, ContractNo. |
| `ProjectActivityType` | `Dimensions.cs:49` | User-defined lookup, starts empty — nothing hard-codes "construction". |
| `CostCenter` | `Dimensions.cs:7` | Mirrors the **org tree** (`SourceHierarchicalId` → `Hierarchicals`). An organisational rollup, **not** a construction cost code. |
| `ProjectMember` | `Models/Context/Accounting/ProjectMember.cs:16` | Employee↔project business relationship; roles Manager/Member/Observer. Carries a denormalised `CompanyID` deliberately (comment lines 20-22). First-tab owned. |
| `BoqItem` | `Models/Context/Accounting/Boq.cs:8` | ProjectId, ParentId (self-ref), SortOrder, Code, Description(+En), Unit (free text), Quantity, UnitPrice, four estimated cost buckets, `VariationOrderId`, CreatedAt. Computed: LineValue, EstimatedCost, Margin. **No** revision, status, source, WBS ref, cost-code ref, RowVersion. |
| `ProjectProgress` / `ProjectProgressLine` | `Models/Context/Accounting/ProjectProgress.cs` | Dated **cumulative** snapshot per BOQ leaf. Header snapshots OverallPercent + ExecutedValue. Status Draft/Confirmed. Line: BoqItemId (nullable = whole-project), CumulativeQty, ManualPercent. |
| `ProgressBilling` / `ProgressBillingLine` | `Models/Context/Accounting/ProgressBilling.cs` | The client certificate. GrossWork, Tax, Retention, AdvanceRecovery, NetDue, links to SalesInvoiceId + two receipt ids. Status Draft/Approved/Posted. Line: BoqItemId, CumulativeExecutedValue, PreviouslyBilledValue, PeriodValue. |
| `Subcontract` / `SubcontractBilling` | `Models/Context/Accounting/Subcontract.cs` | Subcontract: 9 fields (ProjectId, VendorId, Description, ContractValue, RetentionPercent, Status…). Billing: CumulativeWork, GrossWork, Tax, Retention, NetPayable, PurchaseInvoiceId, RetentionPaymentId. **No lines.** |
| `VariationOrder` / `VariationOrderLine` | `Models/Context/Accounting/VariationOrder.cs` | Status Draft/Approved. Line Kind = New \| Adjust; Adjust keeps `OldQuantity`/`OldUnitPrice`. |
| `ProjectMaterialIssue` / `…Line` | `Models/Context/Accounting/ProjectMaterialIssue.cs` | WarehouseId, Status Draft/Posted. Line: ItemId, Qty, **optional** BoqItemId, UnitCost/TotalCost snapshot, StockMovementId. |
| `EquipmentDepreciationAllocation` | `Models/Context/Accounting/EquipmentDepreciationAllocation.cs` | FixedAssetId, PeriodDate, optional Hours/Rate helpers, Amount, JournalEntryId. A **cost reclass**, not a usage log. |

### 1.2 Dimensions available on the cost chain

| Row type | Dimensions it can carry | Evidence |
|---|---|---|
| `JournalEntryLine` | `CostCenterId`, **`ProjectId`**, `EmployeeId`, `CurrencyId` | `Models/Context/Accounting/GeneralLedger.cs:43-46` |
| `StockMovement` | Warehouse, Bin, Batch, Serial, SourceType/SourceId/SourceLineId, JournalEntryId — **no ProjectId** | `Models/Context/Inventory/Inventory.cs:190-213` |
| `Warehouse` | `BranchHierarchicalId`, WarehouseType (Main/Transit/Quarantine/Scrap/Consignment/Virtual) — **no SiteId, no ProjectId** | `Inventory.cs:144-159` |
| `PurchaseOrder` | **`ProjectId`** (analytic, carried to the vendor invoice on convert), WarehouseId | `Inventory.cs:300-316` |
| `PurchaseOrderLine` | no project/BOQ/cost-code reference | `Inventory.cs:323` |
| `GoodsReceipt` | Warehouse, PO, Vendor, Invoice — **no ProjectId** | `Inventory.cs:339-357` |
| `SalesInvoice` / `PurchaseInvoice` | `ProjectId`; purchase side also `CostCenterId` | `Models/Context/Accounting/ReceivablesPayables.cs:66,162,181` |
| `TaskItem` | generic `EntityType`/`EntityId` (may be `"Project"`) — **no ProjectId column** | `Models/Context/Tasks/TaskItem.cs:24-25` |

**Consequence:** below project level, only *material issue* can be attributed (via `BoqItemId`). Labor, equipment,
purchases, transfers and write-offs cannot. `ProjectBudgetService` names this hole honestly:
`UnattributedActual = TotalActual − AttributedActual` (`BL/ProjectBudgetService.cs:28`).

### 1.3 DbSet registration

16 construction-relevant DbSets in `Models/Context/CrossDbContext.cs:421-473` and `:459-473`
(`ProjectMembers`, `CostCenters`, `Projects`, `ProjectActivityTypes`, `BoqItems`, `ProjectProgresses`,
`ProjectProgressLines`, `ProgressBillings`, `ProgressBillingLines`, `ProjectMaterialIssues`,
`ProjectMaterialIssueLines`, `Subcontracts`, `SubcontractBillings`, `VariationOrders`, `VariationOrderLines`,
`EquipmentDepreciationAllocations`).

---

## 2. Services — and what each one actually writes

| Service | Lines | Writes | Through | Notes read from the code |
|---|---|---|---|---|
| `BL/BoqService.cs` | 151 | `BoqItems` | own EF | `ReplaceAllAsync` **deletes all + re-inserts** (108-109), rebuilding hierarchy from row order (128-132). `DeleteItemAsync` checks only for child items (97) — not for referencing certificates. |
| `BL/ProgressService.cs` | 291 | `ProjectProgresses(+Lines)` | own EF | Executed value **capped** at BOQ value (85); over-quantity only sets a UI flag (162). `ConfirmAsync` (256) sets status with **no current-status check**. |
| `BL/ProgressBillingService.cs` | 272 | `ProgressBillings(+Lines)`; posts money via `IReceivableService` | `ReceivableService` + `JournalEntryService` | Period = cumulative − previously **posted** (110). Retention → settlement receipt to 1104 (230-237); advance recovery → 2104, capped at balance (120). **No ambient transaction** across the three postings (202-258). Receipt ids recovered by `MAX(ID)` before/after (233-236, 242-245). |
| `BL/SubcontractBillingService.cs` | 185 | `Subcontracts`, `SubcontractBillings`; posts via `IPayableService` | `PayableService` | `Compute` (80) = cumulative − previouslyBilled, **no cap** vs ContractValue. Retention → payment to 2105 (165). Payment id by `MAX(ID)` (164-167). |
| `BL/ContractService.cs` | 164 | GL only | `JournalEntryService.CreateAndPostAsync` | Advance = Dr cash / Cr **2104 liability** (73-88). Retention release checks the GL balance first (104-105). Sub-retention on **2105**. Balances read from posted JE lines tagged ProjectId (52-58). **Correct design — reuse.** |
| `BL/VariationOrderService.cs` | 164 | `VariationOrders(+Lines)`, **and `BoqItems`** | own EF | `ApproveAsync` inserts New lines as BOQ items tagged `VariationOrderId` (136) and **overwrites Adjust items in place** (128). No journal entry — correct for an operational scope change. |
| `BL/ProjectMaterialIssueService.cs` | 124 | `ProjectMaterialIssues(+Lines)`; stock via `IStockService` | **`StockService`** | Posts each line with `ProjectId` + `CounterAccountOverride = 510104` (98-102). Comment at :88 records that StockService manages its own transaction per movement, so no outer transaction is opened. |
| `BL/ProjectLaborService.cs` | 111 | GL only | `JournalEntryService` | Labor = timesheets on `TaskItem.EntityType == "Project"` (56-58) × `IEmployeeCostService.HourlyCostAsync`. Reclass Dr 510104 [ProjectId] / Cr 520101 [untagged, cost centre] (100-101). Cost centre falls back to *the first cost centre in the company* (89). Post-once via `TaskItem.LaborPostedAt`. |
| `BL/EquipmentDepreciationService.cs` | 172 | GL only | `JournalEntryService` | Reclass to 510104; does not touch accumulated depreciation or `DepreciationRuns`. |
| `BL/ProjectBudgetService.cs` | 85 | **nothing — read-only** | — | Estimated = Σ BOQ cost buckets; actual = net debit on 510104 by ProjectId (76-81); attributed actual = posted material issues with `BoqItemId` (57-62). |
| `BL/ProjectService.cs` | 142 | `Projects`, `ProjectActivityTypes` | own EF | CRUD + `ProfitabilityAsync` (revenue 4xxx − cost 5xxx by ProjectId). |
| `BL/ProjectsAccessService.cs` | 264 | nothing | — | 8 actions (read, create, edit, manage, budget-view, budget-manage, billing, close); 3 module roles; `billing` additionally requires Accounting `post` (104-117); budget is **never** granted by membership (123-130); project company read from the **project row** (215-222). First-tab owned. |

Also read and relevant as reuse targets: `BL/StockService.cs` (`TransferAsync` at :670; write-off reasons
Damaged/Expired/Lost/Other at :92), `BL/FileManagerService.cs` (operates entirely on `LibraryItems`),
`BL/EmployeeCostService.cs`, `BL/InventoryApprovalService.cs`, `BL/LeaveWorkflowService.cs` (the two existing,
module-specific approval patterns), `BL/Reporting/*` (26 files).

---

## 3. Controller, screens, APIs

`CrossBuy/Controllers/ProjectController.cs` — 723 lines, **46 actions**, `[SessionValidation]` at class level,
`ViewData["SidebarMenu"] = MainMenu.Projects()`.

- **Gate coverage: 24 of 46** actions call `GateAsync` (the Batch D1 Wave 1 financial gate, lines 75-109); **14** of
  those also require Accounting `post`.
- **`DefaultCompanyId` (= 1, line 20) is used 61 times**, including in every read screen and in `PopulateFormListsAsync`.
- **Ungated mutations:** `SaveProject`, `DeleteProject` (180-203) — no `GateAsync`, company 1 hardcoded. Also
  `SaveActivityType` / `DeleteActivityType` (707-716).
- `Boq` (216) does an explicit `ProjectsActions.Read` check with an identical refusal/absence response (229-230).

**Views (16):** `Views/Project/` — ActivityTypes, Advance, Billing, Boq, Budget, Dashboard, EquipmentDepreciation,
Labor, MaterialIssues, Profitability, Progress, Projects, RetentionRelease, SubRetentionRelease, Subcontracts,
VariationOrders.

**Menu:** `Models/Menu/MainMenu.cs` → `MainMenu.Projects()` exposes Dashboard, Projects, Advance, RetentionRelease,
SubRetentionRelease, ActivityTypes, Profitability (the remaining screens are reached from the project rows).

**APIs:** none. There is no construction Web API controller — all access is MVC + views.

---

## 4. SQL scripts (idempotent, repository-root `deploy/sql/`)

`boq.sql` (26) · `project_dimension.sql` (18) · `projects_contracting.sql` (28) · `projects_p2_contract.sql` (26) ·
`projects_p3_progress.sql` (44) · `projects_p4_billing.sql` (71) · `projects_p5a_material.sql` (62) ·
`projects_p6c_subcontract.sql` (73) · `projects_p6d_variation.sql` (65) · `projects_p6e_equipment_dep.sql` (28).

Integrity measured across those scripts:

| Constraint kind | Count | Detail |
|---|---|---|
| `ROWVERSION` / `timestamp` | **0** | in every one of the scripts |
| `CHECK` | **0** | in every one of the scripts |
| `FOREIGN KEY` / `REFERENCES` | **4** | all line→header only: `ProjectProgressLines`, `ProgressBillingLines`, `ProjectMaterialIssueLines`, `VariationOrderLines` |
| `CREATE INDEX` | present | `(CompanyID, ProjectId, …)` on each header table |

So `BoqItems` has **no** FK to `Projects`, no `(CompanyID, ProjectId)` composite guard, and no domain CHECKs on
quantity, price or status. Company/project isolation is enforced **only in service predicates**.

*(For contrast, `CrossBuy/deploy/sql/project_members.sql` — first-tab work — does carry CHECK constraints, which is the
standard the construction schema should meet.)*

---

## 5. Platform integration points as they stand

| Concern | State | Evidence |
|---|---|---|
| Business events | `Project` **registered but not onboarded**: `SupportsSearch = true`, `SupportsTimeline = false`, `SupportsComments = false`, `SupportsFiles = false`, `PermissionScope = ScopeNone`, `ListedInRecordPicker = true` | `BL/Platform/EntityRegistry.cs:216-223` |
| Event kernel | live: `BusinessEventService`, `BusinessEventDispatchWorker`, `NotificationProjectionConsumer`, `TimelineProjectionConsumer` | `BL/Platform/` (41 files) |
| Company scope | `CompanyScopeMiddleware` + `CompanyQueryFilters` + `CompanyWriteGuardInterceptor` + `CompanyIsolationBypass` exist | `BL/Platform/` |
| Notifications | `INotificationService.NotifyAsync` with optional `entityType`/`entityId` | `BL/INotificationService.cs` |
| Files | `LibraryItem` (folder tree, `StoredPath`, soft delete via `DeletedAt`) + `FileManagerService`; **no revision, no supersede, no entity binding** | `Models/Context/Library/LibraryItem.cs:6-18` |
| Approvals | no generic engine; two module-specific implementations | `Models/Context/Admin/LeaveApprovalStep.cs`, `BL/InventoryApprovalService.cs` |
| Reporting | platform live, **deliberately no module data sources yet** | `BL/Reporting/PlatformReportDefinitions.cs:14-19` |
| Report contracts | `ReportDefinition` (Code, Module, TitleAr/En, DataSourceKey, PermissionKey, CategoryKey, Columns…), `IReportDataSource` (`Key` = `"<Module>.<Thing>"`, `FetchAsync`, **read-only, AsNoTracking**) | `BL/Reporting/ReportMetadata.cs`, `BL/Reporting/ReportDataSource.cs:170-179` |
| Background jobs | none construction-related. Existing: `CrmReminderHostedService`, `IntegrityCheckHostedService`, `TaskGeneratorHostedService`, `TaskScheduleMatchHostedService`, `BusinessEventDispatchWorker` | `Program.cs`, `BL/` |
| Tests | **no construction tests.** `CrossBuy.Tests/` (50+ files) covers platform kernel, access services, gates, reporting, isolation | `CrossBuy.Tests/` |
| Mobile | 10 screens, none project/site | `crossbuy_mobile/lib/screens/` |
| Prior design docs | `docs/CrossBuy_Projects_Design.md`, `_P2_Contract_`, `_P3_Execution_`, `_P4_ProgressBilling_`, `_P5_ActualCosts_`, `_P6c_Subcontractor_`, `_P6d_VariationOrders_` — the design record of the spine described above | `docs/` |

---

## 6. Current permission guards, company and branch isolation

- **Module authorization:** `ProjectsAccessService` (8 actions, 3 roles) + `ProjectMembers` for record level. `billing`
  requires **both** a projects right and Accounting `post`; `budget-view`/`budget-manage` are never granted by
  membership.
- **Controller enforcement:** partial — 24 of 46 actions (see §3). `SaveProject`/`DeleteProject` are unguarded.
- **Company resolution:** `IRequestCompanyResolver` is used *inside* `GateAsync` only; the remaining 61 sites use the
  literal `DefaultCompanyId = 1`. This is the single largest isolation gap in the current module and it is
  **first-tab owned** (recorded as CR-09).
- **Branch isolation:** effectively none for construction — `Project` has no `BranchID`; the only branch link in the
  chain is `Warehouse.BranchHierarchicalId`.
- **Site isolation:** does not exist — no entity in the model has a site identifier.

---

## 7. Inventory conclusions carried forward

1. The spine is real, is built on the correct writers, and must be **kept** — not replaced (`Stage-Construction-03`).
2. Its three defects (CR-01/02/03) are **structural**, not cosmetic: they come from the absence of revisions,
   scope caps and immutability, so they are fixed by the C2/C3 designs, not by patching a method.
3. The missing dimensions (Site, WBS, Cost Code) are the prerequisite for every cost, progress and site capability —
   which is why they are phase C1 (`Stage-Construction-05`, `Stage-Construction-16`).
4. Nothing in the platform prevents this layer from being additive: events are opt-in, files are reusable, reporting
   expects module-supplied read-only data sources, and the two writers already accept a `ProjectId` dimension.