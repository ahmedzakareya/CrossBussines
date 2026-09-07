# Stage 1 — Finalization Report

**Scope:** finalize, verify, reconcile and document Stage 1. **No new business features, security features or
permissions were added.** Stage 2 has **not** started.

**Suite:** **749 total — 703 passed, 0 failed, 46 skipped.** All 46 skips are the `CROSSBUY_TEST_SQL`-gated SQL
Server tests, which report **skipped, never silently passed**.
**No SQL executed against `CrossBuyDB2` or any production database.**

| Item | Status |
|---|---|
| **F1 — Scanner modernization** | **Completed and verified** |
| **F2 — Evidence regeneration** | **Completed** — evidence now matches source exactly, no manual numbers |
| **F3 — Documentation reconciliation** | **Partial** — §3 |
| **F4 — SQL Server validation** | **Not Started** — §4 |
| **F5 — Company isolation audit** | **Completed** (measure-only, nothing fixed) — §5 |
| **F6 — Test quality audit** | **Partial** — audited and reported; no rewrites — §6 |
| **F7 — Performance verification** | **Partial** — §7 |
| **F8 — Final Stage 1 maturity** | **Blocked by F4** — §8 |

---

## 1. F1 — Scanner modernization (Completed, verified before use)

### 1.1 What was wrong

The scanner decided in-body authorization with **three hardcoded regexes** (`_access.Can*`, `CanAsync`,
`_guard.AuthorizeAsync`) and an attribute list that predated `ApiPerm`. Batch D1 Wave 1 then moved the
authorization call **out** of each action into shared private gates (`GateAsync`, `HrGateAsync`, `PosGateAsync`,
`TaskGateAsync`) — the refactor that stops a predicate being copied into 33 action bodies.

Result: the scanner reported **38 of 40 genuinely protected endpoints as unprotected**. A detector that punishes
the correct structure is a **measurement** defect, not a code defect.

### 1.2 What replaced it

Two changes to `deploy/scan-architecture.ps1`:

1. **`ApiPerm` added to the attribute list.** It asks the same access service for the same action as
   `AccPerm`/`InvPerm`; omitting it made 5 protected endpoints read as unprotected. (`PlatformOps` was already
   there and had been verified to check a real role.)
2. **In-body authorization is now resolved by call graph, not pattern.** Three functions:
   * `Test-DirectAuthorization` — does a body ask a **named authority** (access service, platform permission
     provider, the Hotfix A.1 guard, the role directory)?
   * `Get-AuthorizingHelpers` — which private methods in the file do, **transitively**, iterated to a **fixpoint**
     so a gate delegating to another gate is still recognised.
   * `Test-AuthorizingBody` — an action counts if it authorizes directly **or** calls such a helper.

   A method merely *containing* "Auth" is never credited — CORRECTION-004's lesson about crediting a control that
   checks nothing.

### 1.3 Verified BEFORE regenerating (as F1 requires)

| Check | Result |
|---|---|
| Script parses | **PARSE OK** |
| Reconciliation | **388 = 157 + 88 + 143** ✔ |
| All 40 Wave 1 endpoints detected as protected | **0 still unprotected** |
| False credits (in-body credited in a file with **no** authority call anywhere) | **0** |
| Agreement with independent source-derived count | **exact** — 157 / 88 / 143 both ways |

That last row is the point: the modernized scanner and the per-endpoint source inspection were produced by
different methods and now agree exactly. Before F1 they disagreed by 38.

### 1.4 Honest limitation — and why it is the Stage 2 starting point

F1 asked for **semantic analysis**. What was delivered is a **syntactic call graph over one file**, because the
scanner is PowerShell and has no compiler binding. It cannot follow an authorization call through an **interface**,
a **base class**, or **another file**, and cannot distinguish two same-named methods. It is a large improvement
over three regexes and still not semantic analysis. Declared here rather than implied — see §9.

---

## 2. F2 — Evidence regeneration (Completed)

Regenerated from source by the verified scanner: `Endpoint-Inventory.csv`, `Permission-Coverage.csv`,
`Controller-Inventory.csv`, `Worker-Inventory.csv`, `SQL-Script-Inventory.csv`.

**Final Stage 1 permission figures — no manually edited numbers:**

