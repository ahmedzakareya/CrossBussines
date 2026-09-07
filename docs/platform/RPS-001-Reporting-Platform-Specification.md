# RPS-001 — Reporting Platform Specification

**Product:** CrossBusiness Platform
**Companion to:** `ADR-037-Reporting-Platform-Architecture.md` (decisions and rationale live there)
**This document:** the 32 requested components, mapped to their contracts and files, with each surface stated.
**Status:** architecture complete — awaiting review.

---

## 0. How to read this

ADR-037 answers *why*. This answers *what and where*. Every component below names the file it lives in and the
contract a caller or an extender touches.

Two conventions apply throughout, and they are worth stating once rather than 32 times:

* **Interface + implementation + DTOs are co-located in one file per service**, matching the existing
  feature-module convention in this codebase.
* **A failure is a result, not an exception.** `ReportResult` carries a status and machine-coded diagnostics.
  Exceptions are reserved for *programming* errors — an unregistered report code, a missing data source, an
  unavailable engine — because turning those into empty results would let a broken deployment look normal.

---

## 1. Component map

All paths are relative to `CrossBuy/`.

| # | Component | Contract | File |
| --- | --- | --- | --- |
| 1 | Report Engine | `IReportEngine` | `BL/Reporting/ReportEngine.cs` |
| 2 | Report Catalog | `IReportCatalog` | `BL/Reporting/ReportCatalog.cs` |
| 3 | Report Metadata | `ReportDefinition`, `ReportColumn`, `ReportCapabilities` | `BL/Reporting/ReportMetadata.cs` |
| 4 | Report Template Model | `ReportTemplate`, `ReportLayout` | `Models/Context/Reporting/ReportTemplate.cs`, `BL/Reporting/ReportQuery.cs` |
| 5 | Report Versioning | `ReportTemplateVersion`, `IReportTemplateService` | `Models/Context/Reporting/ReportTemplate.cs`, `BL/Reporting/ReportTemplateService.cs` |
| 6 | Report Renderer Abstraction | `IReportRenderer`, `IReportRendererRegistry` | `BL/Reporting/ReportRenderer.cs` |
| 7 | HTML Renderer | `HtmlReportRenderer` | `BL/Reporting/HtmlReportRenderer.cs` |
| 8 | Playwright PDF Renderer | `PlaywrightPdfReportRenderer`, `IHtmlToPdfConverter` | `BL/Reporting/PlaywrightPdfReportRenderer.cs` |
| 9 | Export Engine | `IReportExportEngine`, `IReportOutputPipeline` | `BL/Reporting/ReportExportEngine.cs` |
| 10 | Excel Export | `ExcelReportExporter` | `BL/Reporting/ExcelReportExporter.cs` |
| 11 | CSV Export | `CsvReportExporter` | `BL/Reporting/CsvReportExporter.cs` |
| 12 | Print Service | `IReportPrintService`, `IReportPrintTransport` | `BL/Reporting/ReportPrintService.cs` |
| 13 | Report Scheduling Architecture | `IReportScheduleService`, `IReportScheduleCalculator`, `IReportScheduleRunner` | `BL/Reporting/ReportScheduleService.cs` |
| 14 | Email Delivery Architecture | `IReportDeliveryService`, `IReportDeliveryChannel`, `IReportMailSender` | `BL/Reporting/ReportDelivery.cs` |
| 15 | Report Permissions Model | `IReportAuthorizationService`, `IReportPermissionEvaluator` | `BL/Reporting/ReportAuthorizationService.cs` |
| 16 | Report DTO Contracts | `ReportRequest`, `ReportResult`, `ReportArtifact` | `BL/Reporting/ReportContracts.cs` |
| 17 | Report Parameter Engine | `IReportParameterBinder`, `ReportParameterSet` | `BL/Reporting/ReportParameterEngine.cs` |
| 18 | Report Filters | `ReportFilter`, `ReportFilterOperator` | `BL/Reporting/ReportQuery.cs` |
| 19 | Report Grouping | `ReportGrouping`, `ReportGroupNode` | `BL/Reporting/ReportQuery.cs`, `BL/Reporting/ReportDataShaper.cs` |
| 20 | Report Sorting | `ReportSort` | `BL/Reporting/ReportQuery.cs` |
| 21 | Report Preview Pipeline | `IReportService.PreviewAsync`, `ReportRunKind.Preview` | `BL/Reporting/ReportService.cs` |
| 22 | Report Archive Architecture | `IReportArchiveService`, `IReportArchiveStore` | `BL/Reporting/ReportArchiveService.cs` |
| 23 | Report History | `IReportHistoryService`, `ReportRun` | `BL/Reporting/ReportHistoryService.cs` |
| 24 | Report Favorite System | `IReportLibraryService` (favourites), `ReportFavorite` | `BL/Reporting/ReportLibraryService.cs` |
| 25 | Report Categories | `ReportCategory`, `ReportCategoryNode` | `Models/Context/Reporting/ReportCategory.cs`, `BL/Reporting/ReportLibraryService.cs` |
| 26 | Report Tags | `ReportTag`, `ReportTagLink` | `Models/Context/Reporting/ReportTag.cs`, `BL/Reporting/ReportLibraryService.cs` |
| 27 | Report Ownership | `ReportTemplate.OwnerEmpId`, `TransferOwnershipAsync` | `BL/Reporting/ReportLibraryService.cs` |
| 28 | Report Sharing | `ReportShare`, `ShareAsync` | `Models/Context/Reporting/ReportShare.cs`, `BL/Reporting/ReportLibraryService.cs` |
| 29 | Personal Templates | `ReportTemplateScope.Personal` | `BL/Reporting/ReportTemplateService.cs` |
| 30 | Team Templates | `ReportTemplateScope.Team` + `IReportTeamResolver` | `BL/Reporting/ReportTemplateService.cs`, `BL/Reporting/ReportAuthorizationService.cs` |
| 31 | Company Templates | `ReportTemplateScope.Company` | `BL/Reporting/ReportTemplateService.cs` |
| 32 | Platform Templates | `ReportTemplateScope.Platform` (`CompanyID = 0`) | `BL/Reporting/ReportTemplateService.cs` |

