# 05 — Workspace UX

Workspace is the personal cross-module work hub, not another ERP module. Current surfaces are `Views/Workspace/Index.cshtml`, `Notifications.cshtml`, `Mentions.cshtml`, `Reports.cshtml`, and supporting panels in `Views/Shared/_WorkspacePanelState.cshtml` and `_LayoutWorkspace.cshtml`.

## Contract

- **Users/goal:** every authenticated worker; understand what needs attention and resume work.
- **Layout:** contextual header; priority/summary band; responsive panel grid; activity/mentions; saved reports.
- **Widgets:** tasks, calendar, approvals, notifications, mentions, reports, business-event summaries where authorized.
- **Navigation/actions:** open source record in its owning module; primary action is the most valuable cross-workflow action, never multiple competing CTAs.
- **Permissions:** aggregate only data already authorized in its source module and active company context.
- **Filters:** time horizon, ownership, status; persist personal preferences without changing authorization.

## State and flow

Render panel-level skeletons so one slow provider does not block the page. Empty says what the panel tracks and offers a safe next step. Unavailable identifies a temporary provider problem and retry. Denied is not rendered as an empty result. Partial data shows a timestamp and affected panel. Success updates only the affected panel and announces it.

Responsive: desktop grid → tablet two-column → mobile prioritized single column. Preserve logical order in RTL. Dark mode uses tokens. Keyboard order follows visual priority; panel menus expose names and state. Defer noncritical panels and cap initial requests. Future evolution may add personalization only with fixed accessible defaults and reset.
