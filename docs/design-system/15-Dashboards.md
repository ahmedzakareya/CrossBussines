# 15 — Dashboards

Dashboards answer a role-specific question; they are not collections of available charts.

## Composition

1. Context/title and period.
2. Four or fewer primary metrics.
3. One dominant trend or operational queue.
4. Supporting distributions/lists.
5. Quick actions only when frequently used and authorized.

Metric widgets contain label, value/unit, comparison period, direction, freshness and link. A positive direction is not always success (for example, expenses); semantics come from the metric definition.

Use the implemented Accounting, Inventory and CRM dashboards as structural evidence: Metronic flush cards, 330 px primary charts, responsive stat cards, recent-record tables, progress lists and quick access. Migrate their raw colors and duplicated markup to tokens/components without changing business content.

Every widget has loading, empty, unavailable, partial and error states. Dashboard aggregation delegates authorization to datasets/module services.
