# 11 — Reporting

**Visual authority: INVENTORY — and that includes the future Report Studio.** Reporting has no chrome, stylesheet,
shell or design language of its own. Every Reporting screen renders on `~/Views/Shared/_LayoutInventory.cshtml`
and reuses Inventory's toolbar, cards, filters, tables and forms over Metronic 8 / Bootstrap 5 / KeenIcons. The
shipped Reports Center and Report Viewer are the worked examples. Report *content* may still use domain semantics,
including financial green inside accounting reports.

> Corrected 2026-08-12: this file previously read "Reporting uses CrossBusiness Blue chrome". That contradicted the
> owner's standing instruction, `CLAUDE.md` and the shipped code. See §0 of
> `docs/platform/Stage-Reporting-Report-Studio-Contract.md` for the full correction.

## Report Center

Catalog/search, category filters, favorites, saved reports, recent runs, schedules, and access-aware cards. A report card contains icon, name, description, category, availability/status, favorite, and run action. Do not expose reports or parameters the caller cannot access.

## Viewer

Page title and metadata, parameter panel, run/export/print actions, result region, truncation notice, diagnostics available only to authorized operators, and history/archive links. Parameters distinguish required, optional, system-supplied, invalid, and unavailable states.

## Output

HTML is accessible and responsive; CSV/Excel are data exports rather than visual replicas; PDF/print use the embed shell. Report content declares currency, timezone, filters, generated time, and row caps. Preview is visually distinct from a complete run.

## Studio

Future authoring uses three zones: dataset/field palette, canvas/configuration, properties/validation. Never expose internal or sensitive fields merely because the Studio can discover them. Unsaved state, version history, preview, validation, and publish are explicit.

Current `.cbr-*` patterns are the implementation reference for Center/viewer cards, filters, chips, notices, parameter fields, run bar, list rows, empty states, and result rendering.
