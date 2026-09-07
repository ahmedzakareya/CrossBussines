# 24 — Dashboard Patterns

A dashboard answers: what is happening, what needs attention, and where to go next. It is not a collage of every available metric. Current evidence includes `Views/Workspace/Index.cshtml`, `Views/People/Dashboard.cshtml`, `Views/Hyper/Dashboard.cshtml`, and module dashboard views.

Composition: title/context + timeframe → exception/priority band → 3–6 key indicators → trends/comparisons → actionable queues → provenance/refresh. Each widget has title, value/content, scope/time, status, action/drill link, and independent states.

Use Metronic card and ApexCharts integrations. Maximum two card layers; avoid nested borders. KPI color encodes domain meaning, not decoration. Charts have text summaries and honest axes. Load priority widgets first and isolate failure. Mobile shows priority and action before decorative trends. Personalization must retain accessible defaults and “reset layout.”
