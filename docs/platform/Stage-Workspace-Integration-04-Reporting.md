# Stage-Workspace-Integration — 04 · Reporting (Phase 4)

**Product:** CrossBusiness Workspace · **Tab:** TAB 3
**Screen:** `/Workspace/Reports` · **Seam:** `IWorkspaceReportSource`

---

## 1. The rule

The Workspace consumes Reporting through **extension sources only**.

**Never called:** a report data source, a renderer, an exporter, the engine, a report table, `IReportEngine`,
`IReportOutputPipeline`, `IReportRendererRegistry`, `IReportArchiveService`, `IReportScheduleService`.

**Called:** `IReportLibraryService.GetFavoritesAsync` and `IReportService.BrowseAsync` — both **public
consumer contracts** of the Reporting platform, and both already permission-filtered by that platform.

The Workspace lists shortcuts and hands off. **Running a report happens inside the Reporting product.** That
boundary is what keeps this screen from becoming a second, competing report surface — and it is why "Report
Studio" being out of scope costs this screen nothing.

---

## 2. The seam

```csharp
public interface IWorkspaceReportSource
{
    string SourceName { get; }
    bool IsAvailable { get; }
    Task<IReadOnlyList<WorkspaceReportLink>> GetAsync(BusinessContext ctx, CancellationToken ct = default);
}
```

A module — or the Reporting platform itself — adds report links by registering another source **after**
`AddCrossBusinessWorkspace()`. The Workspace learns nothing about report internals.

> **SUPERSEDED (2026-08-12).** This section originally named `ReportLibraryWorkspaceReportSource` as the
> shipped implementation. That class has been **removed**, and `AddCrossBusinessWorkspace()` now registers
> **no** `IWorkspaceReportSource` at all.
>
> **Why.** Reporting contributes its own adapter (`ReportingWorkspaceSource`, registered by
> `AddCrossBusinessReporting`). Once both were registered, both called
> `IReportLibraryService.GetFavoritesAsync` and both emitted `Kind = Favorite`, and `LoadReportsAsync` merges
> its sources with no de-duplication — so every pinned report reached the panel twice, once with a working
> `/Reports/Viewer` url and once with `Url = null`. Reporting owns report favourites and report-library
> semantics, so the Workspace's copy was the one withdrawn. There is now ONE authoritative producer.
> Predicted as H-1 in `Stage-Reporting-UI-05-Workspace-Integration.md`.
>
> The removal also ended a side effect: the withdrawn source called `IReportService.BrowseAsync`, which
> **recorded a `Platform.ReportCatalog` report run on every dashboard render** and then fed those runs back
> into the panel's own `Recent` group. Six renders after the removal produced zero new runs.
>
> **Open item for the Reporting owner:** the `Shortcut` kind — the permission-filtered catalogue that gave a
> starting point to somebody who has pinned nothing — was produced ONLY by the withdrawn source and is
> contributed by nothing today. It was deliberately not re-implemented in the Workspace, because a second
> producer of report-library facts inside the Workspace is the defect this removal resolves. Handed over as
> item 5 in `Stage-Workspace-Integration-Delivery-Report.md` §7.1.

Guarded by `CrossBuy.Tests/WorkspaceReportSourceOwnershipTests.cs`, which asserts on the real DI graph that
exactly one `IWorkspaceReportSource` is registered and that only one registered source emits favourites.

---

## 3. The four required kinds

`WorkspaceReportKind` = `Favorite · Recent · Saved · Shortcut`. **All four are populated** — but by *two
different sources*, and that split is the point of the seam.

| Kind | State | Source | Owner |
| --- | --- | --- | --- |
| **Favourite** | ✅ populated | `IReportLibraryService.GetFavoritesAsync` | Workspace + Reporting (both) |
| **Shortcut** (operational) | ✅ populated | `IReportService.BrowseAsync`, capped at 8 | Workspace |
| **Recent** | ✅ populated | `ReportingWorkspaceSource` | **Reporting** |
| **Saved** | ✅ populated | `ReportingWorkspaceSource` | **Reporting** |

### 3.0 The seam was adopted mid-increment — and it cost this tab nothing

