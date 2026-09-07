# 33 — Screen Catalog

This catalog groups actual and planned screens by design family. It complements the roadmap’s detailed screen CSV; disk wins when status differs.

| Family | Implemented surfaces | Target shell | Migration posture |
|---|---|---|---|
| Workspace | Dashboard, Notifications, Mentions, Reports | App/Blue | Reference; consolidate components |
| Reporting | Report Center, Viewer, Workspace reports | App/Blue + Embed | Reference; preserve data authorization |
| Platform operations | Business Event Monitor, timeline, grants API future console | App/Blue | Early migration |
| Accounting/Currency | 66+4 views | App; financial semantics | High-value legacy migration; retain green only in content |
| Inventory/Manufacturing | 78 views | App + Operational floor | High-volume table/form migration |
| CRM | 32 views | App | Migrate after security scope decisions |
| HR/People/Admin/Portal | broad employee/workforce set | App + mobile operational | Consolidate layout and forms |
| Projects | 16 views | App | Base for Construction context |
| Construction C1+ | services now; dedicated workspace pending | App + field | New work starts compliant |
| Tasks | 5 views including reports/hours | App | Integrate, do not rebuild domain |
| Calendar | 1 FullCalendar view | App/mobile agenda | Evolve current implementation |
| New Communication | no dedicated UI yet | App + drawer/embed | New work starts compliant |
| Legacy Comm/Chat/Comments | email inbox, chat, document comments | App compatibility | Do not extend as future platform language |
| POS/PosApp/Hyper/KDS | 30 operational views | Operational | Preserve task flow; adopt tokens/a11y selectively |
| Store/public portal | storefront and portal | Public/operational | Separate customer UX, shared tokens |
| Account/authentication | login/profile | Auth/Embed | Blue identity, accessible forms |
| Shared/print | layouts, partials, invoices, statements | Embed/print | Consolidate last, preserve output fidelity |

## Required screen record

Every new/migrated screen records: owner, purpose, users, route, permissions, data sources, shell, components, primary action, all data states, responsive order, RTL behavior, keyboard route, dark-mode contract, analytics/audit needs, and migration status.

## Status vocabulary

Legacy existing; implemented/reference; implemented/not activated; architecture-only; planned; blocked; deprecated. Never label a screen complete solely because a controller or view file exists.
