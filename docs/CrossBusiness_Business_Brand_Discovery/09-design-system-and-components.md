# 09 — Design system and components

The shell the product is assembled from, the patterns that recur, and the house rules that govern
them.

---

## 1. The stack

**Metronic 8.3.3** (`wwwroot/Backend-assets`), Bootstrap 5 underneath, **Keenicons**, and
`crossbuy-brand.css` layered on top to win the cascade (document 08).

Two style bundles ship: `style.bundle.css` and `style.bundle.rtl.css`. **Runtime:** the Arabic
pass confirmed the RTL bundle is the one served — `style.bundle.rtl.css?v=JaSRwdxz…` — so
direction is handled by swapping the whole stylesheet, not by overriding it.

> **A trap this codebase has already paid for.** A Bootstrap-*looking* utility class is not a
> utility unless this bundle defines it. `w-180px`, `w-220px` and `w-240px` do **not** exist here;
> `w-200px` and `w-250px` do. A class that does not exist fails silently — the element falls back
> to its content width and the text clips.

## 2. Eleven layouts

| Layout | Serves | Sidebar | Tab identity |
|---|---|---|---|
| `_LayoutInventory` | Inventory, **Workspace, Tasks, Reports, Calendar** | `MainMenu.Inventory()` | `Inventory System - CrossBuy` |
| `_LayoutAccounting` | Accounting, **CRM**, Currency | `MainMenu.Accounting()` | `Accounting System - CrossBuy` |
| `_LayoutBackend` | Admin/HR, **Chat, Projects, Restaurant, Hyper** | `MainMenu.Admin()` | `CrossBuy Admin` |
| `_LayoutManufacturing` | Manufacturing | `MainMenu.Manufacturing()` | `Warehouse System - CrossBuy` |
| `_LayoutPos`, `_LayoutPosApp` | Restaurant operator screens | `MainMenu.Restaurant()` | `· CrossBuy POS` |
| `_LayoutHyperPos` | Supermarket lane | — | `· CrossBuy Hyper` |
| `_LayoutPeople` | Employee self-service | — | `People Portal - CrossBuy` |
| `_LayoutEmbed` | Embedded / iframe surfaces | — | `CrossBuy` |
| `_Layout`, `_mainLayout` | generic | — | `… - CrossBuy` |
| `_LayoutWorkspace` | **nothing — zero references** | — | `· CrossBusiness` |

The bold entries are the mismatch: five layouts serve fourteen modules' worth of screens, so a
screen's shell tells the user which layout it borrowed, not which module it belongs to. Documents
02 and 12.

`_MainMenu.cshtml` is the one menu renderer. It takes a module's category list, prepends the
Platform category, and evaluates per-item permission hooks — `manage`, `acc-manage`, `crm-manage`,
`platform-ops`, `report`. The `platform-ops` predicate is required to **mirror
`PlatformOpsAttribute` exactly**, with the reason written in the view: *"Hiding a link the user
could open is as wrong as showing one they cannot, and a nav predicate that drifts from the filter
produces both over time."*

The Reporting module is resolved **optionally** (`GetService<T>()`, not `@inject`) because this
partial renders on every page and a hard dependency would take the whole shell down with it.

## 3. Shared components

Twenty-five shared partials. The ones that define the product's behaviour:

| Partial | What it gives every screen |
|---|---|
| `_MainMenu` | the sidebar, with permission-aware rows |
| `_NotificationBell` | the platform event stream, surfaced |
| `_QuickAdd` | create-anything, from anywhere |
| `_DocTimeline`, `_DocEventTimeline` | a document's history |
| `_EntityConversation` | comments and `@mentions` on any registered entity |
| `_AnnouncementsBanner` | company-wide announcements |
| `_CalendarReminders` | upcoming events |
| `_CbToastr` | the one toast |
| `_ReportPreviewModal`, `_ReportPrintMenu` | the print path (§5) |
| `_WorkspacePanelState` | the Workspace's panel memory |

The timeline, conversation and print partials are generic over the 28 registered entity codes
(document 04 §2). **That is the product's main reuse lever:** a new document type gets history,
comments, mentions and printing by registering, not by building.

## 4. Recurring patterns

### 4.1 The card + toolbar page

Every list screen is the same shape: an `app-toolbar` band carrying the page heading, a
breadcrumb, and right-aligned actions; then `app-content` holding one or more `card`s. Observed on
every one of the 20 screens captured in document 11.

### 4.2 The Trial Balance house style

