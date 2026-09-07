# Stage-Construction-10 — Progress and Project Cash Flow

Covers Phase 16 (progress) and Phase 17 (project cash flow).

---

## 1. Four progresses, not one

| Progress | Definition | Owner of the number | Today |
|---|---|---|---|
| **Physical** | How much work is actually built | Site engineer / QS, by the node's method | **Not separately measured** |
| **Financial** | How much value has been earned/billed | QS / commercial | Exists |
| **Certified** | How much the employer has accepted | Employer / consultant | Conflated with financial |
| **Paid** | How much cash has arrived | Accounting (AR position) | Not shown per project |

### 1.1 What exists and why it is not physical progress

`BL/ProgressService.cs` computes `OverallPercent = Σ executed value ÷ Σ BOQ value × 100` (line 185) — a **value-weighted**
figure. Per line, executed value is `min(cumulativeQty, boqQty) × unitPrice`, or `manualPercent × boqValue` (lines
80-86), and the result is **capped at the BOQ value**.

Three consequences:

1. It is a *financial proxy*. A node whose expensive work is complete and whose cheap work is not reads as nearly
   finished. That is a legitimate commercial view and a poor construction view.
2. **Over-execution is silently truncated.** Cumulative quantity above the BOQ quantity is clipped and only sets a UI
   boolean (`OverBoq`, line 162). Nothing blocks it, notifies anyone, or routes it to a variation (CR-08).
3. `ConfirmAsync` (line 256) sets `Status = "Confirmed"` without checking the current status — a confirmed measurement
   can be re-confirmed, and there is no un-confirm path.

**The brief's rule — do not derive physical progress from invoice value — is therefore currently at risk in the
opposite direction:** the *certificate* is derived from the measurement (correctly), but the measurement's own
percentage is a value calculation, so "progress" in every screen is a money figure wearing a physical label.

## 2. `ProgressRecord`

One record per WBS node per period per method — the entity that makes the four progresses independent.

| Field | Rule |
|---|---|
| `CompanyID`, `ProjectId`, `WbsNodeId` | Required. |
| `PeriodStart`, `PeriodEnd`, `AsOfDate` | A progress record is always *as of* a date. |
| `Method` | `Quantity` \| `Milestone` \| `WeightedActivity` \| `ManualApproved` \| `EarnedValue` (later). Copied from `WbsNode.ProgressMethod` at creation and **stamped on the record**, so a later method change cannot silently reinterpret history. |
| `PlannedPercent`, `PlannedQuantity` | From the baseline (requires C7 baseline; null until then, and reported as null rather than zero). |
| `ActualPercent`, `ActualQuantity` | Measured. |
| `ApprovedPercent`, `ApprovedQuantity` | What the reviewer accepted — may differ from actual. |
| `CertifiedPercent`, `CertifiedQuantity` | Derived from approved certificate lines. Never typed. |
| `ForecastPercent`, `ForecastCompletionDate` | Forward view. |
| `Variance` | Derived: approved − planned. |
| `Status` | `Draft` → `Submitted` → `Approved` (+ `Returned`) |
| `SourceDsrIds` | The site records the quantities came from — no re-typing. |
| `RowVersion` | |

### 2.1 The five methods

| Method | Percent computed as | Guard |
|---|---|---|
| **Quantity** | `Σ executed quantity ÷ Σ contracted quantity` for the node's BOQ items — **quantity-weighted, not value-weighted** | over-quantity is refused or requires a variation (**D-07**), never clipped |
| **Milestone** | Σ weights of achieved milestones ÷ Σ all weights | milestone weights must total 100 at approval |
| **WeightedActivity** | Σ (activity % × activity weight) | weights per node total 100 |
| **ManualApproved** | typed and approved | requires `progress.approve`, a reason, and is flagged as manual in every report |
| **EarnedValue** | later (C9+) | needs baseline + cost data |

