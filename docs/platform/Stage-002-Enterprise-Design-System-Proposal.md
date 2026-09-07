# Stage 2B — Enterprise Design System — Proposal

**Design only. No screen was modified. No mass redesign.** Fulfils P0-C5.
**Accepted as a Stage 2B dependency**: tokens and core components must exist before 2C's Master Data screens, or those
screens get built twice.

---

## 1. What exists today — measured, not assumed

| Fact | Evidence | Consequence |
|---|---|---|
| **There is no token layer** | `crossbuy-brand.css` is **211 lines**; its 52 custom properties are **Bootstrap/Metronic variable overrides** (`--bs-btn-bg`, `--bs-component-active-bg`, …) | Brand colour is applied by overriding a framework, not by owning a token vocabulary. Every new screen re-derives spacing, radius and status colour by hand. |
| **11 layouts** | `_Layout`, `_LayoutAccounting`, `_LayoutBackend`, `_LayoutInventory`, `_LayoutManufacturing`, `_LayoutPeople`, `_LayoutPos`, `_LayoutPosApp`, `_LayoutHyperPos`, `_LayoutEmbed`, `_mainLayout` | The shell is duplicated 11 times. A header or nav change is an 11-file change. |
| **0 ViewComponents** | `find -iname "*ViewComponent*.cs"` → 0 | No compiled, testable UI unit exists. All reuse is partial-based. |
| **20 shared partials**, mostly module-specific | `_DocTimeline`, `_DocEventTimeline`, `_NotificationBell`, `_QuickAdd`, `_CbToastr`, `_AnnouncementsBanner`, `_CalendarReminders` | Genuine reuse **does** exist (timeline, toast, notification bell) — these are the evidenced extraction candidates. |
| **327 views** | `find Views -name '*.cshtml'` | Any mass redesign is out of the question; the system must be **additive**. |

**Two timeline partials already exist** (`_DocTimeline`, `_DocEventTimeline`) — that duplication is exactly what a
component layer prevents, and it is the strongest single piece of evidence for this proposal.

---

## 2. Design tokens

Tokens are **owned by CrossBuy** and *feed* Metronic variables, reversing today's direction. One file,
`crossbuy-tokens.css`, loaded before the brand overrides.

### 2.1 Colour

```
--cb-brand-900: #13433a   /* ledger green — primary actions, active nav, headers */
--cb-brand-700: #1f6253   /* supporting green — hover, secondary emphasis */
--cb-brand-050: #e9f1ee   /* light green surface — selected rows, subtle fills */
--cb-accent-600: #d4a017  /* gold — highlights, warnings-with-brand, KPI emphasis */
```

Gold is an **accent, never a primary action colour** — a gold button competes with the brand and reads as a warning.

**Semantic status** (deliberately distinct from brand, so "success" is never mistaken for "branded"):

```
--cb-success / --cb-warning / --cb-danger / --cb-info / --cb-neutral
  each with -bg (surface), -fg (text), -border  → 15 values
```

**Financial semantics** — this platform needs them and generic status colours do not cover them:

```
--cb-debit / --cb-credit / --cb-posted / --cb-draft / --cb-reversed / --cb-negative-stock
```

`--cb-reversed` matters because the platform's correction primitive is *reverse, never delete* — a reversed document
must be visually distinct from a deleted one, since deleted ones do not exist.

**Surfaces (4 levels, no more):** `--cb-surface-page` · `-raised` · `-sunken` · `-overlay`.
Four is a deliberate cap — "no excessive nested cards" is unenforceable without a limited surface vocabulary.

### 2.2 Spacing, radius, elevation, typography

```
--cb-space-1 … -8      4 8 12 16 24 32 48 64        (4px base)
--cb-radius-sm/md/lg/pill    4 6 10 999
--cb-elev-0/1/2/3      none · subtle · card · overlay   (no gradients; shadow only)
--cb-font-ar: 'Cairo'        --cb-font-en: Metronic default (Inter)
--cb-text-xs … -3xl   12 13 14 16 20 24 30
--cb-leading-ar: 1.75        --cb-leading-en: 1.5
```

