# Workspace UI Conformance Review — Inventory Visual Language

**Reviewer:** TAB 3 (reviewer/integrator role — *not* a concurrent editor)
**Authority:** the Inventory module (`/Inventory/Index`) is the ONLY visual language. Zero parallel themes.
**Snapshot:** 2026-08-09, wall clock 09:33
**Status: PROVISIONAL — the Workspace owner is still writing.** `Views/Workspace/Index.cshtml` changed at
09:23:54, one minute after it was read. Every finding below must be re-run once that owner declares completion.

---

## 1. Scope and non-interference

Per owner instruction, this tab did **not** modify, and has not modified:

* `Views/Workspace/*` · `Views/Shared/_LayoutWorkspace.cshtml` · Workspace Razor views · Workspace CSS rules

**The one write made, and its exact authorization.** A LEGACY banner comment was added to the head of
`wwwroot/Backend-assets/css/crossbusiness-workspace.css` under the explicit instruction *"Mark it as LEGACY."*
It is a comment block only — **no selector, property or value was altered**, so rendering for anything still on
that stylesheet is byte-identical. Nothing else in the Workspace slice was touched.

An earlier edit in this session rewrote `Views/Shared/_WorkspacePanelState.cshtml` onto Metronic `alert
bg-light-*` blocks. That happened **before** the collision was detected, and it is now **dead code** — see §4.2.

---

## 2. Conformance checks

### 2.1 Follows the Inventory visual language — **PASS**

All five Workspace views render through the authority's own shell:

| View | Layout | Line |
| --- | --- | --- |
| `Views/Workspace/Index.cshtml` | `~/Views/Shared/_LayoutInventory.cshtml` | 8 |
| `Views/Workspace/Agenda.cshtml` | `~/Views/Shared/_LayoutInventory.cshtml` | 5 |
| `Views/Workspace/Reports.cshtml` | `~/Views/Shared/_LayoutInventory.cshtml` | 5 |
| `Views/Workspace/Notifications.cshtml` | `~/Views/Shared/_LayoutInventory.cshtml` | 5 |
| `Views/Workspace/Mentions.cshtml` | `~/Views/Shared/_LayoutInventory.cshtml` | 5 |

Same shell ⇒ same header, sidebar (`_MainMenu`), aside, footer, brand stylesheet and RTL/LTR behaviour as
`/Inventory/Index`, by construction rather than by imitation.

### 2.2 Reuses existing Metronic components — **PASS**

`Views/Workspace/Index.cshtml` is built from the exact vocabulary `Views/Inventory/Index.cshtml` uses:

| Component | Inventory reference | Workspace use |
| --- | --- | --- |
| `app-toolbar` + `page-title` + `breadcrumb` | Index.cshtml:18-27 | Index.cshtml:135-168 |
| `card card-flush` + `card-header`/`card-title`/`card-label`/`card-toolbar` | :42-49 | :221-226, :248-261 |
| stat tile — `symbol symbol-40px` › `symbol-label bg-light-*` › `badge badge-light-*` › `fs-2x` | :102-113 | :193-211 |
| `table table-row-dashed align-middle fs-6 gy-3 my-0` + `thead tr.fw-bold.fs-7.text-uppercase.text-muted` | :131-135 | :270-277 |
| `btn btn-sm btn-light` / `btn btn-sm btn-flex btn-primary` | :29-30, :48 | :158-164, :258 |
| `alert` + `ki-outline` iconography | — | :120-126, :177-183 |

No bespoke component was invented; no new CSS file was introduced.

### 2.3 Preserves all Workspace functionality — **PASS (provisional)**

Every section from the pre-conversion build is present: Metrics, Quick Actions, My Work, Unified Agenda, Recent
Activity, Notifications, Mentions, Favourite Reports, Recent Reports, the unresolved-session guard
(`Index.cshtml:114-132`) and the unavailable-panels summary (`:175-184`). Section anchors (`#quick-actions`,
`#my-work`, `#agenda`, `#activity`) are retained.

