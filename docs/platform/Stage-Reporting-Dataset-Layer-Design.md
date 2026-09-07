# Stage — Reporting: The Reusable Dataset Layer

**Platform:** CrossBusiness Reporting Platform
**Companion to:** `ADR-037`, `RPS-001`, `Stage-Reporting-Expression-Language-Design.md`
**Code:** `CrossBuy/BL/Reporting/ReportDataset.cs`, `ReportDatasetRegistry.cs`
**Tests:** `CrossBuy.Tests/ReportingDatasetTests.cs` — 33
**Status:** contracts implemented; **no production module connected**, by owner decision 7.

---

## 1. Why a dataset layer

Before it, a `ReportDefinition` named a `DataSourceKey` directly — so "the shape of the data" and "this particular
report" were the same object. Fine for a fixed catalogue, fatal for a Report Studio: twelve receivables reports
would each redeclare the same twenty columns, the same six parameters and the same permission, and each would
drift.

A **dataset** is the reusable middle. One dataset declares fields, parameters, security, filters and aggregates
**once**; many reports and many Studio layouts select from it.

> A dataset is to a report what a database **view** is to a query — except that it is code-first,
> permission-bearing, version-stamped, and it never emits SQL.

---

## 2. The pipeline, and each stage's single responsibility

```
ReportDefinition   WHICH report: code, title, permission, chosen dataset, defaults
       ↓
DatasetDefinition  WHAT data is available: fields, parameters, sensitivity, allowed operations
       ↓
IReportDataSource  FETCHES authorized rows. The only stage that touches a module.
       ↓
ReportTemplate     HOW it is laid out: chosen columns, order, grouping, page setup. Stored, versioned.
       ↓
IReportRenderer /  Produces bytes: HTML, PDF, CSV, XLSX, JSON.
IReportExporter
```

**A stage may narrow what the previous stage allowed; no stage may widen it.** That single rule is what makes the
security review tractable.

### 2.1 The six concepts, distinguished

| Concept | Is | Is not | Reusable across |
|---|---|---|---|
| **Report Definition** | a named, permissioned report | a layout, a query | — |
| **Dataset Definition** | the declared shape + security of some data | a query, a layout | **many reports** |
| **Data Source** | code that fetches authorized rows for a dataset | a shape declaration | one dataset (swappable) |
| **Template** | a stored layout: columns, order, grouping, page setup | data, permission | many runs; scoped + versioned |
| **Renderer** | turns a shaped result into bytes for a format | a data reader | every report |
| **Exporter** | a renderer specialised for data formats (CSV/XLSX) | a viewer | every report |
| **Dashboard Widget** | a saved report + a presentation mode + a refresh interval | a second query path | — |

Two distinctions people get wrong, stated explicitly:

- **Dataset ≠ Data Source.** The dataset says *what exists and who may see it*; the source *gets the rows*. They
  are separate so a module can swap a source implementation — a cached one, a read-replica one — without the
  dataset contract, and therefore every saved report, changing.
- **Template ≠ Report.** A template is presentation. It carries no permission, no company override and no data
  handle — asserted by `A_stored_template_carries_no_permission_and_no_data_source_handle`. If it did, editing a
  layout would be a way to edit authorization.

---

## 3. What a dataset declares

| Element | Type | Notes |
|---|---|---|
| `DatasetCode` | frozen `<Module>.<Area>.<Dataset>` | persisted in saved Studio reports — a published contract |
| `Version` | `Major.Minor` | §7 |
| `Module`, titles, descriptions | bilingual | |
| `DataSourceKey` | resolves the fetcher | |
| `RequiredPermissionKey` | **required** | an empty key fails validation — "forgot to set it" must not become "data anyone can query" |
| `Fields` | `ReportDatasetField[]` | §4 |
| `Parameters` | existing `ReportParameterDescriptor[]` | reused, so binding/validation is unchanged |
| `DrillDowns` / `DrillThroughTargets` | §6 | |
| `MaxRows`, `RowCapPolicy` | ceiling + what happens at it | the engine takes the **minimum** of this and the report's |
| `ExportPolicy` | `Unrestricted` / `RequirePermissionForDataExport` / `NoDataExport` | |
| `AvailableInStudio`, `AvailableAsWidget` | separate decisions | a widget runs unattended on a shared screen |
| Deprecation notice + `SupersededBy` | bilingual | §7 |

