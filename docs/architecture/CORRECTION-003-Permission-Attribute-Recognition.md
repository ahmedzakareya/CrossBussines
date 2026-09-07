# CORRECTION-003 — The security backlog is 189, not 306

**Status:** Accepted. Issued during Stage 1, Phase 1 (pre-implementation analysis).
**Severity of the error:** **High.** The published Stage 1 security backlog was **overstated by 117 actions (62%)**.
The previous figure was used to size Stage 1 and to justify not remediating it in Stage 0.

Extends [CORRECTION-002](CORRECTION-002-Evidence-Scanner-Defects.md), which recorded defects 1–5.
This records **defects 6 and 7**, both found during Stage 1's Phase 1 inspection, before any Stage 1 code was
written.

---

## What was claimed

CORRECTION-002 and `13-Security-Authorization-Isolation.md` reported:

> **306 of 381 mutating actions carry no action-level module permission (80%).** 305 carry none at any level.

## What is actually true

| Metric | CORRECTION-002 | **Corrected (final)** |
|---|---|---|
Controller actions | 1054 | **1054** |
Mutating actions (POST/PUT/DELETE/PATCH) | 381 | **384** |
— with a module permission at **any** level | 76 | **195** |
— **without** a module permission at any level | **305** | **189** |
— without an **action-level** module permission | 306 | **234** |
Actions carrying `AccPerm` | 20 counted | **55** |
Actions carrying `InvPerm` | 74 | **74** |
Actions carrying `CrmPerm` | **0** | **37** |
Actions under class-level `PosLaneActivityGuard` | 0 | **71** |
Actions under class-level `DevOnly` | 0 | **224** |

**Inventory/Manufacturing is not the only well-protected module.** Accounting carries 55 permission attributes and
CRM carries 37 — none of which the scanner could see.

---

## Defect 6 — fully-qualified attribute names were not recognised

This codebase writes attributes in **both** forms, frequently in the same file:

```csharp
[HttpGet][InvPerm("read")] public async Task<IActionResult> WorkOrders()          // short form
[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.AccPerm("post")]            // fully qualified
[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]            // fully qualified
```

`Get-AttributeName` reduced `[CrossBuy.Models.AccPerm("post")]` to the string `CrossBuy.Models.AccPerm`, which is not
in the recognised-permission list, so the attribute was discarded and the action recorded as unguarded.

Raw counts in source: **77** `[InvPerm`, **46** `[CrossBuy.Models.AccPerm`, **9** `[AccPerm`,
**37** `[CrossBuy.Models.CrmPerm`, **2** `[CrossBuy.Models.PosLaneActivityGuard`, **2** `[PlatformOps]`.
**85 of 173 permission attributes — 49% — were invisible.**

Fixed by stripping any namespace qualifier before matching (take the segment after the last `.`).

**Two real guards were also missing from the recognised list entirely:**

- `PosLaneActivityGuard` — an `ActionFilterAttribute` that whitelists which activity preset a cashier lane may
  serve. It is applied at class level and governs **71** actions. Omitting it counted every POS lane action as
  unguarded.
- `DevOnly` — an `ActionFilterAttribute` that returns 404 outside Development. It governs **224** `DevSeedController`
  actions.

## Defect 7 — an attribute line with a trailing comment aborted the walk

```csharp
[CrossBuy.Models.DevOnly]   // SECURITY: all seeding/test/reset endpoints return 404 outside Development
public class DevSeedController : ControllerBase
```

`Get-AttributesAbove` tested `line.EndsWith(']')` to decide whether a line is an attribute. With a trailing comment
it does not, so the upward walk **aborted at the first line** and `DevSeedController` was recorded with **no class
attributes at all** — including no `[DevOnly]`.

This is the same failure family as CORRECTION-001's defect 1: an assumption about attribute *layout*, silently
returning a smaller answer. Fixed by stripping a `//` comment that occurs after the final `]` on the line
(a `//` inside an attribute argument is left alone).

---

## Corrected backlog: 189 mutating actions with no module permission

