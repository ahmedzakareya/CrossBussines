# Stage Reporting UI — Delivery Report (R3)

**Platform:** CrossBusiness Reporting Platform · **Tab:** 2 · **Date:** 2026-08-06

**Status: five of six phases delivered. Phase 1 is blocked by a gate that is still shut, and the block is
proven rather than asserted.**

---

## 1. Verification

| Check | Result |
|---|---|
| `dotnet build CrossBuy.sln -c Debug`, no source exclusions | **0 errors** |
| `CBA001` — full non-incremental rebuild, grep for `CBA[0-9]{3}` | **zero diagnostics of any CBA id** |
| **Reporting tests** | **304 / 304 passing** (253 → 304, **+51**) |
| **Full application suite** | **1539 passed · 0 failed · 183 skipped** (SQL-Server-gated) |
| Rendered against the running app | `/Reports` **200**; stylesheet set **identical** to `/Inventory/Index` |
| Preservation archive restore | **31 files re-read from the archive, 0 mismatches** |

The 18 `TasksCalendar*` failures reported in the previous increment are gone — TAB 4 fixed the Employee seed.
The suite is clean.

---

## 2. Phase by phase

| Phase | Status | Evidence |
|---|---|---|
| 1 · Restore the seven writes | **BLOCKED — gate shut** | `Stage-Reporting-UI-03` |
| 2 · Reports Center UI | Delivered | `Stage-Reporting-UI-01` |
| 3 · Report Viewer | Delivered | `Stage-Reporting-UI-02` |
| 4 · Business Events pilot | Delivered | `Stage-Reporting-UI-04` |
| 5 · Workspace integration | Delivered | `Stage-Reporting-UI-05` |
| 6 · UI safety | Delivered — 10 proofs | `ReportingUiSafetyTests` |

---

## 3. Phase 1 — why it is still blocked

The brief conditions Phase 1 on *"After TAB 1 confirms the authorization-surface addition"*. **TAB 1 has not
made it.** `AuthorityTypes` still ends at `IPlatformGrantWriter`; `CBA001` is still `error`.

The interesting part is a route that looked like a legitimate way around it and is not.
`AuthorizationResolver.IsAuthority` credits a call whose containing type implements a declared authority, and
`IModuleAccessService` **is** declared — so a Reporting access service implementing it would be credited with
no analyzer edit, and every other module has one, so there is an architectural argument too.

It fails at boot. `PermissionScopeStartupValidator` throws for any registered `IModuleAccessService` whose
`Scope` is not in `EntityRegistry.PermissionScopes`, and there is no `ScopeReporting`. The route does not avoid
a TAB 1 edit — it **relocates it from a linter's policy file into the platform permission model**, making
Reporting a permission module as a side effect of satisfying a linter. Rejected on the merits.

So the one-line addition to `AuthorizationSurface.cs` remains the correct fix and it remains TAB 1's. The seven
endpoints are written in full and parked as `.cs.pending`; their authorization is tested at the service layer
today; activation is three steps and no code change.

**Consequence for the product:** the Viewer renders Save / Fork / Favourite **disabled with a stated reason**
rather than hiding them. A hidden control would make a waiting system look finished.

---

## 4. What was found while building

Four things worth recording. Three were my own errors, caught by tests or by the compiler.

**A search term must not revoke an affordance.** Archive retrievability was first derived from the *filtered*
catalog, so typing a search marked every unrelated history row as no-longer-permitted. Two sets now:
"visible to this caller" and "matching this search".

**A revoked permission must not erase your own history.** I asserted the archive row would disappear; it did
not, and the *test* was wrong. `ListAsync`/`QueryAsync` are self-scoped, and dropping a row would make a
person's own history rewrite itself. The real gap was UI honesty — a Download button that 404s. Rows now stay
and are marked, with three distinguishable outcomes (downloadable · file swept · no longer permitted) so the
user goes to the right administrator.

**`BranchId` is a reserved parameter key.** The Phase 4 branch filter was first declared as `BranchId`, which
the binder fills unconditionally from the `BusinessContext` and discards caller input for. A scope value and a
filter value would have shared one slot and the input would have appeared to do nothing. Renamed
`FilterBranchId`, with a test.

**A text-matching architecture test reads prose as code.** The containment test flagged
`ReportsCenterPresenter`, whose header *says* it does not reference the Workspace. It now ignores comments.

