# Stage — New Screens: Routes and Permissions

**Every route verified against a running application. Every permission read from the attribute that enforces it.**

---

## 1. The route table

| Screen | Exact URL | Menu path | Permission | Status | Blocking reason |
|---|---|---|---|---|---|
| Workspace | `/Workspace` | Platform → Workspace | signed-in employee | **OK 200** | — |
| Workspace Agenda | `/Workspace/Agenda?days=7` | (in-page) | signed-in employee | **OK 200** | — |
| Workspace Notifications | `/Workspace/Notifications` | (in-page) | signed-in employee | **OK 200** | — |
| Workspace Mentions | `/Workspace/Mentions` | (in-page) | signed-in employee | **OK 200** | — |
| Workspace Reports panel | `/Workspace/Reports` | (in-page) | signed-in employee | **OK 200** | — |
| Reports Center | `/Reports` | Platform → Reports center | signed-in employee | **OK 200** | — |
| Report Viewer | `/Reports/Viewer/Platform.ReportCatalog` | Reports Center → card | per-report permission | **500** | Reporting schema not applied |
| Report Export | `/Reports/Export?id={code}&format=Csv` | Viewer → Export | per-report permission | **Blocked** | Reporting schema not applied |
| Report Archive download | `/Reports/Download/{runId}` | Viewer → History | per-report permission | **Blocked** | Reporting schema not applied |
| Business Event Monitor | `/BusinessEventMonitor` | Platform → Business event monitor | `[PlatformOps]` | **OK 200** | — |
| Business Event rows | `/BusinessEventMonitor/Rows` | (AJAX partial) | `[PlatformOps]` | **OK 200** | — |
| Business Event details | `/BusinessEventMonitor/Details/{id}` | (AJAX partial) | `[PlatformOps]` | **OK 200** | — |
| Platform Timeline | `/PlatformTimeline` | not in menu | — | **404** | Controller has no `Index` action |

## 2. Permissions, exactly

### `[SessionValidation]` - Workspace and Reports Center

Reads `HttpContext.Session["Employee"]`. Absent, it redirects to `/Account/Login?returnUrl=...`. It is **not** a role check: any signed-in employee may open the Workspace and the Reports Center. Per-report and per-panel authorization happens deeper, in the services.

That is why neither carries a `Perm` hook in navigation - hiding a screen a user is entitled to open is the mirror-image defect of showing one they cannot.

### `[PlatformOps]` - Business Event Monitor

The predicate, verbatim from `PlatformOpsAttribute`:

```
allowed = IsAdmin(HttpContext)                       // Admin | Administrator | SuperAdmin | PlatformOps
       || (!requireElevated && await IAccountingAccessService.CanAsync("manage"))
```

Denial is a **redirect to `/Home/Index`** for page requests (with `TempData["PlatformErr"]`), and a **403** for AJAX/JSON callers.

Navigation uses the **same predicate**, not an approximation - see the Navigation Evidence document.

### Per-report permissions - Report Viewer

Mapped in `Program.cs` and **fail-closed**: a permission key with no role mapping is denied.

| Key | Roles |
|---|---|
| `reporting.administer` | Admin, SuperAdmin |
| `reporting.businessevents.view` | Admin, SuperAdmin, Auditor |
| `reporting.businessevents.confidential` | Admin, SuperAdmin |
| `reporting.businessevents.restricted` | SuperAdmin |

A report the caller may not view is filtered out of the catalog, and its Viewer URL returns **404** rather than 403 - the report is not disclosed to exist. That is deliberate fail-closed behaviour, not a routing fault.

## 3. What was changed to fix reachability

**One file: `CrossBuy/Program.cs`.** Three additive routes:

```csharp
app.MapControllerRoute(name: "workspace-home", pattern: "Workspace",
    defaults: new { controller = "Workspace", action = "Index" });
app.MapControllerRoute(name: "reports-home", pattern: "Reports",
    defaults: new { controller = "Reports", action = "Index" });
app.MapControllerRoute(name: "business-event-monitor-home", pattern: "BusinessEventMonitor",
    defaults: new { controller = "BusinessEventMonitor", action = "Index" });
```

The global default route is **unchanged**, so the application still lands on `Account/Login` as designed.

**No screen behaviour was changed. No authorization attribute was touched. No controller owned by another tab was modified.**

## 4. Latent defect found, not fixed

`Program.cs` calls `app.UseRouting()` **twice** - at line 602 and again at line 620, with `MapControllerRoute` between them and `MapControllers()` after the second. It is not causing the 404s reported here, and correcting middleware order is a change with product-wide blast radius that belongs to the Integration Owner rather than to a reachability fix.

Recorded so it is not rediscovered.
