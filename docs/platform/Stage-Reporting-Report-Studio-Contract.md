# Stage — Reporting: CrossBusiness Report Studio Contract

**Platform:** CrossBusiness Report Studio (the self-service builder over the CrossBusiness Reporting Platform)
**Companion to:** `ADR-037`, `RPS-001`, `Stage-Reporting-Dataset-Layer-Design.md`,
`Stage-Reporting-Expression-Language-Design.md`
**Status:** CONTRACT ONLY — no UI is implemented, and none may be until §11 is satisfied.

---

## 0. THE PERMANENT DESIGN RULE

> **REPORT STUDIO VISUAL AUTHORITY = INVENTORY. Inventory is the sole visual authority. The design is not to be
> invented independently, and there is no Studio design language to invent it in.**

This applies to the Studio, the report browser, the viewer, the schedule editor and every dashboard widget. Where
this document describes a screen, it describes **what it must be able to express**, never how it looks.

**Corrected 2026-08-12 (Reporting owner).** An earlier revision of this section declared "Official identity:
CrossBusiness Blue" and treated the Studio as having chrome of its own. That is **withdrawn**. It contradicted the
owner's standing instruction, `CLAUDE.md` (Inventory/Metronic 8 `app-*` shell, ledger green `#13433a` + gold, with
`crossbuy-brand.css` overriding Metronic's blue *on purpose*), and — decisively — the code that already shipped:
the Reports Center and the Report Viewer both render on `~/Views/Shared/_LayoutInventory.cshtml` unchanged, and
neither introduced a stylesheet, a shell or a class of its own. The contract was the only artefact still claiming
otherwise.

**What a future Studio reuses, and may not replace:**

| Element | Source |
|---|---|
| Shell + sidebar | `_LayoutInventory.cshtml`, unchanged |
| Toolbar | Inventory's page-heading + breadcrumb pattern |
| Cards / tiles | Inventory cards (`card-flush`, stat tiles) |
| Filters | Inventory filter card (`row g-4 align-items-end`, labelled) |
| Tables | Inventory `table-row-*` Metronic variants |
| Forms | Inventory form patterns |
| Framework | Metronic 8 · Bootstrap 5 · KeenIcons |

**Explicitly NOT retained:** a CrossBusiness Blue Studio shell · a Reporting-specific design language · a separate
Studio theme · any bespoke Studio stylesheet.

Ledger green and gold are the product's brand and are therefore correct in the chrome via Metronic tokens; they
may of course also appear **inside report content** (chart series, financial emphasis in a rendered statement).
There is no colour distinction to police between "the Studio" and "the product" — the Studio *is* the product.

---

## 1. What the Studio is

A user with no SQL builds a report by choosing a **dataset**, picking fields, setting filters, and arranging a
layout. What they produce is a **stored template plus a stored query intent** — never code, never SQL.

```
Studio  →  saves  →  ReportTemplate (layout) + saved query intent
                         ↓  at run time, unchanged
      ReportDefinition → Dataset → Secure Data Source → Template → Renderer
```

**The Studio adds no new execution path.** A Studio-built report runs through exactly the pipeline a
hand-written report runs through, and is therefore governed by exactly the same authorization. That is the single
most important property of this contract: the builder is a way of *composing what the platform already allows*,
not a way of reaching past it.

---

## 2. The safe Query Builder

### 2.1 Dataset selection
The user picks from `IReportDatasetRegistry.ListForStudioAsync(context)` — datasets that are
`AvailableInStudio`, not deprecated, **and permitted for this caller**. A dataset they cannot use is not shown,
and choosing one they later lose access to fails at run time as a denial, not as a broken template.

### 2.2 Field selection
Only fields the caller may see (Dataset Layer sensitivity rules). Consequences the UI must honour:

- A `Confidential`/`Restricted` field the caller lacks is **absent from the picker** — not greyed out. A greyed
  entry discloses that the field exists and what it is called.
- A `Never` field is absent for everyone.
- Field metadata (type, format, alignment, allowed aggregates, allowed operators) comes from the dataset. The
  Studio offers `Sum` only where the dataset says `Sum` is meaningful — summing an exchange rate is not an
  option the user should have to know to avoid.

### 2.3 Filters
Built from `(field, operator, value)` triples, where the operator list comes from
`ReportDatasetOperators.IsAllowed`. Values are **typed inputs**, bound by the existing
`ReportParameterBinder`.

**The user never types a predicate.** There is no expression box for filters, no "advanced SQL" escape, and no
free-text WHERE. A filter is data, and it reaches the source as `ReportFilter`, which is a structure — never a
string spliced into a query.

