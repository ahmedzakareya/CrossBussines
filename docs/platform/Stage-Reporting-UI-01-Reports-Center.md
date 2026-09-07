# Stage Reporting UI — 01 · The Reports Center

**Platform:** CrossBusiness Reporting Platform
**Phase:** R3 Phase 2
**Route:** `GET /Reports`
**Files:** `Views/Reports/Index.cshtml`, `Views/Reports/_ReportCard.cshtml`,
`BL/Reporting/ReportsCenterPresenter.cs`, `Controllers/ReportsController.cs`
**Layout:** `Views/Shared/_LayoutInventory.cshtml` (Inventory's own, unmodified)
**Own stylesheet:** none — see §5

---

## 1. The shape of the screen

Nine required sections, all present:

| Section | Where | Source |
|---|---|---|
| Report catalog | main column, grouped by category | `IReportService.BrowseAsync` |
| Search | GET form, top | query string → `BrowseAsync(search:)` |
| Categories | chip row | `IReportLibraryService.GetCategoryTreeAsync` |
| Tags | chip row | `GetTagsAsync` |
| Favourites | own strip above the catalog | `GetFavoritesAsync` |
| Saved reports | left column, scrollable | `IReportTemplateService.ListAsync` per visible report |
| Recent runs / history | right column | `IReportHistoryService.QueryAsync` |
| Archive | right column | `IReportArchiveService.ListAsync` |
| Dataset availability | right column | `IReportDatasetRegistry.ListForStudioAsync` |

Every filter is a **GET**, so a filtered catalog is a URL a person can bookmark and send to a colleague.

---

## 2. Why there is a presenter

The Phase 6 rules are claims about a **projection** — "internal columns never appear in UI metadata". If that
projection happens in Razor, the only way to test it is to render HTML and grep it, which passes for the wrong
reasons (a column is absent because the fixture had no rows) and fails for the wrong ones (markup changed).

So `IReportsCenterPresenter` owns every decision about what a screen may see, the views are dumb, and the
controller is dumber still. `ReportingUiSafetyTests` asserts against the presenter.

The presenter **does not authorize**. It calls services that authorize and propagates their refusals. A
presenter that made its own access decision would be a second permission engine, and the ordering rule in
`ReportAuthorizationService` would then exist in two places.

---

## 3. Five panel states, not two

`ReportPanelState` = Ready · Empty · Unavailable · AccessDenied · Failed.

Collapsing any pair turns a broken deployment into a quiet week. "No datasets are registered" and "you may not
see any dataset" are different sentences and lead to different actions — one is a deployment task, the other is
a permission request.

Reporting declares **its own** `ReportPanel<T>`, deliberately not reusing the Workspace's identically-shaped
`WorkspacePanel<T>`. See §6.

---

## 4. Three findings from building it

### 4.1 A search term must not revoke an affordance

Retrievability of an archive row ("may I still download this?") was first computed from the **filtered**
catalog. Typing a search term therefore marked every unrelated history and archive row as no-longer-permitted —
a UI that withdraws rights because you searched for something.

Fixed by separating two sets: `definitions` (visible **and** matching the filters, drives the catalog) and
`visible` (everything the caller may run, drives every other panel and every availability question). Pinned by
`A_search_term_does_not_make_unrelated_history_look_unpermitted`.

### 4.2 A revoked permission must not erase your own history

`An_archived_artifact_stops_being_retrievable_when_the_permission_is_revoked` originally asserted the archive
row **disappeared**. It did not, and the test was wrong, not the code.

`ListAsync` and `QueryAsync` are already **self-scoped** — they return the caller's own artifacts and runs (or
everything, for a reporting administrator). Dropping a row when a permission changed elsewhere would make a
person's own history rewrite itself, which is the one thing an audit surface must never do. The bytes are
separately protected: `RetrieveAsync` re-checks the report permission and answers null.

The real gap was honesty in the UI — the screen would have rendered a Download button that 404s. So the row
**stays and is marked**: `ReportArchiveModel.Retrievable`, `ReportRunModel.ReportAvailable`, and a named reason
in the chip. Three outcomes on an archive row, not two: downloadable · file swept · no longer permitted. A user
can tell a retention sweep from a revoked permission, which sends them to the right administrator.

### 4.4 `@section Styles` on a layout that renders none is a runtime crash

While the screens were being moved onto the Inventory shell, they briefly kept an `@section Styles` block
linking the old stylesheets. `_LayoutInventory` declares no such section, and ASP.NET Core throws when a
section is defined but never rendered — *"The following sections have been defined but have not been
rendered"*. The build stayed green; the Viewer was unusable.

Caught because the banned-token guard checks for it by name. Recorded because it is a whole class of Razor
defect the compiler cannot see.

