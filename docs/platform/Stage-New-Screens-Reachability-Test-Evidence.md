# Stage — New Screens: Reachability Test Evidence

**Builds, tests, and live HTTP verification on a verified fresh binary.**

---

## 1. Builds

| Configuration | Result |
|---|---|
| **Debug** | **0 Error(s)** |
| **TestRun** | **0 Error(s)** |
| **Release** | **2 errors — MSB3021/MSB3027 file lock, not a compile error** |

The Release failure is `Unable to copy ... CrossBuy.dll ... The file is locked by: "Microsoft Visual Studio (13396), IIS Express Worker Process (4560)"`. The owner has the project open in Visual Studio with IIS Express running. **No source defect** - closing those processes clears it.

**A correction to my own earlier report:** in the previous increment I attributed a Release failure to TAB-2's Reporting code (`CS0246 ReportingWorkspaceSource`, `CS0029`). That was accurate then; those compile errors have since been fixed. Today's Release failure is a different thing entirely - a file lock. Both were reported as "Release 2 errors", and they are not the same finding.

## 2. Test suites

| Suite | Result |
|---|---|
| **Full application suite** | **Passed! — Failed: 0, Passed: 1676, Skipped: 0, Total: 1676** (8 m 49 s) |
| New reachability tests | **16 / 16** |
| Workspace + Reporting tests | included in the full suite, all green |
| DI validation (`Stage1DiWiringTests`) | included, green - the real graph builds with `ValidateOnBuild` + `ValidateScopes` |

**No stale assembly.** Every run above followed a build whose error count was captured explicitly. When the first restart silently ran an old binary (the routing fix appeared to have no effect), it was caught because the build reported 2 errors from a file lock held by the still-running app - the process was stopped, the rebuild verified at `0 Error(s)`, and the binary timestamp (`13:38:03`) recorded before re-testing.

### One failure, diagnosed and resolved

The first full-suite run reported **1 failed / 1675 passed**:

```
SqlCompanionGuardTests.Guard3_no_leftover_scratch_database_exists_on_the_real_instance [FAIL]
```

Two scratch databases created **during that same run** (14:12:10 and 14:12:17) were still present:
`CrossBuyPlatformTest_db9ccf7c…`, `CrossBuyProbe_B6ReadPath_5daa3975…`

That is my own guard doing its job. Both were dropped with per-name ownership verification (owned scratch prefix, and explicitly not a production catalogue), and the suite re-run clean at **1676 / 1676**.

Worth recording: Guard 3 asserts a whole-instance invariant while other tests in the same suite are creating and dropping scratch databases. It is not a false positive - the databases were genuinely present - but it is order-sensitive, and a future run may trip it again for the same benign reason.

## 3. New reachability tests — 16 passing

`CrossBuy.Tests/NewScreenReachabilityTests.cs`

| Test | Asserts |
|---|---|
| `The_platform_menu_links_to_the_new_screens` (×3) | Workspace, Reports, BusinessEventMonitor each have a menu entry, none marked `Soon` |
| `A_navigation_entry_appears_exactly_once_across_every_menu` (×3) | sweeps **every** static menu method; fails on a duplicate |
| `Every_platform_menu_item_points_at_an_action_that_exists` | resolves each `Controller`+`Action` and each alias by reflection - guards the "menu entry that 404s" failure |
| `The_business_event_monitor_link_is_permission_aware` | `Perm == "platform-ops"` |
| `Workspace_and_reports_center_are_open_to_any_signed_in_employee` | `Perm == null` for both - hiding an entitled screen is as wrong as showing a forbidden one |
| `The_platform_ops_predicate_is_reachable_from_a_view` | `PlatformOpsAttribute.IsAdmin` stays public, or nav silently falls back to "always visible" |
| `Menu_labels_have_an_arabic_resource` (×4) | `Platform`, `Workspace`, `Reports center`, `Business event monitor` exist in `SharedResources.ar.resx` |
| `Every_platform_menu_item_carries_both_labels` | Arabic and English present |
| `The_documented_business_events_report_code_is_the_one_in_source` | `Platform.BusinessEventLog` - if the constant changes, the documented URL becomes a 404 |

**What these tests deliberately do not claim:** they cannot prove a route resolves in a running pipeline. That is what the live HTTP verification below is for.

## 4. Live HTTP verification

Running application, fresh binary `13:38:03`, `http://localhost:5199`, Development.

**Authenticated:**

```
/Workspace                                  200
/Workspace/Agenda                           200
/Workspace/Notifications                    200
/Workspace/Mentions                         200
/Workspace/Reports                          200
/Reports                                    200
/BusinessEventMonitor                       200
/BusinessEventMonitor/Rows                  200
```

**Unauthorized access still refused — no bypass introduced:**

```
/Workspace             302 -> /Account/Login?returnUrl=%2FWorkspace
/Reports               302 -> /Account/Login?returnUrl=%2FReports
/BusinessEventMonitor  302 -> /Account/Login?returnUrl=%2FBusinessEventMonitor
```

**Invalid input fails safely:**

```
/Reports/Viewer/Not.A.Real.Report    404
/Reports/Viewer/            (empty)  404
```

**Blocked by the missing schema:**

```
/Reports/Viewer/Platform.ReportCatalog     500   (SQL 208 - ReportTemplates/ReportRuns/ReportFavorites)
```

**Before the routing fix, for contrast:**

```
/Workspace              404      /Workspace/Index              200
/Reports                404      /Reports/Index                200
/BusinessEventMonitor   404      /BusinessEventMonitor/Index   200
```

## 5. Rendering, assets, localization

| Check | Result |
|---|---|
| View renders | Workspace 200 with full HTML; Inventory page 291 031 bytes with the sidebar |
| Layout loads | `_LayoutWorkspace`, `_LayoutReporting`, `_LayoutBackend` all render |
| Menu partial present | yes, on both Inventory and Accounting layouts |
| Asset load | `/assets/js/app.ajax.js` → 200 |
| No missing DI service | no `InvalidOperationException` in the log; DI wiring test green |
| No missing partial | none reported |
| Server exceptions | only SQL 208 from the missing Reporting schema, isolated to the Reports panels |
| Arabic | `المنصّة`, `مساحة العمل`, `مركز التقارير`, `مراقب أحداث المنصّة` |
| English | `Platform`, `Workspace`, `Reports center`, `Business event monitor` |
| Infinite loading | none observed |

## 6. Database

No SQL was executed against `CrossBuyDB2` - only read-only catalogue queries (`sys.tables`) to establish which Reporting tables are missing. The application itself was run against the configured database with **GET requests only**.

Scratch databases on the instance at completion: **0**. `CrossBuyDB2`: present, untouched.