| Metric | Phase 1 baseline | **Final** | Δ |
|---|---|---|---|
| Mutating actions | 388 | **388** | 0 |
| Attribute-protected | 151 | **157** | +6 |
| Verified in-body protected | 54 | **88** | +34 |
| **Backlog** | 183 | **143** | **−40** |
| Critical remaining | 40 | **0** | **−40** |
| High remaining | 59 | **59** | 0 |
| Medium remaining | ~38 | ~38 | 0 |
| Low remaining | ~46 | ~46 | 0 |
| Concurrent tree additions/removals | — | **none** | — |

`388 = 157 + 88 + 143` reconciles, and `Stage1PermissionBacklogTests` is re-pinned to those figures and passes.
The **stale 151/54/183 and the interim 152/55/181 are superseded and must not be quoted.**

**A measurement artifact found and corrected twice.** Raw grep of `[HttpPost|HttpPut|…]` briefly read **391**
instead of 388, because my own explanatory comments in `AdminController` and `PosController` contain the literal
text `[HttpPost][ValidateAntiForgeryToken]`. Together with the pre-existing commented-out `//[HttpPost]` in
`AccountController.cs:183`, that is exactly 3 comment occurrences: `391 − 3 = 388`. The corrected method excludes
comment lines. The same artifact class recurred in F5 (§5.2) — grep counts text, not code, and the fix is to
exclude comments explicitly rather than to reconcile by subtraction after the fact.

---

## 3. F3 — Documentation reconciliation (Partial)

**Done:** every superseded report carries a **pointer** to its authoritative successor, and **none was rewritten** —
the immutable-stage-report rule is intact. The chain is: Batch C → C.1 → D1 Phase 1 → D1 Wave 1 (increment 1) →
increment 2 → Wave 1 completion → this report. Corrections are separate immutable records
(`CORRECTION-004`, `CORRECTION-005`).

**Not done — exact missing work:**
* A duplicate-statement sweep across ADR-001…029 (the shared-platform rules are restated in several ADRs and in
  `CLAUDE.md`; none is wrong, several are redundant).
* Diagram updates: no diagram yet shows `IRequestCompanyResolver` or the four gates.
* `Stage-001-Roadmap.md` still lists Wave 2–6 as "planned" without the Wave 1 outcome folded in.
* A **CORRECTION-006** record for the scanner-blindness finding in §1.1 — the finding is documented here and in the
  Wave 1 completion report, but not yet as a standalone correction record, which is where the measurement-defect
  pattern belongs.

*Reason:* capacity. *Impact:* documentation-only; no security or behavioural consequence. *Next step:* the four
bullets above, in that order.

---

## 4. F4 — SQL Server validation (Not Started)

**Exact missing work:** real-server tests on a disposable database for `UPDLOCK`/`HOLDLOCK` behaviour, same-row
stock concurrency, denied-transaction rollback integrity, refused stock movement leaving no balance change, and
refused financial posting leaving no journal — for `PostMaterialIssue`, `SaveMaterialIssue`,
`DeleteMaterialIssue`, `PrepareFinished`, `PrepareSemi`, plus the payroll-posting refusals.

**Reason:** `SqlServerFixture` creates the platform-slice tables only; these tests need stock and journal tables in
its scratch database, which is a fixture extension rather than a test addition.

**Impact:** the authorization claim is unaffected — all 40 endpoints are gated in production code and covered by
access-service and structural tests. What remains **unproven** is behaviour under **concurrency and refusal at the
database level**. `StockBalance` locking and its exclusion from global filtering were **not touched** by any Stage 1
wave.

**This is the one item that blocks the final maturity score (§8).**

**Next step:** extend `SqlServerFixture` with stock + journal DDL, then add the six cases. Disposable DB only; the
fixture already refuses a connection string naming a real CrossBuy database.

---

## 5. F5 — Company isolation audit (Completed — measured, classified, NOT fixed)

### 5.1 Controllers

**13 declarations, 908 references** (was 931 — 23 repointed inside remediated methods).

| Controller | Refs | Class |
|---|---|---|
| `InventoryController` | 267 | **A** — largest surface, stock + documents |
| `AccountingController` | 201 | **A** — ledger |
| `CrmController` | 97 | **B** — customer-owned records |
| `ProjectController` | 87 | **A** — 23 refs already repointed; 87 remain on non-Wave-1 actions |
| `PosAppController` | 76 | **C** — sanctioned POS lane |
| `PosController` | 46 | **B** |
| `AdminController` | 43 | **A** — HR/payroll |
| `TasksController` | 38 | **B** |
| `HyperPosController` | 37 | **C** — sanctioned POS lane |
| `HyperController` | 10 | **C** — sanctioned POS lane |
| `Api/InventoryApiController` | 10 | **A** — API, no attribute |
| `CurrencyController` | 9 | **B** |
| `BrandController` | 7 | **B** |