**The five panel states survive and remain visually distinct** — the rule that unavailable must never collapse
into empty. They moved from a shared partial to a per-view `PanelNotice` helper (`Index.cshtml:78-112`), mapping
each state onto a distinct Metronic `alert-*` variant.

*Provisional* because the owner is still editing; re-verify at completion using the checklist in §5.

### 2.4 Introduces no second design language — **ONE ISSUE OPEN**

Live `.cbw-*` class attributes remaining in Razor views:

| File | Live `.cbw-*` class attributes |
| --- | --- |
| `Views/Workspace/Index.cshtml` | **0** |
| `Views/Workspace/Agenda.cshtml` | **0** |
| `Views/Workspace/Reports.cshtml` | **0** |
| `Views/Workspace/Notifications.cshtml` | **0** |
| `Views/Workspace/Mentions.cshtml` | **0** |
| **`Views/Shared/_LayoutWorkspace.cshtml`** | **25** ← the only offender |

Residual `cbw` mentions in the five views are **prose in comments** (`Agenda.cshtml:15`, `Index.cshtml:26`,
`Index.cshtml:76`) plus one inert data attribute, `Notifications.cshtml:99` `data-cbw-notification` — verified
**not referenced by any JavaScript**, so it is a dead hook, not styling. None of these affect rendering.

---

## 3. The legacy stylesheet — dependency inventory and deletion gate

`wwwroot/Backend-assets/css/crossbusiness-workspace.css` — **LEGACY, temporary compatibility layer.**

### 3.1 Referrers (verified 09:33)

| # | File | Line | Kind | Owner | Status |
| --- | --- | --- | --- | --- | --- |
| 1 | `Views/Shared/_LayoutWorkspace.cshtml` | **56** | `<link>` — the live dependency | **Workspace owner** | **OPEN** |
| 2 | `Views/Shared/_LayoutWorkspace.cshtml` | 40 | comment | Workspace owner | cosmetic |

**Reporting has already migrated.** Both of its referrers were removed during this review window:
`Views/Reports/Viewer.cshtml` was stripped of its `<link>` at 09:26:38, and `Views/Shared/_LayoutReporting.cshtml`
was **deleted outright**. Both Reports views now use `~/Views/Shared/_LayoutInventory.cshtml`
(`Index.cshtml:6`, `Viewer.cshtml:6`). Nothing is owed by that tab.

### 3.2 Deletion gate

> Delete `crossbusiness-workspace.css` in a **dedicated cleanup commit** once and only once
> `grep -rn "crossbusiness-workspace.css" CrossBuy/` returns nothing but the file's own banner.

**Exactly one file stands between the current tree and that gate:** `_LayoutWorkspace.cshtml`, which is
**orphaned — zero inbound references.** No view, controller or layout renders it; all five Workspace views moved
to `_LayoutInventory`. It is dead code whose only remaining effect is to keep a second design language alive.

**Blocked, deliberately.** Removing it is the shortest path to the target state, but it sits inside the
do-not-modify set, so this tab has not touched it. Assigned to the Workspace owner as item W-1 (§4.1).

---

## 4. Review items

**Status: REVIEW ITEMS ONLY — accepted by the product owner, assigned to the Workspace owner.**
This tab performs **no cleanup**, and modifies **no UI file**. The three files named below —
`_LayoutWorkspace.cshtml`, `crossbusiness-workspace.css`, `_WorkspacePanelState.cshtml` — belong to the
Workspace owner and are not to be modified, deleted or restored by anyone else.

### W-1 — Legacy layout still references the legacy stylesheet

> Legacy `_LayoutWorkspace.cshtml` still references the legacy stylesheet although it is no longer used by
> active Workspace views.

| | |
| --- | --- |
| File | `Views/Shared/_LayoutWorkspace.cshtml` |
| Evidence | line **56** — `<link href="~/Backend-assets/css/crossbusiness-workspace.css" …>`; line **40** — comment naming it; **25** live `.cbw-*` class attributes in the file |
| Inbound references | **0** — no view, controller, partial or layout renders it; all five Workspace views moved to `_LayoutInventory` |
| Why it matters | This is the **sole remaining live dependency** on the legacy stylesheet, and therefore the only thing standing between the tree and a single visual language |
| Owner | Workspace owner |
| Action | Workspace owner's decision. Not to be performed by this tab. |

