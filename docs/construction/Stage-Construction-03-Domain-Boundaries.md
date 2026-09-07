# Stage-Construction-03 — Domain Boundaries

The purpose of this document is to make **duplication impossible to justify later**. Every capability below has exactly
one owner, and construction reaches the others only through a named contract.

---

## 1. The rule

> Construction owns **construction structure and construction documents**. It owns **no money movement and no stock
> movement**. It carries **dimensions** onto other modules' facts; it does not re-post them.

Two invariants inherited from the platform, restated because they are the boundary in practice:

- **`JournalEntryService` is the only GL writer.** Construction never creates a journal line except through it, and
  reversal (`ReverseAsync`) is the only correction primitive — never row deletion.
- **`StockService` is the only stock writer.** A site store is a `Warehouse`; a site issue is a `StockMovement`
  produced by `StockService`. There is no second inventory engine.

The existing code already honours both (`BL/ProgressBillingService.cs` posts via `ReceivableService`;
`BL/ProjectMaterialIssueService.cs:98-102` issues via `StockService`). This document keeps it that way.

---

## 2. Ownership table

### 2.1 Construction owns

| Capability | Entity/entities | Why construction and not Projects |
|---|---|---|
| Construction site | `ConstructionSite` | A physical location with its own stores, manpower and daily record — meaningless outside construction. |
| Work breakdown | `WbsNode` | Contractual work decomposition with a progress method and budget ownership. |
| Cost breakdown codes | `CostCode` | Construction cost categories, distinct from the org rollup that `CostCenter` provides. |
| BOQ and its revisions | `BoqItem` (extend), `BoqRevision`, `BoqLineHistory` | Contractual quantities and rates with immutability. |
| Construction cost planning | `ProjectBudget`, `ProjectBudgetLine`, `BudgetTransfer` | Budget by WBS × CostCode with versions. |
| Commitment view | `ProjectCommitment` | A *read model* over Purchasing facts, allocated to WBS × CostCode. |
| Cost allocation | `CostAllocation` | The construction dimension row attached to any cost fact. |
| Client and subcontract contracts | `ClientContract`, `Subcontract` (extend), `SubcontractScope` | Contract terms are construction commercial data. |
| Progress measurement and records | `ProjectProgress` (extend), `ProgressRecord` | Physical execution measurement. |
| Certificates | `ProgressBilling`/`SubcontractBilling` (extend into certificates) | The certificate document, its review chain and its snapshots. |
| Variations, claims, delays | `VariationOrder` (extend), `Claim`, `DelayEvent` | Contractual change and entitlement. |
| Site operations documents | `MaterialRequest`, `DailySiteReport`, `ManpowerLog`, `EquipmentLog` | Site record-keeping. |
| Quality and technical control | `Rfi`, `InspectionRequest`, `Submittal`, `MethodStatement`, `Ncr`, `PunchItem` | Construction quality process. |
| Drawing/document control | `DocumentRegister`, `DocumentRevision` | Revision control and current-revision safety. |
| Cash-flow plan | `CashFlowPlan`, `CashFlowLine` | Project funding projection (planning, not posting). |
| Construction audit | `ConstructionAudit` | Field-level immutable history for commercial values. |

### 2.2 Accounting owns — construction must not re-implement

Journal entries and posting · receivables · payables · payments and receipts · currencies and FX · tax · fiscal
periods and closing · financial statements · retention and advance **GL treatment** · reversal.

**Construction's contract with Accounting**

| Construction needs | It uses | Never |
|---|---|---|
| Post a client certificate | `IReceivableService.CreateSalesInvoiceAsync` + `CreateReceiptAsync` (as today) | its own AR rows |
| Post a subcontractor certificate | `IPayableService.CreatePurchaseInvoiceAsync` + `CreatePaymentAsync` (as today) | its own AP rows |
| Advance, retention release | `IContractService` → `IJournalEntryService.CreateAndPostAsync` (as today) | a direct JE from a construction service |
| Correct a posted certificate | `IJournalEntryService.ReverseAsync` **through** the owning service | delete or edit a posted row |
| Read cost actuals | posted `JournalEntryLines` filtered by `ProjectId` (as `ProjectBudgetService` does) | a shadow cost table as the source of truth |

Retention (1104), customer advance (2104) and subcontractor retention (2105) stay exactly where they are — balances
derived from posted GL lines, not from a construction table. That is already the design in `BL/ContractService.cs` and
it is correct.

### 2.3 Inventory owns

Items · warehouses and bins · stock balances · receipts, issues, transfers, write-offs · valuation · batches/serials.

**Construction's contract with Inventory**

| Construction needs | It uses | Never |
|---|---|---|
| Issue material to site | `IStockService.PostMovementAsync` with `ProjectId` + `CounterAccountOverride` (as today) | direct `StockMovements` insert |
| Move material between sites | `IStockService.TransferAsync` | a construction transfer table with its own quantities |
| Record waste/damage | the existing write-off path (`Reason` = Damaged/Expired/Lost/Other) | a construction stock-adjustment writer |
| A site store | a `Warehouse` (typed), optionally bound to a `ConstructionSite` | a construction stock table |