Components 29–32 are **one mechanism**, not four features — see ADR-037 §6. Building them as four tables would have
produced four copies of "find the default, check access, load the version, fall back", and they would have diverged.

### 1.1 Supporting files (not on the numbered list, but part of the platform)

| File | Role |
| --- | --- |
| `BL/Reporting/ReportService.cs` | The façade. **The only public door.** |
| `BL/Reporting/ReportDataSource.cs` | `IReportDataSource`, `ReportDataSet`, `ReportRow`, registry, `StaticReportDataSource` |
| `BL/Reporting/ReportDataShaper.cs` | Filter → sort → group → aggregate → cap |
| `BL/Reporting/ReportValues.cs` | Value semantics: text→typed, compare, format, `IReportClock` |
| `BL/Reporting/PlatformReportDefinitions.cs` | The platform's own two reports + their data sources |
| `BL/Reporting/ReportingRegistration.cs` | `AddCrossBusinessReporting()` — the one DI line |
| `deploy/sql/reporting_platform.sql` | 12 tables, idempotent, additive |

---

## 2. Report DTO Contracts (#16)

### `ReportRequest`

```csharp
string  ReportCode;                             // required, frozen catalog code
int?    TemplateId;                             // null = resolve by precedence
int?    TemplateVersionNo;                      // null = current; set = reproduce history exactly
ReportOutputFormat Format;                      // Html | PrintHtml | Pdf | Xlsx | Csv
ReportRunKind      Kind;                        // Full | Preview | Scheduled
IReadOnlyDictionary<string,string?> Parameters; // raw text, exactly as a query string delivers it
IReadOnlyList<ReportFilter>   Filters;          // ADDITIVE over the template's
IReadOnlyList<ReportSort>     Sorts;
IReadOnlyList<ReportGrouping> Groupings;
IReadOnlyList<string>         VisibleColumns;
ReportPageSetup? PageSetup;                     // document formats only
string?  Culture;                               // null = ambient CurrentUICulture
bool     Archive;
int?     MaxRows;                               // can only LOWER the ceiling
Guid?    CorrelationId;
```

