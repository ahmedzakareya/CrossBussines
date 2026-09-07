# Platform screen checklist

**Status:** Checklist (Stage 1 Hotfix A.1 / A6.3). Apply to every **new** platform or administrative screen.
Authority: `CrossBuy-Platform-UI-Standard.md`; evidence: `Accounting-Visual-Identity-Reference.md`.

Not for the domain exceptions (POS, restaurant cashier, KDS, manufacturing floor, mobile attendance, public
storefront) — Standard §8.

---

## Structure

- [ ] Exactly **one** `h1.page-heading text-gray-900 fw-bold fs-3` on the page.
- [ ] `app-toolbar pt-6 pb-2` → `app-container container-fluid d-flex flex-stack`, with the action group in the
      **same row** as the title, not beneath it.
- [ ] Breadcrumbs `breadcrumb breadcrumb-separatorless fw-semibold fs-7`, separator
      `span.bullet.bg-gray-500.w-5px.h-2px`.
- [ ] Content in `app-content flex-column-fluid` → `app-container container-fluid`.
- [ ] Cards are `card card-flush`. **No card nested in a card.**
- [ ] KPI rows use `row g-5 gx-xl-10 mb-5 mb-xl-10`.

## Actions

- [ ] **One** primary button per toolbar: `btn btn-sm btn-flex btn-primary fw-bold`, placed last.
- [ ] Secondary actions: `btn btn-sm btn-flex btn-light fw-bold`.
- [ ] Row actions: `btn btn-icon btn-active-color-primary w-35px h-35px`, or one dropdown when > 3.
- [ ] Icons are Keenicons only: `ki-outline ki-* fs-3`.

## Data

- [ ] Filters live in the **card header** (`card-toolbar`) of the card they filter — never above the page title.
- [ ] Tables: Metronic DataTables bundle, `table align-middle table-row-bordered`.
- [ ] Numeric alignment left to the RTL/LTR bundle — no hardcoded `text-end`.
- [ ] Status shown as `badge badge-light-{state}` **with text**, never colour alone.
- [ ] A loading state exists (`spinner-border`).
- [ ] An empty state exists. ⚠ **No project-wide pattern exists yet** (Reference §2.10) — write a plain
      `text-muted` centred block and record it, so the eventual shared partial has real precedent.

## Localization

- [ ] Every user-visible string via `@Localizer["…"]`. Zero hardcoded strings.
- [ ] `Resources/Views/<Area>/<View>.ar.resx`, `.en.resx` **and** `.fr.resx` — all **three** cultures present.
- [ ] Keys added to the **end** of each resx (never a whole-file rewrite — it collides with the selective-commit
      plumbing for shared resources).

## Direction and theme

- [ ] Renders correctly in **both** `dir="rtl"` and `dir="ltr"` — verified, not assumed.
- [ ] Arabic uses **Cairo**, Latin uses **Inter** (already loaded by the layout; do not add a third font).
- [ ] **Light theme only.** No dark-only styling — there is no dark palette to inherit (Reference §2.11).
- [ ] Brand palette used through Metronic's tokens (`btn-primary`, `text-primary`, `bg-primary`) rather than
      hardcoded hexes, so `crossbuy-brand.css` stays the single source: deep `#13433a`, mid `#1f6253`,
      light `#e9f1ee`, gold `#d4a017` / `#f0c64b`.
- [ ] The measured focus ring is not removed or overridden.

## Responsive

- [ ] `container-fluid` throughout.
- [ ] Desktop/mobile variants of a control via `d-none d-lg-flex` / `d-flex d-lg-none` — never a separate view.
- [ ] Wide tables scroll inside their own container; the page body never scrolls horizontally.

## Security — the part a UI checklist usually forgets

- [ ] The screen's **server-side** authorization is enforced in the controller (a module permission attribute, or an
      access-service call). **Hiding a button is not a control** — Hotfix A.1 exists because ten financial endpoints
      were reachable regardless of what any screen displayed.
- [ ] Any company id the screen sends is treated as **compatibility-only** by the server and validated against the
      resolved `BusinessContext`.
- [ ] No accounting figure, employee identifier or company id appears in a client-side log or error string.

## Review

- [ ] Checked against `CrossBuy-Platform-UI-Standard.md` §3-§7.
- [ ] If this screen introduces a block that a previous screen also hand-rolled, note it in Standard §10 as an
      extraction candidate — **do not** extract it mid-feature.
