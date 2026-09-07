# 17 — UI Screen Inventory

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


**361 Razor views**, 57 controllers.

| Module | Views | Controller(s) | Notable screens |
|---|---:|---|---|
| Inventory | 79 | `InventoryController`, `InventoryListRows`, `Api/InventoryApiController` | Items, Warehouses, Quotations, Valuation/Stagnant/Reorder/Manuf reports, ItemLocations |
| Accounting | 66 | `AccountingController`, `Api/AccountingApiController` | Journals, Invoices, Customers, Period control |
| CRM | 34 | `CrmController` | Leads, Accounts, Opportunities, Pipeline, Tickets, Campaigns, Forecast, 360° Reports, 2 AI Insights |
| Admin / HR | 31 + 12 | `AdminController` (+3 partials), `PeopleController` | Appraisals, Recruitment, Training, Employees |
| Project | 17 | `ProjectController`, `ProjectCloseoutController` | Projects, Dashboard, Team, Budget, Progress, Billing, BoQ, Subcontracts |
| POS | 16 + 9 | `PosController`, `PosAppController`, `HyperController`, `HyperPosController` | cashier, catalog, restaurant |
| Tasks | 8 | `TasksController` | Index, Board, Detail, AutoRules, Templates, MatchSuggestions, Reports, HoursReport |
| ClientPortal | 6 + 1 | `ClientPortalController`, `PortalController` | Index, Invoices, Projects, Quotations, NoAccess, Choose |
| Workspace | 5 | `WorkspaceController` | Index, Agenda, Reports, Notifications, Mentions |
| Reports | 4 | `ReportsController`, 3 API controllers | Index, Studio, Viewer, _ReportCard |
| Calendar | 3 | `CalendarController` | |
| Comm / Chat | 3 + 1 | `CommController`, `ChatController`, `CommentsController` | |
| Roster / Store | 2 + 2 | `RosterController`, `StoreController` | |
| **Documents** | **1** | `DocumentsController` | single view over a substantial service |
| **Approvals** | **1** | `ApprovalsController` | single inbox over three mechanisms |
| Notifications | 1 | `NotificationsController`, `Api/NotificationsApiController` | |
| Monitoring | — | `BusinessEventMonitorController`, `PlatformTimelineController` | outbox + timeline monitoring |
| Onboarding | — | `EmployeeOnboardingController` | |

## Orphan / thin surfaces

Only two are called out, and both on the evidence of a service/UI mismatch rather than taste:

- **Documents — 1 view** over `PlatformDocumentService`, `DocumentEvents`, `DocumentExpiryProjection`
  and a hosted service. The module produces business events and creates tasks; it has almost no UI.
- **Approvals — 1 view** over three separate approval mechanisms.

No view was found that is unreachable from a controller. `RestaurantIntelligenceController` and
`ServiceController` were not traced to a view folder in this pack — recorded as unverified, not orphan.

## Client Portal


| Aspect | Evidence |
|---|---|
| Controllers | `ClientPortalController.cs`, `PortalController.cs` |
| Views | `ClientPortal/Index`, `Invoices`, `Projects`, `Quotations`, `NoAccess`, `_Layout`; `Portal/Choose` |
| Customer scope | `ClientPortalController` filters on `CustomerId` |
| Access refusal | a dedicated `NoAccess` view — refusal is a designed state, not an error page |
| Company choice | `Portal/Choose` |

**Implemented:** authentication and customer scoping, invoice list, quotation list, project list, an
explicit no-access path.

**Not found:** quotation accept/reject, client data capture, portal document exchange, portal
communication, portal-originated tasks, portal business events. The portal is **read-only** on the
evidence available.