| Controller | Count | Note |
|---|---|---|
`AdminController` | **42** | no HR RBAC service exists at all |
`PosController` | **42** | POS setup/config; the *lane* actions are covered by `PosLaneActivityGuard`, these are not |
`ProjectController` | **30** | no Projects RBAC service exists |
`TasksController` | **13** | no Tasks RBAC service exists |
`AccountingApiController` | **10** | API surface; `AccPerm` is applied on the MVC controller, not here |
`ChatController` | **9** | |
`FileManagerController`, `PeopleController` | 5 each | |
`AiController`, `CommController` | 4 each | |
`AnnouncementsController`, `BrandController` | 3 each | |
`ServiceController`, `AccountController`, `CommentsController`, `LeaveApiController`, `HrApiController`, `AccountingController`, `NotificationsApiController`, `CalendarController` | 2 each | `AccountingController`'s remaining 2 are the only accounting writes without `AccPerm` |
`NotificationsController`, `AuthApiController`, `InventoryController` | 1 each | `AuthApiController` is intentionally anonymous (login) |

By module: **HR 47 · POS 42 · Projects 30 · Communication 28 · Identity 14 · Tasks 13 · Other 7 · AI 4 · Retail 3 ·
Inventory 1**.

**The shape of the problem changed, not just its size.** The backlog is now concentrated in the four modules that
have **no access service at all** — HR, Projects, Tasks and the API surface — rather than spread across Accounting
and CRM, which turn out to be substantially covered. That materially changes Stage 1's remediation sequencing: the
fix for 132 of the 189 is *building three missing access services*, not *adding attributes*.

---

## A separate finding, recorded here because the same scan surfaced it

**`DevSeedController` exposes 224 `[HttpGet]` endpoints and zero `[HttpPost]`** — 224 GET endpoints that seed, reset
and mutate data, under `[AllowAnonymous]`. They are mitigated **only** by `[DevOnly]` returning 404 outside
Development.

This is why the original discovery's `.Add(`-based heuristic reported "176 unguarded writes" here: they are real
mutations, but they are GET requests, so a verb-based mutating count correctly excludes them. Both statements are
true and neither is the whole picture:

- On the HTTP-verb basis they are **not** in the 189 backlog, and should not be — a GET is not a mutating verb.
- As a security matter, **mutating via GET is worse than a missing permission attribute**: it is CSRF-able by an
  `<img>` tag, cacheable, and pre-fetchable.

`[DevOnly]` is a genuine control and it is correctly applied. The residual risk is a single attribute, and a single
`ASPNETCORE_ENVIRONMENT=Development` on a production host. **Recorded as a risk, not remediated in Stage 1** — it is
out of the stated scope, and moving 224 endpoints to POST is a change to a dev-only surface with no security benefit
while `[DevOnly]` holds.

---

## Reconciliation performed (ADR-016 rule 2)

Every scanner count was compared against an independent raw-grep count before publication. All differences are
explained:

| Attribute | Scanner (distinct actions) | Raw grep (text occurrences) | Difference explained |
|---|---|---|---|
`AccPerm` | 55 | 55 | exact |
`CrmPerm` | 37 | 37 | exact |
`InvPerm` | 74 | 77 | **2** occurrences are inside comments written during Stage 0 Batch A; **1** is a duplicate in the reconciliation script's own crude extractor |
`PlatformOps` | 5 (inherited) | 2 (class-level) | class attribute × 5 actions on that controller |
`PosLaneActivityGuard` | 71 (inherited) | 2 (class-level) | class attribute × actions on those controllers |
`DevOnly` | 224 (inherited) | 1 (class-level) | class attribute × 224 actions |

---

## Documents corrected

| Document | Change |
|---|---|
`13-Security-Authorization-Isolation.md` | coverage table and backlog table replaced with the 189/194 figures |
`19-Current-Gaps.md` | **E1** restated at 189 |
`20-Architecture-Risks.md` | "missing permission enforcement" restated; `DevOnly`/GET-mutation risk added |
`21-Modernization-Roadmap.md` | Stage 1 backlog size and sequencing corrected |
`CORRECTION-002` | superseded on the backlog figures only; its defects 1–5 and its SQL findings stand |
`Platform-Maturity-Baseline.md` and the Stage 000/001/002 records | **NOT edited** — see below |
`evidence/Endpoint-Inventory.csv`, `Permission-Coverage.csv`, `Controller-Inventory.csv` | regenerated |

