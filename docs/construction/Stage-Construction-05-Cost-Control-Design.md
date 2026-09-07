# Stage-Construction-05 — Cost Code and Cost Control Design

Covers Phase 6 (cost codes and cost control) and Phase 7 (project budget).

**Boundary restated:** this document changes **no** accounting or stock posting behaviour. It designs the *dimensions*
that cost facts must carry and the *extension points* where those dimensions are captured. Actual cost remains the
general ledger, exactly as `BL/ProjectBudgetService.cs:76-81` already reads it.

---

## 1. Why a cost code is a new entity

`CostCenter` (`Models/Context/Accounting/Dimensions.cs:7`) mirrors the organisation tree — it carries
`SourceHierarchicalId` pointing at `Hierarchicals`, so any expense tagged with it rolls up to a branch or department.
That is a valid and *different* axis. Construction needs to answer "how much of this project is labour?" — an axis that
does not exist today. The BOQ's four fixed decimal buckets (`Boq.cs:22-25`: MaterialCost, LaborCost, SubcontractCost,
EquipmentCost) are the current stand-in: not codes, not extensible, present only on an estimate line, and never
compared against an actual of the same category.

## 2. `CostCode`

| Field | Rule |
|---|---|
| `ID`, `CompanyID` | Company-scoped reference data (see `Stage-Construction-11`). |
| `Code`, `Name`, `NameEn` | `Code` unique per company. |
| `Category` | One of the required set below. |
| `ParentId` | Optional grouping within a category. |
| `DefaultAccountId` | Optional GL account hint — a *hint*, never an override of a posting rule. |
| `IsActive` | Deactivate, never delete. |
| `AllowDirectPosting` | Whether cost may be allocated to this code directly or only to its children. |
| `RowVersion` | Concurrency token. |

**Required categories:** `Material` · `Labor` · `Equipment` · `Subcontractor` · `SiteExpense` · `Transportation` ·
`Accommodation` · `Overhead` · `Contingency` · `Other` (extensible by the company, category set fixed).

**Rules:** a code with any allocation cannot be deleted or have its `Category` changed (that would silently rewrite
history); code changes require `costcode.manage` and an audit reason (decision **D-09** governs who may).

## 3. The ten cost views

| View | Definition | Source |
|---|---|---|
| **Budget** | Approved budget amount per WBS × CostCode | `ProjectBudgetLine` (approved version) |
| **Committed** | PO + subcontract value not yet actualised | `PurchaseOrder.ProjectId` (`Inventory.cs:316`) + `SubcontractScope`, minus actualised portion |
| **Actual** | Posted cost | `JournalEntryLines` where `ProjectId` set and account is a cost account (as today) |
| **Accrued** | Work/goods received not yet invoiced | GRN without supplier invoice + approved-not-posted certificates |
| **Certified** | Value certified to subcontractors | Subcontract certificates `Approved`/`Posted` |
| **Paid** | Cash actually out | AP payments against those invoices |
| **Forecast** | Expected final cost per code | `ProjectForecast` (manual, method-stamped — decision **D-12**) |
| **ETC** | Estimate to complete = Forecast − Actual | derived |
| **EAC** | Estimate at completion = Actual + ETC | derived |
| **Variance** | Budget − EAC (and Budget − Actual for the period view) | derived |

Each view is a *projection*, not a stored balance. Only `Budget` and `Forecast` are stored, because only those two are
human decisions.

## 4. `CostAllocation` — the dimension carrier

The single row type that makes construction cost traceable. It is **not a second ledger**: it records *which
construction dimensions* a cost fact carried, keyed back to the fact.

| Field | Required | Note |
|---|---|---|
| `CompanyID` | ✔ | Never defaulted. No company-1 fallback. |
| `ProjectId` | ✔ | |
| `WbsNodeId` | ✔ where the node `OwnsCost` | The dimension that is entirely missing today |
| `CostCodeId` | ✔ | |
| `BoqItemId` | where applicable | |
| `ClientContractId` / `SubcontractId` | where applicable | |
| `SiteId` | where applicable | |
| `BranchID` | where resolvable | |
| `SourceEntityType`, `SourceEntityId`, `SourceLineId` | ✔ | The fact this allocation describes |
| `JournalEntryId` / `StockMovementId` | where the fact posted | The link to the authoritative record |
| `Amount`, `Quantity`, `CurrencyId`, `PostedAt` | ✔ | `Amount` is a **copy** for analysis; the GL remains authoritative |
| `Status` | ✔ | `Committed` \| `Accrued` \| `Actual` \| `Reversed` |
| `RowVersion` | ✔ | |

**Reconciliation invariant (must be a test):** for any project, `Σ CostAllocation.Amount where Status = Actual` equals
the net debit on the project cost accounts in the GL for the same period. If they diverge, the allocation table is
wrong and the GL is right — never the other way round.

## 5. Allocation sources and their extension points

Nothing below changes posting behaviour in this increment. Each row states *where* the dimensions would be captured.