**Note what is absent and cannot be expressed:** no `CompanyId`, no `EmployeeId`, no renderer, no connection, no
SQL, no file path. Isolation comes from the resolved `BusinessContext`.

### `ReportResult`

```csharp
ReportRunStatus  Status;        // Succeeded | Failed | Denied | Cancelled
string           ReportCode;
ReportOutputFormat Format;
ReportArtifact?  Artifact;      // FileName, ContentType, Content, ContentHash, IsInline
ReportRunSummary? Run;          // RunId, RowCount, Truncated, DurationMs, ArchiveEntryId, ParametersHash
IReadOnlyList<ReportDiagnostic> Diagnostics;   // Code (stable machine string) + Message + Severity + Field
bool IsSuccess, IsDenied;
```

`Truncated` is reported separately from `RowCount` because *"1,000 rows"* and *"1,000 rows of an unknown larger
number"* are different answers.

Diagnostic `Code` values are part of the contract and are never reworded. User-facing text is the **caller's** job
via Resources — a BL service does not localise for a view it cannot see.

---

## 3. Report Metadata (#3) and Catalog (#2)

`ReportDefinition` is the contract for one report: frozen `Code`, `DataSourceKey`, `PermissionKey`, `Columns`,
`Parameters`, `Capabilities`. **Code-first and immutable at runtime** — see ADR-037 §5 for why a definition in a
table would be a privilege escalation performed with an `UPDATE`.

**Field types** (`ReportFieldType`): `String · Integer · Decimal · Money · Date · DateTime · Boolean · Percent ·
EntityRef`. A small closed set, because each one needs a defined text form, comparison and presentation.

**`ReportColumn` flags:** `VisibleByDefault`, `Filterable`, `Sortable`, `Groupable` (opt-in), `Aggregate`,
`Internal`. `Internal` is the export-leak guard: never rendered, never exported, and — because filtering a hidden
column leaks it by inference — never filterable either.

**`ReportCapabilities`:** permitted `Formats`, `AllowSchedule`, `AllowArchive`, `AllowShare`, permitted
`TemplateScopes`, `MaxRows`, `PreviewRows`. Every one is enforced by the engine; a definition cannot be a suggestion.

**Catalog validation, in the constructor** (so a malformed definition fails at DI-graph construction):

| Check | Why |
| --- | --- |
| `Code` matches dotted PascalCase | it is a URL segment, a file-name fragment and a persisted key |
| No duplicate code (error names **both** providers) | codes are a globally unique frozen vocabulary |
| `PermissionKey` non-empty | **omission must never mean unrestricted** |
| Bilingual titles on report and every column | every user-facing string is bilingual |
| `EntityRef` names a `LookupEntityCode` | otherwise a renderer cannot build a deep link |
| Default sorts/groups/filters reference declared, non-internal, correctly-flagged columns | otherwise it fails at render time, unrecognisably |
| `SystemSupplied` and `Required` are mutually exclusive | the flag would lie to the UI |
| `DefaultFormat` ∈ `Capabilities.Formats` | otherwise the default is unproducible |

**Extension point:** `IReportDefinitionProvider` — `ProviderName` + `GetDefinitions()`. Contract: **pure**. No
`DbContext`, no `HttpContext`, no I/O, same definitions for the process lifetime. That purity is what makes the
singleton catalog safe.

---

## 4. Report Parameter Engine (#17)

The one place raw text becomes a typed, validated value. Parameters arrive from three unrelated places — a query
string, a saved layout, a schedule's frozen JSON — and all three go through this binder. That is what makes *"the
report I scheduled"* and *"the report I ran by hand"* provably the same question.

**System parameters** (`ReportSystemParameters`): `CompanyId · BranchId · EmployeeId · UserId · Culture · Now`.
Declared `SystemSupplied = true`; filled from the resolved `BusinessContext`. A caller-supplied value is **dropped
with a warning**, never coerced. `CompanyId`, `EmployeeId`, `BranchId`, `Now` and `Culture` are added **whether or
not the definition declares them**, so no data source can ever receive a set without a company.

