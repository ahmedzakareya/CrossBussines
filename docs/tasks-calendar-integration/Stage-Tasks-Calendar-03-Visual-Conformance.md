# Stage-Tasks-Calendar-03 — Visual Conformance to the Inventory Authority

**Rule:** CrossBusiness has ONE visual language. The **Inventory module is the only visual authority**
(`https://localhost:44368/Inventory/Index`). If a screen can be visually distinguished from Inventory, the
implementation has failed.

**Status:** Tasks (5 screens) and Calendar (1 screen) now conform, and the conformance is **mechanically guarded**
by 36 tests.

---

## 1. What the authority actually is — read from source, not described

The contract was extracted from `Views/Inventory/Items.cshtml` (list-page reference) and
`Views/Inventory/Index.cshtml` (dashboard reference), not from a mockup or a memory of one:

| Element | Inventory pattern |
|---|---|
| Shell | `Layout = "~/Views/Shared/_LayoutInventory.cshtml"` |
| Localisation | `@inject IViewLocalizer Localizer` + `@inject IHtmlLocalizer<SharedResources> SR` |
| Page frame | `div.d-flex.flex-column.flex-column-fluid` |
| Toolbar | `#kt_app_toolbar.app-toolbar.pt-6.pb-2` → `.app-container.container-fluid.d-flex.flex-stack` |
| Title | `h1.page-heading.text-gray-900.fw-bold.fs-3.m-0` |
| Breadcrumb | `ul.breadcrumb.breadcrumb-separatorless.fw-semibold.fs-7.my-0` with a `bullet.bg-gray-500` separator |
| Primary action | `a.btn.btn-primary` + `i.ki-outline.ki-plus.fs-2` |
| Content | `#kt_app_content.app-content.flex-column-fluid` → `.app-container.container-fluid` |
| Filters | `card.card-flush.mb-5` → `card-body.py-5` → `row.g-4.align-items-end`, `form-control-solid` / `form-select-solid` |
| Card | **`card card-flush`** — with `card-header.align-items-center.py-4` (`card-title` + `card-toolbar`) and `card-body.pt-0` |
| Table | `table.align-middle.table-row-dashed.fs-6.gy-4`, head `tr.text-start.text-muted.fw-bold.fs-8.text-uppercase.gs-0` with `border-bottom:2px solid #13433a` |
| View-local CSS | **none** |

## 2. What Tasks and Calendar looked like before

| Divergence | Evidence |
|---|---|
| **Wrong shell** | all six views used `_LayoutBackend.cshtml` |
| **Bespoke card** | `.tasks-card { border-radius:16px; box-shadow:0 6px 24px …}` instead of `card-flush` |
| **A second design language, self-declared** | the Tasks stylesheet said `/* matched to the approved mockup */` and `/* Tabs — active is blue like the mockup (not the brand green) */` |
| **Invented palette** | five bespoke KPI colour schemes (`#3e97ff`, `#f79009`, `#17c653`, `#f1416c`) plus `#eaf3ef`, `#eaecf3`, `#181c32`, `#00a3ff` |
| **50 + 25 lines of view-local CSS** | Tasks/Index and Calendar; Inventory list pages carry **zero** |
| **Custom components** | `.kpi-card`, `.kpi-icon`, `.task-tabs`, `.sort-ico`, `.tasks-card` |

The stylesheet's own comments are the clearest evidence: it was written against a **different mockup** and
deliberately overrode the brand colour. That is the second design language the rule forbids.

## 3. What was changed

**Reused — nothing was invented.**

