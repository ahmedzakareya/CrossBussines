# Stage — New Screens Reachability: Delivery Report

**Every newly delivered screen is now reachable, discoverable, permission-aware and documented — except the Reporting screens, which are blocked by a schema that was never applied.**

No feature was built. No screen was redesigned. No business logic changed. No authorization was weakened.

---

## 1. The two defects

**1 · A routing defect made the obvious URLs 404.** The default route defaults the *action* to `Login`, not `Index`:

```csharp
pattern: "{controller=Account}/{action=Login}/{id?}"
```

So `/Workspace` resolved to `WorkspaceController.Login` — which does not exist — and returned an **empty 404 with `Content-Length: 0`**, while `/Workspace/Index` returned 200. Same for `/Reports` and `/BusinessEventMonitor`. This is systemic: `/Inventory` 404s too.

**2 · Nothing linked to the new screens.** A grep across all 327 views found no link to `/Workspace` or `/Reports` outside the Reporting views themselves. `_LayoutWorkspace.cshtml` contained **zero** navigation links.

Together: the screens existed, compiled, and passed tests, and the owner had no way to reach them. Both defects were invisible to every existing gate.

## 2. What was changed

| File | Change |
|---|---|
| `CrossBuy/Program.cs` | Three additive routes mapping bare `/Workspace`, `/Reports`, `/BusinessEventMonitor` to `Index`. **The global default is unchanged** — the app still lands on `Account/Login`. |
| `CrossBuy/Models/Menu/MainMenu.cs` | New `Platform()` category (Workspace, Reports Center, Business Event Monitor). Business Event Monitor **moved** out of `Admin()`, not copied. |
| `CrossBuy/Views/Shared/_MainMenu.cshtml` | Prepends `Platform()` once; adds the `platform-ops` permission hook using the **real** `[PlatformOps]` predicate. |
| `CrossBuy/Resources/SharedResources.ar.resx` | Three keys: `Platform`, `Workspace`, `Reports center`. |
| `CrossBuy.Tests/NewScreenReachabilityTests.cs` | 16 tests pinning the invariants. |

**No controller, service, view or business file owned by another tab was modified.** The routing and navigation files are Integration Owner territory; the screens themselves were read only.

## 3. Verified result

| Screen | URL | Status |
|---|---|---|
| Workspace | `/Workspace` | **200** |
| Agenda | `/Workspace/Agenda?days=7` | **200** |
| Notifications | `/Workspace/Notifications` | **200** |
| Mentions | `/Workspace/Mentions` | **200** |
| Reports panel | `/Workspace/Reports` | **200** |
| Reports Center | `/Reports` | **200** |
| Business Event Monitor | `/BusinessEventMonitor` | **200** |
| Report Viewer | `/Reports/Viewer/Platform.BusinessEventLog` | **blocked — schema** |
| Platform Timeline | `/PlatformTimeline` | **404 — no `Index` action** |

Unauthenticated requests still redirect to login; invalid report codes still 404. **No bypass was introduced.**

## 4. The blocking dependency

**`reporting_platform.sql` has never been applied.** `sys.tables WHERE name LIKE 'Report%'` returns **0**. Runtime SQL 208 on `ReportFavorites`, `ReportRuns`, `ReportTemplates`.

This blocks the Report Viewer, Export, Archive download, Saved Reports, and the Favorites/Reports panels inside the Workspace. The Workspace dashboard still renders — panels are assembled independently, so one failure does not take the page down. That design held under real failure.

**No SQL was applied.** The configured database is `CrossBuyDB2` and that was not authorized. Applying the slice to an approved environment is the single action that unblocks every Reporting screen.

## 5. Builds and tests

| Gate | Result |
|---|---|
| Debug | **0 Error(s)** |
| TestRun | **0 Error(s)** |
| Release | 2 errors — **file lock** from the owner's Visual Studio (13396) and IIS Express (4560), not a compile error |
| Full suite | **Failed: 0, Passed: 1676, Skipped: 0** |
| New reachability tests | **16 / 16** |
| Scratch databases remaining | **0** · `CrossBuyDB2` untouched |