### 4.3 A whole-row anchor cannot express a withdrawn action

The Recent-runs row was one `<a>` to Re-run. That cannot represent "show this row, but not as a link". The row
is now a `<div>` and only the title is a link — present and inert when the report is no longer permitted.

---

## 5. Identity — the Inventory module, and nothing else

**Superseded by an owner decision.** This screen was first built on a bespoke shell
(`_LayoutReporting.cshtml`) with its own stylesheet (`crossbusiness-reporting.css`) over the CrossBusiness Blue
tokens. The owner then set a global rule: *the Inventory module is the only visual authority; if any new screen
can be visually distinguished from Inventory, the implementation is FAILED.*

That also supersedes the line in `CLAUDE.md` naming **Accounting** as "the visual identity reference for future
platform and administrative tools". The two now disagree; the owner's rule wins, and CLAUDE.md needs the
correction. Flagged, not silently resolved.

**What was deleted, not merely unreferenced:**

| Removed | Why |
|---|---|
| `Views/Shared/_LayoutReporting.cshtml` | a second shell |
| `wwwroot/Backend-assets/css/crossbusiness-reporting.css` | a second component set |
| `Resources/Views/Shared/_LayoutReporting.{en,ar,fr}.resx` | its strings |

A stylesheet left on disk gets re-linked by the next person who finds it and assumes it is the house style, so
the files are gone rather than orphaned. `The_bespoke_reporting_shell_and_stylesheet_are_gone_from_disk`
asserts it.

**What the screen is built from now**, component for component:

| Element | Source |
|---|---|
| shell + sidebar | `_LayoutInventory.cshtml`, unchanged — it hardcodes `MainMenu.Inventory()`, so the rail is the real Inventory menu |
| page wrapper | `Inventory/Index.cshtml` — `d-flex flex-column flex-column-fluid` |
| toolbar | `Inventory/Reports.cshtml` — `page-heading` + `breadcrumb-separatorless` + `bullet` |
| KPI tiles | `Inventory/Index.cshtml` row 2 — `col-6 col-md-4 col-xl-2` stat cards, verbatim |
| filter card | `Inventory/Items.cshtml` — `card-flush` + `row g-4 align-items-end`, labelled `form-control-solid` / `form-select-solid` + select2, `ki-arrows-circle` reset |
| report cards | `Inventory/Reports.cshtml` — the `symbol-50px` card, verbatim |
| tables | `Inventory/Items.cshtml` — `table-row-dashed` + the `2px solid #13433a` header rule |
| empty state | `text-center text-muted py-10` |
| buttons / badges | Metronic `btn-*` and `badge-light-*` as Inventory uses them |

**`_LayoutInventory` declares no `Styles` section.** A custom stylesheet therefore cannot be loaded from these
screens even by accident — the attempt throws at run time. That is a guardrail, not a convention, and it caught
a real regression during this work (§4.4).

**Green is the identity again.** The bespoke stylesheets declared their own blue scale and loaded *after*
`crossbuy-brand.css`, overriding the ledger-green brand. Removing them restores it: a rendered diff of
`/Reports` against `/Inventory/Index` now shows the **same seven stylesheets, byte for byte**.

RTL/LTR and responsiveness come from Metronic and Inventory's own grid classes — there is no Reporting-specific
CSS left to keep in step.

---

## 6. Not exposed

Per the brief:

- **Internal columns** — the presenter never puts them in the model, so no view can render them.
- **SystemSupplied parameters** — same. `CompanyId` in particular: an input for it would look exactly like a
  tenant selector.
- **Sensitive dataset metadata** — the availability strip reports title, module, version, row cap and a field
  **count**. Not field names, not sensitivities, not the data source key. The count itself excludes
  `Never`-sensitivity fields, because the strip reports what somebody could build with.
- **Engine and renderer names** — a user picks a *format* ("Excel"), never a renderer. The strings
  "Playwright", "ClosedXML" and "HtmlReportRenderer" appear nowhere in this product's UI.

---

## 7. Localisation

Every user-facing string goes through `IViewLocalizer` →
`Resources/Views/Reports/Index.{en,ar,fr}.resx` (47 keys each).

The layout uses its **own** resource file rather than `SharedResources`: that file is edited concurrently by
several tabs and CLAUDE.md records it must never be whole-file staged. Reporting's chrome strings are
Reporting's own.

`Every_literal_localizer_key_in_a_reporting_view_has_an_arabic_translation` walks each view's literal
`@Localizer[...]` keys against its `.ar.resx`. This matters because an untranslated key in Razor **renders as
the key** — a missing Arabic entry breaks nothing and silently ships an English sentence into an RTL screen.