**Class A** = financial/stock/payroll surface, highest priority. **Class B** = business data, lower blast radius.
**Class C** = the documented permanent POS catalog exception; correct as-is, must not be copied elsewhere.

No controller lost its **last** reference, so the declaration count is unchanged at 13 — the honest unit of
progress, and why the structural guard pins the count rather than an endpoint tally.

### 5.2 Services — a NEW finding, and a corrected one

A first pass reported **11 files in `BL/`** carrying a hardcoded company. **Nine of those eleven are comments
documenting constants that Stage 1 REMOVED** (`AccountingAccessService`, `CrmAccessService`,
`InventoryAccessService`, `InventoryApprovalService`, `BusinessContextAccessor`, `NotificationProjectionConsumer`,
`PlatformPermissionProvider`, `RequestCompanyResolver`, `WorkerCompanyScope`) — the grep matched documentation, not
code. The same comment artifact as §2.

**Only two are live constants, and both are the sanctioned POS catalog exception:**

* `PosAccessService.CatalogCompanyId = 1` (public)
* `PosSetupService.PosCompanyId = 1` — *"cash accounts / catalog live under company 1 (branches sit under 65–79)"*

**Presentation layer: 0** — no view hardcodes a company comparison.

### 5.3 Remaining shared risks

* All parallel-team work remains **uncommitted**, so our base can still shift invisibly (HM-D44).
* A tracked Razor view (`Views/Accounting/CustomerAnalytics.cshtml`) is **currently broken by parallel WIP** — 14
  errors, none in our code. The application will not build until they fix it; verification during Wave 1's final
  increment therefore ran with `-p:RazorCompileOnBuild=false`, which **did not compile any view**. Declared per the
  HM-D52 rule.
* `PlatformRoleAssignments` and `ProjectMembers` remain **absent from `CrossBuyDB2`**.

---

## 6. F6 — Test quality audit (Partial: audited, reported, not rewritten)

**44 test files, 749 tests. 0 failing. 46 skipped — all SQL-gated, none silently passing.**

**Findings (no behaviour changed):**

* **Duplicate coverage, deliberate:** the bootstrap-open trap is pinned three times (accounting, projects, HR). This
  is duplication with a purpose — each module has its own default and each must be pinned separately — but it should
  be consolidated into one parameterised guard when a fourth module arrives.
* **Weak-assertion risk, already mitigated:** structural tests originally used naive substring windows, which can
  read the *next* method and pass falsely. All now extract method bodies by **brace matching**. Two live examples of
  the failure mode this catches: a Tasks gate edit that silently no-oped, and a project billing expectation that was
  simply wrong.
* **Long-running:** none individually slow; full suite 28–49 s across six runs.
* **Unstable fixtures:** one real instance found and fixed — a cyclic-hierarchy fixture that EF refused to insert in
  one `SaveChanges` (self-referencing FK); it now closes the cycle with a second `UPDATE`.
* **Flakiness:** **none.** Six consecutive full passes, identical results.
* **Missing edge cases (not added — F6 forbids behaviour change):** SQL-level concurrency (§4); a Roslyn-level test
  that the scanner's call-graph resolver cannot be fooled by an interface hop (§1.4).

**Not done:** no test was deleted or merged. *Reason:* every candidate duplicate is currently load-bearing, and
consolidation is a behaviour-adjacent change better made with the fourth module in hand.

---

## 7. F7 — Performance verification (Partial)

**Measured indirectly, and honestly labelled as such.**

| Path | Observation |
|---|---|
| Authorization overhead per gated action | ≤ 1 role-directory read + (GL actions) 1 accounting-role read — indexed, **once per request, never per row** |
| `BusinessContext` creation | per-DI-scope **cached** (`BusinessContextAccessor`), so once per request |
| `RequestCompanyResolver` | reads the cached context; no query of its own |
| Permission evaluation growth | **proven flat**: 1 evaluation for 10 rows and for 500 (`TaskScopeQuery` tests) — 50× data, identical cost |
| Global query filters | 12 pilot entities; filter state read through the executing `DbContext`, no per-request model rebuild |
| Workers | 8 hosted services, all behind the single-worker gate, **0 still hardcoding company 1** (scanner-reported) |
| Suite wall-clock | 28–49 s over six runs — **no regression trend** |

