# CrossBuy Platform UI Standard

**Status:** Standard (Stage 1 Hotfix A.1 / A6.2-A6.3). **Documentation only — no screen was changed by this hotfix.**
**Derived entirely from** `Accounting-Visual-Identity-Reference.md`, which is a measurement of the existing code.
**Awards no maturity points** — a standard is not an implementation.

---

## 1. The rule, and the one open conflict

**Accounting is the visual identity reference for every future platform and administrative tool.** A platform tool
must look like part of the Accounting suite, not like a separate product.

**Open conflict, for the owner to settle (A6.2 vs the codebase):** A6.2 states the standard must *"preserve the
existing CrossBuy blue identity."* The application's brand stylesheet does the opposite by design — it overrides
Metronic's blue primary with **ledger green `#13433a` + gold `#d4a017`**, and says so in its header comment. This
standard therefore specifies **green**, because that is what every existing screen renders, and flags the wording
for correction. Nothing was rebranded. See the reference document §0.

## 2. Principles

1. Modern Metronic 8 enterprise interface — use the framework's components, do not re-style them.
2. Professional, soft, clean. **No gradients** beyond what Metronic ships.
3. **Flush cards, not nested cards** (`card card-flush`). A card inside a card inside a card is a defect.
4. Strong hierarchy: one `h1.page-heading` per page, then cards, then tables.
5. Toolbar and filters in the **same** place on every screen (§4).
6. Consistent table actions and consistent status badges (§5, §6).
7. **Cairo** for Arabic, **Inter** for Latin — both already loaded by the accounting layout.
8. **Full RTL/LTR parity**, from the request culture — never a per-user toggle.
9. Responsive: `container-fluid` + `row g-5 gx-xl-10`, with `d-none d-lg-flex` / `d-flex d-lg-none` for the desktop
   and mobile variants of a control, exactly as the layout already does.
10. Accessibility-conscious: the measured focus ring (`0 0 0 .25rem rgba(19,67,58,.15)`) must not be removed, and
    status must never be conveyed by colour alone — the badge carries **text**.
11. **Light theme only** today. Do not ship a dark-only style; there is no dark palette to inherit (reference §2.11).
12. **Specialized operational screens keep their own UX** (§8). POS is not an administration screen.

## 3. Approved page structure

Copy this skeleton; it is `Views/Accounting/Index.cshtml`'s, verbatim in structure:

```html
<div class="d-flex flex-column flex-column-fluid">
  <div class="app-toolbar pt-6 pb-2">
    <div class="app-container container-fluid d-flex flex-stack">
      <div class="page-title d-flex flex-column justify-content-center gap-1">
        <h1 class="page-heading text-gray-900 fw-bold fs-3 m-0">@Localizer["Title"]</h1>
        <ul class="breadcrumb breadcrumb-separatorless fw-semibold fs-7 my-0">
          <li class="breadcrumb-item text-muted">…</li>
          <li class="breadcrumb-item"><span class="bullet bg-gray-500 w-5px h-2px"></span></li>
          <li class="breadcrumb-item text-muted">…</li>
        </ul>
      </div>
      <div class="d-flex align-items-center gap-2 gap-lg-3">
        <!-- secondary actions, then ONE primary action, last -->
      </div>
    </div>
  </div>
  <div class="app-content flex-column-fluid">
    <div class="app-container container-fluid">
      <!-- cards -->
    </div>
  </div>
</div>
```

**Rules:** exactly one `page-heading`; the toolbar is on the same row as the title, never beneath it; breadcrumbs
use the bullet separator; every visible string goes through `@Localizer`.

## 4. Approved filter layout

Filters live in the **card header** of the card whose data they filter — `card-header pt-5` with the controls in
`card-toolbar` — not in a separate floating panel and not above the page title. A report screen (`TrialBalance`)
puts date range + cost centre there and the totals in the card footer.

## 5. Approved table structure

* Metronic DataTables bundle (already loaded by the accounting layout).
* `table align-middle table-row-bordered`.
* Row actions right-aligned as `btn btn-icon btn-active-color-primary w-35px h-35px`, or a single Metronic dropdown
  when there are more than three.
* Numeric columns right-aligned in LTR and left-aligned in RTL — let the RTL bundle do it; do not hard-code
  `text-end`.

## 6. Approved cards, buttons, badges