### The maturity records are deliberately left alone

Model rule **R8** makes stage records immutable, and rule **R7** requires a measurement correction to be disclosed
rather than retro-fitted. Stage 000/001/002 were all scored on the same (wrong) 306 basis, so the *deltas* between
them are unaffected — the error is a constant across the series.

**The absolute level of dimension 2 (Security and Company Isolation) was, however, scored too harshly.** On the
corrected basis, 194 of 383 mutating actions (51%) carry a module permission, not 20%. That is still "a minority
covered by a mechanism, with manual tenancy" — level 2 — so the *score* does not move, but the reasoning behind it
changes and the Stage 1 record will say so explicitly. The next stage record carries the corrected figures forward;
history stays as it was believed.

---

## Lesson recorded

This is the **third** correction in the same family, and the pattern is now unmistakable:

> Defects 1, 4, 6 and 7 are all *attribute-recognition* failures, and all four silently returned a smaller,
> plausible answer.

The countermeasure that actually worked, three times out of three, was **reconciling against an independently
derived count**. It has been what caught every one of these — never re-reading the regex. Rule 2 of ADR-016 is
therefore promoted from "do this before publishing" to **"a permission or isolation metric may not be published at
all without a stated reconciliation and an explanation for every difference"**, which is how the table above is now
formatted.

---

## Evidence and documents updated (Stage 1 Batch A)

Regenerated by `CrossBuy/deploy/scan-architecture.ps1` after both fixes, then reconciled:

| Artefact | State |
|---|---|
`evidence/Endpoint-Inventory.csv`, `Permission-Coverage.csv`, `Controller-Inventory.csv` | regenerated. **1054 actions · 384 mutating · 195 with a module permission · 189 without.** |
`13-Security-Authorization-Isolation.md` | coverage and backlog tables replaced with the 189/195 figures |
`19-Current-Gaps.md` | **E1** restated at 189 |
`20-Architecture-Risks.md` | permission-enforcement section restated; the `DevOnly` / GET-mutation finding added |
`docs/platform/Stage-001-Roadmap.md` | Batch C and Batch D sized on 189, with the 132-in-three-modules split |
`maturity/Stage-003-Stage-1-Batch-A-Result.md` | dimension 2 rescored, with the correction's contribution reported **separately** from Batch A's work (R7) |
Stage 000/001/002 maturity records | **not edited** (R8). All three were scored on the same wrong basis, so the deltas between them are unaffected. |

### A third scanner defect found during the same regeneration

`public static readonly (string key, string ar, string en)[] Capabilities = new[]` matched the action-declaration
pattern, because the **tuple type contains parentheses** and the captured "name" came out as the C# keyword
`readonly`. Two such rows existed in `PosController`. Fixed by excluding C# keywords and `public [static] readonly`
field declarations — the narrow fix, because excluding anything with `=` before `(` would also drop expression-bodied
actions.

### Reconciliation of the final numbers

| Check | Independent count | Scanner | Difference explained |
|---|---|---|---|
Mutating-verb attributes | **385** text occurrences of `[HttpPost` / `[HttpPut` / `[HttpDelete` / `[HttpPatch` | **384** | 1 is a **commented-out** `//[HttpPost]` at `AccountController.cs:183` |
`AccPerm` | 55 | 55 | exact |
`CrmPerm` | 37 | 37 | exact |
`InvPerm` | 77 | 74 | 2 occurrences are inside Stage 0 comments; 1 was a duplicate in the reconciliation script's own crude extractor |
Duplicate `(controller, action)` rows | 32 | 32 | all legitimate MVC GET/POST overload pairs (`Login`, `CreateItem`, `EditItem`, `CompanyDetails`, …) after the `readonly` false positives were removed |

**Batch A changed none of these numbers by remediating an endpoint** — it was explicitly scoped not to. The 189 is the
same before and after the batch.