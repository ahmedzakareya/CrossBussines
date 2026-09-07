# Stage-Construction-07 — Site Operations

Covers Phase 11 (site operations), Phase 12 (daily site report) and Phase 13 (manpower and equipment).

**The rule for this whole document:** *no second inventory engine.* Site stores are `Warehouse` rows; every stock
movement is produced by `StockService`. Construction adds **documents and dimensions**, never stock arithmetic.

---

## 1. `ConstructionSite` — the missing dimension

No entity in the model has a site identifier. `Warehouse` carries `BranchHierarchicalId`
(`CrossBuy/Models/Context/Inventory/Inventory.cs:152`) and nothing else locational; `GoodsReceipt` has no `ProjectId`
at all (`Inventory.cs:339-357`).

| Field | Rule |
|---|---|
| `CompanyID`, `ProjectId` | Required. A site belongs to exactly one project (a shared compound is modelled as two sites). |
| `Code`, `Name`, `NameEn` | `Code` unique per project. |
| `BranchID` | Where resolvable — links site cost to the branch rollup. |
| `Latitude`, `Longitude`, `GeofenceRadiusM` | Optional; only used where location capture is approved (see `Stage-Construction-14` privacy section). |
| `SiteManagerEmployeeId` | References HR. |
| `Status` | `Planned` → `Active` → `Suspended` → `Closed` |
| `RowVersion` | Concurrency token. |

**Site ↔ warehouse binding.** Preferred: `Warehouse.SiteId` (nullable, additive) — an **Inventory-owned** change to be
requested, not made by this tab. Until it exists, construction holds a `SiteWarehouse` map
(`SiteId`, `WarehouseId`, `Purpose`, `IsPrimary`) so no Inventory file is touched. Cross-site access policy is decision
**D-10**.

---

## 2. Site transaction set

Every one of these documents carries the same dimension block and the same discipline.

| Document | Exists today? | Stock effect | Through |
|---|---|---|---|
| Material Request | no | none (demand only) | — |
| Material Reservation | no | soft allocation | reads `StockService` balances; no movement |
| Material Transfer to Site | yes (generic) | transfer | `IStockService.TransferAsync` (`BL/StockService.cs:670`) |
| Site Receipt | partly (GRN, no project) | receipt | existing Inventory receipt path |
| Material Issue | **yes** | issue | `IStockService.PostMovementAsync` with `ProjectId` + `CounterAccountOverride = 510104` (`BL/ProjectMaterialIssueService.cs:98-102`) |
| Material Return | no | receipt (reverse of issue) | `StockService` |
| Material Consumption | implicit in issue | none extra | issue is the consumption event unless a company distinguishes them |
| Waste / Damage | partly (write-off, `Reason` = Damaged/Expired/Lost/Other, `BL/StockService.cs:92`) | write-off | `StockService` |
| Equipment Request | no | none | — |
| Labor Request | no | none | — |
| Inspection Request | no | none | `Stage-Construction-08` |
| Work Approval Request | no | none | `Stage-Construction-08` |

### 2.1 Mandatory dimension block

Every site transaction and every line carries:

`CompanyID` · `ProjectId` · `SiteId` · `WbsNodeId` · `ActivityId` (where activities exist) · `CostCodeId` ·
`BoqItemId` (where applicable) · `RequestedByEmployeeId` · `ApprovedByEmployeeId` · `WarehouseId` (stock documents) ·
`Quantity` · document date + required dates · `Status` · source-document links · `RowVersion`.

Today `ProjectMaterialIssueLine` carries `ItemId`, `Qty` and an **optional** `BoqItemId`
(`Models/Context/Accounting/ProjectMaterialIssue.cs:27-40`) — no site, no WBS, no cost code, no requester, no approver.

### 2.2 `MaterialRequest`

Header: site, project, requested-by, required-by date, priority, status
(`Draft` → `Submitted` → `Approved` → `PartiallyIssued` → `Issued` → `Rejected` → `Cancelled`), approval chain.
Line: item (or free-text description for a non-stock item), quantity, unit, WBS, cost code, BOQ item, required date,
`ReservedQuantity`, `IssuedQuantity`, `Remarks`.

