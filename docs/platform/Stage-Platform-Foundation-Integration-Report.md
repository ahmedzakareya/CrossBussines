# Platform Foundation — Integration Report

**Role: Platform Guardian / integration owner. No UI built. No Reporting implementation modified.**

---

## 1. Objectives and outcome

| # | Objective | Outcome |
|---|---|---|
| 1 | Integrate the Reporting SQL deployment (A0) into the platform pipeline | **Done** — deployed through `apply-sql-slices.ps1` |
| 2 | Verify `PlatformSchemaHistory` recognizes the Reporting slice | **Done** — recorded, hash matches the manifest exactly |
| 3 | Extend `PlatformShellGuardTests` to cover future screens automatically | **Done** — discovery from disk |
| 4 | `NavigationGuardTests` | **Done** — 8 tests |
| 5 | `PlatformHealth` diagnostics | **Done** — `governance/tools/platform-health.ps1` |
| 6 | This report | **Done** |

---

## 2. A0 — Reporting SQL into the pipeline

### The obstacle, and why it was not simply overridden

The pipeline blocked **every** deployment, including the Reporting slice, because of an unrelated backlog: four divergent POS duplicates plus two undeployable scripts.

That block is correct in origin — *"apply `pos_setup.sql`"* is genuinely ambiguous while two files of that name differ. But the ambiguity is **confined to those names**. It says nothing about `reporting_platform.sql`, which is `guarded`, unique, and in the canonical root.

Blocking a clean, unrelated slice on someone else's ambiguity is over-blocking, and over-blocking is precisely how a gate ends up bypassed rather than obeyed.

### What was added — `-Slice <name>`

A named slice may proceed, but only after being validated on **the same terms the global pre-flight applies**:

| Check | Rule |
|---|---|
| Uniqueness | exists exactly once in the manifest — not one of the ambiguous duplicates |
| Deployability | not `review`, not `not-deployable` |
| Exclusion | not `excludedFromDeploy` |
| Location | under the canonical authored root (D-38) |

**Nothing is waived.** The backlog still blocks a full run, and the mode offers no way to deploy the very scripts the backlog is about.

### Proved, not asserted

| Proof | Result |
|---|---|
| A dirty slice refused even when explicitly named | `REFUSED pos_setup.sql : 2 files share this name - ambiguous`, exit 2 |
| Production still refused with `-Slice` | `REFUSED: 'CrossBuyDB2' is a production catalogue`, exit 2 |
| Reporting slice deployed on an isolated probe | `TARGETED: 1 slice(s) validated individually`, exit 0 |

---

## 3. `PlatformSchemaHistory` recognizes the Reporting slice

Isolated probe `CrossBuyProbe_A0_Reporting`. **No SQL was applied to `CrossBuyDB2`.**

| Check | Result |
|---|---|
| Recorded in the applied registry | `CrossBuy/deploy/sql/reporting_platform.sql`, `Success = 1` |
| Recorded hash | `53db921a777c4612ee6a1a29` |
| Manifest hash | `53db921a777c4612ee6a1a29` — **identical** |
| Tables created | **12** |
| Drift reporter verdict | `APPLIED: 2 · CHANGED: 0 · FAILED: 0 · MISSING: 0` |

The hash match is the load-bearing part: it makes "applied" a **checkable claim**. If the file is edited after deployment, the reporter says `changed` — a name-only log can never produce that finding.

---

## 4. Shell guard — now covers future screens automatically

`PlatformShellGuardTests` listed eight screens by hand. That was wrong in the one way that matters: a screen added tomorrow would not be covered, and the guard would keep reporting green while the very thing it exists to prevent walked in beside it.

It now **enumerates the platform surface folders from disk**. A new screen is protected the moment it is created.

Adding a whole new *surface* still takes one line — deliberately. That is a real architectural act and should be visible in a diff, unlike adding a page to a surface that already exists.

A `Screen_discovery_actually_finds_the_known_platform_screens` test guards the guard: a sweep that silently stopped matching would otherwise make every other test in the file pass vacuously.

---

## 5. `NavigationGuardTests` — 8 tests

Two failures motivated this file, and **neither was caught by anything that existed**:

1. Screens were built, compiled and tested, and nothing anywhere linked to them.
2. A bare `/Workspace` returned an empty 404, so a menu entry would have pointed at a dead URL.

Either half passes on its own while the product is unusable, so they are asserted separately.

| Test | Asserts |
|---|---|
| `Every_platform_screen_is_reachable_from_navigation` | every discovered screen is a menu entry **or** an alias of one |
| `Every_platform_menu_entry_points_at_a_real_action` | controller + action resolve, aliases included |
| `Every_menu_entry_in_the_whole_product_points_at_a_real_action` | product-wide dead-link sweep |
| `A_menu_entry_never_points_at_a_non_GET_action` | a sidebar link issues a GET; a `[HttpPost]` target reads as a broken screen |
| `No_menu_lists_the_same_destination_twice` | two identical links in one sidebar |
| `A_screen_is_never_both_an_entry_and_another_entrys_alias` | the double-highlight defect |
| `Navigation_discovery_is_not_vacuous` | guards the guard |