**Arabic typography rules** — the parity detail most often missed: Cairo needs **greater line-height** (1.75 vs 1.5)
because Arabic ascenders/descenders and diacritics collide at Latin leading; Arabic numerals render **LTR inside RTL
text**, so numeric cells are `dir="ltr"` even in Arabic; and Arabic text must never be letter-spaced, which breaks
glyph joining.

### 2.3 Density, states, motion

**Density:** `comfortable` (default), `compact` (dense grids), `operational` (POS/WMS — larger touch targets).
A token switch on `<html data-cb-density>`, not per-screen CSS.

**States, defined once for every component:** hover · focus-visible (**2px `--cb-brand-700` outline with 2px offset —
never `outline:none`**) · active · selected (`--cb-brand-050`) · disabled (opacity + `not-allowed`, never colour-only) ·
loading · error · **read-only vs disabled distinguished** (read-only is legitimate for a user with view-only
permission; disabled reads as broken).

**Motion:** 120 ms micro, 200 ms panel, 250 ms drawer; `ease-out` entering, `ease-in` leaving; all wrapped in
`@media (prefers-reduced-motion: reduce)` → none.

**Dark mode: readiness only, not implemented.** Tokens are declared on `:root` with semantic names so a future
`[data-cb-theme="dark"]` block can re-point them. No dark mode is proposed for approval now.

---

## 3. Component families — extraction gated on evidenced reuse

The brief's rule — *only recommend shared components where reuse is evidenced across at least two screens* — is
applied literally. Each component is placed in one of three tiers.

### Tier 1 — Extract in 2B (reuse already evidenced in source)

| Component | Evidence | Form |
|---|---|---|
| **Timeline** | `_DocTimeline` **and** `_DocEventTimeline` already exist — duplicated | **ViewComponent** (first one in the codebase) |
| **Toast/notification** | `_CbToastr` used across modules | JS component + token styling |
| **Notification bell** | `_NotificationBell` in multiple layouts | ViewComponent |
| **Quick add** | `_QuickAdd` used by several document screens | ViewComponent |
| **Page header** | repeated inline in 11 layouts | Razor partial + tokens |
| **Status badge** | posted/draft/reversed rendered ad hoc across accounting and inventory | CSS utility + partial |
| **Empty / no-permission / loading states** | needed by every 2B capability panel and every 2C area | Razor partials (3) |
| **Attachments panel · Comments panel** | 2B unifies **three** file stores — reuse guaranteed by design | ViewComponents |
| **Data table shell** | filter + table + pagination repeated throughout | partial + JS, not a grid framework |

### Tier 2 — Standardise by token/utility only (do NOT extract yet)

Buttons, inputs, selects, date/time, validation messages, tabs, accordions, modals, drawers, breadcrumbs, KPI cards,
pagination, command bars. These are Metronic/Bootstrap components already; extracting wrappers would add a layer
without removing duplication. **Standardise their token usage and document the pattern; extract only when a third
divergent copy appears.**

### Tier 3 — Design now, build with their consumer

Wizards (with import), designer canvas (with unit-conversion/variant designers), bulk actions (with approval queue),
related-object links (with 2B relations), audit history (with 2B audit), conflict/stale-data (with optimistic
concurrency), enterprise search box (with search).

**This tiering is the core recommendation.** A 30-component library built speculatively would be the UI equivalent of
the 20 speculative analyzers already rejected.

---

## 4. Page patterns

| Pattern | Structure | Used by |
|---|---|---|
| **Record workspace** | header + status + tab rail + area body + capability rail (timeline/comments/attachments) | Product Workspace, HR employee, project, invoice |
| **List page** | header + filter toolbar + table + bulk bar + pagination | every master/document list |
| **Dashboard** | KPI row + chart grid + action list | compliance, quality, exec |
| **Setup screen** | grouped form sections, explicit save, dirty-state guard | unit sets, rules, policies |
| **Approval queue** | queue list + preview pane + decide bar | Approval Center |
| **Designer** | canvas + palette + property panel | workflow, escalation, variants, conversions |
| **Operational console** | full-bleed, keyboard-first, minimal chrome | POS, KDS, shop floor |
| **Split view** | list ⇆ detail, persistent | search results, related objects |
| **Details drawer** | right (LTR) / left (RTL) overlay, non-destructive | quick inspect from any list |
| **Mobile workflow** | one task per screen, thumb-reachable, offline-tolerant | mobile WMS, attendance |
| **Import wizard** | upload → map → validate → preview → commit | product import |
| **Validation review** | error table grouped by rule, row-level fix | import validation, duplicates |
| **Diagnostic page** | read-only evidence, copyable | analyzer diagnostics, CI evidence |

