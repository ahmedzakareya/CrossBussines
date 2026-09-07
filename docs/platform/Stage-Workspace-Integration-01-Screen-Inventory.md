# Stage-Workspace-Integration — 01 · Screen Inventory (Phase 1)

**Product:** CrossBusiness Workspace · **Tab:** TAB 3 · **Measured:** 2026-08-06

---

## 1. Every Workspace surface, with its exact URL

All five are served by one controller, one layout and one stylesheet.

| # | Screen | URL | Action | View | Auth |
| --- | --- | --- | --- | --- | --- |
| 1 | **Home / Dashboard** | `/Workspace` · `/Workspace/Index` | `WorkspaceController.Index` | `Views/Workspace/Index.cshtml` | `[SessionValidation]` |
| 2 | **Agenda** | `/Workspace/Agenda?days=7` | `WorkspaceController.Agenda` | `Views/Workspace/Agenda.cshtml` | `[SessionValidation]` |
| 3 | **Reports** | `/Workspace/Reports` | `WorkspaceController.Reports` | `Views/Workspace/Reports.cshtml` | `[SessionValidation]` |
| 4 | **Notifications** | `/Workspace/Notifications?unreadOnly=false` | `WorkspaceController.Notifications` | `Views/Workspace/Notifications.cshtml` | `[SessionValidation]` |
| 5 | **Mentions** | `/Workspace/Mentions` | `WorkspaceController.Mentions` | `Views/Workspace/Mentions.cshtml` | `[SessionValidation]` |

**Route mechanism:** the application's default conventional route
(`{controller=Home}/{action=Index}/{id?}`). No route was added, renamed or reordered — a Workspace-specific
route entry would be a change to shared routing that nothing here needs.

**In-page anchors** (not separate screens; they scroll the dashboard):
`/Workspace/Index#my-work` · `#agenda` · `#reports` · `#favorites` · `#activity` · `#quick-actions`

### 1.1 Query parameters

| Screen | Parameter | Default | Clamp |
| --- | --- | --- | --- |
| Agenda | `days` | `7` | clamped **1–60** inside the service, so a crafted value cannot become an unbounded scan |
| Notifications | `unreadOnly` | `false` | boolean |

Both are filters expressed **as links, not JavaScript** — the state lives in the URL, so every view is
bookmarkable, back-button-correct, and works before any script loads.

---

## 2. Supporting files

| Kind | Path |
| --- | --- |
| Layout | `Views/Shared/_LayoutWorkspace.cshtml` |
| Shared partial | `Views/Shared/_WorkspacePanelState.cshtml` |
| Stylesheet | `wwwroot/Backend-assets/css/crossbusiness-workspace.css` |
| Read model | `BL/Workspace/WorkspaceService.cs` |
| Contracts | `BL/Workspace/WorkspaceContracts.cs` |
| Navigation | `BL/Workspace/WorkspaceNavigation.cs` |
| Source adapters | `BL/Workspace/WorkspaceSources.cs` |
| DI | `BL/Workspace/WorkspaceRegistration.cs` |
| Controller | `Controllers/WorkspaceController.cs` |

**Assets:** the Workspace adds ONE stylesheet. It loads Metronic's existing `plugins.bundle`,
`style.bundle(.rtl)` and `scripts.bundle` — no new JavaScript file, no new font, no new image, no CDN.

---

## 3. The main screen — Phase 1 checklist

| Requirement | State | Evidence |
| --- | --- | --- |
| **Stable route** | ✅ | `/Workspace` via the default conventional route. Nothing shared was changed |
| **Authorization** | ✅ | `[SessionValidation]` on the controller — the same filter every other backend screen uses |
| **Navigation entry** | ✅ | Its own capability-aware rail (document 02). It is the root of that rail |
| **Correct layout** | ✅ | `_LayoutWorkspace.cshtml`, Metronic 8 `app-*` shell, `.cbw` scoping the Blue design system |
| **Localization readiness** | ✅ | Every user-facing string is an Ar/En pair resolved from `CurrentUICulture`; `dir`/`lang` from the culture; RTL from one stylesheet via logical properties |
| **No direct database reads** | ✅ | §4 |

---

## 4. "No direct database reads" — how it is structurally true

`WorkspaceService` **takes no `CrossDbContext`.** It cannot perform a database read, because the type it would
need is not injected. Every fact arrives through a contract:

| Fact | Contract | Owner |
| --- | --- | --- |
| My Work, metrics | `ITaskService` | Tasks module |
| Agenda | `CrossBuy.BL.TasksCalendar.IWorkspaceAgendaService` | **TAB 4** |
| Mentions | `ICommMentionService` | Communication platform |
| Report links | `IWorkspaceReportSource` → `IReportLibraryService` / `IReportService` | Reporting platform |
| Favourites | `IWorkspaceFavoritesSource` | extension point |
| Recent activity | `IWorkspaceActivitySource` | extension point |
| Notifications | `IWorkspaceNotificationSource` | Workspace adapter |
| Employee/company name | `IWorkspaceIdentityResolver` | Workspace adapter |

### 4.1 The two adapters that do read, and why they are not an exception

`BL/Workspace/WorkspaceSources.cs` is the only file where a `DbContext` appears. Two facts have **no read
service to consume**:

* **Notifications** — `INotificationService` is a **writer** (`NotifyAsync`); it exposes no query.
* **Employee / company display names** — no contract returns them.

The options were to leave a `DbContext` in the read model, or to put these two reads behind contracts and give
the read model none. The second makes the rule literally true rather than nearly true, and makes each read
swappable: a future `INotificationQueryService` replaces the adapter and the Workspace does not change.

**Both read PLATFORM tables only** — `Notifications`, `Employee`, `Companies`. **No module table is queried
anywhere in this product**: no Tasks table, no Calendar table, no Comm table, no Report table.

Both filter on **company AND recipient in the `WHERE` clause**, not after loading — a row belonging to another
tenant or another person is never materialised.

---

## 5. Writes

**There are none.** No `[HttpPost]`, no `SaveChanges`, no writer service. The Workspace owns no table.

The mark-as-read endpoint remains withdrawn pending an approved authority — document 05 §6.

---

## 6. Reachability caveat for review

**Mentions is reachable at `/Workspace/Mentions` even when Communication is not activated.** That is
deliberate: the *nav entry* is hidden (there is nothing to click through to), but the *route* still renders the
explicit "platform not activated" state, so a reviewer can open the URL and see exactly what a user will see
once it is switched on. Hiding the route as well would make the state unreviewable.