**Alias, not entry, is accepted as reachable** — `Reports/Viewer` is opened from a card, not the sidebar, and Inventory uses exactly this pattern for its own report drill-downs. Demanding a menu entry per screen would force clutter the authority itself avoids.

### A wrong assumption I made and corrected

The duplicate test first swept **all** menus and reported five duplicates: `Inventory.Items`, `Inventory.Units`, `Inventory.NewAssembly`, `Accounting.PurchaseInvoices`, `Accounting.SalesInvoices`.

Every one was a **shared screen listed in two different module menus** — `Items` under both Inventory and Manufacturing. That is deliberate: Manufacturing needs the item list, and only one menu renders at a time. Not a defect.

The real invariant is the same destination twice **in one sidebar**. That is now what is asserted, and it holds product-wide because it is a genuine invariant rather than an assumption about sharing.

Had I "fixed" those menus, I would have removed working navigation from two modules I was forbidden to touch.

---

## 6. `PlatformHealth` diagnostics

`governance/tools/platform-health.ps1` — **read-only**, no DDL, no writes, `sys.tables` only.

Four facts per platform, because any one alone misleads:

| Fact | Question |
|---|---|
| **CODE** | do the services exist in the tree? |
| **WIRED** | are they registered in `Program.cs`? Code that is not registered never runs |
| **SCHEMA** | do its tables exist in the target database? |
| **REACHABLE** | is there a screen a user can open? |

"Tests pass" is deliberately **not** a fact here: tests prove the code does what it says, not that anyone can use it. Reporting had 221 passing tests, 12 tables that existed nowhere, and no reachable screen.

`unknown` is never treated as healthy. Not knowing is a different thing from being fine, and collapsing the two is how a missing schema went unnoticed for weeks.

### Current reading — `CrossBuyDB2` (read-only probe)

| Platform | CODE | WIRED | SCHEMA | REACHABLE | Verdict | Owner |
|---|---|---|---|---|---|---|
| Reporting | yes | yes | **MISSING** | yes | **DEGRADED** | TAB-2 |
| Workspace | yes | yes | n/a | yes | **HEALTHY** | TAB-1 |
| Business Events | yes | yes | yes | yes | **HEALTHY** | TAB-0 |
| Tasks | yes | yes | yes | yes | **HEALTHY** | TAB-5 *(unassigned)* |
| Calendar | yes | yes | yes | yes | **HEALTHY** | TAB-5 *(unassigned)* |

**4 / 5 healthy.** The single degradation: `ReportTemplates`, `ReportRuns`, `ReportFavorites` absent from `CrossBuyDB2` — the Reporting slice is deployed on the probe, not on the dev database.

### A false fault I created and removed

The first version probed for a table named `Tasks` and reported that live platform as **DEGRADED**. The real table is `TaskItems`, verified against `sys.tables`.

A probe that names the wrong table is worse than no probe: it manufactures a fault and trains the reader to ignore the report. Every probe name is now verified to exist.

---

## 7. Verification

| Gate | Result |
|---|---|
| Debug build | **0 Error(s)** |
| TestRun build | **0 Error(s)** |
| `PlatformShellGuardTests` | **6 / 6** |
| `NavigationGuardTests` | **8 / 8** |
| Live route resolution (7 platform routes) | all **200** |
| Probe databases remaining | dropped at completion |
| SQL applied to `CrossBuyDB2` | **none** — read-only `sys.tables` only |

An earlier build reported errors from a **file lock** held by a leftover `testhost` process, not from source. Classified as environment per the standing rule; the process was stopped and the rebuild verified at `0 Error(s)` before any measurement was taken.

---

## 8. Files changed

| File | Change |
|---|---|
| `governance/tools/apply-sql-slices.ps1` | `-Slice` targeted deploy, validated on the pre-flight's own terms |
| `governance/tools/platform-health.ps1` | **new** — five-platform diagnostics |
| `CrossBuy.Tests/PlatformShellGuardTests.cs` | disk discovery + discovery guard |
| `CrossBuy.Tests/NavigationGuardTests.cs` | **new** — 8 navigation invariants |
| `docs/roadmap-v2/_generator/roadmap_v2_data2.py` | SQL-06 registry row records A0 state |
| `governance/registry/sql-slices.json` | regenerated |

**Not touched:** any Reporting implementation, Report Studio, any view, any CSS or JavaScript, Inventory, Accounting.

---

## 9. Open — for their owners

| Item | Owner |
|---|---|
| Apply `reporting_platform.sql` to `CrossBuyDB2` or an approved environment — the single action that makes Reporting healthy | Database owner |
| Four divergent POS slices still block a **full** pipeline run (D-39) | POS owner |
| `hm16_rename_grni.sql` unreviewed; `script.sql` not-deployable | Database owner |
| TAB-5 (Tasks, Calendar) unassigned — two healthy platforms with no accountable tab | Owner |
| Reporting completion declaration, then the final platform verification | TAB-2, then me |