`Warehouse.SiteId` is the one field construction would like added to an Inventory-owned entity. It is **additive and
nullable**, and it is the Inventory owner's change — see decision **D-10**. Until it exists, construction maps
site↔warehouse in its own table rather than editing Inventory.

### 2.4 Purchasing owns

Purchase requests · purchase orders · goods receipt · three-way match · supplier invoice linkage
(`BL/ProcurementService.cs`, `BL/ThreeWayMatchService.cs`).

**Construction's contract with Purchasing**

- Reads `PurchaseOrder.ProjectId` (already present, `Inventory.cs:316`) to derive **committed cost**. Read-only.
- **Wants** a cost-code/WBS allocation on the PO line so a commitment can be placed against a budget line. That is a
  Purchasing-owned additive change and must be requested, not made here.
- Budget control at PO approval (decision **D-08**) would mean Purchasing *calling* a construction check. Construction
  supplies the check as a service; it does not modify Purchasing's approval flow itself.

### 2.5 HR owns

Employees · attendance · payroll · employee cost rates · organisational hierarchy
(`BL/EmployeeService.cs`, `BL/AttendanceService.cs`, `BL/EmployeeCostService.cs`, `Hierarchicals`).

**Construction's contract with HR**

- `ManpowerLog` references `EmployeeId`; it never stores a name, a salary or an attendance record.
- Labor cost rate comes from `IEmployeeCostService.HourlyCostAsync` — already the source used by
  `BL/ProjectLaborService.cs:47`.
- Attendance stays HR's fact. A manpower log may *reference* an attendance record; it must not become a second
  attendance system. Site headcount by trade for a subcontractor's workers is construction data (those people are not
  employees) — that distinction is the boundary line.

### 2.6 Projects owns (or shares)

The general project master, project team (`ProjectMembers`), generic milestones, generic project status.

**Shared-with-construction rules**

- `Project` stays the single project identity. Construction **extends** it (nullable, additive) rather than creating a
  `ConstructionProject` table that would fork project identity and break `ProjectId` attribution everywhere.
- `ProjectMembers` stays the record-level access relationship and stays **first-tab owned**. Construction roles
  (QS, Site Engineer, Document Controller) are expressed as construction permissions plus, where a person's access to
  a specific project must be limited, existing membership — not a second membership table.
- Generic project status stays; construction adds its own lifecycle states (handover, DLP, closed) as a separate,
  explicit state machine rather than more strings in `Project.Status`.

### 2.7 Owned by other tabs — construction supplies contracts only

| Concern | Owner | Construction's deliverable |
|---|---|---|
| Authorization, bootstrap policies, access services, security console | FIRST tab | A proposed permission vocabulary (`Stage-Construction-11`), unwired. |
| Reporting platform, Report Studio, renderers | SECOND tab | Read-only data-source keys + secure DTOs (`Stage-Construction-13`). |
| Comments, mentions, notifications, timeline | THIRD tab | Event names and payloads (`Stage-Construction-12`). |
| Tasks, Calendar | later increments | Nothing started here. |

---

## 3. Duplication watchlist

These are the specific temptations this design forbids, each with the reason:

| Tempting duplicate | Forbidden because |
|---|---|
| "Construction stock" table for site material | `StockService` is the only stock writer; a parallel table would diverge from valuation and stock balances. |
| "Construction cost ledger" as the source of cost truth | Actual cost is the GL (`ProjectBudgetService` already reads it). `CostAllocation` is a **dimension carrier**, not a second ledger. |
| "Construction vendor" for subcontractors | A subcontractor **is** a `Vendor` (already the case, `Subcontract.VendorId`). |
| "Construction customer" for employers | An employer **is** a `Customer` (already the case, `Project.CustomerId`). |
| "Construction employee" for site staff | Employees are HR's. Non-employee site labour is a trade headcount on a manpower log, not a person record. |
| "Construction files" storage | `LibraryItem` stores bytes; construction stores **metadata + revision** that reference it. |
| "Construction approval engine" | Ask the platform first (CR-14). Only if refused, build a construction-local chain — and never copy the HR one. |
| A `CostCode` that is really a `CostCenter` | `CostCenter` mirrors the org tree (`SourceHierarchicalId`); a cost code is a cost *category* breakdown. Collapsing them destroys both analyses. |
| A second project master | Would break every `ProjectId`-tagged GL line, stock movement source and existing report. |

---

## 4. Boundary tests to write (C1 onward)

Boundaries that are only documented get crossed. Each of these is a test in `CrossBuy.Tests`:

1. No construction service references `_db.JournalEntries` / `_db.JournalEntryLines` for **writing**.
2. No construction service references `_db.StockMovements` / `_db.StockBalances` for writing.
3. No construction service calls `IgnoreQueryFilters()`.
4. Every construction entity has `CompanyID`, and every construction query filters it.
5. No construction entity duplicates a Vendor, Customer or Employee field set (name/contact/salary).
6. Construction file metadata rows always resolve to a `LibraryItem` id; construction never writes `StoredPath`.