### 3.1 A report over a dataset needs BOTH permissions

The report's own `PermissionKey` **and** the dataset's `RequiredPermissionKey`. That is what stops a
low-permission report from becoming a window onto high-permission data.

---

## 4. Fields — the data contract, distinct from the rendering contract

`ReportDatasetField` is a superset of `ReportColumn`: everything a column needs, plus what only the *data* can
know. `ToColumn()` projects one to the other, so a report never restates column metadata by hand.

### 4.1 Sensitivity

| Level | Meaning | Gate |
|---|---|---|
| `Normal` | ordinary business data | the report's permission |
| `Confidential` | cost, margin, supplier price, credit limit | **+ the field's own `RequiredPermissionKey`** |
| `Restricted` | salary, national id, bank account, medical note | same, **plus** excluded from bulk export without the export permission |
| `Never` | a technical key the dataset needs to group or join | **nobody, ever** — including administrators |

Why four and not a boolean: "cost" is readable by a purchasing manager and not by a salesperson, while "IBAN" is
readable by almost nobody and must never reach a CSV. One flag cannot express that, and a per-report ACL would
have to be restated on every report over the same data.

**A sensitive field with no permission key fails validation.** That combination — marked Confidential, everyone
assumes it is protected, nothing checks anything — is the one that looks safe and is not.

**`Never` projects to `ReportColumn.Internal = true`**, so the data layer's exclusion and the rendering layer's
agree *by construction* rather than by two flags somebody keeps in step. An export that includes a column the
screen hides is the classic reporting leak.

### 4.2 Declared capabilities

`Filterable` · `Sortable` · `Groupable` · `SupportedAggregates` · `SupportedOperators`.

These are capabilities of the **data**, not preferences of a report. Summing a percentage or an exchange rate
produces a meaningless number, so the dataset says which aggregates make sense rather than letting a UI offer
`Sum` on everything.

Operators default to the field type's natural set and may be **narrowed, never widened** — a widening is a
validation error. `EntityRef` deliberately offers no `Contains`/`StartsWith`: a substring match against an entity
id is never what the user meant, and offering it invites a report that half-works.

### 4.3 Calculated fields

`Expression` (REX) + `DependsOn`. Three rules, all enforced by the validator:

1. **Not filterable or sortable.** The source never produced the column, and the shaper computes it *after*
   filtering — so a "filterable" calculated field would silently filter on nulls.
2. **`DependsOn` is mandatory.** Without it the source would not know which underlying fields to fetch, and the
   expression would evaluate against nulls and produce a plausible wrong number.
3. **No dependency cycles.** Detected by an **iterative** DFS — a recursive walk would stack-overflow on exactly
   the input the check exists to catch.

---

## 5. Startup validation

Every rule below would otherwise fail at **run** time, on a user's screen, with a message about an internal key.
`ReportDatasetRegistry` validates in its **constructor**, so each becomes a wiring error the author sees first —
the same discipline `ReportCatalog` and `ReportDataSourceRegistry` already apply.

Checked: empty code / source key / permission key · no fields · duplicate field keys · sensitive-but-ungated ·
calculated-and-filterable · calculated-without-DependsOn · unknown dependency · dependency cycle ·
`EntityRef` without `LookupEntityCode` · operator invalid for type · drill-down level unknown or not groupable ·
drill-through mapping from an unknown field · `SystemSupplied` + `Required` with no default · negative `MaxRows` ·
superseded-without-deprecation.

**Duplicate dataset codes throw at construction.** Last-registration-wins would let one module silently hijack
another's dataset — and with it that dataset's permission key.

---

## 6. Drill-down vs drill-through — two things constantly confused

| | Stays in | Needs |
|---|---|---|
| **Drill-down** | the **same** dataset, expanding a grouping hierarchy (Region → Branch → Salesperson) | groupable levels only |
| **Drill-through** | a **different** report, carrying row values as parameters (customer balance → statement) | a field→parameter map |

