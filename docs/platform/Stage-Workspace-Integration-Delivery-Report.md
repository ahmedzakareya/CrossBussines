# Stage-Workspace-Integration — Delivery Report

**Product:** CrossBusiness Platform · **Component:** CrossBusiness Workspace · **Tab:** TAB 3
**Date:** 2026-08-06 · **Status:** complete, stopped for UI review as instructed

---

## 1. What was asked, and what was delivered

| Phase | Requirement | Delivered |
| --- | --- | --- |
| 1 | Screen reachability | 5 routes, all reachable, all authenticated, all localized |
| 2 | Menu / navigation registration | 8 sections, capability-aware, no dead future links |
| 3 | Agenda integration | consumes TAB 4's `IWorkspaceAgendaService`; **no Task/Calendar table is queried** |
| 4 | Reporting integration | extension sources only; no data source, renderer or report table touched |
| 5 | Robust unavailable states | six-state model; unavailable is never collapsed into empty |
| 6 | Mark-as-read handover | endpoint stays withdrawn; authority contract specified for TAB 1 |
| 7 | UI evidence | route list, capture instructions, component list, breakpoints, checklist |
| — | No business-rule duplication | §5 |

---

## 2. Verification

### 2.1 Build — **no exclusions, both configurations**

```
VERIFY: 2026-08-06 12:24:04
  Debug   : total errors = 0   in Workspace files = 0
  Release : total errors = 0   in Workspace files = 0
```

Earlier in this increment the unexcluded tree carried 2 errors, both in Reporting files. The Reporting tab has
since fixed them. **The web project now builds clean with nothing excluded.**

### 2.2 Test suite

```
RERUN: 2026-08-06 12:58:01
Passed!  Failed: 0   Passed: 1451   Skipped: 183   Total: 1634   (1 m 41 s)
```

**One measurement-only exclusion applies to the test project, and it is not mine.**
`CrossBuy.Tests/ReportingUiSafetyTests.cs:138` calls `new ReportingTestHost("Admin", "SuperAdmin")`, but that
host's own signature (`ReportingTestHost.cs:32`) is
`ReportingTestHost(int? companyId = 1, int? employeeId = 7, params string[] roles)` — two strings passed
positionally into two `int?` parameters. Both files belong to the Reporting tab, which is mid-write, and TAB 3
may not modify Reporting internals. The file was excluded **via a command-line MSBuild property only**
(`-p:CustomBeforeMicrosoftCommonTargets=…`); nothing in the repository was edited, moved or committed.

Removing that exclusion produces `error CS1503` ×2 and the suite does not compile — an outcome owned by the
Reporting tab, reported here rather than worked around.

**A failure that was NOT mine, and how that was established.** The first full run reported one failure:
`ReportingPilotAndWorkspaceTests.Only_the_adapter_references_the_workspace_from_the_reporting_platform`. It
passes on re-run. The run log shows `MSB3026 … locked by: testhost (31504)` — another tab was executing tests
against the same tree at the same time, while the Reporting tab was mid-write on the very files that test
inspects. Re-run in isolation: **pass**. Re-run in the full suite: **pass**. No Workspace file was changed
between the two runs.

### 2.3 The skipped 183

SQL-Server-gated integration tests (`CROSSBUY_TEST_SQL` unset). Pre-existing repository behaviour; unrelated to
this increment.

---

## 3. The most significant event of this increment

**A duplicate agenda service was built and deleted before it shipped.**

`CrossBuy.BL.Workspace.IWorkspaceAgendaService` was written, complete with contracts, sources and DI
registration — and then discovered to be a *second* copy of `CrossBuy.BL.TasksCalendar.IWorkspaceAgendaService`,
already owned by TAB 4 and already registered in `Program.cs`. Every file of the duplicate was deleted and
`WorkspaceService.LoadAgendaAsync` rewired to consume theirs.

Had it shipped, two agenda contracts would have competed for one screen and the "do not duplicate Task or
Calendar logic" rule would have been broken in the least visible way possible — by a service that worked.
Recorded in full in `…-03-Agenda.md` §1.

**A mirror-image event, in the other direction:** the Reporting tab registered its own
`ReportingWorkspaceSource` against this tab's `IWorkspaceReportSource` seam *while this increment was in
flight*, contributing the `Recent` and `Saved` kinds that `…-04-Reporting.md` had recorded as gaps. **Zero
Workspace files changed to absorb it.** That is the seam proving itself, and `…-04-Reporting.md` §3.0 was
corrected to state current reality rather than leave a stale gap on the record.