**Rule:** the *value-weighted* rollup that exists today remains available — as a **reporting rollup for commercial
purposes**, clearly labelled, and never as the definition of physical progress (decision **D-11**).

### 2.2 Rollup

A parent node's progress is computed from its children, weighted by the parent's chosen weighting basis (budget value,
contract value or manual weights — recorded on the node). Rollups are **derived at read time**; a stored parent
percentage would be a second source of truth.

## 3. Certified and paid

- **Certified** comes only from certificate lines in state `Approved` or `Posted` (`Stage-Construction-06`). It is the
  employer's acceptance and belongs to the certificate, not to the measurement.
- **Paid** is read from the AR position of the certificate's linked `SalesInvoiceId` (already stored,
  `Models/Context/Accounting/ProgressBilling.cs:26`). Construction never records a payment; it reads Accounting's.
- Consequence to surface in every report: **physical ≠ financial ≠ certified ≠ paid**, and the gaps between them are the
  useful numbers (unbilled work, uncertified work, unpaid certified work).

## 4. Project cash flow

Absent today. Designed as a **plan**, not a posting: `CashFlowPlan` (version header) + `CashFlowLine` (period × category
× scenario).

### 4.1 Inflows

| Source | Derivation |
|---|---|
| Client certificates | approved/posted certificates + the contract's `PaymentTermsDays` and `CertificateCycle` |
| Expected collections | AR ageing for the project's customer, project-tagged invoices |
| Advance payments | contract `AdvancePercent` × value, expected at commencement; actual from 2104 movements |
| Retention releases | retention balance on 1104 per project (`BL/ContractService.cs:58`) + the DLP/trigger date |
| Approved claims | settled claim amounts with their agreed payment dates |

### 4.2 Outflows

| Source | Derivation |
|---|---|
| Purchase commitments | open POs with `ProjectId` (`Inventory.cs:316`) + supplier terms |
| Subcontract certificates | approved sub certificates + terms; plus committed remaining scope by period |
| Payroll | manpower plan × cost rates (HR rates, not duplicated) |
| Equipment | equipment plan (owned share + rental) |
| Site expenses | budget lines by cost code with a spend curve |
| Tax | Accounting's tax position for the project's documents |
| Retention released to subs | 2105 balance per project (`ContractService.SubRetentionBalanceAsync`) + trigger dates |

### 4.3 Four series per line

`Planned` (from the budget and the contract) · `Committed` (from POs/subcontracts) · `Actual` (from the GL and AR/AP) ·
`Forecast` (planned adjusted by actuals and by the commercial manager's method). `ScenarioName` allows best/likely/worst
without duplicating the plan.

### 4.4 Rules

1. **No posting.** The cash-flow plan writes nothing to the GL, AR or AP. It is a projection over facts those modules
   own.
2. Actuals are **read** from Accounting, never copied as truth into `CashFlowLine` — the line stores a snapshot for
   period comparison and is always re-derivable.
3. Visualisation is the **second tab's** (Reporting) work. Construction supplies the data source
   (`Stage-Construction-13`), not the chart.
4. Currency: the plan is stated in the **contract currency** and in the company functional currency, using Accounting's
   FX — construction performs no conversion of its own (and must not add a sixth `Currencies.First()` site, CR-10).

## 5. What must be reported together

A construction status view is only honest when these appear on the same page: physical % (with its method named),
financial %, certified %, paid %, budget vs committed vs actual vs EAC, and the current forecast completion date with
awarded EOT. Any one of them alone can be made to look acceptable while the project is failing — which is the reason the
brief separates them.

## 6. Open decisions

| Decision | Effect |
|---|---|
| **D-11** physical progress method | Which methods must ship at C6 and which node types default to which. |
| **D-12** forecast methodology | Whether forecast is manual-authoritative (recommended) or computed. |
| Cash-flow period granularity (weekly vs monthly) | Screen 28's unresolved column; monthly recommended, weekly for the next quarter. |