Rules: the request never moves stock; `IssuedQuantity` is maintained only by an issue posting; over-issue beyond the
approved request requires either an amendment or an explicit authorised exception; a request against a WBS node with no
budget line is allowed but flagged in cost control as unbudgeted demand.

### 2.3 `MaterialReturn` (new)

Site surplus back to store, or to another site. Line references the **original issue line** where known, so the
project's material cost is credited against the same WBS/cost code it was charged to. Stock in via `StockService`;
valuation is Inventory's rule, not a construction re-valuation.

### 2.4 Waste and damage

Reuses the existing write-off path, adding the dimension block. Two figures make it useful and neither exists today:
**waste quantity per BOQ item** and **waste against the BOQ's allowed wastage** (the manufacturing module already has
the concept — `ScrapPct` at `Inventory.cs:130` — but not for site material).

---

## 3. Daily Site Report (DSR)

The most-used document in contracting, entirely absent today. It must produce **structured data, not free text** —
free-text-only DSRs are why site records cannot be reported on.

### 3.1 `DailySiteReport` (header)

`CompanyID` · `ProjectId` · `SiteId` · `ReportDate` (unique per site per date) · `WeatherCondition`, `TemperatureC`,
`RainfallStopped` (bool) · `WorkStartTime`, `WorkEndTime`, `TotalWorkingHours` · `ShiftCount` ·
`Status` (`Draft` → `Submitted` → `Reviewed` → `Approved`) · `SubmittedBy/At`, `ReviewedBy/At`, `ApprovedBy/At` ·
`NextDayPlan` · `GeneralRemarks` · `RowVersion`.

### 3.2 Structured children

| Child | Key fields |
|---|---|
| `DsrManpowerLine` | trade, source (`OwnEmployee` \| `Subcontractor`), `EmployeeId` **or** `SubcontractId` + headcount, `WbsNodeId`, regular hours, overtime hours |
| `DsrEquipmentLine` | `FixedAssetId` **or** rental `SubcontractId`, `EquipmentDescription`, operator, `WbsNodeId`, working hours, idle hours, downtime hours + reason, fuel quantity, meter reading |
| `DsrQuantityLine` | `WbsNodeId`, `BoqItemId`, quantity executed today, unit, location/level, remarks — **this is the line that feeds progress** |
| `DsrMaterialReceivedLine` | item, quantity, supplier/`GoodsReceiptId`, delivery note no |
| `DsrMaterialConsumedLine` | item, quantity, `WbsNodeId`, `MaterialIssueId` where posted |
| `DsrVisitorLine` | visitor name, organisation, purpose, in/out time |
| `DsrInstructionLine` | source (consultant/client/internal), instruction reference, description, resulting `RfiId`/`VariationId` |
| `DsrInspectionLine` | `InspectionRequestId`, result |
| `DsrDelayLine` | cause, start/end time, affected `WbsNodeId`, responsible party, `DelayEventId` where escalated |
| `DsrIncidentLine` | severity, type, persons involved, description, reportable flag, `HsEventId` |
| `DsrBlockerLine` | description, owner, needed-by date |
| `DsrPhoto` | `LibraryItemId`, caption, `WbsNodeId`, taken-at, optional geotag |
| `DsrAttachment` | `LibraryItemId`, kind |

**Photos and attachments store `LibraryItemId` only.** Bytes stay in the platform file store
(`Models/Context/Library/LibraryItem.cs`, `BL/FileManagerService.cs`) — no construction file storage.

### 3.3 Rules

1. One DSR per site per date; a second submission for the same date is an **amendment** with a reason, not a new row.
2. `Approved` is immutable; corrections are amendments that preserve the previous values.
3. Quantity lines feed the progress record but **do not by themselves certify anything** — certification requires the
   measurement/approval flow in `Stage-Construction-06`.
