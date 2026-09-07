# Accounting visual identity — measured reference

**Status:** Reference (Stage 1 Hotfix A.1 / A6.1). **Documentation only — no screen was changed.**
**Method:** inspection of the actual layout, views, brand stylesheet and resx files. Every value below is quoted
from the codebase; nothing is proposed here.

---

## 0. One correction before anything else — the identity is GREEN, not blue

The instruction asks to *"preserve the existing CrossBuy blue identity."* **The codebase says otherwise, in its own
words.** `wwwroot/Backend-assets/css/crossbuy-brand.css`, lines 1-6:

```
/* ============================================================
   CrossBuy brand identity — overrides Metronic's blue primary
   with the "ledger green + gold" palette across the whole app.
   Loaded AFTER style.bundle(.rtl).css so it wins the cascade.
   Palette: deep #13433a · mid #1f6253 · light #e9f1ee · gold #d4a017 / #f0c64b
   ============================================================ */
```

The file overrides `--bs-primary`, `--kt-primary`, `.btn-primary`, `.bg-primary`, link colours and the active-state
component background to **deep ledger green `#13433a`**, with **gold `#d4a017` / `#f0c64b`** as the secondary accent.
Blue (`#009ef7`) is Metronic's stock primary — the colour this file exists to *replace*.

**This document records the identity that EXISTS.** A6's principle "preserve the existing CrossBuy blue identity" is
therefore reported as a **conflict for the owner to settle**, not silently resolved in either direction:

* if "blue" meant *Metronic's default*, then the standard is green and the principle should be reworded;
* if a blue rebrand is genuinely wanted, that is a **visual change to the whole application** — far outside a
  security hotfix — and needs its own decision.

Nothing was changed either way. See `CrossBuy-Platform-UI-Standard.md` §1.

## 1. The reference screens

Chosen on evidence — size, completeness of the pattern set, and consistency with the layout:

| Screen | Lines | Why it is a reference |
|---|---|---|
| `Views/Accounting/Index.cshtml` | 361 | The most complete example: toolbar + breadcrumbs + status badge + KPI card row + tables. The page-structure reference. |
| `Views/Accounting/Executive.cshtml` | 332 | KPI/summary-card density and chart placement. |
| `Views/Accounting/ChartOfAccounts.cshtml` | 230 | Tree + table + row actions; the most structural data screen. |
| `Views/Accounting/TrialBalance.cshtml` | 167 | Filter toolbar + totals row — the report reference. |
| `Views/Accounting/CreateJournal.cshtml` | 175 | Form controls, dynamic line editing, validation — the form reference. |

Layout: **`Views/Shared/_LayoutAccounting.cshtml`** (4776 lines) — Metronic 8 app shell.

## 2. The measured visual language

### 2.1 Shell and page structure

Metronic 8 `app-*` shell, in this order (from `Index.cshtml`):

```
div.d-flex.flex-column.flex-column-fluid
  div.app-toolbar.pt-6.pb-2
    div.app-container.container-fluid.d-flex.flex-stack
      div.page-title.d-flex.flex-column.justify-content-center.gap-1
        h1.page-heading.text-gray-900.fw-bold.fs-3.m-0
        ul.breadcrumb.breadcrumb-separatorless.fw-semibold.fs-7.my-0
      div.d-flex.align-items-center.gap-2.gap-lg-3     ← the action toolbar
  div.app-content.flex-column-fluid
    div.app-container.container-fluid
```

* **Title**: `h1.page-heading.text-gray-900.fw-bold.fs-3`.
* **Breadcrumbs**: `breadcrumb-separatorless`, items `breadcrumb-item.text-muted`, separator
  `span.bullet.bg-gray-500.w-5px.h-2px`.
* **Toolbar** sits on the RIGHT of the same flex row as the title (`d-flex.flex-stack`), never below it.

### 2.2 Buttons — a two-level hierarchy, observed

| Level | Classes |
|---|---|
| Primary | `btn btn-sm btn-flex btn-primary fw-bold` |
| Secondary | `btn btn-sm btn-flex btn-light fw-bold` |
| Icon-only | `btn btn-icon btn-active-color-primary w-35px h-35px` |
| Flush | `btn btn-flush btn-active-color-primary` |

`btn-sm` + `fw-bold` + `btn-flex` is the toolbar signature. Icons precede labels as `i.ki-outline.ki-*.fs-3`.

### 2.3 Cards

`card card-flush` — **flush, not nested**. Header `card-header pt-5`, title
`card-title align-items-start flex-column` (title + muted subtitle stacked). Row spacing
`row g-5 gx-xl-10 mb-5 mb-xl-10`. Full-height variants use `h-lg-100`.

### 2.4 Tables