**Relative date tokens** — required for scheduling to make sense (a daily report cannot store a literal date):

`today · yesterday · tomorrow · week-start · week-end · month-start · month-end · quarter-start · quarter-end ·
year-start · year-end`, each with an optional day offset (`today-7`, `month-start+14`).

Resolved against `IReportClock`, never `DateTime.Now`, so *"month-end on 2026-02-10 is 2026-02-28"* is assertable
without waiting for February. Month-end clamps to the real month length, including leap years.

**Diagnostic codes:** `parameter_required · parameter_invalid · parameter_not_allowed · parameter_out_of_range ·
parameter_unknown · parameter_system_supplied · parameter_single_valued`.

Notable behaviours: a required parameter with no default and no value **fails** (a trial balance silently defaulting
to "this month" is how a report gets signed for the wrong period); a comma in a single-valued parameter is an
**error**, not "take the first"; an unrecognised boolean is **refused**, not treated as false; an undeclared
parameter is a **warning** so a cache-buster in a URL cannot break a report.

### 4.1 Value semantics (`ReportValues`)

* **Invariant/ISO first, then culture.** A stored `"1234.56"` must not become 123456 in a group-separator culture,
  and `"2026-01-31"` means the same thing in every UI language.
* **Nulls sort first ascending** (stated, because the opposite convention is equally defensible and a silent change
  would reorder every report).
* **String comparison is culture-aware** — Arabic ordered by codepoint reads as unordered to its user.
* **Formatting never changes a value.** See ADR-037 §12.

---

## 5. Filters (#18), Sorting (#20), Grouping (#19)

**`ReportFilterOperator`:** `Equals · NotEquals · GreaterThan · GreaterOrEqual · LessThan · LessOrEqual · Contains ·
StartsWith · Between · In · IsNull · IsNotNull`.

Semantics that are decisions, not accidents:

| Rule | Reason |
| --- | --- |
| `Between` is **inclusive both ends** | "1 Jan to 31 Jan" means both; a half-open range silently drops the last day |
| An empty `In` list matches **nothing** | SQL agrees; returning all rows would be a spectacular leak |
| Ordering comparisons **exclude nulls** | "unknown > 100" is not true, and not false-because-zero either |
| A null cell is in **no** range | "unknown" must not satisfy a bounded question |
| Filter values are **literals**, never date tokens | a filter is part of a saved layout and must mean the same on every run |

**Sorting:** applied in list order, per-key direction. Grouping keys sort **first**, in grouping order — the group
builder walks rows in order and starts a new band on a key change, so unsorted input would produce one band per
*run* of equal keys instead of one band per key.

**Grouping:** a tree (`ReportGroupNode`), not a flat list of levels, so a renderer walks it without reconstructing
the hierarchy. Detail rows live **only at the deepest level** — an intermediate node holding its children's rows
would double every subtotal a naive renderer computed.

**Aggregates:** `Sum · Average · Min · Max · Count · CountDistinct`. `Average` is taken over rows that **have** a
value, not over all rows — a null is "not measured", and treating it as zero drags every average toward zero.
`Count` subtotals are formatted as whole numbers regardless of the column's money scale.

### 5.1 Push-down negotiation

A data source **declares** what it applied (`AppliedFilters`, `AppliedSorts`); the shaper does the remainder.
Without that declaration the platform would either double-apply or never push down. Filters are matched
**structurally** (field + operator + values), not by reference, so a source that rebuilds its filter objects — or a
deserialized template — still matches.

---

## 6. Renderers (#6, #7, #8) and Export (#9, #10, #11)

See ADR-037 §4 for the pluggability diagram and the substitution mechanism.

**`IReportRenderer`:** `EngineName`, `Formats`, `IsAvailable`, `UnavailableReason`, `RenderAsync(ReportRenderContext)`.
`UnavailableReason` is propagated verbatim by the registry, because *"PDF is unavailable"* is not actionable and
*"add a Playwright reference and run `playwright install chromium`"* is.

**`ReportRenderContext`:** the shaped `ReportView`, format, page setup, culture, `ReportBranding`, resolved title,
the **parameter strip**, `GeneratedAt`/`GeneratedBy`, `IsPreview`. Nothing else — no request, no parameters
dictionary, no `DbContext`.