| Source | Today | Extension point (design only) |
|---|---|---|
| Purchase Order | `PurchaseOrder.ProjectId` exists; line has no allocation | Purchasing-owned additive line allocation (WBS/CostCode). Until then, header-level commitment only, flagged as approximate. |
| Goods Receipt | no `ProjectId` (`Inventory.cs:339-357`) | Inherit from the PO; accrual allocation created at receipt. |
| Inventory Issue | `ProjectMaterialIssueLine.BoqItemId` optional | Add `WbsNodeId`, `CostCodeId`, `SiteId` to the line; allocation written in the same transaction as the issue. Posting still via `StockService`. |
| Inventory Return | does not exist | New construction document; negative allocation, stock via `StockService`. |
| Supplier Invoice | `PurchaseInvoice.ProjectId`, `CostCenterId` | Convert the accrual allocation to `Actual` on invoice; no new writer. |
| Subcontractor Certificate | posts via `PayableService` with `ProjectId` | Allocation per **certificate line** (lines are being introduced — see `Stage-Construction-06`). |
| Payroll / labor | reclass Dr 510104 [ProjectId] / Cr 520101 (`ProjectLaborService.cs:100-101`) | Allocation per manpower-log line (employee, trade, WBS, hours × rate). The reclass itself stays as-is. |
| Equipment usage | depreciation reclass only | Allocation per equipment-log line; the depreciation reclass remains the accounting treatment. |
| Journal Entry | `JournalEntryLine.ProjectId` | Manual JEs touching a project cost account **should** require construction dimensions — see §8. |
| Manual adjustment | none | New construction adjustment document with mandatory reason; posts through `JournalEntryService`. |

## 6. Project budget

### 6.1 Entities

`ProjectBudget` (version header) → `ProjectBudgetLine` (WBS × CostCode) → `BudgetTransfer` (line-to-line movement).

| `ProjectBudget` field | Rule |
|---|---|
| `CompanyID`, `ProjectId` | Required. |
| `VersionNo` | Sequential. |
| `Kind` | `Original` \| `Revised` \| `Baseline` \| `Scenario` \| `Forecast` |
| `Status` | `Draft` → `Submitted` → `Approved` → `Superseded` |
| `IsBaseline` | Exactly one approved baseline at a time per project. |
| `ApprovedBy/At`, `Reason` | Required to reach `Approved`. |
| `RowVersion` | Concurrency token. |

| `ProjectBudgetLine` field | Rule |
|---|---|
| `WbsNodeId`, `CostCodeId` | The two axes; unique together per version. |
| `Amount`, `Quantity`, `Rate` | Quantity/rate optional but preserved when given. |
| `IsContingency` | Contingency is a line, not a hidden margin. |
| `PreviousAmount`, `ChangeReason` | Carried from the version being revised — the previous value is never lost. |

### 6.2 Rules

1. **An approved version is immutable.** Revising creates a new version; the previous stays readable forever.
2. **Transfers require approval** and record from-line, to-line, amount, reason, approver. A transfer never changes the
   version total unless it is an explicit increase/decrease, which is a *revision*, not a transfer.
3. **No negative budget line** without an explicit company policy allowing it.
4. **Commitment check before PO/subcontract approval** — designed here, wired later; behaviour set by decision **D-08**
   (warn or block, per company). The check is a construction service that Purchasing *calls*; construction does not
   modify Purchasing's approval flow.
5. **Every revision preserves previous values and a reason** (`PreviousAmount`, `ChangeReason`, plus
   `ConstructionAudit`).
6. **Financial progress and physical progress are distinct** and neither is derived from the other
   (`Stage-Construction-10`).

### 6.3 Relationship to what exists

`BL/ProjectBudgetService.cs` stays useful as a **reporting projection** (BOQ estimate vs attributed actual) and is
superseded as the "budget" only when real budget versions exist. `Project.Budget` (a single nullable decimal read by
nothing) is retained for backward compatibility and marked deprecated in favour of the approved version total.

## 7. Required traceability keys

Every construction cost fact must be resolvable through: `CompanyID` · `ProjectId` · `WbsNodeId` · `CostCodeId` ·
`BoqItemId` (where applicable) · `ContractId`/`SubcontractId` (where applicable) · `SiteId` · `BranchID` ·
`SourceEntityType` · `SourceEntityId`.

Two of those (`WbsNodeId`, `CostCodeId`) do not exist anywhere in the current model, and two more (`SiteId`, `BranchID`)
exist nowhere on the project chain. That is the substance of the C1/C2 work.

## 8. The manual-posting hole (open item)

A manual journal entry can debit project cost with only `ProjectId` set (`GeneralLedger.cs:44`) and no construction
dimensions. Nothing prevents it, and the cost then appears in `Actual` but in no cost code.

Options: (a) accept and report it as an explicit "unallocated cost" bucket, as `ProjectBudgetService.UnattributedActual`
already does honestly; (b) require construction dimensions on manual entries touching project cost accounts — an
**Accounting-owned** posting-rule change; (c) post-hoc allocation screen where a commercial user assigns unallocated
cost to codes.

**Recommendation:** (a) + (c). (b) changes Accounting behaviour and is out of scope for this tab to decide. Whichever
is chosen, the unallocated bucket must be *visible* — a cost-control report that silently omits it is worse than one
that shows it.