`Accounting/TrialBalance` is the **stated visual authority for accounting screens** — a standing
rule in this project: every accounting screen is expected to look like it. The vocabulary it
establishes, counted from the view:

```
card · p-6 · flex-stack · flex-center · flex-wrap
fw-bold · fw-semibold · text-muted · text-gray-900
fs-lg-2tx (the headline figure) · fs-4 · fs-6 · fs-7 · fs-8
text-end (every money column) · min-w-175px
form-control-solid (every filter input)
ki-outline (every icon)
```

167 lines. The pattern is: a row of summary tiles with `fs-lg-2tx` figures, solid-styled filters,
a right-aligned numeric table, and the accounting identity — debits equal credits — shown rather
than asserted.

### 4.3 Lists and refusals

Two house rules recorded in this project:

- **A refusal is a dashed notice coloured by kind** — not a toast, not a redirect. The user is
  told what was refused and why, in place.
- **A mutation never reloads the page.** The row updates; the screen stays.

The second rule has a matching defect pattern worth naming, because it was found here: an AJAX
endpoint that answers a denial with a **302 redirect** produces a generic failure in the browser —
jQuery follows the redirect, receives a login or list page with HTTP 200, and the real reason is
discarded. An AJAX refusal must be JSON.

### 4.4 The modal is the entry point

**No click may open a new tab or start a download.** 36 views use `data-bs-toggle="modal"`; 15
contain a `target="_blank"`. The rule exists because a download or a new tab takes the user out of
the product and loses their place.

### 4.5 Bilingual display fields

The owner's standing rule: **every displayed field has a twin column**, both captured on input,
and rendered through `DisplayName.Of` so the current culture picks. The menu model itself is built
this way — `LabelAr` and `LabelEn` on every one of the 186 entries.

Where this is *not* done, it shows: an Arabic employee name on an English page is a bug, not a
fallback (document 10).

### 4.6 Numbers, direction and time

- Arabic uses a **mutated `CultureInfo`** (`FixNumbers(new CultureInfo("ar"))`) so numbers
  normalise to Latin digits and `.` while dates and RTL behaviour are kept. `en` and `fr` are
  unchanged.
- Fractions and Latin names inside Arabic text must be **bidi-isolated**, or the digits reorder.
- Relative times are **assembled**, not formatted, so the word order is right in each language.
- **A timestamp with no zone is UTC.** Rendering it as local without saying so is how a document
  appears to have been posted tomorrow.

## 5. Printing is a component, not a screen decision

> **Mandatory:** no screen prints its own HTML. Register a dataset, link to `/Reports/Viewer`.

`_ReportPrintMenu` and `_ReportPreviewModal` are the entry points. Eight Inventory documents share
a single `DocumentDetails.cshtml` and opt into printing by naming themselves.

Layout rules that come with it (document 06): header bands bind the first row; a dataset repeats
its header; a saved layout must fetch what it draws; `Contain` is 0; and the report font stack
must be dual-script or the document splits across two typefaces.

## 6. Verification harnesses

This project ships its own UI conformance tooling under `tools/ui-conformance/` — among them
`btn-variant-audit.mjs`, `alert-variant-audit.mjs`, `button-contrast-probe.mjs`,
`authority-contrast-probe.mjs`, `cbev-colour-check.mjs`. Plus `tools/authz-probe/` for
authorisation and `governance/tools/check-file-ownership.ps1` for ownership.

**A design system with a contrast probe in the repository is a design system somebody intends to
enforce.** That is the strongest signal in this document — and document 12 measures how far the
enforcement has actually reached.

> **A caution recorded by this project about its own harnesses:** a grep-shaped structural test
> fails *open*. It passes when the thing it forbids is absent for any reason, including the file
> having moved. Mutate the code a guard forbids and watch it fail before trusting it.

---

## 7. What a design system document would still need

Absent from the repository and needed before this could be handed to a designer or an agency:

1. **Spacing, radius, elevation and z-index scales.** The colour layer is tokenised; geometry is
   not — it is Metronic's defaults plus per-view literals.
2. **A type scale.** `fs-lg-2tx`, `fs-4`, `fs-7` are Metronic's; no CrossBuy-level heading scale
   is declared.
3. **Component states.** Rest/hover/pressed are defined for brand fills only.
4. **Logo usage rules.** Clear space, minimum size, what may sit behind it, and which of the eight
   files is canonical (document 08 §2.3 lists all eight).
5. **Accent vs. warning.** Both are `#F59E0B`. Until the system distinguishes them by token, the
   amber rule cannot be enforced by a tool — only by review.