| Was | Now |
|---|---|
| `_LayoutBackend.cshtml` (6 views) | **`_LayoutInventory.cshtml`** |
| `.card.tasks-card` | `card card-flush` |
| `.card` (Calendar) | `card card-flush` |
| bespoke `.kpi-card` tiles | `card card-flush` tiles with Metronic `symbol` + `bg-light-*` / `text-*` semantic colours |
| `.task-tabs` forcing blue | plain Metronic `nav nav-tabs nav-line-tabs nav-line-tabs-2x` |
| `.sort-ico` double chevron | `ki-outline ki-arrow-up/down/up-down` + `text-primary` / `text-muted` |
| `table-row-bordered table-row-gray-100 fs-7` | `table align-middle table-row-dashed fs-6 gy-4` + the Inventory header rule |
| results card with no header | `card-header align-items-center py-4` + `card-title` + `card-toolbar` |
| raw hex in the progress ring | `var(--bs-success/primary/info/warning/gray-300)` design tokens |
| `style="background:#eaf3ef;color:#13433a"` | `bg-light-primary` / `text-primary` |
| 75 lines of view-local CSS | **0 in Tasks**; Calendar keeps only its FullCalendar CSS (§4) |

Two screens were also missing the Inventory breadcrumb; it was added from the authority's own markup.

## 4. Calendar's stated exemption

The rule says *"Calendar keeps FullCalendar functionality but must live inside the Inventory shell."*

So Calendar keeps its view-local CSS, and only that: FullCalendar event colours (already brand `#13433a` and
`#1b84ff`), the responsive toolbar rules, and the attendee-picker widget. It is now in the Inventory shell with
`card card-flush`.

**The exemption is narrow and encoded**, not assumed: `No_tasks_screen_carries_a_view_local_stylesheet` skips
Calendar explicitly, and `No_screen_introduces_a_colour_the_authority_does_not_use` allows exactly seven
FullCalendar colours for Calendar and nothing else.

## 5. The guard — 36 tests

`CrossBuy.Tests/TasksCalendarVisualConformanceTests.cs` reads the **authority itself** from disk and compares:

1. every screen uses `_LayoutInventory` and none of the three other shells;
2. every screen carries the seven Inventory toolbar/content markers — asserted against the authority first, so the
   test cannot pass by agreeing with a stale copy;
3. every screen uses `card card-flush`;
4. no screen reintroduces any of the eleven bespoke class names (matched inside `class="…"` only, so a comment may
   still explain why one was removed);
5. no Tasks screen carries a view-local stylesheet;
6. no screen introduces a hex colour the authority does not use.

**Test 6 found three real regressions my own conversion had missed** — `#eaf3ef` in two report screens and six
colours still in `Tasks/Index`. A rule enforced only by prose would have shipped them.

**On banning `style=`:** the test bans *unknown colours*, not the attribute — because the authority itself paints
inline (`Inventory/Index.cshtml` uses `style="background:linear-gradient(135deg,#1f6253 0%,#13433a 100%)"`). A
blanket ban would forbid exactly what the reference does.

## 6. Verification

| Check | Result |
|---|---|
| Debug build | **0 errors** |
| Release build | **0 compiler errors** (output copy blocked by the running app — expected) |
| Razor view compilation | **0 cshtml errors** |
| Visual conformance guard | **36 passed, 0 failed** |
| Full application suite | **1539 passed, 0 failed**, 183 skipped |
| Inventory files modified | **none** — the authority was read, never edited |
| Other tabs' files modified | **none** |
| Their tests weakened or deleted | **none** |

## 7. Honest limits

- **This was verified structurally, not visually.** The application was not running during the change, so no screen
  was rendered and compared by eye. The tests prove the shell, containers, card, components and palette match the
  authority's source; they cannot prove the rendered result is pixel-identical. **A visual pass against
  `https://localhost:44368/Inventory/Index` with the app running is still worth doing.**
- **The Calendar attendee picker (`cbrecip-*`) remains a bespoke component.** Inventory uses `select2`
  (`data-control="select2"`) for pickers. Converting it is a functional change to a working widget with avatars and
  email display, and it sits inside the module still pending handover (TCI-D-02). Recorded, not silently kept.
- **Screens that do not exist yet** — a task board, an agenda page, activity history — inherit this contract by the
  guard: add them to `AllScreens()` and they are checked from the first commit. A screen not listed there is a
  screen nobody is checking.