Metronic DataTables bundle (`plugins/custom/datatables/datatables.bundle.css`), used by ≥8 accounting views
(`AccountingRoles`, `ApAging`, `ArAging`, `BankAccounts`, `CashBoxes`, `CreateJournal`, `CustomerAnalytics`,
`CustomerStatement`, …). Table classes: `table align-middle table-row-bordered`.

### 2.5 Badges and status

`badge badge-light-{state}` with the state computed in the view — e.g. `Index.cshtml` renders
`badge badge-light-@(Model.FiscalYearStatus == …)`. Icons `ki-outline ki-calendar fs-6 me-1`.

### 2.6 Colour, radius, shadow, focus — from `crossbuy-brand.css`

| Token | Value |
|---|---|
| Primary (deep) | `#13433a` |
| Primary active / mid | `#1f6253` |
| Primary light | `#e9f1ee` |
| Primary pressed | `#0c2e28` |
| Gold accent | `#d4a017`, light `#f0c64b` |
| Border radius | `10px` menu links, `8px` system rows, `50%` dots |
| Signature shadow | `0 8px 20px rgba(19,67,58,.30)` (active sidebar pill) |
| Focus ring | `border-color:#6e9c90; box-shadow:0 0 0 .25rem rgba(19,67,58,.15)` |
| Active menu bullet | gold `#f0c64b` with `0 0 0 3px rgba(240,198,75,.25)` |

The active sidebar leaf is *"a solid green pill with a gold bullet (the signature)"* — the file's own comment.

### 2.7 Typography

| Direction | Font | Source |
|---|---|---|
| LTR | **Inter** 300-700 | Google Fonts, `_LayoutAccounting.cshtml:42` |
| RTL / Arabic | **Cairo** 400-800 | Google Fonts, line 66, applied as `font-family:'Cairo',sans-serif; direction:rtl` |

### 2.8 RTL / LTR

Chosen per request culture, not per user toggle:

* `<html lang="@culture" dir="@(culture.TextInfo.IsRightToLeft ? "rtl" : "ltr")">` (line 24);
* RTL loads `plugins.bundle.rtl.css` + `style.bundle.rtl.css`; LTR loads the non-RTL bundles;
* `crossbuy-brand.css` is loaded **after** either bundle so the brand wins the cascade in both directions.

**Full parity by construction** — one template, two stylesheet sets.

### 2.9 Localization

`@Localizer[...]` — **991 occurrences** across the Accounting views, with **per-view** resource files:
`Resources/Views/Accounting/<ViewName>.{ar,en,fr}.resx` — **three cultures** (ar, en, fr). This is the pattern
platform screens must follow.

### 2.10 Loading and empty states — the honest finding

* **Loading:** `spinner-border` (14 occurrences), plus a page-level `span.spinner-border.text-primary` in the layout.
* **Empty states:** **no consistent pattern found.** The search for a shared empty-state partial or a repeated
  "no data" block returned nothing. Recorded as a **gap**, not documented as a standard — the alternative would be
  inventing one and calling it existing.

### 2.11 Dark mode — not currently supported

`crossbuy-brand.css` defines its palette under `:root, [data-bs-theme="light"]` **only**. There is no
`[data-bs-theme="dark"]` block, and no layout sets a dark theme. Metronic's dark hook exists; the brand does not
implement it. So the answer to A6.1's *"dark/light compatibility if currently supported"* is: **light only**.
Documented rather than assumed either way.

## 3. Shared components that already exist

| Partial | Purpose |
|---|---|
| `_LayoutAccounting.cshtml` | the accounting shell |
| `_MainMenu.cshtml` | sidebar navigation |
| `_NotificationBell.cshtml` | notification bell + dropdown |
| `_QuickAdd.cshtml` | quick-create menu |
| `_CbToastr.cshtml` | toast notifications |
| `_DocTimeline.cshtml` / `_DocEventTimeline.cshtml` | document history |
| `_AnnouncementsBanner.cshtml`, `_CalendarReminders.cshtml` | the parallel team's banners, embedded in the shared layouts |
| `_ValidationScriptsPartial.cshtml` | client validation |

**Duplication observed, for later consolidation:** there are **eleven** layouts
(`_Layout`, `_mainLayout`, `_LayoutAccounting`, `_LayoutBackend`, `_LayoutInventory`, `_LayoutManufacturing`,
`_LayoutPeople`, `_LayoutPos`, `_LayoutPosApp`, `_LayoutHyperPos`, `_LayoutEmbed`). The `app-*` shell, the toolbar and
the page header are re-implemented in several of them. No page header, filter toolbar, empty-state or DataTable
wrapper exists as a shared partial — each screen rebuilds them.

## 4. What this document is not

It is a **measurement**, not a proposal. It changes no screen, extracts no component and redesigns nothing. The
standard derived from it is `CrossBuy-Platform-UI-Standard.md`; the per-screen checklist is
`Platform-Screen-Checklist.md`.
