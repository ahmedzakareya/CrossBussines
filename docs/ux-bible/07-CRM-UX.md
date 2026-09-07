# 07 — CRM UX

CRM supports relationship continuity from prospect/customer context through activities and outcomes. Current implementation is the 32-view `Views/Crm` estate; use its workflows as domain evidence while migrating composition to this Bible.

## Screen model

Collection screens expose owner, stage/status, next activity, value where authorized, search, saved filters, and list/Kanban choice only when both aid the task. Record screens lead with identity and status, then activity timeline, contacts, opportunities, files, tasks, and related financial/operational links. Editors use progressive disclosure and preserve unsaved work.

All screens apply the universal contract: role-based purpose and actions; server permissions; list/detail/editor states; adjacent validation; recoverable errors; success with next step; responsive priority; RTL logical layout; token dark mode; named controls and keyboard-operable timeline/Kanban. Customer absence is empty; inaccessible customer is denied; integration outage is unavailable—never conflate them.

Primary flow: search/queue → qualify context → open record → review history → perform next action → record outcome. Optimize list queries, lazy-load history, and avoid loading all related modules at once. Future evolution should connect Tasks, Calendar, Communication, and Reporting via governed links rather than duplicate their models.