The parameter strip is printed on every document deliberately: a report without its parameters cannot be
interpreted three months later, and every reporting system that omits them grows a *"which period is this?"*
problem.

**`HtmlReportRenderer`** claims `Html` (embeddable fragment) **and** `PrintHtml` (complete `@page` document, which
is also exactly what the PDF renderer converts — one layout, so preview, printout and PDF cannot drift).
Self-contained by rule: inline `<style>`, **no** external stylesheet, font URL or script. RTL/LTR from **one**
stylesheet using logical properties (`inline-start`/`inline-end`) — duplicating it per direction is how RTL parity
rots. Every interpolated value is HTML-escaped, without exception.

**Page sizes:** `A4 · A5 · Letter · Legal · Thermal80`. `Thermal80` is `80mm auto` — a continuous roll has no fixed
height, and a fixed one would clip a long receipt or pad a short one.

**`CsvReportExporter`:** RFC 4180, with two deliberate deviations for Excel (UTF-8 **BOM**, written explicitly; the
culture's list separator) and one non-optional security measure (formula-injection neutralisation for cells starting
`= + - @` or a control character).

**`ExcelReportExporter`:** reuses the existing `BL/ExcelExporter` rather than forking the product's spreadsheet
conventions. Two limitations are declared rather than discovered — see ADR-037 §10 #8.

---

## 7. Report Engine (#1) and Preview Pipeline (#21)

The ten-step pipeline is in ADR-037 §3. Two properties are worth repeating here because they are the reason the
façade exists at all:

* **Authorization is step 2, before any data is read.**
* **History is step 10, on every path** — success, failure and **denial**.

There is exactly **one failure path** (`FailAsync`), so every failure is recorded identically. A second ad-hoc
`return Failed(...)` somewhere in the pipeline is how a failure mode ends up missing from history.

**Preview** is a `Kind`, not a shortcut. `PreviewAsync` forces three things and lets the caller keep everything else:

| Forced | Effect |
| --- | --- |
| `Kind = Preview` | row cap from `Capabilities.PreviewRows`, banner on the page, recorded separately in history |
| `Format = Html` | an embeddable fragment; a preview is not a download |
| `Archive = false` | a provisional document never enters the archive (refused with a diagnostic if asked) |