### W-2 — Orphaned panel-state partial

> `_WorkspacePanelState.cshtml` appears orphaned after the Inventory migration.
> The Workspace owner must either **restore shared rendering**, or **officially retire the partial and update
> the documentation/contracts**.

| | |
| --- | --- |
| File | `Views/Shared/_WorkspacePanelState.cshtml` |
| Evidence | no `.cshtml` renders it; the five panel states were inlined per-view as a local `PanelNotice` helper (`Views/Workspace/Index.cshtml:78-112`) |
| Stale contract | `BL/Workspace/WorkspaceContracts.cs:111` still describes it as *"the shared … partial … so the five non-data states live in ONE place"* — no longer true |
| Why it matters | Visually correct today. Structurally, *"the five never drift into looking the same"* is no longer enforced by a single renderer: five copies can now diverge independently. This is a **maintenance** risk, not a visual-rule violation |
| Owner | Workspace owner |
| Either outcome is acceptable | (a) re-adopt the partial across the five views, **or** (b) retire it and correct `WorkspaceContracts.cs:111` plus the delivery docs. What is **not** acceptable is leaving the contract comment asserting something the code no longer does. |

### W-3 — Sweep every `cbw-*` reference before the stylesheet is deleted

> Verify that no runtime JavaScript, bundle, layout or partial still references any `cbw-*` classes before
> deleting the legacy stylesheet.

Deletion is **gated on this sweep returning clean**, across all five surfaces — a Razor-only grep is not
sufficient evidence, because a class can be referenced from script or a bundle that no view mentions.

| Surface | Command | Result at 2026-08-09 09:33 |
| --- | --- | --- |
| Razor views/layouts/partials | `grep -rnE 'class="[^"]*cbw-' --include=*.cshtml CrossBuy/` | `_LayoutWorkspace.cshtml` only — **25** |
| Runtime JavaScript | `grep -rn "cbw-" --include=*.js CrossBuy/wwwroot/` | **0** |
| Bundles / minified assets | `grep -rln "cbw-" CrossBuy/wwwroot/Backend-assets/` | *to re-run at completion* |
| C# emitting markup | `grep -rn "cbw-" --include=*.cs CrossBuy/` | **0** |
| Data hooks (not styling, still a reference) | `grep -rn "data-cbw" --include=*.cshtml --include=*.js CrossBuy/` | `Views/Workspace/Notifications.cshtml:99` — `data-cbw-notification`, **verified read by no JavaScript**; inert, but must be swept before deletion |

**Known non-blockers** (prose in comments, no rendering effect): `Views/Workspace/Agenda.cshtml:15`,
`Views/Workspace/Index.cshtml:26`, `Views/Workspace/Index.cshtml:76`.

| | |
| --- | --- |
| Owner | Workspace owner (sweep) · this tab (independent re-verification at §7) |
| Gate | Until this sweep is clean, `crossbusiness-workspace.css` is a **temporary compatibility artifact** and is **not** to be deleted |

### 4.4 Governance contradiction — for the product owner

`CLAUDE.md` still records *"Accounting is the visual identity reference for future platform and administrative
tools"*, and separately notes the unresolved *"preserve the CrossBuy blue identity"* item. The standing rule is
now **Inventory**, sole authority. That governance file belongs to TAB 1 and was not edited here.

---

## 5. Final verification protocol

This tab executes verification **only** — no cleanup, no UI modification, regardless of what the checks find.
Findings become review items, as W-1…W-3 did.

### 5.0 Wait conditions — ALL three must be true before §5.1 runs

| # | Declaration required | Status |
| --- | --- | --- |
| 1 | **Workspace owner** declares implementation complete | ⏳ waiting |
| 2 | **Reporting owner** declares implementation complete | ⏳ waiting |
| 3 | **Integration owner** declares platform integration complete | ⏳ waiting |