§3.1 and §3.2 below were written as *gaps*: Recent and Saved were modelled but empty, and the recommendation
was that the Reporting tab register its own `IWorkspaceReportSource`. **They did**, while this increment was
still in flight:

* `CrossBuy/BL/Reporting/ReportingWorkspaceSource.cs` — `SourceName = "Reporting"`, contributing `Favorite`,
  `Recent` and `Saved`;
* one line in `ReportingRegistration.cs`:
  `services.AddScoped<CrossBuy.BL.Workspace.IWorkspaceReportSource, ReportingWorkspaceSource>();`

**Zero Workspace files changed to absorb it.** That is the seam working exactly as designed, and it is the
strongest available evidence that this integration is a real contract rather than a diagram. Their own
containment test (`Only_the_adapter_references_the_workspace_from_the_reporting_platform`) now pins Reporting's
Workspace contact to exactly two files.

Their registration comment states the same rule from the other side: *"Reporting CONTRIBUTES to the Workspace;
the Workspace does not reach into Reporting."*

**Consequence for §4:** two sources now feed this panel, so `PartiallyAvailable` is reachable in production —
one source can fail while the other answers. It is no longer a theoretical state.

The two subsections below are **retained as the record of why the gap existed and how it was closed** — not as
current state.

Rows are ordered favourites-first, then shortcuts: a pinned report is a stronger signal of intent than a
catalogue entry the caller merely has permission to open. The view inserts a heading per kind so favourites
read as favourites rather than as an undifferentiated list.

### 3.1 Recent reports *(historical — closed by §3.0)*

Reporting **records** run history (`ReportRuns`, `IReportHistoryService.QueryAsync`), so the data exists. It is
not surfaced here for two reasons:

* consuming `IReportHistoryService` would reach further into Reporting than this seam is meant to allow, and
  history rows carry the parameters a colleague used — which that platform deliberately restricts;
* the Reporting tab is **actively building a "Reports Center"** over exactly this ground
  (`ReportsCenterPresenter`, `ReportsCenterApiController`, `Views/Reports/*` — all appeared during this
  increment).

**Recommendation:** the Reporting tab registers its own `IWorkspaceReportSource` contributing `Recent` and
`Saved`. That is one class on their side and **zero change here** — which is the point of the seam.
**→ Done. See §3.0.**

### 3.2 Saved reports *(historical — closed by §3.0)*

Same shape. `ReportTemplates` (saved layouts) is Reporting's table, and enumerating it from the Workspace would
mean reading a module table directly — forbidden, and the wrong owner. **That Reporting-side source now
exists** (§3.0): `ReportTemplates` is still read only by Reporting, and the Workspace still never touches it.

---

## 4. Unavailable is explicit

| Condition | State | Message |
| --- | --- | --- |
| No source reports `IsAvailable` | `Unavailable` | *"The Reporting platform is not registered in this environment, so report shortcuts cannot be listed."* |
| Every available source threw | `TemporaryFailure` | names the failing sources |
| Some sources threw | `PartiallyAvailable` | banner above real rows, naming what is missing |
| Sources answered, no rows | `Empty` | *"No reports available to you yet"* |

**Unavailable is never collapsed into empty.** "Reporting is not installed here" and "you have not pinned
anything" are different facts, and a user who cannot tell them apart raises the wrong support ticket.

---

## 5. Stale favourites are shown, not hidden

A pinned report whose definition no longer resolves (`IsStale`) is **rendered with a "stale" chip**, never
filtered out — otherwise the user cannot unpin what they cannot see. Reporting already models the flag; the
Workspace only renders it.

---

## 6. Where reports appear

| Surface | Content |
| --- | --- |
| `/Workspace/Reports` | full list, grouped by kind |
| Dashboard rail → **Reports** card | first 8, `Trim` preserving panel state |
| Dashboard rail → **Favorites** card | report favourites via `IWorkspaceFavoritesSource` — a separate seam, deliberately: favourites are cross-module, reports are one module |
| Rail nav → **Reports** | hidden entirely when `Capabilities.Reporting` is false |
| Quick Actions → **Reports** | added only when `Capabilities.Reporting` is true |
