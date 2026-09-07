# Stage Reporting UI — 05 · Workspace integration

**Phase:** R3 Phase 5
**File:** `BL/Reporting/ReportingWorkspaceSource.cs` (one file) + one line in `ReportingRegistration.cs`
**TAB 3 files modified:** **none**

---

## 1. The extension point already existed

TAB 3 publishes `IWorkspaceReportSource` in `BL/Workspace/WorkspaceContracts.cs` for exactly this purpose:

```csharp
// Contributes report links of one or more kinds. A module (or the Reporting platform itself) registers
// one of these; the Workspace learns nothing about report internals.
public interface IWorkspaceReportSource { … }
```

`WorkspaceService` consumes `IEnumerable<IWorkspaceReportSource>` and reports the panel `Unavailable` when
nothing is registered. **No implementation was registered** — that was the seam Phase 5 fills.

No Workspace business logic was touched. No Workspace file was edited. The integration is one adapter plus one
`AddScoped` line, both in Reporting's own files.

---

## 2. Registered from Reporting, not from the Workspace

```csharp
// ReportingRegistration.cs
services.AddScoped<CrossBuy.BL.Workspace.IWorkspaceReportSource, ReportingWorkspaceSource>();
```

Registered inside `AddCrossBusinessReporting()` rather than in `AddCrossBusinessWorkspace()` so that a
deployment which has not activated Reporting simply has **no contributor** — the Workspace panel reports
"unavailable" honestly instead of failing to resolve a service.

It also means the Workspace's registration file is not a place two products have to agree on.

---

## 3. Containment: one file, and a test that keeps it one file

`ReportingWorkspaceSource.cs` is the **only** file in the Reporting platform that references
`CrossBuy.BL.Workspace` (plus the single `AddScoped` line in the registration).

That is not tidiness. The Workspace is another tab's product developed concurrently in the same working tree,
and its contracts move — during **this** increment `WorkspaceNavigation.Build` gained a parameter and broke the
whole tree for everyone with a call site. Reporting has exactly one call site into that product, so a contract
change over there costs one file here.

Concretely: Reporting's view models are its own (`ReportPanel<T>`, `ReportCardModel`, …) and deliberately do
**not** reuse `WorkspacePanel<T>`, even though the two are shaped alike. Sharing them would put a second
product's release cycle inside this one's UI.

`Only_the_adapter_references_the_workspace_from_the_reporting_platform` enforces it.

> That test's first version matched raw file text and flagged `ReportsCenterPresenter`, whose header *says* it
> deliberately does not reference the Workspace — a comment explaining the rule was read as a violation of it.
> It now ignores comment lines. Recorded because the failure was informative: a text-matching architecture test
> will read prose as code unless told otherwise.

---

## 4. What is contributed

| Kind | Source | Notes |
|---|---|---|
| `Favorite` | `IReportLibraryService.GetFavoritesAsync` | a pinned report whose definition no longer resolves is shown and flagged `IsStale`, so it can be unpinned |
| `Recent` | `IReportHistoryService.QueryAsync` | **de-duplicated by report code** — running one report eleven times while tuning a date range is one entry, not eleven, or the panel shows one afternoon and nothing else the person did all week |
| `Saved` | `IReportTemplateService.ListAsync` | Personal and Team layouts only — a Company or Platform layout is what everyone already gets by opening the report, so pinning it adds a row and no information |
| **failed runs** | same as Recent, flagged | see below |

### The judgement call on failed runs

The brief says *"failed operational reports where appropriate"*. The judgement taken:

**A failed run is worth surfacing to the person who ran it, and to nobody else.** A failed report is usually a
bad parameter, not an operational alert, and broadcasting other people's failures onto a shared dashboard would
turn the Workspace into a noticeboard of colleagues' mistakes.

So failures are included only from the caller's **own** history — which is all `IReportHistoryService` returns
for a non-administrator anyway.