`IReportService` also exposes `BrowseAsync` (catalog filtered to what the caller may see), `DescribeAsync` (metadata
for building a parameter form; returns **null** rather than throwing for an unauthorized probe) and
`AvailableFormatsAsync` (so a screen's download buttons reflect what this deployment can actually produce).

---

## 8. Data seam

**`IReportDataSource`:** `Key` + `FetchAsync(ReportDataQuery)`. Must be **read-only** — not the GL, not stock, not
even its own audit row — and should be `AsNoTracking`.

**`ReportDataQuery`** carries: `Definition`, `Context` (the resolved `BusinessContext` — a source **must** filter on
`Context.CompanyId`), typed `Parameters`, query intent it **may** push down, `RequestedColumns`, `MaxRows`, `Kind`,
`Culture`.

It is handed the whole context rather than a bare company id so a source can also honour branch/employee narrowing,
and so the fail-closed guarantee is one type rather than a convention about an `int`.

**`ReportDataSet`** returns: `Columns`, `Rows`, `Truncated`, `TotalRowCount` (**null = unknown**, an honest answer),
`AppliedFilters`, `AppliedSorts`.

`ReportRow` is an array plus a **shared** column index — one dictionary per data set, not per row. With 50k rows,
one dictionary each would be 50k dictionaries.

**The platform ships no data source over a production table.** That is a scope decision: a data source is exactly
where reporting would start reading Accounting or Inventory. The two shipped reports
(`Platform.ReportCatalog`, `Platform.ReportRunHistory`) read the catalog itself and the platform's own `ReportRuns`
— enough to exercise every layer end to end, touching no production module.

`Platform.ReportCatalog` is also the reference example of a source enforcing **row-level** visibility itself, which
is why its definition can be `Public` without leaking the existence of restricted reports.

---

## 9. Templates (#4, #5, #29–32)

Scope precedence, fork semantics and versioning immutability are in ADR-037 §6.

**`IReportTemplateService`:** `ResolveAsync · ListAsync · SaveAsync · ForkAsync · ListVersionsAsync ·
RollbackAsync · SetDefaultAsync · DeleteAsync`.

**`ReportLayout`** (the serialized version body) carries `SchemaVersion`, `VisibleColumns`, `Filters`, `Sorts`,
`Groupings`, `Parameters`, `PageSetup`, `TitleOverride`, `ShowGrandTotals`. Presentation and query intent **only** —
never data, never a data-source key, never a permission.

An **empty** `VisibleColumns` means "the definition's default visible set", which is what lets a saved layout
survive a definition gaining a column.

**Layout validation at save time**, mirroring the shaper's asymmetry:

| Problem | Severity |
| --- | --- |
| Unknown or internal **column** | Warning — the template still saves |
| Unknown, non-sortable **sort** / non-groupable **grouping** | Warning |
| Unusable **filter** | **Error** — dropping it would widen the result |
| A `SystemSupplied` **parameter** baked in | **Error** — that would be a stored cross-tenant read |

"At most one default per scope group" is enforced by the service, not a unique index: the group differs per scope
(company+report+scope, **plus** owner for Personal and team for Team), which would need one filtered index per
scope shape.

`ReportLayoutJson` uses **one shared** `JsonSerializerOptions` instance — both because `JsonSerializerOptions`
caches reflection metadata (constructing one per call is a documented performance trap) and because it guarantees
what `SaveAsync` writes is exactly what `ResolveAsync` reads.

---

## 10. Permissions (#15)

Evaluation order and the "shares raise, never open" rule are in ADR-037 §7.

**`ReportAccessLevel`** is ordered, each implying the ones below: `None → View → Run → Edit → Manage`.
**`ReportPrincipalType`:** `Employee · Role · Team · Company`.

**Level by template scope:**

| Scope | Non-owner who passed the gate | Owner | Administrator |
| --- | --- | --- | --- |
| Personal | **no access at all** | Manage | Manage (someone must clean up after a leaver) |
| Team (member) | Run | Manage | Manage |
| Company | Run | Manage | Manage |
| Platform | Run | — | Run — **Edit is refused even for an administrator** |

**Seams (all read-only, none decides authorization):**

| Seam | Shipped implementation | Behaviour |
| --- | --- | --- |
| `IReportPermissionEvaluator` | `RoleMapReportPermissionEvaluator` | role map; **unmapped key ⇒ denied** |
| `IReportTeamResolver` | `EmployeeDepartmentTeamResolver` | reads `Employee.DepartmentID`; empty = belongs to no team, never "all teams" |
| `IReportSchedulePrincipalFactory` | `IdentityReportSchedulePrincipalFactory` | reads the owner's Identity roles; an inactive employee resolves to **null** |

---

## 11. History (#23) and Archive (#22)

**`IReportHistoryService`:** `RecordAsync · QueryAsync · GetAsync · GetParametersAsync`.

`ReportRuns` is append-only with **no** `DeletedAt`. Recorded per run: report, template + **exact version**,
employee, kind, status, format, parameters (caller's text only — never the system keys), a canonical
**parameters hash**, row count, duration, archive id, error code, correlation id.

`GetParametersAsync` is what lets a user re-run *"the same report as last month"*.

Default visibility: **your own runs**, unless you administer reporting. A run row carries the parameters someone
used, which is itself sensitive.

`RecordAsync` **never throws** — the only deliberate swallow in the platform. Reasoning in ADR-037 §10.

**`IReportArchiveService`:** `ArchiveAsync · ListAsync · GetAsync · RetrieveAsync · SweepExpiredAsync`.
**`IReportArchiveStore`:** `PutAsync · GetAsync · ExistsAsync · DeleteAsync` — shipped as
`FileSystemReportArchiveStore`, content-addressed, fanned out `<root>/<company>/<hash[0..2]>/<hash>.<ext>`
(a flat directory with 200k files is measurably slow on both NTFS and ext4).

`GetAsync` returning **null** is a supported outcome: an archive row whose bytes vanished is a fact to report, not a
crash. `ListAsync` deliberately does **not** stat 500 files — presence is checked on retrieval, where it matters.
`SweepExpiredAsync` only deletes bytes whose hash is no longer referenced by a **live** row, because
content-addressing means several rows can share one file.

`ArchiveAsync` refuses an artifact above `MaxArtifactBytes` (64 MB default) with a log line: a 400 MB "report" is a
runaway query, and storing it silently is how a disk fills at month end.

---

## 12. Library: favourites, categories, tags, ownership, sharing (#24–28)

One service, `IReportLibraryService`, because these are all metadata *about* a report rather than part of producing
one, and they share one rule that must not be restated four times: **every row is company-scoped and every write
re-checks access.**

| Group | Members |
| --- | --- |
| Categories | `GetCategoryTreeAsync · SaveCategoryAsync · DeleteCategoryAsync · SyncPlatformCategoriesAsync` |
| Tags | `GetTagsAsync · EnsureTagAsync · TagAsync · UntagAsync · GetReportCodesByTagAsync` |
| Favourites | `GetFavoritesAsync · AddFavoriteAsync · RemoveFavoriteAsync · ReorderFavoritesAsync` |
| Ownership | `TransferOwnershipAsync` |
| Sharing | `GetSharesAsync · ShareAsync · RevokeShareAsync` |

Decisions worth noting:

* **Category ≠ tag.** A report sits in exactly one category (a navigation folder) and carries any number of tags
  (cross-cutting: "month-end", "board pack", "VAT"). Keeping them separate lets the tree stay stable while tagging
  stays disposable.
* Category **counts** come from the catalog filtered by what the caller may see, never from a stored counter — a
  stored count would drift the moment a permission changed.
* A **system** category (from the catalog) may be relabelled but not re-keyed or deleted — a definition points at
  its key.
* **Tags dedupe case-insensitively.** Without it a tag list becomes near-duplicates within a week.
* **Favourites are per employee**, idempotent, and hidden when the report's permission is revoked (a pinned link
  the user cannot open is worse than no link). A favourite whose *report* no longer exists is kept and flagged
  **stale**, so it can be unpinned.
* **Ownership transfers only to an active employee of the same company** — otherwise a transfer could park a
  template on an unreachable employee id.
* **Tagging needs only `View`.** Requiring `Edit` would stop a user organising reports they are allowed to read.
* **Sharing needs `Manage`**, and `Manage` is also the ceiling — closing the escalation loop in both directions.
* Shares and templates are **soft-deleted** (who revoked whose access, and when, is what an audit asks); tag links
  and favourites are **hard-deleted** (no history worth keeping, and nothing references them).

---

## 13. Print Service (#12)

Printing is a **destination**, not a format — conflating them is why report engines grow a "PrintPdf" format nobody
can explain.

`IReportPrintService.PrintAsync(request, printerProfileKey, copies)` →
`ReportPrintResult { Status: Prepared | Sent | Failed, Job, Diagnostics }`.

**`ReportPrinterProfile`** is a named destination + the page geometry it imposes: `a4-office` and `receipt-80`
(80 mm, minimal margins, no page numbers on a continuous roll). **The profile's page setup replaces the request's —
paper wins over preference**; an A4 layout sent to a receipt roll must be re-laid out, not scaled. Everything else
about the request is honoured, so "print this" prints what is on screen.

**`IReportPrintTransport`** is the destination. The shipped `BrowserPrintTransport` returns the document and lets
the browser print it — the correct default for a web application, because the browser already knows the user's
printers, trays and permissions. It reports **`Prepared`, not `Sent`**: nothing has printed yet, and logging "Sent"
would log something that had not happened.

The print service calls `IReportService.GenerateAsync` like any other caller, so authorization, history and
archiving all still happen.

---

## 14. Scheduling (#13) and Delivery (#14)

Full rationale in ADR-037 §8, including why **no hosted service is registered**.

**`ReportScheduleFrequency`:** `Interval · Hourly · Daily · Weekly · Monthly`. An enum, not a cron string.
`Interval` has a **15-minute floor** — below that a heavy report can still be running when its next occurrence is
due, and the platform would queue work faster than it completes.

**Timezone handling:** every calculation is done in the **schedule's own** zone and converted back. Computing in
server-local time and adding an offset at the end gets DST wrong — a 09:00 report would arrive at 08:00 for half the
year. An unknown zone falls back to server local **with a warning**, never an exception.

**Owner:** `OwnerEmpId` is `NOT NULL` by intent. An unattended run with no principal would have to either skip
authorization or invent a company-wide identity, and both turn a scheduler into a data-exfiltration path. Rights are
checked at **save** time (a schedule is an authorization decision that outlives the request that made it) **and** at
run time.

**Runner:** builds the owner's context → generates → **always archives** (nobody is watching, so what was sent must
be recoverable) → delivers → **advances `NextRunAt` on every path, including failure**. A schedule that failed and
kept its old `NextRunAt` would be due forever and re-run on every sweep.

