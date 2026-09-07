# 25 — Mobile

Mobile is task prioritization, not desktop shrinkage.

## Baseline

At under `lg`, sidebar becomes a drawer, multi-column dashboards stack, contextual rails follow primary work, command bars wrap or become sticky bottom actions, and tables scroll internally or use approved stacked rows. The page body must not scroll horizontally.

Primary touch targets are at least 44 px. Keep one primary action readily reachable. Inputs use appropriate keyboard modes. Respect safe-area insets and prevent fixed controls from covering validation or content.

## Domain behavior

- Workspace: ordered agenda/work/attention stream.
- Calendar: agenda/list default.
- POS/KDS/hyper: specialized operational shell; no generic sidebar intrusion.
- Construction/field: capture-first, resilient uploads, explicit offline/sync state when implemented.
- Reports: parameter drawer plus scrollable viewer; exports remain available.

Test widths 320, 375, 768 and 1024, portrait/landscape, Arabic/English, zoom and virtual keyboard.
