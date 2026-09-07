# 16 — Charts

ApexCharts is the current web chart engine. Charts supplement—not replace—values and tables.

## Palette and forms

Use Blue-first categorical series with accessible secondary colors. Use semantic green/red only for favorable/unfavorable meaning. Financial charts may use financial green/gold. Line/area for time; bar for comparison; stacked bar for composition; donut only for 2–6 parts; avoid 3D, gauges without a meaningful threshold, and rainbow palettes.

## Required behavior

- Title, unit, period, legend and data freshness.
- Text summary or accessible table for critical information.
- Tooltips localized and keyboard reachable where the library permits.
- Zero, missing and not-applicable are distinct.
- Truncated axes and dual axes require explicit justification.
- RTL reverses layout/labels where linguistic, not time-series chronology by default.
- Mobile reduces labels/series before reducing legibility.

Charts use token values and `fontFamily: inherit`. Exported charts carry sufficient legend/context outside interactive tooltips.