The drill-through map is deliberately a **field→parameter map, not a URL template**. A template would let an
author compose a query string, and a query string is where a caller-supplied `CompanyId` would sneak back in. The
target report re-authorizes independently in any case — arriving by drill-through is not evidence of permission.

---

## 7. Versioning and deprecation

A dataset is referenced by stored templates, saved Studio reports and schedules, so its shape is a published
contract. Two numbers, because the two kinds of change have different blast radii:

| Change | Bump | Effect on stored reports |
|---|---|---|
| Add a field, widen an operator set, add a drill target | **Minor** | keep working |
| Remove or rename a field, narrow a type, tighten sensitivity | **Major** | must be migrated or fail validation |

**Deprecation** is the pair that matters: a deprecated dataset **leaves the Studio picker** but **still
resolves**, so existing saved reports keep working while no new ones can be created over it. Asserted by
`A_deprecated_dataset_leaves_the_studio_but_keeps_resolving`.

`AvailableInStudio = false` is the same pair for a different reason: available to hand-written platform reports,
withheld from self-service, for data whose correct interpretation depends on domain knowledge a builder cannot
convey.

---

## 8. Test coverage — 33 tests

| Area | Tests |
|---|---|
| Validator: well-formed, sensitive-ungated, calculated rules, cycles, duplicates, EntityRef, drill-down/through, system parameters, superseded | 13 |
| Field visibility: `Never` hides from everyone incl. administrators; Confidential needs its own key; `VisibleFields` filtering; projection to `Internal`/non-filterable | 5 |
| Operator surface: text vs numeric, EntityRef exclusions, narrow-not-widen | 3 |
| Registry: duplicate codes, invalid-at-construction, unknown code, **empty registry**, Studio filtering by permission, withheld, deprecated, unresolved company | 10 |
| Versioning | 2 |

`An_empty_registry_offers_nothing_to_the_studio` is the one that makes owner decision 7 — *no production data
source connected* — a **mechanical check** rather than a claim.

---

## 9. Adding a dataset (the next increment's whole surface)

```csharp
services.AddScoped<IReportDatasetDefinition>(_ => new ReportDatasetDefinition
{
    DatasetCode = "Accounting.Receivables.Aging",
    Module = "Accounting",
    TitleAr = "أعمار الذمم المدينة",  TitleEn = "Receivables aging",
    DataSourceKey = "Accounting.Receivables.Aging",
    RequiredPermissionKey = "accounting.reports.view",
    Fields = new[]
    {
        new ReportDatasetField { Key = "CustomerName", TitleAr = "العميل", TitleEn = "Customer", Groupable = true },
        new ReportDatasetField
        {
            Key = "Balance", TitleAr = "الرصيد", TitleEn = "Balance", Type = ReportFieldType.Money,
            SupportedAggregates = new[] { ReportAggregate.Sum },
        },
        new ReportDatasetField
        {
            Key = "CreditLimit", TitleAr = "حد الائتمان", TitleEn = "Credit limit",
            Type = ReportFieldType.Money,
            Sensitivity = ReportFieldSensitivity.Confidential,
            RequiredPermissionKey = "accounting.reports.credit",
        },
    },
    MaxRows = 50_000,
    RowCapPolicy = ReportRowCapPolicy.FailTheRun,   // a partial receivables report is a wrong one
});

services.AddScoped<IReportDataSource, ReceivablesAgingDataSource>();
```

Two registrations. Nothing in the platform changes.

---

## 10. Open decisions for the owner

| # | Decision | Recommendation |
|---|---|---|
| D1 | May one dataset compose two sources (a join)? | **No in v1.** A join has its own authorization and cardinality questions; a module declaring a combined dataset is the safe form. |
| D2 | Should `MaxRows` default to `FailTheRun` for anything financial? | Yes. A silently partial trial balance is a wrong trial balance, and "declared" is not enough when somebody will sign it. |
| D3 | Per-row (row-level) security inside a dataset? | Today the data source enforces it via `BusinessContext`. A declarative row filter on the dataset would be clearer but needs its own design. |
| D4 | Should `ExportPolicy` default to `RequirePermissionForDataExport` when any field is Confidential? | Recommended — make the safe pairing automatic rather than remembered. Currently the author sets both. |