| Element | Classes |
|---|---|
| Card | `card card-flush` (+ `h-lg-100` when in a KPI row) |
| Card header | `card-header pt-5`, title `card-title align-items-start flex-column` |
| KPI row | `row g-5 gx-xl-10 mb-5 mb-xl-10` |
| Primary button | `btn btn-sm btn-flex btn-primary fw-bold` — **one per toolbar** |
| Secondary button | `btn btn-sm btn-flex btn-light fw-bold` |
| Icon button | `btn btn-icon btn-active-color-primary w-35px h-35px` |
| Status badge | `badge badge-light-{success\|warning\|danger\|info}` with text |
| Icons | `ki-outline ki-* fs-3` (Keenicons) — never a different icon set |

## 7. Approved modal / drawer, localization, responsive

* **Modals/drawers:** Metronic's own (`modal`, `drawer drawer-end`) with the header carrying the same
  title/subtitle pair as a card header. No custom overlay implementations.
* **Localization:** `@Localizer["…"]` for **every** user-visible string, with a per-view resource file
  `Resources/Views/<Area>/<View>.{ar,en,fr}.resx` — **all three cultures**, as Accounting has for its 991 strings.
  A hardcoded string in a platform screen is a defect.
* **Responsive:** `container-fluid`; `d-none d-lg-flex` / `d-flex d-lg-none` for desktop/mobile variants of a
  control; never a separate mobile view.

## 8. Domain exceptions — deliberately NOT standardised

These are operational tools whose UX is driven by speed, glanceability or hardware, and forcing the administrative
shell on them would make them worse:

| Exception | Why |
|---|---|
| **POS / restaurant cashier** (`_LayoutPos`, `_LayoutPosApp`) | Touch targets, one-hand operation, no sidebar. |
| **Hypermarket POS lane** (`_LayoutHyperPos`) | Scale/barcode-driven, keyboard-first. |
| **KDS / kitchen display** | Read-at-distance, high contrast, auto-refresh, no navigation. |
| **Manufacturing floor screens** | Gloves, glare, large state indicators. |
| **Mobile attendance** | Phone-first, camera/GPS. |
| **Public storefront** (`Views/Store`, `Home/Store`) | End-user commerce, its own visual identity. |

They keep their layouts. What they still share: localization, RTL/LTR parity and the brand palette.

## 9. Screens that currently violate the standard

Recorded from the reference measurement, **not fixed here**:

1. **Eleven layouts**, with the `app-*` shell, toolbar and page header re-implemented across several. Consolidation
   candidates: `_Layout` / `_mainLayout` / `_LayoutBackend`.
2. **No shared page-header partial** — every screen rebuilds the toolbar/title/breadcrumb block by hand, so the
   structure in §3 is a convention held by copy-paste.
3. **No shared filter-toolbar, empty-state, loading-state or DataTable wrapper.**
4. **No consistent empty state anywhere**, including in Accounting (reference §2.10). The standard cannot cite an
   existing pattern, so it does not pretend to: defining one is a follow-up.
5. **Business Event Monitor** (`Views/BusinessEventMonitor/Index.cshtml`) — a Stage 0 platform screen built before
   this standard existed. It uses the brand palette and localization, but its stat cards and filter placement were
   authored independently. **Deliberately not realigned here** (A6.4 forbids redesigning it without a separately
   documented, clearly safe change).

## 10. Migration strategy

| When | What |
|---|---|
| **Now (this hotfix)** | Documentation only. Zero screens changed. |
| **Next platform screen** | Follow §3-§7 by hand. If two consecutive new screens copy the same block, extract it then — not before. |
| **A later UI stage** | Extract `_PlatformPageHeader`, `_PlatformFilterToolbar`, `_PlatformEmptyState`, `_PlatformLoading`, `_PlatformDataTable`; define the missing empty state; then realign Business Event Monitor. Consolidate the eleven layouts last — it is the highest-risk change and the least visible. |
| **Never** | A mass restyle of POS/KDS/manufacturing/storefront (§8). |

## 11. A6.4 — shared component extraction: NOT performed, and why

A6.4 permits extraction only when an equivalent already exists in Accounting, an existing platform tool needs it,
extraction is behaviour-preserving, and it does not widen the hotfix. **The first condition fails for every
candidate:** no page-header, filter-toolbar, empty-state, loading-state or DataTable partial exists in Accounting to
extract — those patterns live as inline markup repeated per screen. Extracting would mean *authoring* new shared
components inside a security hotfix and touching the accounting views to adopt them.

So nothing was extracted. The candidates are listed in §10 for the UI stage that owns them.
