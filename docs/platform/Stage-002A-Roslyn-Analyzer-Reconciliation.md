# Stage 2A — Batch 00-A — Roslyn Analyzer Reconciliation

**The analyzer reproduces the accepted Stage 1 baseline exactly. No number was forced and the baseline was not
changed.**

| Figure | Authoritative | Analyzer | Verdict |
|---|---|---|---|
| Mutating actions | 388 | **388** | match |
| Attribute protected | 157 | **157** | match |
| Verified in-body | 88 | **88** | match |
| Authorization debt | 143 | **143** | match |
| Controllers | 39 | **39** | match |
| False credits | 0 | **0** | match |
| Wave 1 protected | 40 / 40 | 40 / 40 | match (§6) |

The stronger result: agreement is **not merely at the count**. Compared endpoint by endpoint against the accepted
scanner's `Endpoint-Inventory.csv` — 388 identities, **identical sets**, and **zero classification disagreements**.

```
scanner rows 388 · analyzer rows 388 · id sets identical: True
only in scanner: []   only in analyzer: []
classification disagreements: 0
scanner : Attr 157 · InBody 88 · Gap 143
analyzer: Attr 157 · InBody 88 · Gap 143
```

Two independent derivations — a syntactic PowerShell text scanner and a semantic Roslyn call graph — agree on all
388 endpoints. That is the reconciliation, and it is a much stronger statement than four equal totals.

---

## 1. Source 1 — the Roslyn analyzer

Produced by `AuthorizationInventory` over a real semantic compilation of the application's own sources
(`RealSourceCompilation`), which reports **0 compilation errors**. That matters: an unbound access-service call
cannot be credited, so compilation errors would systematically **understate** coverage and **overstate** debt, and do
it silently. The error count is asserted, not assumed.

Evidence written by the same run that asserts the counts: `docs/architecture/evidence/Roslyn-Authorization-Inventory.csv`
(388 rows + header). Stage 1 lost a batch to documents quoting one scan while the CSVs came from another; the
inventory and the assertions now come from one execution.

## 2. Source 2 — `engineering/authorization-baseline.json`

| Check | Result |
|---|---|
| Frozen header vs analyzer | 388 / 157 / 88 / 143 — all four match |
| `count` vs actual entries | 143 = 143 |
| Gap set vs baseline ids | **identical, id for id** — 0 stale, 0 unlisted |
| Classifications | 141 `AuthorizationGap` + 2 `AnonymousByDesign` |

Asserted end-to-end: the real analyzer, over the real compilation, with the real baseline as an AdditionalFile,
reports **zero CBA001 and zero CBA004**. That is what "ready to enforce" means.

## 3. Source 3 — the modern PowerShell scanner

Re-run this increment (`CrossBuy/deploy/scan-architecture.ps1`):

```
controller files / distinct classes : 42 / 39
mutating actions (POST/PUT/DELETE)  : 388
mutating WITH a permission ATTRIBUTE at any level: 157
  ...of which authorized IN-BODY (access svc)   : 88
  ...with NO authorization this scan can see    : 143   <-- the backlog
CORRECTION-001 re-check — work-order write actions found: 9 of 9 expected
```

42 files, 39 classes: `AdminController` is partial across four files. The analyzer counts **types**, so it reports 39
directly — the scanner's two-pass merge exists to reach the same answer from text.

## 4. Source 4 — an independent raw grep inventory

Neither the scanner nor the analyzer; a plain mechanical count, reconciled to the digit.

### Verbs

| | Count |
|---|---|
| Bracketed `HttpPost`/`Put`/`Delete`/`Patch` occurrences under `Controllers/` | **391** |
| …of which inside a comment | **−3** |
| Real applications | **388** |

The three: `AccountController.cs:183` (`//[HttpPost]`, commented-out code) and prose comments at
`AdminController.cs:137` and `PosController.cs:38`. One `HttpPut` exists
(`HrApiController.cs:69`) and is a distinct action, not a second verb on a POST action.

This is CORRECTION-001's defect family **in reverse**: raw text counts comments, symbols cannot. The 3-occurrence
delta is the measurable value of semantic analysis.

### Permission attributes

| | Count |
|---|---|
| Bracketed permission-attribute occurrences | **178** |
| …inside a comment | **−4** |
| Real applications | **174** |
| …at class level | 1 (`[PlatformOps]` on `BusinessEventMonitorController`) |
| …at action level | **173** — matches the scanner exactly; 0 actions carry two |
| Action-level on **mutating** actions | 156 |
| Action-level on GET/read actions | 17 |
| **Mutating, attribute-protected** = 156 + 1 class-level-only | **157** ✓ |

Occurrence totals by attribute: `InvPerm` 77 · `AccPerm` 57 · `CrmPerm` 37 · `ApiPerm` 5 · `PlatformOps` 2.

## 5. Capability differences with a measured delta of zero

The analyzer is deliberately **more capable** than the accepted scanner in five ways. Each was measured; each moves
nothing, so capability was added without moving a published number.