**Delivery layering:** `IReportDeliveryService` → `IReportDeliveryChannel` (`Email` today) → `IReportMailSender`.

`ReportDeliveryStatus`: `Pending · Sent · Failed · **Skipped**`. One attempt row per recipient, whatever the
outcome — that is the record which makes *"was it delivered?"* answerable, including the answer *"no, and here is
why"*. With no transport bound every attempt is `Skipped`, never `Sent`. A malformed address fails **that recipient
only**. A schedule with **no** recipients is `Info`, not an error — a schedule may exist purely to archive on a
cadence.

---

## 15. Wiring

```csharp
builder.Services.AddCrossBusinessReporting(reporting => reporting
    .UseArchiveRoot(Path.Combine(env.WebRootPath ?? env.ContentRootPath, "uploads", "reports"))
    .MapPermission(ReportPermissions.Administer, "Admin", "SuperAdmin"));
```

One line in `Program.cs`, by design. Lifetimes are tabulated in ADR-037 §11.

Adding a module report — the complete integration surface — is in ADR-037 §15.

---

## 16. Files added

**26 files** under `CrossBuy/BL/Reporting/`:

`ReportContracts.cs · ReportMetadata.cs · ReportQuery.cs · ReportValues.cs · ReportCatalog.cs ·
ReportParameterEngine.cs · ReportDataSource.cs · ReportDataShaper.cs · ReportRenderer.cs · HtmlReportRenderer.cs ·
PlaywrightPdfReportRenderer.cs · ReportExportEngine.cs · CsvReportExporter.cs · ExcelReportExporter.cs ·
ReportPrintService.cs · ReportAuthorizationService.cs · ReportTemplateService.cs · ReportLibraryService.cs ·
ReportHistoryService.cs · ReportArchiveService.cs · ReportScheduleService.cs · ReportDelivery.cs · ReportEngine.cs ·
ReportService.cs · PlatformReportDefinitions.cs · ReportingRegistration.cs`

**8 files** under `CrossBuy/Models/Context/Reporting/`:

`ReportTemplate.cs · ReportCategory.cs · ReportTag.cs · ReportFavorite.cs · ReportShare.cs · ReportRun.cs ·
ReportArchiveEntry.cs · ReportSchedule.cs`

**1 file** under `CrossBuy/deploy/sql/`: `reporting_platform.sql`

**9 files** under `CrossBuy.Tests/` (179 tests): `ReportingTestHost.cs · ReportingCatalogTests.cs ·
ReportingParameterEngineTests.cs · ReportingShaperTests.cs · ReportingOutputTests.cs · ReportingEngineTests.cs ·
ReportingTemplateScopeTests.cs · ReportingScheduleTests.cs · ReportingDiWiringTests.cs`

**Shared files touched — additively only:**

| File | Change |
| --- | --- |
| `Models/Context/CrossDbContext.cs` | 12 `DbSet` properties + a comment block |
| `Program.cs` | 1 `using` + 1 registration call |

Per CLAUDE.md's selective-commit rule, both shared files must be committed with **our lines only**, via git
plumbing — never a whole-file `git add`.