They carry the failure **code**, never the engine's message: a Workspace tile is read by people who did not ask
for a diagnostic, and a message can quote a parameter value.
`A_failed_run_is_surfaced_as_needing_attention_without_its_error_text` bounds the sub-line length.

A failed run reuses TAB 3's existing `IsStale` flag rather than adding an enum member — the Workspace renders
`IsStale` as "needs attention", which is the right treatment for both, and adding to their enum would be a
change to their contract.

---

## 5. Safety

**No authorization here, deliberately.** The adapter reads through `IReportLibraryService`,
`IReportHistoryService` and `IReportTemplateService`, each of which applies the report gate itself. A link
cannot appear for a report the caller may not run —
`A_report_the_caller_cannot_run_contributes_no_workspace_link`.

**Links authorize on arrival.** Every URL is `/Reports/Viewer/…`, which gates again. So even a stale link — a
pinned report whose permission was revoked yesterday — leads to a 404, not to data. That is why a link whose
definition no longer resolves is shown and flagged rather than hidden: the user can act on it, and it cannot
leak.

**A tile does not run a report.** `run=true` is deliberately absent from every URL. A panel whose tiles each
execute a query on click is a load test with a friendly face. `No_workspace_link_triggers_a_run`.

**Fails closed and quietly.** An unresolved company contributes nothing rather than throwing — throwing would
push the whole Reports panel into `TemporaryFailure` and invite a retry that cannot succeed.

---

## 6. Handover to TAB 3

Three items. None blocks this increment.

### H-1 · A duplicate implementation exists in Workspace-owned code

`BL/Workspace/WorkspaceSources.cs` already contains `ReportLibraryWorkspaceReportSource`, an unregistered
`IWorkspaceReportSource` that resolves `IReportLibraryService` through `IServiceProvider`.

It was not touched (TAB 3's file) and it is not registered, so there is no live conflict. **If TAB 3 registers
it, the Workspace will show duplicate report links** — both sources contribute favourites.

*Recommendation:* delete `ReportLibraryWorkspaceReportSource` and let Reporting contribute through the
extension point, which is the direction the contract's own comment describes ("a module … registers one of
these"). TAB 3's call.

The same applies more weakly to `ReportFavoritesWorkspaceSource` (an `IWorkspaceFavoritesSource`, registered):
it contributes report favourites to the **Favourites** panel while this adapter contributes them to the
**Reports** panel. Two panels, so not a duplicate — but worth a deliberate decision rather than an accident.
Its links point at `/Workspace/Reports`; ours point at `/Reports/Viewer/{code}`.

### H-2 · ~~Promote the Blue tokens to a shared file~~ — WITHDRAWN

Superseded. The owner's global UI rule makes the **Inventory module the only visual authority**, so Reporting
no longer consumes the CrossBusiness Blue token set at all: `crossbusiness-reporting.css` was deleted and the
screens render on Inventory's Metronic components with `crossbuy-brand.css` (ledger green) as the only brand
stylesheet.

Nothing is owed to TAB 3 here. Left in place, struck through, because the recommendation was made in this
document and a silently vanished handover item is worse than a withdrawn one.

**Still worth TAB 3's attention:** `crossbusiness-workspace.css` remains the Workspace's own, and it loads
*after* `crossbuy-brand.css` while declaring its own blue scale under `.cbw`. That is the same mechanism that
made Reporting's screens diverge from the house identity. Whether the Workspace should also come under the
Inventory rule is the owner's call, not this tab's.

### H-3 · Navigation

`WorkspaceNavigation` already renders a **Reports** entry when `capabilities.Reporting` is true, pointing at
`/Workspace/Reports` — TAB 3's shortcut screen, which hands off to the Reporting product. That still works, and
the shortcuts now have somewhere real to go.

*Optional:* point the rail entry directly at `/Reports` once the Reports Center is considered the primary
destination. Deliberately not changed here — it is a Workspace information-architecture decision, in a TAB 3
file.
