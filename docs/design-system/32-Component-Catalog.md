# 32 — Component Catalog

Status: **Adopt** (Metronic/current), **Evolve** (current CrossBusiness pattern), **Specify** (contract exists, reusable implementation pending).

| Component | Status | Current evidence | Required variants/states |
|---|---|---|---|
| Button/icon button/menu button | Adopt | `.btn*`, Keen icons | primary, secondary, quiet, danger, loading, disabled |
| Card/panel/widget | Evolve | Metronic cards, `.cbw-card`, `.cbr-card` | standard, flush, metric, interactive, stateful |
| Page header/breadcrumb | Specify | repeated in module views | title, context, actions, responsive |
| Form controls | Adopt | Bootstrap/Metronic | text, number, amount, date, textarea, validation |
| Lookup/Select2 | Evolve | 569 references, `cbItemSelect` | local, remote, multi, loading, empty, error |
| Tag search | Evolve | `cbTagify`, `cbServerList` | tokens, suggestions, removable, keyboard |
| Table/server list | Evolve | 364 tables, 233 wrappers | sort, filter, select, page, totals, all states |
| Tree view | Adopt/Evolve | jsTree references, BOQ/hierarchy | lazy, select, check, keyboard, virtualized |
| Tabs | Adopt | Bootstrap/Metronic nav tabs | line, contained, overflow, keyboard |
| Timeline | Evolve | platform/doc timelines, `.cbw-row` | business event, comment, audit, legacy |
| Notification row | Evolve | Notifications/Workspace | read/unread, severity, deep link, failure |
| Alert/banner/toast | Evolve | Metronic + shared confirm/toastr | semantic, persistent, actionable |
| Dialog | Adopt/Evolve | 504 modal occurrences | confirm, form, large, dirty, processing |
| Drawer | Adopt | Metronic drawer | navigation, context, collaboration, filters |
| Kanban | Specify | plugin available, task need | columns, cards, limits, keyboard move |
| Chart | Evolve | ApexCharts | line, area, bar, stacked, donut, empty |
| Dashboard widget | Evolve | Accounting/Inventory/CRM | metric, trend, queue, distribution, freshness |
| Calendar | Evolve | FullCalendar | month, week, day, agenda, personal/company |
| Report card/viewer | Evolve | `.cbr-*`, Reports views | catalog, parameter, preview, run, export, error |
| Business Event card | Evolve | monitor/timeline | visibility, consumers, failure, retry, audit |
| Workspace panel | Evolve | `.cbw-*` | data, empty, unavailable, denied, partial, error |
| Comment/mention/reaction | Specify | Communication services/contracts | visibility, edit, delete, audit, keyboard |
| Chat message | Evolve | Chat + legacy Comm | inbound/outbound, delivery, failure, unread |
| Attachment/upload | Specify | FileManager, Comm/Library uploads | queued, progress, scanned, failed, downloadable |
| Status chip/badge | Evolve | 786 badge references | neutral, info, success, warning, danger |
| Progress/spinner/skeleton | Specify | progress/spinner; no skeleton found | determinate, indeterminate, local, structural |
| Empty/state display | Evolve | `.cbw-state`, `.cbr-empty` | seven canonical states |
| Search/filter bar | Evolve | module filters/Tagify | query, facets, active chips, reset, count |
| Sidebar/topbar | Evolve | Metronic layouts, Workspace Blue | desktop, drawer, active, collapsed, context |

Each component implementation must link back to its owning specification and include a stable demo or test fixture.
