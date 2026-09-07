# 09 — Navigation

## Hierarchy

Global product switcher → module/sidebar navigation → page tabs → in-page anchors. Do not duplicate the same hierarchy in multiple levels.

The sidebar uses dark Blue 900, white/blue text, a Blue selected state, and logical start indicators. It collapses to Metronic’s drawer under `lg`. Counts use badges only when actionable.

## Topbar

Contains drawer trigger, optional global search, company/business context, notifications, locale, help, and identity. Search must be functional or explicitly marked unavailable; presentational inputs are prohibited.

## Breadcrumbs and tabs

Breadcrumbs show location, not history; maximum four items before collapsing. Tabs switch peer views without changing the underlying record. Use 2–7 tabs; beyond that use secondary navigation or a menu. Keyboard arrow navigation is required for ARIA tabs.

## Deep links

Entity links use the owning module’s route resolver. Workspace, notifications, and reports do not invent routes or bypass module authorization.