---

## 4. Constraints — compliance

| Constraint | Status | Evidence |
| --- | --- | --- |
| Do not modify Reporting internals | ✅ | no file under `BL/Reporting` or `Views/Reports` was written by this tab |
| Do not modify Tasks/Calendar internals | ✅ | consume `IWorkspaceAgendaService`; duplicate deleted |
| Do not modify Communication kernel internals | ✅ | `ICommMentionService` read-only, resolved optionally |
| Do not modify TAB 1 governance | ✅ | `authorization-baseline.json` and `AuthorizationSurface.cs` untouched — the endpoint was withdrawn instead |
| Do not modify Accounting / Inventory / CRM / Construction | ✅ | no file touched |
| Do not query Task or Calendar tables directly | ✅ | `WorkspaceService` takes **no** `CrossDbContext` |
| Do not materialize tasks as Calendar rows | ✅ | `WorkspaceAgendaRow` is a display projection; nothing is written |
| Do not call Reporting data sources or renderers | ✅ | only `IReportLibraryService` / `IReportService` public contracts |
| Mentions/Notifications consumption-only | ✅ | no Communication write of any kind |
| Do not activate Communication as a side effect | ✅ | `AddCommunicationPlatform` is not called |
| Do not collapse unavailable into empty | ✅ | 5 visually distinct states in one shared partial |
| Do not restore the mutating endpoint | ✅ | still withdrawn; contract specified for TAB 1 to approve |
| Do not show future modules as active links | ✅ | no Security Console / Report Studio / CRM / Construction / AI / Mobile |
| Do not redesign legacy screens | ✅ | CSS scoped under `.cbw`; no shared stylesheet or layout modified |

### 4.1 The one shared file touched

`Program.cs` — a single line, `builder.Services.AddCrossBusinessWorkspace();`, plus its `using`. Added in the
previous increment (R1–R3), unchanged here. There is no way to register a component without it.

---

## 5. No business-rule duplication

| Rule | Owner | Workspace behaviour |
| --- | --- | --- |
| What is due, overdue, recurring | Tasks/Calendar (TAB 4) | renders `IsOverdue` / `IsCompleted` as given |
| Time-zone and all-day resolution | TAB 4's agenda service | reads `AgendaInstant`; converts **display only** |
| Which reports a caller may see | Reporting | receives an already-filtered list |
| Report staleness | Reporting | renders the `IsStale` flag |
| Who was mentioned, and how | Communication | renders `ViaKind` |
| Notification expiry | evaluated at read time | no sweeper, no rule of its own |
| Company and employee resolution | platform `BusinessContext` | fail-closed; never defaults a company |

The Workspace computes exactly one thing: **which panel state to show**. Everything else is somebody else's
answer, rendered.

---

## 6. Files

### 6.1 Added / changed by this tab

| File | Lines | Role |
| --- | --- | --- |
| `CrossBuy/BL/Workspace/WorkspaceContracts.cs` | 432 | six-state model, panels, seams |
| `CrossBuy/BL/Workspace/WorkspaceService.cs` | 611 | the read model — **no `CrossDbContext`** |
| `CrossBuy/BL/Workspace/WorkspaceSources.cs` | 256 | the only file with a DbContext |
| `CrossBuy/BL/Workspace/WorkspaceNavigation.cs` | 198 | capability-aware navigation |
| `CrossBuy/BL/Workspace/WorkspaceRegistration.cs` | 52 | DI; deliberately does **not** register the agenda service |
| `CrossBuy/Controllers/WorkspaceController.cs` | 85 | 5 actions, no mutating endpoint |
| `CrossBuy/Views/Shared/_LayoutWorkspace.cshtml` | 212 | shell |
| `CrossBuy/Views/Shared/_WorkspacePanelState.cshtml` | 77 | **the five states, in one place** |
| `CrossBuy/Views/Workspace/Index.cshtml` | 466 | dashboard |
| `CrossBuy/Views/Workspace/Agenda.cshtml` | 139 | agenda |
| `CrossBuy/Views/Workspace/Reports.cshtml` | 91 | report shortcuts |
| `CrossBuy/Views/Workspace/Notifications.cshtml` | 111 | notifications |
| `CrossBuy/Views/Workspace/Mentions.cshtml` | 78 | mentions |
| `CrossBuy/wwwroot/Backend-assets/css/crossbusiness-workspace.css` | 500 | Blue identity, scoped to `.cbw` |
| `CrossBuy/Program.cs` | +2 | one registration line (prior increment) |