A partial run is worse than none: the tree is being written by several owners at once, and a check that passes
against a half-finished slice certifies nothing. **Nothing in §5 executes until all three declarations land.**

### 5.0.1 Inventory authority baseline — recorded 2026-08-09 09:33

"Inventory remains unchanged" can only be verified against a baseline captured **before** the other owners
finish. Recorded here for that purpose:

| Artefact | SHA-256 |
| --- | --- |
| `Views/Shared/_LayoutInventory.cshtml` | `ee32c203c79cf174175e49eb6e11e6176e1c2952cf7604367bf7d964996d8b1c` |
| `Views/Inventory/Index.cshtml` | `1848fe5d54b8d07c512ef7b724ab64a121530cab0b1f05cabd833509d92e1c6d` |
| `wwwroot/Backend-assets/css/crossbuy-brand.css` | `2f0d8599ebc66a015ca8a380ce0e958e1dd22eb9fe763aa4984d33628464564f` |
| All 78 `Views/Inventory/*.cshtml`, rolled up | `8bd83578299552499c9ca534a6009ecf8407bbb06286f56e420bfbc634943b2e` |

```bash
# re-run at final review; any difference is a change to the visual AUTHORITY and must be justified
sha256sum CrossBuy/Views/Inventory/*.cshtml | sha256sum
sha256sum CrossBuy/Views/Shared/_LayoutInventory.cshtml \
          CrossBuy/wwwroot/Backend-assets/css/crossbuy-brand.css
```

A drift here is not automatically a failure — the Inventory owner may legitimately change their own module —
but it **must be surfaced**, because every other module is being measured against it. Silently re-baselining
would make "matches Inventory" unfalsifiable.

All three builds are **fresh** (obj/bin cleared for the measured configuration) and carry **no exclusions**: a
measurement that hides a file cannot certify the tree.

### 5.1 Fresh builds — all three configurations

```bash
# TestRun is a DECLARED configuration that defines DEBUG (CrossBuy.csproj:9-22). It exists because
# VS/IIS Express lock bin\Debug. It must be built: an undeclared TestRun once compiled a DIFFERENT
# program than Debug, and every acceptance run against it was measuring the wrong binary.
for C in Debug Release TestRun; do
  dotnet build CrossBuy/CrossBuy.csproj -c $C --no-incremental --nologo
done
```

Pass = **0 errors in each of the three**, with nothing excluded. Any error is attributed to its owning tab
before it is called a failure.

### 5.2 Zero active references to the legacy stylesheet

```bash
grep -rn "crossbusiness-workspace.css" CrossBuy/
```

Pass = the file's **own LEGACY banner only**. Any `<link>` in any view, layout or partial fails the gate.

### 5.3 Zero active references to the legacy layout

```bash
grep -rn "_LayoutWorkspace" --include=*.cshtml --include=*.cs CrossBuy/
```

Pass = no `Layout = ` assignment and no `PartialAsync`/`RenderPartial` naming it. Comment prose is noted, not
failed.

### 5.4 W-3 sweep — every `cbw-*` surface

Run all five commands in §W-3. Pass = **clean on every surface**, including JavaScript, bundles and data
hooks — not Razor alone.

### 5.5 Inventory visual language — across every migrated module

```bash
grep -n "Layout = " CrossBuy/Views/Workspace/*.cshtml \
                    CrossBuy/Views/Reports/*.cshtml \
                    CrossBuy/Views/BusinessEventMonitor/*.cshtml   # expect: all _LayoutInventory.cshtml
```

State at baseline (09:33) — all three already conform, to be re-confirmed at final review:

| Module | Views | Layout | Status |
| --- | --- | --- | --- |
| Workspace | 5 | `_LayoutInventory.cshtml` | ✅ (owner still writing) |
| Reporting | `Index`, `Viewer` | `_LayoutInventory.cshtml` | ✅ own layout deleted, `<link>` stripped 09:26:38 |
| Business Event Monitor | `Index` (+ `_Rows`, `_Details` partials) | `_LayoutInventory.cshtml` | ✅ |

