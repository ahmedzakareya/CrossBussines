# Stage — New Screens Reachability Inventory

**Every URL below was verified with a real HTTP request against a running application on a freshly-built binary. None was inferred from a filename.**

Verified: 2026-08-06 · `http://localhost:5199` · ASPNETCORE_ENVIRONMENT=Development · binary timestamp 13:38:03.

---

## 1. The defect that made screens unreachable

The default route is:

```csharp
pattern: "{controller=Account}/{action=Login}/{id?}"
```

The default **action** is `Login`, not `Index`. So a bare `/Workspace` resolved to `WorkspaceController.Login` — which does not exist — and returned an **empty 404 with `Content-Length: 0`**. Meanwhile `/Workspace/Index` returned 200.

That is exactly the reported symptom: screens were built, compiled, tested, and the owner typing the obvious URL got nothing.

**Proof before the fix:**

| URL | Before |
|---|---|
| `/Workspace` | **404** |
| `/Workspace/Index` | 200 |
| `/Reports` | **404** |
| `/Reports/Index` | 200 |
| `/BusinessEventMonitor` | **404** |
| `/BusinessEventMonitor/Index` | 200 |

The defect is **systemic, not specific to the new screens** — `/Inventory` also returns 404 while `/Inventory/Index` returns 200. Only the three platform surfaces were fixed, because widening the change would alter the entry point of every controller in the product.

## 2. Screen inventory

| Screen | Controller | Action | View | Exact URL | Verb | Permission | Feature/activation | Service | Schema | State |
|---|---|---|---|---|---|---|---|---|---|---|
| Workspace Home / Dashboard | `WorkspaceController` | `Index` | `Views/Workspace/Index.cshtml` | **`/Workspace`** | GET | `[SessionValidation]` only | — | `IWorkspaceService` | Reporting tables for the Reports panel | **Reachable** (panel degrades, §4) |
| My Work | — | — | panel inside `Index` | — | — | delegated | — | `IWorkspaceService` | — | **Panel, not a standalone route** |
| Favorites | — | — | panel inside `Index` | — | — | delegated | — | `IReportLibraryService` | `ReportFavorites` | **Panel** — needs schema |
| Recent Activity | — | — | panel inside `Index` | — | — | delegated | — | `IWorkspaceService` | — | **Panel** |
| Quick Actions | — | — | panel inside `Index` | — | — | delegated | — | `IWorkspaceService` | — | **Panel** |
| Agenda | `WorkspaceController` | `Agenda` | `Views/Workspace/Agenda.cshtml` | **`/Workspace/Agenda?days=7`** | GET | `[SessionValidation]` | — | `IWorkspaceAgendaService` | — | **Reachable** |
| Workspace Notifications | `WorkspaceController` | `Notifications` | `Views/Workspace/Notifications.cshtml` | **`/Workspace/Notifications?unreadOnly=false`** | GET | `[SessionValidation]` | — | notification source | Notifications | **Reachable** |
| Workspace Mentions | `WorkspaceController` | `Mentions` | `Views/Workspace/Mentions.cshtml` | **`/Workspace/Mentions`** | GET | `[SessionValidation]` | — | Comm actor/mention DTOs | Comm tables | **Reachable** |
| Workspace Reports panel | `WorkspaceController` | `Reports` | `Views/Workspace/Reports.cshtml` | **`/Workspace/Reports`** | GET | `[SessionValidation]` | — | `IReportLibraryService` | Reporting tables | **Reachable** |
| Reports Center | `ReportsController` | `Index` | `Views/Reports/Index.cshtml` | **`/Reports`** | GET | `[SessionValidation]` | Reporting registered in `Program.cs` | `ReportsCenterPresenter` | Reporting tables | **Reachable** |
| Report Viewer | `ReportsController` | `Viewer` | `Views/Reports/Viewer.cshtml` | **`/Reports/Viewer/{code}`** | GET | `[SessionValidation]` + per-report permission | Reporting registered | `ReportsCenterPresenter` | **`ReportTemplates`, `ReportRuns`, `ReportFavorites`** | **Dependency missing** — 500 |
| Report Export | `ReportsController` | `Export` | — | `/Reports/Export?id={code}&format={fmt}` | GET | per-report | Reporting registered | exporters | Reporting tables | **Dependency missing** |
| Report History | `ReportsController` | — | panel inside `Index`/`Viewer` | — | GET | per-report | Reporting registered | `IReportHistoryService` | `ReportRuns` | **Panel** — needs schema |
| Report Archive | `ReportsController` | `Download` | — | `/Reports/Download/{id}` | GET | per-report | Reporting registered | `IReportArchiveService` | `ReportArchiveEntries` | **Dependency missing** |
| Saved Reports | — | — | panel; writes are API-only | `/api/reports-center/...` | POST | in-body | Reporting registered | `IReportTemplateService` | `ReportTemplates` | **API-only + dependency missing** |
| Business Event Monitor | `BusinessEventMonitorController` | `Index` | `Views/BusinessEventMonitor/Index.cshtml` | **`/BusinessEventMonitor`** | GET | `[SessionValidation]` + **`[PlatformOps]`** | — | monitor service | `BusinessEvents`, `BusinessEventDispatch` | **Reachable** |
| Business Event rows (partial) | `BusinessEventMonitorController` | `Rows` | `_Rows.cshtml` | `/BusinessEventMonitor/Rows` | GET | `[PlatformOps]` | — | monitor service | kernel tables | **Reachable** (AJAX partial) |
| Business Event details (partial) | `BusinessEventMonitorController` | `Details` | `_Details.cshtml` | `/BusinessEventMonitor/Details/{id}` | GET | `[PlatformOps]` | — | monitor service | kernel tables | **Reachable** (AJAX partial) |
| Platform Timeline | `PlatformTimelineController` | — | — | `/PlatformTimeline` → **404** | GET | — | — | timeline projection | projection tables | **Route missing** — no `Index` action |