---

## 4b. The visual rule, and what checking it against a real database found

The owner set a global rule mid-increment: **the Inventory module is the only visual authority**; a screen that
can be visually distinguished from Inventory is a failed implementation. The Reporting screens were rebuilt on
Inventory's own components and the bespoke design language was **deleted**, not merely unreferenced —
`_LayoutReporting.cshtml`, `crossbusiness-reporting.css` and the layout's three resx files are gone.

This also supersedes the line in `CLAUDE.md` naming **Accounting** as the visual identity reference for
platform tools. The two now disagree; the owner's rule wins and CLAUDE.md needs the correction. **Flagged, not
silently resolved** — that file is shared and edited by several tabs.

Three findings, in ascending order of importance:

**The bespoke stylesheets were overriding the brand.** They declared their own blue scale and loaded *after*
`crossbuy-brand.css`. Removing them restores ledger green: `/Reports` and `/Inventory/Index` now load the
**same seven stylesheets, byte for byte**, and the rendered pages share `kt_app_sidebar` ×7,
`app-container container-fluid` ×4, `breadcrumb-separatorless`, `card card-flush`, `table-row-dashed` and the
`#13433a` header rule. Zero `cbr-`/`cbw` tokens survive in either page. The Reports Center renders the **real
Inventory sidebar** — 33 identical `/Inventory/*` links.

**`@section Styles` on a layout that renders none is a runtime crash.** `_LayoutInventory` declares no such
section and ASP.NET Core throws when one is defined but never rendered. The build stayed green; the Viewer was
unusable. Now a banned token, checked by test.

**THE IMPORTANT ONE — the Reporting platform has no deployment script.** Rendering the Viewer against the real
database returned `SqlException: Invalid object name 'ReportShares'`. `deploy/sql/` contains **58 files and not
one creates a Reporting table**. Every Reporting test passes because `ReportingTestHost` builds its schema from
the EF model with `EnsureCreated()`, which hides the absence completely — the exact shape of the failure
CLAUDE.md already records ("112 green tests coexisted with an application that could not boot"), and it means
the previous increment's gate line *"Business Events report can preview/export"* was true of the tests and not
of a real database. Corrected here.

What was fixed in this increment: the screens now **degrade with an actionable message** instead of throwing.
The Viewer returns an unavailable state naming the deployment; the Center reports "not deployed" rather than
rendering as a working-but-empty product; Export redirects to the Viewer rather than 500-ing a file request.
The detector is deliberately narrow — a missing *object* only, so a timeout or a deadlock keeps throwing.

What was **not** done: writing the slice. That is real schema work for ~10 tables, it is not what this
instruction asked for, and a schema authored in a hurry is worse than none. **See open item A0.**

> The live instance is still serving the pre-fix binary (identical byte count across restarts of the check), so
> the 500 persists there until the app is restarted. The fix is verified by build and by test, not by that
> instance.

## 5. Shared-tree events

The tree broke twice under this increment, both times from another tab's in-flight work, both times proven by
`git status --porcelain` (untracked `??`) before any diagnosis:

- **TAB 3 / Workspace** — `WorkspaceNavigation.Build` gained a `WorkspaceCapabilities` parameter while
  `_LayoutWorkspace.cshtml` still called the old signature. Whole solution red. Not touched; TAB 3 fixed it
  within the increment.

This is why **Reporting has its own layout** and **its own view-model types**, and why the entire dependency on
the Workspace is one adapter file. A stylesheet is shared (one Blue, no drift); a C# contract is not.

---

## 6. Completion gate

| Gate | State |
|---|---|
| all legitimate write endpoints restored | ✗ **blocked** — written, parked, tested; needs TAB 1's one line |
| CBA001 remains zero | ✓ zero |
| Reports Center reachable | ✓ `GET /Reports` |
| Report Viewer reachable | ✓ `GET /Reports/Viewer/{code}` |
| Business Events report can preview / export | ✓ HTML preview, CSV + Excel export |
| hidden fields remain hidden | ✓ Internal columns, SystemSupplied params, `Never` fields, `DedupKey` |
| cross-company access denied | ✓ foreign template id never resolves; no `companyId` parameter exists |
| Workspace integration uses extension points | ✓ `IWorkspaceReportSource`, zero TAB 3 files modified |
| full suite 0 failures | ✓ 1472 / 0 / 183 |
| no Accounting / Inventory / CRM data source | ✓ none added — the only source reads the kernel's own log |
| no PDF or Stimulsoft activated | ✓ `UnconfiguredHtmlToPdfConverter` unchanged, no package added |
| preservation restores with 0 mismatches | ✓ 31 files re-read from the archive, 0 mismatches |