Partials (`_Rows.cshtml`, `_Details.cshtml`) declare no layout by design — they inherit the parent's. Verify
their **component vocabulary** by eye instead.

**Inventory itself must be unchanged** — re-run §5.0.1 and report any drift in the authority.

Then confirm by eye against `/Inventory/Index` that the component vocabulary of §2.2 is what is on screen:
`app-toolbar` · `card card-flush` · `card-header`/`card-title`/`card-label`/`card-toolbar` ·
`table table-row-dashed align-middle fs-6 gy-3` · `symbol` + `symbol-label bg-light-*` · `badge badge-light-*` ·
`btn btn-sm btn-light` · `alert` · `ki-outline`. **If any Workspace screen can be visually distinguished from
the Inventory module, verification fails.**

### 5.6 Communication still renders correctly

No Communication visual work was required or performed — `Views/Comm/*` was already Metronic on
`_LayoutBackend`. This step is a **regression check**, confirming the Workspace migration did not disturb it:

```bash
grep -n "Layout = " CrossBuy/Views/Comm/*.cshtml           # expect: unchanged, _LayoutBackend.cshtml
grep -c "cbw-" CrossBuy/Views/Comm/*.cshtml                # expect: 0 in every file
```

Then open `/Comm` (Index), `/Comm/Compose` and a `/Comm/View/{id}`: folder rail with All/Outbox/Sent/Failed
counts, the message list, and compose — all rendering, in Arabic and English.

### 5.7 No functionality regression

Every section present before the migration must still be present, in both languages:

Metrics · Quick Actions · My Work · Unified Agenda · Recent Activity · Notifications · Mentions ·
Favourite Reports · Recent Reports · unresolved-session guard · unavailable-panels summary · section anchors
(`#quick-actions`, `#my-work`, `#agenda`, `#activity`).

**And the state rule that matters most:** `/Workspace/Mentions` must still read as *unavailable* — visibly
different from *empty*. A deployment with Communication switched off must never look like a quiet week.

```bash
dotnet test CrossBuy.Tests/CrossBuy.Tests.csproj -c Debug --nologo
```

### 5.8 Recommendation gate — this tab deletes NOTHING

**Only after every check in §5.1–§5.7 passes** may `crossbusiness-workspace.css` be **recommended** for
deletion. The recommendation is issued to the **Workspace owner**, who performs the removal in a **dedicated
cleanup commit**. This tab does not delete, and does not stage a deletion for someone else to press.

If any check fails, no recommendation is issued and the failure is logged as a new review item with exact
`file:line`, in the form of W-1…W-3.

Until then the stylesheet is a **temporary compatibility artifact** and stays exactly where it is.

### 5.9 Full verification matrix

| # | Check | Section |
| --- | --- | --- |
| 1 | Fresh **Debug** build | 5.1 |
| 2 | Fresh **Release** build | 5.1 |
| 3 | Fresh **TestRun** build (declared config, defines `DEBUG`) | 5.1 |
| 4 | **No exclusions** in any build | 5.1 |
| 5 | **No incremental** build (`--no-incremental`) | 5.1 |
| 6 | **Inventory remains unchanged** (baseline hashes) | 5.0.1 |
| 7 | Workspace uses the Inventory visual language | 5.5 |
| 8 | Reporting uses the Inventory visual language | 5.5 |
| 9 | Business Event Monitor uses the Inventory visual language | 5.5 |
| 10 | Communication still renders correctly | 5.6 |
| 11 | No functionality regression | 5.7 |
| 12 | No active reference to `_LayoutWorkspace` | 5.3 |
| 13 | No active reference to `crossbusiness-workspace.css` | 5.2 |
| 14 | No active `cbw-*` runtime dependency (JS · bundles · layouts · partials) | 5.4 / W-3 |
| 15 | **No second design language** — the conclusion the other 14 support | 5.2-5.5 |

---

## 6. Verification performed for this review

