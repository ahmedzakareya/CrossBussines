# 05 — Layout System

## Target layout families

1. **App shell** — Workspace, Reporting, Accounting, Inventory, CRM, Projects, HR, Tasks, Calendar, Communication, Construction and administration.
2. **Operational shell** — POS, KDS, hyper lane, manufacturing floor, mobile attendance: minimal chrome, persistent operational controls, touch targets.
3. **Embed/print shell** — report output, invoice print, embedded panels; no redundant navigation.

This is a migration target, not permission to rewrite the current 13 layouts immediately.

## App shell anatomy

Sidebar, topbar, page header, optional command bar, content canvas, optional contextual rail, global overlays. Sidebar becomes Metronic’s off-canvas drawer below `lg`. Content must set `min-width:0`; wide tables scroll inside their cards.

## Page anatomy

Breadcrumb (when hierarchy matters), title and subtitle, primary action, secondary actions, filters, content, state feedback. One page-level primary action maximum. Sticky command bars are allowed for long editing flows, with safe-area handling on mobile.

## Nesting and elevation

- Maximum visual card nesting: **2 levels**. Prefer sections/dividers inside a card.
- Level 0 canvas; level 1 card; level 2 popover/modal/drawer.
- Do not place a shadowed card inside another shadowed card.
- Use border-only cards for dense lists; shadow only for separation from canvas or overlays.