| Capability | Scanner | Analyzer | Delta | Why zero |
|---|---|---|---|---|
| `TaskPerm`, `CommunicationPerm` recognised | no | yes | **0** | both attributes exist and are applied to **zero** actions |
| `AcceptVerbs` | no | yes | **0** | applied nowhere today |
| `[NonAction]` honoured | no | yes | **0** | used nowhere today |
| Cross-file / base-class / interface helper chains | no | yes | **0** | every real gate is in its controller's own file today |
| `IsActivityAllowedForLane` excluded from authority | credited via `_posAccess.` | excluded | **0** | its two call sites (`HyperPosController:94`, `PosAppController:100`) sit in helpers that also call a **real** authority, so the endpoint is credited on the real check |

The last row is the one worth noting: the scanner would have credited the lane predicate, and the analyzer refuses
to — and the count is unchanged only because those endpoints have genuine authorization as well. Had they not, the
analyzer would have found a false credit the scanner was carrying.

## 6. Wave 1 — 40 / 40

All 40 Wave 1 endpoints classify as protected, and the classification names the guard in every case (evidence CSV
columns `permission_attributes` / `in_body_authority`). No Wave 1 endpoint appears in the 143.

The in-body credit is concentrated in ten authority members, summing to exactly 88:

| Authority | Endpoints |
|---|---|
| `IPosAccessService.CanOrder` | 26 |
| `IProjectsAccessService.CanAsync` | 23 |
| `IPosAccessService.CanSell` | 11 |
| `IAccountingApiAuthorization.AuthorizeAsync` | 10 |
| `IHrAccessService.CanAsync` | 5 |
| `PosAccessService.CanAsync` | 4 |
| `IPosAccessService.IsManager` | 3 |
| `ITasksAccessService.CanAsync` | 3 |
| `IPosAccessService.ResolveByUserIdAsync` | 2 |
| `IPosAccessService.IsKitchen` | 1 |
| **total** | **88** |

## 7. False credits = 0, asserted structurally

Not counted by eye. Every credited endpoint must name either a permission attribute **from the declared set** or an
authority member; a credited endpoint naming neither fails the test. Additional assertions: the lane guard credits
nothing on its own, and `DevOnly` credits nothing on its own.

## 8. Diagnostic profile on the real tree

Generated: `docs/architecture/evidence/Roslyn-Analyzer-Diagnostic-Profile.csv`. Identical across two consecutive runs
of the same compilation (determinism is asserted — concurrent execution is enabled, and an analyzer whose output
depends on scheduling would make every future count a coin toss).

| Id | Count | Reading |
|---|---|---|
| CBA001 | **0** | no new debt — the tree is clean against the frozen baseline |
| CBA002 | **124** | of the 143 gaps, 124 carry authentication / anti-forgery / a lane guard and no permission |
| CBA003 | **0** | after the fix in §9 |
| CBA004 | **0** | no stale allowance |
| CBA005 | **0** | both anonymous mutating endpoints are accounted for (§9) |
| CBA006 | **0** | no suppressions exist |
| **total** | **124** | |

CBA002 = 124 is the most useful new fact this increment produces: **124 of the 143 debt endpoints are not
unguarded** — they are authenticated, CSRF-protected requests with **no permission decision**. The remaining 19
carry nothing at all.

## 9. Two findings from dogfooding

### 9.1 A CBA003 false positive — found and fixed

The first run produced exactly one CBA003, on `AdminController.PostFinalSettlement`, naming
`PermissionTarget.ForSubjectEmployee`. That is a **DTO factory** that builds the target of a check and performs
none; the endpoint was already correctly credited in-body.

Cause: the heuristic matched the **containing type's** name, and `PermissionTarget` contains "Permission". A
guardrail that warns about a data carrier, on an endpoint that is already protected, is a guardrail people switch
off. Fixed by matching the **method name only**, and `CanAsync` / `RolesAsync` were added to the fragment list so an
undeclared ninth access service is still caught. Both directions are pinned by tests.

### 9.2 An anonymous mutating endpoint that is genuinely authorized — verified, not assumed

`HyperPosController.Login` and `PosAppController.Login` are `[AllowAnonymous]` **POST** actions, and CBA005 stayed
silent. Read rather than trusted: both perform a password sign-in and then call
`_access.ResolveByUserIdAsync(user.Id)`, refusing when it returns null ("you do not have cashier permission for any
branch"), then apply a lane whitelist and a cross-company guard. That is a real authorization decision taken after
authentication, so the in-body credit is correct and CBA005 is right to stay silent.

The baseline's two `AnonymousByDesign` entries are different endpoints — `AccountController.Login` and
`AuthApiController.Login` — and both are in the 143.

## 10. One documentation inconsistency, reported and not "fixed"

`engineering/authorization-baseline.json` states in its own `rules` array that a stale entry is **"diagnostic
CBA007"**. The brief assigns **CBA004** to stale baseline entries and defines no CBA007.

The brief was followed: **CBA004** is implemented. The baseline file was **not edited** — it is frozen and
shrink-only, and rewriting its rules text is an owner decision, not a side effect of an analyzer increment. The
inconsistency is one line of prose in a file whose *data* reconciles perfectly. **Owner decision needed:** align the
prose to CBA004, or reserve CBA007 for a distinct rule.

## 11. Verdict

No reconciliation failure occurred, so the fallback instruction ("do not change the baseline; produce a
reconciliation report") did not need to be invoked — although this document is that report either way. Nothing was
forced: the counts were not tuned to fit, and the two capability decisions that *could* have moved a number
(excluding the lane predicate, refusing to descend into application services) were made on principle and then
measured.