| Check | Result |
| --- | --- |
| `dotnet build CrossBuy/CrossBuy.csproj -c Debug`, **no exclusions** | **0 errors** |
| Live `.cbw-*` class attributes in `Views/Workspace/*` | **0** |
| Workspace views on `_LayoutInventory` | **5 of 5** |
| Legacy-stylesheet `<link>` referrers | **1** (`_LayoutWorkspace.cshtml:56`, orphaned) |
| Reporting referrers outstanding | **0** — migrated during this window |
| `Views/Comm/*` — already Metronic (`card card-flush`, `alert alert-*`, `badge badge-light-*`, `menu-*`) on `_LayoutBackend` | **no conversion needed** |

**Communication is closed.** It requires no visual work, and no further Communication UI change is required or
authorised. It appears in §5.6 purely as a regression check.

---

## 7. Standing posture

This tab is in **review mode** and stays there.

| | |
| --- | --- |
| Will do | review, verification, evidence, precise file:line findings |
| Will **not** do | modify any UI file · perform cleanup · delete the stylesheet · remove or restore the partial · touch `_LayoutWorkspace.cshtml` |
| Owned by the Workspace owner | `Views/Workspace/*` · `Views/Shared/_LayoutWorkspace.cshtml` · `Views/Shared/_WorkspacePanelState.cshtml` · `wwwroot/Backend-assets/css/crossbusiness-workspace.css` |
| Also not to be modified | any Reporting file · any shared layout |
| Next action | **wait.** Execute §5 only when all three §5.0 declarations have landed. Nothing before that. |

### 7.1 Status board

| Track | Status |
| --- | --- |
| **Communication** | **COMPLETE** — no visual work required, none performed, none authorised |
| **Workspace** | WAITING FOR FINAL REVIEW |
| **Reporting** | WAITING FOR OWNER |
| **Platform** | WAITING FOR FINAL INTEGRATION REVIEW |
| **This tab** | **IDLE — review mode.** Resumes only on request, once §5.0 is satisfied |

### 7.2 Authority of this document

This is the **authoritative review record**. W-1, W-2 and W-3 (§4) are accepted exactly as documented and are
review items only — no cleanup is authorised against them. Any later finding is appended in the same form, with
exact `file:line`, and never as a silent fix.

---

# 8. ADDENDUM — Final UI Conformance Review mandate

**§1-§7 are FROZEN as accepted.** Baseline hashes, protocol, W-items and matrix stand unchanged. This addendum
records a **superseding mandate**; it does not edit the frozen core.

## 8.1 Role

**Final UI Conformance Reviewer. Verification only.** No longer an implementation owner. Production code is not
modified unless ownership is explicitly transferred.

**Do not modify:** `Views/*` · layouts · CSS · JavaScript · Resources · controllers · services · Reporting ·
Workspace · Communication · Inventory.

## 8.2 Wait conditions — SUPERSEDES §5.0

| # | Declaration required | Status |
| --- | --- | --- |
| 1 | **TAB-1** declares **Platform** complete | ⏳ waiting |
| 2 | **TAB-2** declares **Reporting** complete | ⏳ waiting |
| 3 | **TAB-4** declares **Tasks/Calendar** complete | ⏳ waiting |

The §5.0 set (Workspace / Reporting / Integration owners) is replaced by the three above. Nothing executes
until all three land.

## 8.3 Tasks & Calendar baseline — recorded 2026-08-09 09:42

Added for the same reason as §5.0.1: *"remain visually consistent"* is unverifiable without a reference point
captured before the owner finishes.

| Artefact | Value |
| --- | --- |
| `Views/Tasks/*.cshtml` — 5 views, rolled up | `6b7018b53a4c57f8c412242fed9953b34124f10e5781e1fb65c3b6544c87dc6f` |
| `Views/Calendar/*.cshtml` — 1 view, rolled up | `4d1db0751b278731981735bfed82cde66c6dc9bd3b0dd0d66fd6c1620002e57f` |
| Layout, all 6 views | `~/Views/Shared/_LayoutInventory.cshtml` — already conforming |
| `cbw-*` occurrences | **0** |