Filter groups (`AND`/`OR` nesting) are a **future** addition and need one decision recorded first: how a nested
group interacts with a dataset's mandatory filters. Until then, filters are conjunctive.

### 2.4 Sorting, grouping, totals
- Sorting: any `Sortable` field, ordered list, ascending/descending.
- Grouping: any `Groupable` field, ordered list, with per-level subtotals — the existing `ReportGrouping`.
- Totals: per-group and grand total, using the dataset's declared aggregates.

### 2.5 Parameters
The user may promote a filter to a **prompted parameter**, choosing its label, default and whether it is
required. `SystemSupplied` parameters (CompanyId, EmployeeId, Culture, AsOfDate) are **never** promotable — the
engine supplies them, and a caller-supplied value is ignored.

### 2.6 Calculated fields
Authored in **REX** (`Stage-Reporting-Expression-Language-Design.md`), validated at save with a character offset
so the editor can underline the token. Not filterable or sortable server-side (§7 of that document).

---

## 3. Layout

| Element | Contract |
|---|---|
| Sections | Report header/footer, page header/footer, group header/footer, detail |
| Columns | Order, width (mm), alignment, format, visibility |
| Page setup | The existing `ReportPageSetup`: A4/A5/Letter/Legal/Thermal80, portrait/landscape, margins |
| Conditional visibility | A REX `Boolean` per section/column/row — evaluated per row, no side effects |
| Conditional formatting | A REX `Boolean` plus a **named style token**, never a raw colour or CSS |
| Branding | From `IReportBrandingProvider` (company logo, name). The user does not upload chrome per report. |

**Conditional formatting yields a style token, not CSS.** A user-supplied style string is an injection surface in
HTML output and meaningless in CSV. A token (`emphasis-negative`, `emphasis-positive`, `muted`) is renderable in
every format and safe in all of them.

---

## 4. Charts

Declared, not drawn, by the Studio: `{ chartType, categoryField, seriesFields[], aggregate, sortOrder, topN }`.

Types: `Column · Bar · Line · Area · Pie · Donut · Stacked column · Stacked bar · Combo · KPI tile · Sparkline`.

Rules:
- A chart reads **the same shaped, authorized row set** as the table. It is never a second query, so it can never
  show data the table would have hidden.
- Series fields must be numeric and must declare the aggregate they use.
- `topN` plus an explicit "Other" bucket, so a 5 000-category pie is impossible.
- **Chart colours may use ledger green and gold** — this is report *content* (§0).
- Charts are rendered by the renderer, so a format that cannot draw (CSV) omits them and **says so** in a
  diagnostic rather than silently dropping them.

---

## 5. Reusable components

A **Report Component** is a saved, named fragment reusable across reports: a header block, a signature block, a
standard filter set, a chart definition, a calculated field.

- Scoped like templates: Personal · Team · Company · Platform.
- **Versioned and copy-on-use by default.** A component edit does not silently change every report that used it;
  a report opts in to an update. Live-linked components are a future decision (§12 S4).
- A component carries **no permission of its own**. It is layout, and layout never grants data access.

---

## 6. Drill-down and drill-through

Both come from the dataset, not from the Studio:

- **Drill-down** expands a grouping hierarchy within the same dataset (`ReportDatasetDrillDown`).
- **Drill-through** opens another report with parameters mapped from the clicked row
  (`ReportDatasetDrillTarget.ParameterMap` — a field→parameter map, deliberately not a URL template).

**The target report re-authorizes independently.** Arriving by drill-through is not evidence of permission; if the
caller may not run the target, they get a denial. A drill-through that inherited the source's permission would be
a permission-laundering path through a link.

---

## 7. Dashboard widgets

A widget is a saved report **plus a presentation mode** (`Table · Chart · KPI · Sparkline · List`), constrained by
`IReportDatasetDefinition.AvailableAsWidget`.

- Widgets run **as the viewing user**, always. Never as the author, never as a service account. A widget that ran
  as its author would turn a shared dashboard into a permission-laundering surface — the single most common
  reporting-platform data leak.
- Widgets have a **row ceiling far below a report's** (default 100) and a mandatory refresh interval, because a
  dashboard runs its widgets unattended and repeatedly.
- A widget the viewer may not run renders as an explicit "not available to you" tile, not as an empty one — an
  empty tile reads as "no data", which is a different and misleading claim.

---

## 8. JSON output

`ReportOutputFormat.Json` produces `{ definition, parameters, columns, rows[], groups[], totals, diagnostics }`.