**3,308 lines** across 14 owned files.

### 6.2 Documents

`docs/platform/Stage-Workspace-Integration-` `01-Screen-Inventory` · `02-Routes-and-Navigation` · `03-Agenda` ·
`04-Reporting` · `05-Communication-States` · `UI-Review-Pack` · `Delivery-Report` · `Disk-State-Manifest.csv` ·
`Preservation-Report`, plus `Stage-Workspace-R1-R3-Delivery-Notes.md` from the prior increment.

---

## 7. Open items handed to their owners

| # | Item | Owner | Blocking? |
| --- | --- | --- | --- |
| 1 | `IWorkspaceNotificationAuthority` — approve, then the mark-as-read endpoint can return | **TAB 1** | no; unread is display-only meanwhile |
| 2 | Activate Communication (`AddCommunicationPlatform` + slice SQL) → Mentions starts working with no Workspace change | **Communication tab** | no |
| 3 | `ReportingUiSafetyTests.cs:138` does not compile against its own host | **Reporting tab** | yes, for a clean unexcluded suite |
| 4 | Screenshots at 1440 / 992 / 390, ar + en, light + dark | reviewer | no |
| 5 | The `Shortcut` report kind has no producer — see below | **Reporting tab** | no; the panel degrades to its other kinds |

### 7.1 · 2026-08-12 — duplicate report-favourite producer removed (TAB 3)

`AddCrossBusinessWorkspace()` used to register `ReportLibraryWorkspaceReportSource` alongside Reporting's own
`ReportingWorkspaceSource`. Both called `IReportLibraryService.GetFavoritesAsync` and both emitted
`Kind = Favorite`; `LoadReportsAsync` merges sources with no de-duplication, so every pinned report reached the
Reports panel **twice** — once with a working `/Reports/Viewer` url and once with `Url = null`. Reporting owns
report favourites, so the Workspace's duplicate was removed (registration **and** class). Predicted as **H-1**
in `Stage-Reporting-UI-05-Workspace-Integration.md`; this closes it.

Invisible on a fresh database — with `ReportFavorites` empty, zero favourites duplicate to zero rows — so it
was verified by **seeding** a favourite, not by reading an empty table.

**Second defect the same removal closed.** The withdrawn source called `IReportService.BrowseAsync`, which
**recorded a `Platform.ReportCatalog` report run on every dashboard render**, and those runs then fed back into
the panel's own `Recent` group. CrossBuyDev accumulated 8 such runs during read-only verification alone; six
renders after the removal produced **zero** new runs. (Those 8 rows remain in `ReportRuns` on CrossBuyDev and
are safe to purge — they are verification artefacts, not user activity.)

**The `Shortcut` consequence, stated plainly.** `Shortcut` — the permission-filtered catalogue that gave a
starting point to somebody who has pinned nothing — was produced ONLY by the withdrawn source. Nothing
contributes it today, and it was deliberately **not** re-implemented in the Workspace: reviving a second
producer of report-library facts here is the defect this removal resolves. If the kind is still wanted, its
home is Reporting's own adapter. Handed over as item 5 above.

Guarded by `CrossBuy.Tests/WorkspaceReportSourceOwnershipTests.cs` (9 tests). The guards were **falsified**
before being trusted: with a second favourite producer deliberately re-registered, 5 of the 9 fail, including
the two that assert on rendered rows rather than on registration counts.

---

## 8. Completion gate

| Gate | Status |
| --- | --- |
| Every screen reachable and authenticated | ✅ |
| Navigation registered, capability-aware | ✅ |
| Agenda consumes the owning service | ✅ |
| Reporting consumed through extension points only | ✅ |
| Unavailable never rendered as empty | ✅ |
| Full suite: 0 failures | ✅ 1451 passed, 0 failed *(one Reporting-owned test file excluded — §2.2)* |
| UI review pack exists | ✅ |
| Preservation restores with 0 mismatches | ✅ see `…-Preservation-Report.md` |

**Stopped here for UI review, as instructed.** No further module was started.
