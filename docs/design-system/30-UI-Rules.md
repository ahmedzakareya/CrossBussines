# 30 — Mandatory UI Rules

## Visual

1. CrossBusiness Blue is platform primary; financial green is restricted to financial meaning.
2. Use tokens; no new raw colors, radii, spacing, shadows or animation values.
3. Maximum visual card nesting is two; never shadow nested cards.
4. One page-level primary action; destructive is never primary-blue.
5. Radius: 6 px controls/compact, 10 px cards, 16 px prominent panels, pill only for chips.
6. Prefer borders for normal separation; shadows for canvas elevation and overlays.
7. Use the typography and 4 px spacing scales only.

## Behavior

8. Every async data region distinguishes loading, data, empty, unavailable, denied, partial and error.
9. No infinite spinner; long operations explain progress and recovery.
10. Dialogs trap/restore focus; menus/tabs/trees/comboboxes support keyboard patterns.
11. No nested modals; use a page/drawer when the task is too large.
12. Use shared confirmation and toast infrastructure.
13. Search and filters show applied state, result count and reset.
14. Wide tables scroll inside the card; never widen the page body.

## Architecture

15. UI visibility never replaces server authorization.
16. Cross-module surfaces delegate access and scope to owning modules.
17. Do not duplicate domain rules, routes, timelines, task/calendar state or report permissions in presentation code.
18. Metronic/Bootstrap/Keen primitives are preferred; exceptions are recorded.
19. Shared behavior belongs in a partial/ViewComponent/module—not copied inline JavaScript.
20. New screens use the target shell; legacy screens migrate only through an approved slice.

## Localization and inclusion

21. User-facing strings use resources/current localization convention; no new hardcoded monolingual UI.
22. Arabic RTL and English LTR are acceptance targets; use logical CSS.
23. WCAG 2.2 AA, visible focus and status beyond color are required.
24. Touch-first controls are at least 44 px; compact desktop controls at least 36 px.
25. Honor reduced motion and theme tokens.

## Definition of done

Token check, component catalog check, all states, authorization review, localization, keyboard, contrast, RTL/LTR, mobile widths, dark-mode contract, visual regression, and owning-tab approval.
