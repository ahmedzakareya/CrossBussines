# Stage-Construction-13 — Reporting Requirements

Covers Phase 21. **This tab builds no renderers, no report definitions and no screens.** It defines the read-only data
sources and the secure DTOs that the **SECOND TAB** (Reporting Platform / Report Studio) will consume.

---

## 1. The contracts we must fit

Read from the live reporting platform (`CrossBuy/BL/Reporting/`, 26 files):

- **`IReportDataSource`** (`BL/Reporting/ReportDataSource.cs:170-179`) — `Key` by the convention
  `"<Module>.<Thing>"`, and one method `FetchAsync(ReportDataQuery, CancellationToken)`. Its own comment states the rule
  we must honour: *"Must be READ-ONLY. A data source may not write: not the GL … not stock … not its own audit row.
  Reads should be `AsNoTracking`."*
- **`ReportDefinition`** (`BL/Reporting/ReportMetadata.cs:207`) — `Code`, `Module`, `TitleAr`/`TitleEn`,
  `DescriptionAr`/`En`, `DataSourceKey`, `PermissionKey`, `CategoryKey`, `Tags`, `Icon`, `SortOrder`, `Columns`,
  `Parameters`, `Capabilities`.
- **`ReportColumn`** — `Key`, `TitleAr`/`TitleEn`, `Type`, `Format`, `Align`, `WidthMm`, `VisibleByDefault`,
  `Filterable`, `Sortable`, `Groupable` (opt-in), `Aggregate`, `LookupEntityCode`, **`Internal`**.
- **`ReportDataSourceRegistry`** — two sources claiming one key is a **wiring error that fails at graph construction**
  (`ReportDataSource.cs:205-206`), so construction keys must be unique and namespaced.
- The platform deliberately ships **no module data sources yet** (`BL/Reporting/PlatformReportDefinitions.cs:14-19`) —
  module data sources are "a later, separately reviewed change". This document is construction's input to that review.

## 2. Non-negotiable rules for construction data sources