## 8.4 Final review — full check list

| # | Check | Method |
| --- | --- | --- |
| 1 | Inventory unchanged from recorded baseline | §5.0.1 hashes |
| 2 | Workspace matches Inventory | §5.5 |
| 3 | **Reports Center** matches Inventory | `Views/Reports/Index.cshtml` |
| 4 | **Report Viewer** matches Inventory | `Views/Reports/Viewer.cshtml` — checked **separately**; it renders report output, the surface most likely to carry its own styling |
| 5 | Business Event Monitor matches Inventory | `Index` + `_Rows`/`_Details` partials by vocabulary |
| 6 | Communication still renders correctly | §5.6 |
| 7 | Tasks and Calendar visually consistent | §8.3 baseline |
| 8 | No second design language | §5.2-5.5 |
| 9 | No orphaned runtime references | §5.3, §5.4, W-3 |
| 10 | No functionality regression | §5.7 |
| 11 | **No layout regression** | §8.5 |
| 12 | **No responsive regression** | §8.5 |
| 13 | **No RTL regression** | §8.5 |
| 14 | **No accessibility regression** | §8.5 |
| 15 | Fresh Debug · Release · TestRun, no exclusions, no incremental | §5.1 |

## 8.5 Method for checks 11-14 — stated in advance, honestly

Layout, responsive, RTL and accessibility regressions **cannot be established by grep or by a build**. They
require rendering. The method, declared now so the final verdict cannot rest on an assumption:

1. **Headless render** of each route (`/Workspace`, `/Workspace/Agenda|Reports|Notifications|Mentions`,
   `/Reports`, `/Reports/Viewer`, `/BusinessEventMonitor`, `/Comm`, `/Tasks`, `/Calendar`, and `/Inventory` as
   the control) at **1440 · 992 · 390**, in **ar** and **en** — capturing console errors, failed requests and
   screenshots.
2. **RTL** — confirm `dir="rtl"` under `ar`, sidebar and drawer mirror, and that **one** stylesheet serves both
   directions.
3. **Accessibility** — keyboard focus visible on every interactive control, `aria-label` on icon-only controls,
   heading order descending, `prefers-reduced-motion` honoured.
4. **Control comparison** — every screen is judged against `/Inventory/Index` rendered in the same run, not
   against memory.

**If the app cannot be run and rendered at final-review time, checks 11-14 are reported as NOT VERIFIED** —
never as passed. A verdict of PASS that silently skipped four checks would be worth less than a FAIL.

## 8.6 Output contract — exactly one of two conclusions

### PASS
Issued only when **every** check in §8.4 passes. Accompanied by a **cleanup recommendation only**:
`crossbusiness-workspace.css` recommended for deletion by the **Workspace owner** in a dedicated cleanup commit.
**Cleanup is never performed by this reviewer** — no deletion, and no deletion staged for another to press.

### FAIL
Issued when any check fails. Every issue listed with all five fields, no exceptions:

| Field | Requirement |
| --- | --- |
| **File** | repository-relative path |
| **Line** | exact line number |
| **Reason** | what rule it breaks and how it manifests |
| **Severity** | Critical (second design language / functional regression) · High (visible layout, RTL or a11y regression) · Medium (orphaned reference, inconsistent component) · Low (comment or dead attribute) |
| **Owner** | the tab that owns the file |

No third verdict. "Mostly passing" is a FAIL with a list.

## 8.7 State

| | |
| --- | --- |
| Role | **Final UI Conformance Reviewer** |
| State | **FROZEN** |
| Waiting on | **TAB-1** (Platform) · **TAB-2** (Reporting) · **TAB-4** (Tasks/Calendar) |
| Authorised implementation work | **none** |
| Next action | idle until all three declare, then execute §8.4 and return PASS or FAIL |

The single write this tab made to the Workspace slice — the LEGACY banner comment at the head of
`crossbusiness-workspace.css` — was made under explicit instruction, is comment-only, and altered no selector,
property or value.
