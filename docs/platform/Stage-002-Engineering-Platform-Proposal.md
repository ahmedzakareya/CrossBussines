# Stage 2A — CrossBuy Engineering Platform — Proposal

**Analysis only. Nothing implemented.** Covers P0-3 (analyzers) and P0-4 (CI evidence guardrails).

---

## 1. Scope recommendation — build TWO guardrails, not twenty analyzers

The brief lists 20 analyzers. **Only two are backed by evidence from Stage 1, and I recommend Stage 2A ship exactly
those two.**

| Evidence | Guardrail it justifies |
|---|---|
| The scanner reported **38 of 40** protected endpoints as unprotected because its detector was three regexes | **Authorization Analyzer** (#1) |
| **46 SQL tests reported "skipped"** for two batches and were counted as coverage; first execution failed with 20 errors | **Test-Evidence Analyzer / CI policy** (#20) |

The other 18 are catalogued in §7 with a trigger condition each. Building them now would be speculative, and
speculative analyzers have a specific failure mode worth naming: **false positives teach developers to suppress
diagnostics**, after which the real ones are suppressed too. An analyzer with no defect history behind it is a
liability. They should be added when a defect makes the case — which is exactly how #1 and #20 earned their place.

---

## 2. The Authorization Analyzer

### 2.1 Goal

Fail the build when a **mutating controller action** does not reach an **approved authorization authority**.

### 2.2 Diagnostics

| ID | Title | Default severity |
|---|---|---|
| **CBA001** | Mutating action reaches no authorization authority | **Error** (after rollout) |
| **CBA002** | Mutating action's company comes from a compile-time constant | Warning → Error in 2B |
| **CBA003** | JSON-returning action uses a redirecting permission attribute | **Error** |
| **CBA004** | `[SessionValidation]` / `PosLaneActivityGuard` is the only guard present | Warning |
| **CBA005** | Authorization authority reached only after a write in the same method | Warning |
| **CBA006** | Suppression present with no baseline entry and no justification | **Error** |

CBA003 and CBA005 encode two defects Stage 1 actually found: the 302-to-HTML denial that reads as success, and the
leave chain where the authorization answer arrived after rows were written.

### 2.3 What counts as an authority (semantic, not textual)

Resolved through Roslyn's **symbol model**, which is the whole point of replacing the PowerShell scanner:

* a call to any type implementing `IModuleAccessService` (interface **or** implementation — the syntactic scanner cannot do this);
* `IPlatformPermissionProvider.CanAsync`;
* `IAccountingApiAuthorization.AuthorizeAsync` (Hotfix A.1 shape);
* `IPlatformRoleDirectory.RolesAsync`;
* an attribute in the approved set — `AccPerm`, `InvPerm`, `CrmPerm`, `HrPerm`, `ProjectPerm`, `PosPerm`,
  `PlatformOps`, `ApiPerm`, and future `TaskPerm`/`CommunicationPerm`;
* **transitively**, any method whose body reaches one of the above.

### 2.4 Supported patterns — all of which exist in the codebase today

| Pattern | Example | Resolution |
|---|---|---|
| Action-level attribute | `[AccPerm("post")]` | attribute symbol |
| Controller-level attribute | class-level `[InvPerm]` | inherited attribute |
| API guard attribute | `[ApiPerm(acc,"post")]` | attribute symbol |
| Direct in-body call | `_access.CanSell(c.Roles)` | invocation symbol |
| **Shared private gate** | `GateAsync` / `HrGateAsync` / `PosGateAsync` / `TaskGateAsync` | intra-type call graph |
| **Property-resolved service** | `HrAccess.CanAsync(...)` where `HrAccess` is a `RequestServices` property | property → symbol |
| **`Func<T>` deferred** | `_permissions().CanAsync(...)` | invocation on invocation |
| Interface call | `ITasksAccessService.CanAsync` | interface symbol |
| Base class | a future `AuthorizedController` | base-type walk |
| Cross-file helper | a static gate in another file | compilation-wide symbol graph |

The last three are precisely what the current scanner **cannot** do, and are the reason a Roslyn analyzer is the
recommended entry point rather than more PowerShell.

### 2.5 Call-graph resolution

Bounded, deterministic, and honest about its limits:

* build a call graph over the compilation, from each mutating action;
* **depth cap 5** (Stage 1's deepest real chain is 2: action → gate → access service);
* cycle-guarded (Stage 1 hit three DI cycles; the analyzer must not repeat that shape);
* virtual/interface calls resolve to **all** implementations in the compilation — if **any** implementation
  authorizes, the call is credited. This is deliberately generous: an analyzer that produces false *positives* gets
  suppressed, so it must err toward silence on ambiguity and let the baseline carry the residue.

### 2.6 False-positive controls

1. **Generous ambiguity rule** (§2.5).
2. **Explicit non-mutating opt-out** — `[AuthorizationNotRequired("reason")]` for the two genuinely anonymous login
   actions. Requires a non-empty reason string; CBA006 fires without one.
3. **Analyzer unit tests are mandatory per pattern** in §2.4 — 10 positive and 10 negative cases minimum, using
   `Microsoft.CodeAnalysis.Testing`. The analyzer is itself the instrument; Stage 1's lesson is that an unverified
   instrument is worse than none.
4. **Dogfood gate:** the analyzer must reproduce the known-good split — **157 attribute + 88 in-body + 143 backlog** —
   on the current tree before it is allowed to fail any build. If it disagrees with the verified scanner, the analyzer
   is wrong until proven otherwise.

### 2.7 Rollout without breaking 143 endpoints on day one

A **baseline file**, `engineering/authorization-baseline.json`, listing the 143 known-unprotected actions by
`Controller.Action`:

| Rule | Effect |
|---|---|
| Action **in** baseline | diagnostic suppressed, reported as **known debt** |
| Action **not** in baseline | **build fails** |
| New action added to baseline | **CI rejects the commit** — the baseline may only shrink |
| Action fixed | removed from baseline; a test asserts the count never rises |

`Stage1PermissionBacklogTests` already pins 143, so the baseline and the pinned test guard each other. Rollout:
Warning-only for one iteration → dogfood check → Error. **Developers cannot silently add an unprotected endpoint**,
which is the actual requirement.

### 2.8 CI integration

Runs in the normal build (`dotnet build`) so a developer sees it locally before pushing; `TreatWarningsAsErrors` is
**not** used globally — only these diagnostic IDs are escalated, so unrelated warnings do not gate the build.

---

## 3. P0-4 — CI and test-evidence guardrails

The most important item in this proposal after the analyzer, because it addresses the failure that made a whole
category of evidence fictional for two batches.

| # | Requirement | Proposed mechanism |
|---|---|---|
| 1 | `CROSSBUY_TEST_SQL` configured in CI | Pipeline variable pointing at the **instance** (no catalog). Verified reachable in a pre-step. |
| 2 | Required SQL tests fail if skipped | A `[RequiredEvidence]` trait on the 14 F4 proofs + the concurrency suites; a post-run assertion fails the build if any trait-carrying test reports Skipped. |
| 3 | Production DB names rejected | Already implemented (`ForbiddenCatalogs`). Add an assertion test so the guard itself is tested. |
| 4 | Unique scratch names | Already implemented (`CrossBuyPlatformTest_<guid>`). |
| 5 | Always dropped | Already implemented (`DisposeAsync`). |
| 6 | Leftovers fail acceptance | New CI step: `SELECT COUNT(*) FROM sys.databases WHERE name LIKE 'CrossBuyPlatformTest_%'` must be **0**. Phase 0 ran this manually — it returned 0. |
| 7 | Reports distinguish 4 states | `--logger trx`; parse Passed/Failed/**Skipped**/**NotDiscovered** separately. NotDiscovered matters: a test that fails to load is currently invisible. |
| 8 | Critical evidence never skipped | Enforced by #2. |
| 9 | Razor enabled in final acceptance | CI acceptance job builds **without** `RazorCompileOnBuild=false`. Phase 0 verified this now passes: **0 errors, 763/763**. |
| 10 | Razor-disabled = diagnostic only | The flag is permitted only in a job explicitly named `diagnostic`; an acceptance job carrying it fails. |
| 11 | Multiple identical runs | Acceptance runs the suite **3×**; differing pass counts fail the build (flake detection). |
| 12 | `TestRun` explicit | Assert the configuration defines `DEBUG` — the exact defect Stage 0 found when `TestRun` compiled a different program than Debug. |

### 3.1 The EF cascade-path debt — recommended treatment

**Facts:** `GenerateCreateScript()` is rejected by SQL Server (`FK_Branches_CountriesLookup_CountryID`, multiple
cascade paths). Production schema is hand-written idempotent SQL. Unit tests run on SQLite, which does not enforce the
restriction. **No production database was ever created from that script**, so this is *relational-model validation
debt*, not a schema defect.

**Do NOT** change cascade behaviour to make the script pass. Cascade rules affect deletion semantics in a codebase
whose correction primitive is *reverse, never delete* — altering them to satisfy a DDL generator would be changing
production behaviour to fix a test.

**Recommended, in order:**

1. **Measure first (2A):** a test that generates the script and reports **every** rejected constraint. Right now one
   is known; the true count is unmeasured, and acting on a sample of one would repeat the mistake Stage 1 made twice.
2. **Compare model to database (2A):** assert the EF model's FK/cascade configuration against `sys.foreign_keys` on a
   deployed scratch DB. Divergence becomes visible instead of theoretical.
3. **Decide per constraint (2B), with an owner:** each divergence is either a model bug (fix the model) or a
   deliberate DB choice (annotate the model to match, `OnDelete(NoAction)`). Never a blanket change.
4. **Keep FK-stripping in the F4 fixture** until (3) completes, and keep it declared in the report rather than quietly
   relied upon.

---

## 4. Analyzer #20 — Test-Evidence Analyzer

Compile-time complement to the CI policy: flags a `[SkippableFact]` whose skip condition can never be false in CI
(**CBE001**), and a test asserting only `Assert.True(true)`-class tautologies (**CBE002**). Both are cheap and both
target the "green but vacuous" failure mode Stage 1 hit.

---

## 5. Suppression policy

* Suppression is **only** via the baseline file or `[AuthorizationNotRequired("reason")]`.
* `#pragma warning disable CBA*` is **banned** — enforced by a grep test, the same mechanism that already enforces the
  `IgnoreQueryFilters` ban.
* Every baseline entry carries `addedBy`, `date`, `reason`, `targetPhase`.
* CI diffs the baseline: **additions rejected, removals celebrated.**

---

## 6. Analyzer Dashboard (screen)

One screen in 2A: current diagnostics, baseline size and its trend, per-module debt, and CI evidence status
(Passed/Failed/Skipped/NotDiscovered + leftover scratch DBs). It makes the baseline's shrinkage visible, which is what
stops it becoming permanent. Detail in `Stage-002-Screen-Forecast.md`.

---

## 7. The other 18 analyzers — catalogued with trigger conditions

Each stays unbuilt until its trigger fires. This is the discipline that keeps the guardrail set trustworthy.

| # | Analyzer | Build when |
|---|---|---|
| 2 | Company Isolation | after the Class A company-source sweep, to hold the line |
| 3 | BusinessContext | folded into CBA002 |
| 4 | API Authorization | folded into CBA003 |
| 5 | Anti-Forgery | 38 mutating MVC actions lack anti-forgery — measure, then decide |
| 6 | Transaction Boundary | when a second writer or a nested-tx defect appears |
| 7 | BusinessEvent | when >5 entities are onboarded to events |
| 8 | Outbox/Dispatcher | when a second consumer exists |
| 9 | SQL Safety | partly covered by `Stage1RawSqlSafetyTests` |
| 10 | Raw SQL / Locking | when a second locking path appears |
| 11 | Workflow Integration | with the workflow engine (2B+) |
| 12 | Task-Linking | when >3 modules create tasks |
| 13 | Communication Integration | when entity-scoped threads exist |
| 14 | File Authorization | **with the unified attachment framework — three file stores exist today, so this one is close** |
| 15 | Background-Worker Scope | 8 workers, 0 hardcoding company 1 — a test already covers it |
| 16 | Dependency/Layering | if a controller ever writes the GL directly |
| 17 | API Contract | when a public API is versioned |
| 18 | Localization | measure hardcoded user-facing strings first |
| 19 | UX Convention | only where build-time validation is practical — likely just Tag Helper usage |

---

## 8. Effort and risk

| | |
|---|---|
| Authorization analyzer + tests + baseline | the bulk of 2A |
| CI policy | small; mostly pipeline configuration |
| EF model measurement | small (two tests) |
| Dashboard | one screen |
| **Principal risk** | **the analyzer is wrong and blocks the build.** Mitigated by: Warning-only first iteration, the dogfood gate against 157/88/143, generous ambiguity handling, and mandatory per-pattern unit tests. |
| **Secondary risk** | the baseline becomes permanent. Mitigated by CI rejecting additions and the dashboard making the trend visible. |
| **User-visible value** | **none, honestly stated.** 2A is a developer-facing phase. Its value is that Waves 2–6 become self-verifying instead of self-reported. |