Two things worth stating rather than burying:

**A stale binary nearly produced a false result.** After the routing fix the app appeared unchanged — because the build had failed on a file lock held by the still-running app, and the old binary was still serving. Caught by reading the error count rather than the test output; the process was stopped, the rebuild verified at `0 Error(s)`, and the binary timestamp recorded before re-testing.

**One suite failure was real, not flaky.** `Guard3_no_leftover_scratch_database_exists` failed because two scratch databases created during that same run were genuinely still present. Dropped with per-name ownership verification; re-run clean at 1676/1676. The guard is order-sensitive by nature — it asserts a whole-instance invariant while sibling tests create and drop databases — and may trip again for the same benign reason.

## 6. Corrections to my own earlier reporting

**Release failures are not the same finding twice.** Last increment I attributed a Release failure to TAB-2's Reporting code (`CS0246`, `CS0029`). That was accurate then, and those errors have since been fixed. Today's Release failure is a **file lock**. Both surfaced as "Release: 2 errors" and they are unrelated.

## 7. Stated plainly — what is not a page

**My Work · Favorites · Recent Activity · Quick Actions · Report History · Saved Reports** are **panels** inside `/Workspace` or `/Reports`. They have no URL and cannot be linked to directly.

**Saved-report writes** (save, fork, set-default, delete, favourite, unfavourite, reorder) are **API-only** under `/api/reports-center/`, consumed by JavaScript. They are not screens.

**`/PlatformTimeline`** returns 404 — the controller has no `Index` action. It is not a delivered screen and is not in the menu.

## 8. Completion gate

| # | Condition | Status |
|---|---|---|
| 1 | Every new screen inventoried | **Yes** — 19 rows with controller, action, view, URL, verb, permission, dependency, state |
| 2 | Exact verified URL for each | **Yes** — every one from a live HTTP request |
| 3 | Workspace reachable | **Yes — 200** |
| 4 | Reports Center reachable | **Yes — 200** |
| 5 | A valid Report Viewer URL demonstrated | **Code given (`Platform.BusinessEventLog`), URL documented, blocked by schema — reported, not hidden** |
| 6 | Business Event Monitor URL documented and verified | **Yes — 200** |
| 7 | Approved screens in navigation | **Yes** — one Platform category, each link exactly once |
| 8 | Permissions explicit | **Yes** — attribute-level and per-report, with the real predicates |
| 9 | No authorization bypass | **Yes** — unauthenticated still redirects; nothing disabled |
| 10 | Nothing falsely described as reachable | **Yes** — panels, API-only and backend-only called out |
| 11 | Application actually run, routes verified | **Yes** — live HTTP, fresh binary |
| 12 | Owner review instructions complete | **Yes** — start command, URLs, menu paths, report code, permission routes, screenshot instructions, troubleshooting |
| 13 | Builds/tests green or blockers attributed | **Yes** — Debug/TestRun 0 errors, 1676/1676; Release attributed to a file lock |

**Condition 5 is partially met and I will not describe it otherwise.** The report code, URL, permission and navigation path are all documented and the route resolves — but it returns 500 because the Reporting schema is absent. Applying `reporting_platform.sql` to an approved environment completes it.

## 9. Owner actions

1. **Apply `reporting_platform.sql`** to an approved environment — unblocks every Reporting screen. *(This was not done: the configured database is `CrossBuyDB2`.)*
2. **Grant yourself `Admin`, `SuperAdmin` or `Auditor`** to view the Business Event log — see the Owner Review Guide §4.
3. **Close Visual Studio / IIS Express** before a Release build.
4. **Decide `/PlatformTimeline`** — add an `Index` action or leave it as an internal partial provider.
5. **Consider the systemic routing default** — `/Inventory`, `/Accounting` and every other bare controller URL still 404. Fixing it globally changes the entry point for every controller and belongs to the Integration Owner.