1. **Read-only.** `AsNoTracking`, no writes of any kind, not even an audit row.
2. **Company-scoped always**, from the resolved context — never a parameter default, never company 1.
3. **Project scope enforced in the data source**, not in the screen: a caller with membership-only scope sees only their
   projects (the platform's own catalog source sets this precedent — permission is enforced *per row*, not per screen).
4. **Commercial confidentiality is a column concern.** Rate, margin, subcontractor rate and cost columns are marked
   `Internal` and/or gated behind the `cost.confidential` permission (`Stage-Construction-11` §2). A report that a site
   engineer may open must not leak margin because the column happened to be in the dataset.
5. **No derived truth.** A data source reports what the entities hold; it does not recompute a certificate or invent a
   forecast. Where a figure is derived (EAC, variance), the derivation is documented in the DTO and identical to the
   service's.
6. **Never derive physical progress from financial value** (`Stage-Construction-10`), including in reports.

## 3. Data source keys (proposed)

Namespace `Construction.*`, one key per DTO shape:

| Key | Feeds |
|---|---|
| `Construction.BoqSummary` | BOQ Summary |
| `Construction.CostControl` | Budget vs Committed vs Actual; Cost Code Analysis; Forecast at Completion |
| `Construction.Profitability` | Project Profitability |
| `Construction.ClientCertificateRegister` | Client Certificate Register |
| `Construction.SubcontractorCertificateRegister` | Subcontractor Certificate Register |
| `Construction.RetentionLedger` | Retention Report |
| `Construction.VariationRegister` | Variation Register |
| `Construction.ClaimRegister` | Claim Register; Delay Events |
| `Construction.MaterialConsumption` | Material Consumption |
| `Construction.DailySiteReport` | Daily Site Report |
| `Construction.Manpower` | Manpower Report |
| `Construction.EquipmentUtilization` | Equipment Utilization |
| `Construction.RfiRegister` | RFI Register |
| `Construction.InspectionRegister` | Inspection Register |
| `Construction.DocumentRegister` | Drawing Register |
| `Construction.Progress` | Physical vs Financial Progress |
| `Construction.CashFlow` | Project Cash Flow |
| `Construction.ExecutiveDashboard` | Executive Project Dashboard |

## 4. The twenty report contracts

For each: the DTO's significant columns, required parameters, and the columns that are **confidential**.

| # | Report | DTO columns (significant) | Parameters | Confidential columns |
|---|---|---|---|---|
| 1 | **BOQ Summary** | WbsPath, BoqCode, Description, Unit, ContractQty, Rate, ContractValue, ExecutedQty, CertifiedQty, RemainingQty, Percent, RevisionNo | Project*, ContractId, RevisionId, AsOfDate | Rate, ContractValue (where the viewer lacks `financial.detail`) |
| 2 | **Budget vs Committed vs Actual** | WbsPath, CostCode, Category, Budget, Committed, Accrued, Actual, Forecast, ETC, EAC, Variance, VariancePct | Project*, DateFrom, DateTo, CostCode, WbsNode | all money columns require `cost.read`; margin-bearing require `cost.confidential` |
| 3 | **Cost Code Analysis** | CostCode, Category, Budget, Actual, Variance, ActualPctOfTotal, TopSources | Project*, Period, Category | as #2 |
| 4 | **Project Profitability** | Project, Revenue, Cost, GrossMargin, MarginPct, CertifiedValue, UninvoicedWork | Project, DateFrom*, DateTo* | GrossMargin, MarginPct — `cost.confidential` |
| 5 | **Client Certificate Register** | CertificateNo, Date, Status, GrossWork, MOS, Tax, Retention, AdvanceRecovery, LD, Deductions, NetDue, InvoiceNo, PaidAmount, Outstanding | Project, ContractId, DateFrom, DateTo, Status | none beyond `financial.detail` |
| 6 | **Subcontractor Certificate Register** | Subcontract, Vendor, CertificateNo, Date, Status, GrossWork, Retention, NetPayable, InvoiceNo, PaidAmount, CumulativeVsScope | Project, SubcontractId, Vendor, DateFrom, DateTo | SubRate, CumulativeVsScope margin view — `cost.confidential` |
| 7 | **Retention Report** | Project, Contract, WithheldToDate, ReleasedToDate, Balance, ReleaseTrigger, DlpEndDate, DueDate; same for subcontractor side (2105) | Project, AsOfDate*, Party (Client/Sub) | none |
| 8 | **Variation Register** | VoNo, Date, Originator, Reason, Status, CostImpact, TimeImpactDays, BoqRevisionNo, IncludedInCertificateNo | Project, Status, DateFrom, DateTo | CostImpact where `financial.detail` absent |
| 9 | **Claim Register** | ClaimNo, Type, Direction, Cause, ResponsibleParty, NoticeDate, NoticeWithinTime, Claimed, Assessed, Settled, AwardedDays, Status | Project, Status, Direction | Assessed, Settled — `cost.confidential` |
| 10 | **Material Consumption** | Item, Unit, IssuedQty, ReturnedQty, NetQty, WasteQty, WastePct, Value, WbsPath, CostCode, BoqCode, Site | Project*, DateFrom, DateTo, Site, Item, WbsNode | Value — `financial.detail` |
| 11 | **Daily Site Report** | Date, Site, Weather, WorkingHours, ManpowerTotal, EquipmentTotal, QuantitiesExecuted, MaterialsReceived, Delays, Incidents, Status | Project*, Site, DateFrom*, DateTo* | cost rates (excluded from the DTO entirely) |
| 12 | **Manpower Report** | Date, Site, Trade, Source, Headcount, RegularHours, OvertimeHours, WbsPath, Cost, ProductivityQtyPerHour | Project*, Site, DateFrom*, DateTo*, Trade | Cost — `financial.detail`; per-employee cost — `cost.confidential` |
| 13 | **Equipment Utilization** | Asset/Description, Owned/Rented, Site, WorkingHours, IdleHours, DowntimeHours, UtilizationPct, Fuel, Cost, CostPerHour | Project, Site, DateFrom*, DateTo*, Asset | Cost, CostPerHour — `financial.detail` |
| 14 | **RFI Register** | RfiNo, Discipline, Subject, RaisedBy, RaisedAt, DueDate, DaysOpen, Overdue, Status, AnsweredAt, LinkedVariation | Project*, Status, Discipline, Overdue | none |
| 15 | **Inspection Register** | IrNo, Type, Discipline, WbsPath, Location, RequestedAt, InspectedAt, Result, Reinspections, Status | Project*, Status, Result, DateFrom, DateTo | none |
| 16 | **Drawing Register** | DocumentNo, Title, Discipline, Type, CurrentRevision, RevisionStatus, IssueDate, ReceivedDate, SupersededRevisions, DistributionCount | Project*, Discipline, Type, Status | Confidentiality-restricted documents filtered by row |
| 17 | **Physical vs Financial Progress** | WbsPath, Method, PlannedPct, PhysicalPct, FinancialPct, CertifiedPct, PaidPct, Variance, ForecastCompletion | Project*, AsOfDate*, WbsNode | financial/certified/paid columns — `financial.detail` |
| 18 | **Project Cash Flow** | Period, Category, Direction, Planned, Committed, Actual, Forecast, Net, Cumulative, Scenario | Project*, PeriodFrom*, PeriodTo*, Scenario, Granularity | all — `cost.read` + `financial.detail` |
| 19 | **Forecast at Completion** | WbsPath, CostCode, Budget, Actual, ETC, EAC, Variance, ForecastMethod, ForecastBy, ForecastAt | Project*, AsOfDate* | all — `cost.confidential` |
| 20 | **Executive Project Dashboard** | Project, Client, ContractValue, RevisedValue, PhysicalPct, CertifiedPct, PaidPct, EAC, MarginPct, ForecastCompletion, OpenVariations, OpenClaims, OverdueRfis, SafetyIncidents, Status, RagFlag | Company*, Branch, Status, PortfolioFilter | MarginPct, EAC — `cost.confidential` |

`*` = required parameter. Every parameter carries `LookupEntityCode` where it is an entity picker (Project, Site,
Vendor, WbsNode), matching the platform's `ReportParameter` contract.

## 5. Shared DTO conventions

1. **Localised titles.** Every column supplies `TitleAr` and `TitleEn`; the DTO exposes both `Name` and `NameEn` for
   every named entity, following the codebase convention (e.g. `BoqItem.DescriptionEn`, `Boq.cs:17`).
2. **RTL/LTR safe formats.** Numbers, dates and currency use the platform's format tokens, never a hand-formatted
   string.
3. **Currency-explicit.** Every money column names its currency; a project reported in contract currency and company
   currency states both. No implicit conversion inside a data source.
4. **Rounding.** Amounts round through the currency rounding rule with explicit `AwayFromZero` — never a baked-in
   2-decimal assumption. (The construction services already use `MidpointRounding.AwayFromZero`, e.g.
   `BL/ProgressBillingService.cs:64`.)
5. **Null means unknown.** A planned percent with no baseline is `null`, never `0` — a zero reads as "no progress
   planned" and is a lie.
6. **`AsOfDate` is honoured for real.** A register asked as of a past date must exclude later documents, which is only
   possible because certificates carry revision snapshots (`Stage-Construction-06` §4.3).
7. **Row-level security inside `FetchAsync`** — project scope, confidentiality and branch scope are applied in the
   query, so an export cannot exceed a screen.

## 6. What the second tab is asked for

1. Review these keys and DTOs as a batch (they are additive; no existing report changes).
2. Report definitions, categories, permission keys and renderers per report.
3. A decision on where `Internal`/confidential columns are enforced — the platform's `ReportColumn.Internal` flag, the
   `PermissionKey`, or both. Construction's requirement is only that **one of them is guaranteed** before a
   margin-bearing report ships.

## 7. What construction will not do

No `ReportDefinition` instances, no renderer, no category rows, no Report Studio work, and no data source implemented in
this increment — Phase C9 in the roadmap, after the data those sources read actually exists.