**Not done:** no instrumented benchmark of authorization latency under load, and no measurement of the filters' SQL
plan impact. *Reason:* that needs a load harness this repo does not have; suite timing is not a substitute and is
not presented as one. *Next step:* a scoped benchmark if Stage 2 adds hot-path authorization.

---

## 8. F8 — Final Stage 1 maturity (BLOCKED)

**No definitive Stage 1 score is published, and that is a deliberate refusal rather than an omission.**

F8's own precondition is *"after every metric is regenerated"*. Every metric except **F4** is regenerated and
reconciled. F4 is not merely a missing test file: it is the only evidence that a **refused** financial or stock
operation leaves no journal, no balance change and no partial row **under real transaction and lock semantics**.
Stage 1's central claim is *"rejected operations create no partial data or side effects."* Scoring that claim as
complete while its database-level proof is absent would be publishing a number ahead of its evidence — the exact
failure CORRECTION-004 records.

**What can be stated factually, without a score:**

| Dimension | Movement during Stage 1 | Verified by |
|---|---|---|
| **Security — authorization coverage** | Backlog 183 → **143**; **Critical 40 → 0** | Regenerated evidence + 749 tests |
| **Security — company isolation** | Every Critical endpoint now resolves its company from `BusinessContext`; 2 legacy cross-company hierarchy leaks closed; 1 unauthorized realtime read closed | Multi-company tests |
| **Architecture** | One RBAC foundation replacing four proposed role tables; set-based `AccessScope` proven no-N+1; two writers untouched | ADRs + tests |
| **Testing** | 112 → **749** tests; DI graph now verified by container build; bootstrap-open trap pinned | Suite |
| **Deployment** | Idempotent SQL only, no EF migrations; 115 scripts manifested; **2 tables still unapplied to `CrossBuyDB2`** | Manifest + schema tests |
| **Measurement integrity** | Two corrections issued (**004**, **005**) and a third finding pending a record (§3) | This report |

**Exact next step to unblock F8:** complete F4, then compute the score once, against fully regenerated evidence.

---

## 9. Known limitations

1. **F4 outstanding** — no database-level proof of refusal/rollback/lock behaviour. Blocks F8.
2. **Scanner is syntactic, not semantic** (§1.4) — cannot follow authorization through interfaces, base classes or
   across files.
3. **908 hardcoded company references remain** in 13 controllers (§5.1), plus 2 sanctioned POS constants. Only the
   40 Critical endpoints have a verified company source; **no claim is made that the 157 attribute-protected
   endpoints are company-safe.**
4. **`leave-manage` is bootstrap-open**, so on a company with no HR role rows `Encash`, `PostLeaveProvision` and
   `RunLeaveCarryOver` remain open. `payroll-manage` is never bootstrap-open, so `PostFinalSettlement` and
   `SaveSalaryPolicy` are closed by default.
5. **59 High + ~84 Medium/Low endpoints** remain unprotected (Waves 2–6).
6. **The shared tree does not currently build** — parallel WIP broke a Razor view (§5.3).
7. **`PlatformRoleAssignments` / `ProjectMembers` absent from `CrossBuyDB2`**; four Batch C proof endpoints stay
   gated on that deployment, which is the operator's call.
8. **F3 and F6 partial**, F7 partial — all documentation/quality, no security consequence.

---

## 10. Recommended Stage 2 starting point

**Start with a Roslyn-based authorization analyzer — not with Wave 2.**

The single most valuable thing Stage 1 learned is that *the instrument was behind the architecture*, and it cost two
corrections and one 38-endpoint misreading to notice. A compiled analyzer would resolve authorization through
interfaces, base classes and files; could assert "every mutating action reaches an access service" as a **build
failure** rather than a CSV column; and would make Waves 2–6 self-verifying instead of self-reported. Everything else
in Stage 2 is measured by this tool, so it should be trustworthy first.

**Then, in order:** (1) close **F4**, publish the F8 score; (2) **Wave 2** (59 High — HR/identity/privilege, using
the now-proven gate pattern); (3) the **Class A company-source sweep** (`InventoryController` 267,
`AccountingController` 201) as its own batch with its own regression surface — never folded into a wave; (4) **Batch
D2** Security Administration Console, which finally makes the RBAC foundation administrable.

**Do not start** Workflow, Unified Inbox, AI, or any portal until Critical/High is zero and F4 is closed.

---

**Stage 2 has not started. Awaiting review.**