**Universal rule:** every pattern must render the **no-permission state** as a first-class layout, not an error.
Stage 1 made hiding a control explicitly *not* a control — so a partially-permitted screen must show what the user may
see and state plainly what they may not.

---

## 5. Application states

Loading (skeleton, never a spinner-only page) · empty (distinguish *no data* from *no matching filter* — different
actions) · error (what failed, what to do, correlation id) · **no permission** (explain, never blank) · **partial
permission** (show permitted areas, mark the rest) · offline (POS/WMS only, queue depth visible) · **conflict**
(another user's change, side-by-side, no silent overwrite) · stale data (age + refresh) · unsaved changes (block
navigation).

**Conflict and partial-permission are the two the current codebase has no pattern for**, and both become common in 2B
once approvals and capability trimming exist.

---

## 6. Accessibility

Keyboard: every action reachable; tab order follows visual order **in both directions** (RTL tab order is a real
defect source). Focus: always visible, 2px offset outline. Contrast: 4.5:1 body, 3:1 large — **`#13433a` on white is
compliant; `#d4a017` on white is not**, so gold carries no body text and needs a dark foreground on its surface.
Screen readers: landmarks, labelled controls, `aria-live` for toasts, tables with real `<th scope>`. Touch: 44px
minimum, 48px in `operational` density. Reduced motion honoured. `lang`/`dir` set on `<html>` per request culture.

---

## 7. Specialized experiences — optimize interaction, keep the brand

| System | Optimisation | Non-negotiable |
|---|---|---|
| **POS** | full-bleed, keyboard/barcode-first, no nav chrome, large tap targets, offline queue | brand colours, Cairo, RTL |
| **KDS** | high-contrast tiles, colour+shape status (readable across a kitchen, and colour-blind safe), auto-refresh | status token semantics |
| **Mobile WMS** | one scan per screen, glove-friendly, torch/haptic, works one-handed | tokens, status semantics |
| **Shop floor** | large type, minimal input, machine-adjacent readability | tokens |
| **Supervisor console** | dense multi-pane, real-time | `compact` density tokens |
| **Customer service workspace** | split view, timeline-centred, keyboard macros | full component set |

These may drop the standard shell. They may **not** invent colours, status meanings or typography.

---

## 8. Implementation boundaries

| Becomes | What | Why |
|---|---|---|
| **Design tokens** (`crossbuy-tokens.css`) | §2 in full | Own the vocabulary; feed Metronic instead of overriding it |
| **CSS utilities** | spacing, surface, status, density, numeric-LTR | cheap, no server coupling |
| **Metronic configuration** | map Metronic/Bootstrap vars **to** tokens | keeps the framework, inverts the dependency |
| **Razor partials** | page header, the 3 state partials, status badge, table shell | markup-only reuse |
| **ViewComponents** (first in codebase) | timeline, notification bell, quick add, attachments, comments | need logic + are testable |
| **JavaScript components** | toast, table behaviour, drawer, wizard stepper, designer canvas | interaction |
| **Page templates** | the 13 patterns in §4 as scaffolds | new screens start correct |
| **Layout consolidation** | **11 → 3**: `_LayoutApp` (business modules, module nav injected), `_LayoutOperational` (POS/KDS/floor), `_LayoutEmbed` | an 11-file header change is the clearest duplication in the UI today |

**Layout consolidation is Recommended Now, not Required Now** — it touches every screen's rendering, so it needs its
own batch with visual verification, exactly like the company-constant sweep.

---

## 9. What this proposal explicitly does not do

No existing screen is redesigned. No layout is merged in Phase 0. No dark mode. No component library beyond Tier 1.
No new CSS framework. **2B ships tokens + Tier 1 + the 13 page templates**; that is the minimum that stops 2C being
built twice, and nothing more.