## 3. Verified HTTP results

**Authenticated (session primed via the existing `[DevOnly]` endpoint):**

```
/Workspace                                  200
/Workspace/Agenda                           200
/Workspace/Notifications                    200
/Workspace/Mentions                         200
/Workspace/Reports                          200
/Reports                                    200
/BusinessEventMonitor                       200
/BusinessEventMonitor/Rows                  200
/Reports/Viewer/Platform.ReportCatalog      500   <- schema missing
/Reports/Viewer/Not.A.Real.Report           404   <- fails safely
/PlatformTimeline                           404   <- no Index action
```

**Unauthenticated — authorization is intact, no bypass was introduced:**

```
/Workspace             302 -> /Account/Login?returnUrl=%2FWorkspace
/Reports               302 -> /Account/Login?returnUrl=%2FReports
/BusinessEventMonitor  302 -> /Account/Login?returnUrl=%2FBusinessEventMonitor
```

## 4. The blocking dependency — Reporting schema is not applied

`reporting_platform.sql` (12 tables) has **never been applied** to the configured database.

```
sys.tables WHERE name LIKE 'Report%'  ->  0
```

Runtime errors observed, SQL error **208 — Invalid object name**:

* `ReportFavorites`
* `ReportRuns`
* `ReportTemplates`

**Consequences:** the Report Viewer returns 500; the Workspace Reports/Favorites panels fail server-side while the rest of the dashboard still renders (the Workspace service assembles panels so one failure does not take the page down — that design held).

**No SQL was applied.** The configured database is `CrossBuyDB2`, and applying schema to it was not authorized. This is reported, not fixed.

## 5. Assets

`/assets/js/app.ajax.js` → 200. The Workspace and Reporting layouts load their CSS/JS from the shared Metronic bundle already served by every other screen; no new asset path was introduced.

## 6. What is a panel, not a page

Stated explicitly so nothing is described as reachable when it is not a route:

**My Work · Favorites · Recent Activity · Quick Actions · Report History · Saved Reports** are panels rendered inside `/Workspace` or `/Reports`. They have no URL of their own and cannot be linked to directly.

**Saved-report writes** (save, fork, set-default, delete, favourite, unfavourite, reorder) are **API-only** under `/api/reports-center/`, consumed by JavaScript. They are not screens.
