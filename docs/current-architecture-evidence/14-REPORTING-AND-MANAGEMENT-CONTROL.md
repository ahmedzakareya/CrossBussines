# 14 — Reporting and Management Control

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


## Reporting infrastructure

Substantial and well-factored — 42 files under `BL/Reporting/`.

| Layer | Types |
|---|---|
| Catalogue | `IReportCatalog`, `ReportCatalog`, `IReportDefinitionProvider`, `PlatformReportDefinitionProvider` |
| Datasets | `ReportDatasetRegistry`, `ReportDataset`, `ReportDataSource` |
| Execution | `ReportEngine`, `ReportService`, `ReportParameterBinder`, `ReportDataShaper` |
| Rendering | `HtmlReportRenderer`, `PlaywrightPdfReportRenderer`, `IReportVisualRenderer`, `ReportRendererRegistry` |
| Export | `CsvReportExporter`, `ExcelReportExporter`, `ReportExportEngine`, `ReportOutputPipeline` |
| Delivery | `ReportDelivery`, `ReportPrintService`, `BrowserPrintTransport`, `IReportMailSender` → **`NullReportMailSender`** |
| Scheduling | `ReportScheduleService`, `ReportScheduleCalculator` |
| Archive | `IReportArchiveStore` → `FileSystemReportArchiveStore`, `ReportArchiveService` |
| Studio | `ReportStudioService`, `ReportTemplateService`, `ReportLibraryService` |
| Authorization | `ReportAuthorizationService` |
| History | `ReportHistoryService` (raises events) |
| Workspace feed | `ReportingWorkspaceSource` |

Controllers: `ReportsController.cs`, `Api/ReportStudioApiController.cs`,
`Api/ReportsCenterApiController.cs`, `Api/ReportsCenterWriteEndpoints.cs`.
Views: `Reports/Index`, `Reports/Studio`, `Reports/Viewer`, `Reports/_ReportCard`.

## Registered dataset providers — five

| Provider | Datasets |
|---|---|
| `AccountingDatasets.cs` | accounting |
| `InventoryDatasets.cs` | inventory |
| `CrmDatasets.cs` | **2** — `Crm.Leads`, `Crm.Opportunities` |
| `HrRosterDatasets.cs` | HR roster |
| `BusinessEventsDataset.cs` | the outbox itself |

**There is no Task dataset.** No `TaskDatasets.cs`, and no Task entry in the registry.

## Module-level report screens

| Module | Screens |
|---|---|
| Inventory | `Reports`, `ValuationReport`, `StagnantReport`, `ReorderReport`, `ManufReports` |
| CRM | `Reports` (360°) |
| Tasks | `Reports`, `HoursReport` — backed by `BL/TaskReportService.cs` |
| Workspace | `Reports` — backed by `ReportingWorkspaceSource` |
| Platform | `Reports/Index`, `Reports/Studio`, `Reports/Viewer` |

So Task reporting **does** exist as two bespoke screens over `TaskReportService`; it does **not** exist
as a Report Studio dataset, which is what makes it composable, schedulable, exportable and
permission-governed like every other report.

## Management control — the eleven questions

`TaskManagementQueryService` (phase4) answers six of these at the service layer. It is DI-registered at
`Program.cs:683` and **wired to no controller, view or report** — so today the answers exist in code
and nowhere a manager can reach them.

| # | Question | Classification | Evidence |
|---|---|---|---|
| 1 | What work is overdue? | **ANSWERABLE NOW** (query only) | `TaskLifecycle.OverdueAt`; `TeamOverdueAsync` |
| 2 | Who owns it? | **ANSWERABLE NOW** | `TaskItem.AssigneeEmployeeId` |
| 3 | Which manager is responsible? | **PARTIALLY** | `OrgHierarchy.DirectManagerAsync` resolves it, but the tree has no company column so each caller must re-filter |
| 4 | Which tasks are escalated? | **ANSWERABLE NOW** | `TeamEscalatedAsync` |
| 5 | What is due soon? | **ANSWERABLE NOW** | `TeamDueSoonAsync` |
| 6 | Who is overloaded? | **PARTIALLY** | `WorkloadAsync` gives counts. The service says explicitly: *"Deliberately no 'capacity' or 'utilisation': the platform has no hours budget and no availability model, and a number invented from nothing is worse than an absent one."* |
| 7 | Which module creates most work? | **NOT ANSWERABLE** | no module/source column on `TaskItem`; automated doors raise no event, so the outbox cannot answer it either |
| 8 | Which work is unassigned? | **ANSWERABLE NOW** | `UnassignedAsync` |
| 9 | Which tasks came from automation? | **PARTIALLY — REQUIRES QUERY** | `TaskAutoLog.RuleKey` links automation to tasks for the generator and orchestration doors; **not** for document expiry's own rows or manual tasks. A join, not a column |
| 10 | Which tasks came from which module? | **REQUIRES SCHEMA** | nothing on `TaskItem` records the producing module |
| 11 | Which SLA is being missed? | **NOT ANSWERABLE** | only `CrmSlaPolicy` exists, scoped to CRM tickets; there is no task SLA model |

Six answerable, three partial, two not answerable, one needs schema.

## Is Reporting ready to consume Task/management data without schema changes?

**No.** Two independent blockers, neither of which is a schema change to `TaskItem`:

1. **No Task dataset is registered**, so Report Studio cannot see tasks at all. This is additive work
   in `BL/Reporting/`, not DDL.
2. **`TaskManagementQueryService` has no consumer**, so even the six answerable questions have no
   surface.

Questions 7 and 10 *do* require schema — a source/module discriminator on `TaskItem`.