4. A DSR may be created offline; every line carries a client-generated idempotency key (CR-18, decision **D-14**).
5. Whether a DSR is mandatory per working day is a company policy, not a hard-coded rule.

---

## 4. Manpower

`ManpowerLog` — either its own document or the DSR's manpower child promoted to a first-class row (recommended: the DSR
child **is** the manpower log, with an independent entry path for sites that log manpower separately from the DSR).

| Field | Note |
|---|---|
| `EmployeeId` | HR-owned identity. Never a duplicated person record. |
| `SubcontractId` + `TradeId` + `Headcount` | Subcontractor labour: those people are **not** employees, so the log records a trade headcount, not persons. This is the boundary line with HR. |
| `TradeId` | New construction lookup (mason, steel fixer, carpenter, electrician…), user-defined. |
| `WbsNodeId`, `ActivityId` | Where the hours went. |
| `RegularHours`, `OvertimeHours` | Separated — overtime is a different cost rate. |
| `CostRateSource` | `HrEmployeeCost` (from `IEmployeeCostService.HourlyCostAsync`, already used at `BL/ProjectLaborService.cs:47`) \| `SubcontractRate` \| `ManualApproved` |
| `AttendanceRecordId` | Optional reference to HR attendance where an employee is on site. **Reference, not a copy** — construction must not become a second attendance system. |
| `Productivity` | Derived: executed quantity ÷ hours for the node. Reported, never stored as truth. |

**Relationship to what exists.** `BL/ProjectLaborService.cs` derives labor from timesheets on tasks where
`EntityType == "Project"` (lines 56-58) — a *costing* path that works but knows nothing about trade, site, WBS or
overtime. The manpower log becomes the site record; the existing reclass remains the accounting treatment, and the log
supplies the dimensions to `CostAllocation`.

---

## 5. Equipment

`EquipmentLog` — a **usage** record, which is what today's `EquipmentDepreciationAllocation` is not (its `Hours` and
`Rate` are optional helpers that merely fill an amount,
`Models/Context/Accounting/EquipmentDepreciationAllocation.cs`).

| Field | Note |
|---|---|
| `FixedAssetId` | Owned equipment — the existing `FixedAsset` (`Models/Context/Accounting/FixedAssets.cs:22`). No equipment master duplication. |
| `SubcontractId` + `EquipmentDescription` | Rented equipment: the rental is a subcontract/vendor arrangement. |
| `OperatorEmployeeId` | HR reference. |
| `ProjectId`, `SiteId`, `WbsNodeId`, `ActivityId` | Dimensions. |
| `WorkingHours`, `IdleHours`, `DowntimeHours`, `DowntimeReason` | The three-way split is the point; utilisation and disruption claims depend on it. |
| `MeterStart`, `MeterEnd`, `FuelQuantity`, `FuelCostRate` | Fuel is a major construction cost and is untracked today. |
| `MaintenanceRecordId` | Reference to the existing maintenance module (`Models/Context/Accounting/Maintenance.cs`) — not a copy. |
| `CostRateSource`, `CalculatedCost` | Owned: depreciation share and/or an internal hire rate; rented: the contract rate (**decision** — recorded in screen 20's unresolved column). |

**Accounting treatment stays as it is.** Owned-equipment cost reaches the project through the existing depreciation
reclass; the log adds the dimensions and the utilisation truth. Fuel and rental reach the project as ordinary payables
with construction dimensions attached.

---

## 6. What this design explicitly refuses to build

| Refused | Because |
|---|---|
| A construction stock table or balance | `StockService` is the only stock writer; a parallel balance would diverge from valuation. |
| A construction valuation rule for returns/waste | Valuation is Inventory's. |
| A construction attendance table | Attendance is HR's; site logs reference it. |
| A construction equipment master | `FixedAsset` exists. |
| A construction file store | `LibraryItem` exists. |
| A site "mini-ERP" screen that posts stock directly | Every stock effect goes through the existing documents and `StockService`. |