# Stage — New Screens: Owner Review Guide

**How to open every new screen yourself, with no authorization weakened.**

---

## 1. Start the application

```
set ASPNETCORE_ENVIRONMENT=Development
dotnet run --project CrossBuy/CrossBuy.csproj -c Debug --urls "http://localhost:5199"
```

Sign in normally at `http://localhost:5199/Account/Login` with your own account. **No test credential is published here, and none is needed** - every screen below is reachable with an ordinary signed-in session, except where a role is stated.

## 2. Screens you can open right now

| Screen | URL | Menu path |
|---|---|---|
| **Workspace** | `http://localhost:5199/Workspace` | Platform → Workspace |
| Agenda | `/Workspace/Agenda?days=7` | inside Workspace |
| Notifications | `/Workspace/Notifications` | inside Workspace |
| Mentions | `/Workspace/Mentions` | inside Workspace |
| Reports panel | `/Workspace/Reports` | inside Workspace |
| **Reports Center** | `http://localhost:5199/Reports` | Platform → Reports center |
| **Business Event Monitor** | `http://localhost:5199/BusinessEventMonitor` | Platform → Business event monitor |

The **Platform** category is the first group in the sidebar on every module screen.

## 3. The Report Viewer - the exact code you need

**Report code: `Platform.BusinessEventLog`**

```
http://localhost:5199/Reports/Viewer/Platform.BusinessEventLog
```

**How to reach it from the Reports Center:** open `/Reports`, find the *Business event log* card under the **Platform operations** category, and click through. The card renders only if you hold the permission below.

| Field | Value |
|---|---|
| Report code | `Platform.BusinessEventLog` |
| URL | `/Reports/Viewer/Platform.BusinessEventLog` |
| Permission | `reporting.businessevents.view` → roles **Admin**, **SuperAdmin**, **Auditor** |
| Expected today | **500** - the Reporting tables do not exist yet (§5) |
| Expected after schema | the Business Event log with parameters, HTML preview, CSV and Excel export |

**Two other registered codes**, both also requiring the Reporting schema:

* `Platform.ReportCatalog` - the report catalogue itself
* `Platform.ReportRunHistory` - run history

**If you get 404 rather than 500** on a Viewer URL, the report exists but your account does not hold its permission. The platform does not disclose that a report exists to someone who may not view it. That is deliberate.

## 4. Getting the permission legitimately

To review the Business Event log you need one of **Admin**, **SuperAdmin** or **Auditor**. Three approved routes, in order of preference:

1. **Assign the role to your own employee record** through the existing role administration screens. This is the ordinary mechanism and needs no code change.
2. **Widen the mapping** in `Program.cs` - one line, reviewed like any other change:
   `.MapPermission(BusinessEventsReportPermissions.View, "Admin", "SuperAdmin", "Auditor", "<your role>")`
3. **`[PlatformOps]` screens** additionally accept the accounting management tier: holding `Accounting.manage` reaches the Business Event Monitor without an Admin role.

**Not used, and not to be used:** temporary anonymous access, disabled authorization attributes, hard-coded user IDs, query-string bypasses, or an authorization-baseline exception. None was introduced by this work, and the unauthenticated check below proves it.

## 5. What is blocked, and why

**The Reporting schema has never been applied.** `reporting_platform.sql` (12 tables) is not present in the configured database:

```
sys.tables WHERE name LIKE 'Report%'  ->  0
```

Runtime errors, SQL 208 (Invalid object name): `ReportFavorites`, `ReportRuns`, `ReportTemplates`.

This blocks the Report Viewer, Export, Archive download, Saved Reports, and the Favorites and Reports panels inside the Workspace. The rest of the Workspace dashboard still renders - panels are assembled independently, so one failing panel does not take the page down.

**No SQL was applied.** The configured database is `CrossBuyDB2`, and applying schema to it was not authorized in this increment. Applying `reporting_platform.sql` to an approved environment is the single action that unblocks every Reporting screen.

## 6. Capturing screenshots

For each screen: open the URL, wait for the sidebar to render (the Platform category is the first group), then capture the **full page** at 1440×900 in both directions:

* Arabic (default): open the URL directly.
* English: append `?culture=en-US&ui-culture=en-US`.

Suggested filenames: `workspace-ar.png`, `workspace-en.png`, `reports-center-ar.png`, `business-event-monitor-ar.png`.

For the Report Viewer, capture the current **500** as-is - it is the evidence that the schema is the blocker, and it should be re-captured after the schema is applied.

## 7. If a screen does not open

| Symptom | Meaning |
|---|---|
| Redirected to `/Account/Login` | your session expired - sign in again |
| Redirected to `/Home/Index` with an Arabic error toast | `[PlatformOps]` denied you - see §4 |
| **404** on a Viewer URL | the report exists but you lack its permission |
| **500** on a Viewer URL | the Reporting schema is missing - §5 |
| **404** on `/PlatformTimeline` | that controller has no `Index` action; it is not a delivered screen |