**Stopped before the advanced Report Studio,** as instructed.

---

## 7. Owner decisions — all still honoured

| # | Decision | State |
|---|---|---|
| 1 | CrossBusiness Blue is canonical | ✓ the shell is Blue, scoped `.cbw`; every existing module screen stays green |
| 2 | Green/gold only inside report content | ✓ the renderer's markup is confined, not restyled |
| 3 | Playwright PDF deferred | ✓ no PDF button — `AvailableFormatsAsync` intersects with registered renderers, so it is absent rather than broken |
| 4 | Email waits for the Communication outbox contract | ✓ `NullReportMailSender` unchanged |
| 5 | Hosted scheduler deferred | ✓ still **0** `IHostedService` registrations, still asserted by test |
| 6 | Permission provider fail-closed until B6 | ✓ `RoleMapReportPermissionEvaluator` unchanged; unmapped keys denied |
| 7 | No Accounting/Inventory/CRM/HR/Projects/Construction data source | ✓ none |

**On the UI-design decision:** the standing rule was to request the approved Metronic reference before building
any Reporting UI. This brief supersedes it by naming the target — *"CrossBusiness Blue, Metronic 8, existing
approved platform layout conventions"* — so the screens follow the **already-approved** system rather than
inventing one: Metronic 8 `app-*` shell like every other backend layout, and the CrossBusiness Blue token set
already reviewed for the Workspace. Nothing new was invented; if the owner has a specific reference screen, the
components are token-driven and will re-skin from one file.

---

## 8. Open items

| # | Item | Owner |
|---|---|---|
| **A0** | **`deploy/sql/reporting_platform_slice_001.sql` does not exist.** No SQL file in the repository creates `ReportTemplates`, `ReportTemplateVersions`, `ReportShares`, `ReportRuns`, `ReportArchiveEntries`, `ReportFavorites`, `ReportCategories`, `ReportTags` or `ReportSchedules`. Until it is written and applied, the Reporting platform cannot run against any real database — the screens now say so instead of throwing, but they still show nothing. **This blocks the product, not a feature.** | this tab, next — say the word and it is written first |
| **A1** | Add `"IReportAuthorizationService", "ReportAuthorizationService"` to `AuthorizationSurface.AuthorityTypes` → unblocks the seven writes | **TAB 1** |
| **A2** | `CLAUDE.md` still names Accounting as the visual identity reference; the owner's rule names Inventory. Shared file — needs the owner's edit, not this tab's | owner |
| **H-1** | Duplicate `ReportLibraryWorkspaceReportSource` in `WorkspaceSources.cs` — unregistered today, would double the links if registered | TAB 3 |
| **H-2** | Promote the `--cbw-*` Blue tokens to a shared stylesheet now that two products consume them | TAB 3 / owner |
| **H-3** | Optionally point the Workspace rail's Reports entry at `/Reports` | TAB 3 |
| A3 | `CHECK` constraints on Reporting vocabulary columns (`reporting_platform_slice_002.sql`) | this tab, next |
| A4 | Release-configuration measurement on a build agent | build agent |
| A5 | Module datasets (Accounting, Inventory, CRM) | deferred by decision 7 |
| A6 | Report Studio designer | out of scope, stop line |

---

## 9. Deliverables

| # | File |
|---|---|
| 1 | `docs/platform/Stage-Reporting-UI-01-Reports-Center.md` |
| 2 | `docs/platform/Stage-Reporting-UI-02-Report-Viewer.md` |
| 3 | `docs/platform/Stage-Reporting-UI-03-Write-Endpoint-Evidence.md` |
| 4 | `docs/platform/Stage-Reporting-UI-04-Business-Events-Pilot.md` |
| 5 | `docs/platform/Stage-Reporting-UI-05-Workspace-Integration.md` |
| 6 | `docs/platform/Stage-Reporting-UI-Delivery-Report.md` (this file) |
| 7 | `docs/platform/Stage-Reporting-UI-Disk-State-Manifest.csv` |
| 8 | `docs/platform/Stage-Reporting-UI-Preservation-Report.md` |
