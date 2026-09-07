# 04 — Spacing and Grid

The base unit is 4 px. Approved steps: 0, 4, 8, 12, 16, 20, 24, 32, 40, 48, 64.

## Rules

- Control internal gaps: 8–12 px.
- Form field vertical gap: 16 px; group gap: 24 px.
- Card padding: 20 px compact, 24 px standard, 32 px prominent.
- Page section gap: 24 px mobile, 32 px desktop.
- Never use spacing to simulate a separator when a semantic divider is needed.
- Use logical properties (`margin-inline`, `padding-block`) in authored CSS.

## Grid

Retain Bootstrap’s 12-column grid. Standard content max-width is 1440 px; dense operational surfaces may use fluid width. Page gutters: 16 px under 768, 24 px from 768, 32 px from 1200. Dashboard grids use `minmax(0, 1fr)` and collapse 4→2→1 or 3→2→1 based on content, not device labels.

Breakpoints follow Bootstrap: sm 576, md 768, lg 992, xl 1200, xxl 1400. Avoid new breakpoints unless a component’s content demonstrably requires one.