- It is **an output format, not an API**: it goes through the same authorization, the same field-sensitivity
  filtering and the same row cap as HTML.
- `Internal` and `Never` fields are absent from JSON exactly as they are from CSV. An export format that included
  a column the screen hides is the classic reporting leak, and JSON is the format most likely to be scraped.
- Governed by the dataset's `ExportPolicy` — JSON counts as a **data export**, not a view.

---

## 9. AI-generated draft reports

**A strictly bounded feature**, and the boundary is the point.

```
natural language + the caller's PERMITTED dataset list
        → model proposes a DRAFT: dataset, fields, filters, grouping, chart
        → validated exactly as a hand-built report
        → PRESENTED TO THE USER FOR REVIEW
        → the user saves it, or does not
```

Non-negotiable:

1. **The model never sees data.** It sees dataset *metadata* — field names, types, descriptions — only. It is a
   layout suggester, never a query executor.
2. **The model's output is a proposal in the platform's own structures**, not SQL, not REX text it invented, and
   not a template written directly to the database. It goes through `ReportDatasetValidator` and the REX
   validator like anything else.
3. **The candidate dataset list is filtered by the caller's permissions first.** The model cannot propose a
   dataset the user may not use, because it is never told that dataset exists.
4. **Nothing auto-runs and nothing auto-saves.** A draft is always reviewed by a human before it becomes a report.
5. **Every AI-drafted report is flagged** as such in its stored metadata, so an auditor can tell how a report came
   to exist.
6. A generated report is **not more trusted** than a hand-built one: same permissions, same row caps, same
   archive rules.

---

## 10. Publishing workflow

| Scope | Who may create | Visible to | Notes |
|---|---|---|---|
| **Personal** | any user with dataset access | the author | the default for a new draft |
| **Team** | a team member | that team | resolved via `IReportTeamResolver` |
| **Company** | a reporting administrator | the company | |
| **Platform** | a platform administrator only | every company | a tenant can never create one |

Rules, all inherited from the existing template-scope work rather than invented here:

- Precedence on resolution is Personal → Team → Company → Platform.
- Publishing **widens visibility, never permission**. Everyone who sees a Company report still needs the
  underlying report's and dataset's permissions to run it. A shared layout is not a shared dataset.
- A report definition may forbid Personal templates (`ReportCapabilities.TemplateScopes`) — a statutory return
  must not be filed through a layout somebody invented.
- Promotion (Personal → Team → Company) is an explicit, audited act, not a checkbox on save.

---

## 11. Before ANY Studio UI is written

Required from the owner, in this order:

1. **The approved Metronic reference screen or component set** for the surfaces Inventory has no equivalent of:
   the Studio canvas, the field picker, the filter builder and the chart designer. The report browser, the viewer
   and the schedule editor need nothing further — Inventory already supplies their vocabulary, and the shipped
   Reports Center and Report Viewer are the worked examples.
2. ~~Confirmation that CrossBusiness Blue is the chrome.~~ **Settled — see §0. Inventory is the sole visual
   authority; there is no separate Studio chrome to confirm.**
3. A decision on RTL/LTR parity for the canvas specifically — a drag-and-drop designer is the one screen where
   mirroring is not automatic.
4. The open decisions in §12 that affect stored shapes (S1, S2), because they change what a saved report *is*.

Until 1, 3 and 4 exist, this remains a contract. **Do not invent the design.**

---

## 12. Open decisions for the owner

| # | Decision | Recommendation |
|---|---|---|
| **S1** | Is a Studio report a `ReportDefinition` row, or a `ReportTemplate` + saved query intent over an existing definition? | **Template + query intent.** Definitions stay code-first and immutable (ADR-037 §5); a database-editable definition is a database-editable permission. |
| **S2** | Can a Studio report join two datasets? | **No, in v1.** A join is a data-source concern with its own authorization and cardinality questions; a "combined dataset" declared by a module is the safe form. |
| **S3** | Nested `AND`/`OR` filter groups? | Yes in v2, once mandatory-filter interaction is decided. Conjunctive for now. |
| **S4** | Live-linked reusable components, or copy-on-use? | **Copy-on-use** to start. Live links mean an edit silently changes reports the editor has never seen. |
| **S5** | May a widget cache its result across viewers? | **No.** A shared cache across users with different permissions is the same leak as running a widget as its author. Cache per (widget, viewer) or not at all. |
| **S6** | Which model backs the AI drafter, and does metadata leave the tenant? | Owner decision. If any metadata leaves the deployment, it needs an explicit, documented consent — field names are business information. |
