# Stage 1 — FINAL CLOSURE REPORT

**Stage 1 = CLOSED.** F4 and F8 are complete. No functional development, no architecture expansion, no permission
changes. **Stage 2 has not started.**

| | |
|---|---|
| **Suite** | **763 total — 763 passed, 0 failed, 0 SKIPPED** |
| **Final maturity** | **57.60 / 100** (was 54.20) — **+3.40 pts**, **+6.3 % relative** |
| **Critical endpoints unprotected** | **0** (was 40) |
| **Backlog** | **143** (was 183) |
| **Production databases touched** | **none** — every proof ran on a disposable scratch database |

The headline number is the **0 skipped**. For two batches this suite reported 46 skipped SQL tests as coverage. They
had never executed.

---

## 1. Stage 1 summary

Stage 1 turned an application with **no authorization architecture** into one with a single RBAC foundation, a
validated company source, and enforcement on every Critical financial, stock and payroll endpoint — verified against
a real SQL Server.

| Batch | Delivered |
|---|---|
| **A** | `BusinessContext`, the one context pipeline; session-free access services |
| **B** | Global company query filters (12 pilot entities), the audited bypass, worker company scope, notification policy |
| **Hotfix A.1** | `AccountingApiController` — 10 Critical API endpoints authorized in-body |
| **C** | Shared `PlatformRoleAssignments` RBAC (replacing four proposed per-module role tables), `IOrgHierarchy`, `AccessScope`, HR/Projects/Tasks/Communication access services |
| **C.1** | `TaskScopeQuery` (proven no-N+1), SignalR conversation authorization, company-safe hierarchy for CRM + Leave |
| **D1 Phase 1** | Five-source inventory reconciliation; discovery of the hardcoded-company defect |
| **D1 Wave 1** | 40 of 40 Critical endpoints enforced; `IRequestCompanyResolver`; `ApiPerm` API-safe guard |
| **Finalization** | Scanner modernized and verified; evidence regenerated; company-isolation audit; **F4 database proofs**; final maturity |

---

## 2. Implemented architecture

* **One context pipeline.** `IBusinessContextFactory` / `IBusinessContextAccessor`, per-scope cached. `ForHttpAsync`
  throws rather than returning company 1; `ForEmployeeAsync` excludes inactive employees.
* **Two writers, untouched.** `JournalEntryService` (GL) and `StockService` (stock) remain the only writers. Stage 1
  added authorization *around* them and changed neither.
* **Composite gates, not copied predicates.** Four controller gates (`GateAsync`, `HrGateAsync`, `PosGateAsync`,
  `TaskGateAsync`) resolve the company and **ask** the access services. No authorization predicate lives in a
  controller.
* **The GL half.** Any action reaching the ledger requires its module right **and** accounting `post`. This is not
  redundant: `budget-manage` does not delegate to accounting, so for `PostLabor` / `PostMaterialIssue` /
  `PostEquipmentDepreciation` the gate is the **only** accounting check in the path.
* **Set-based reads.** `TaskScopeQuery` turns one resolved `AccessScope` into one server-side predicate —
  **1 permission evaluation for 10 rows and for 500**, with a control test proving the per-row shape really costs N.

## 3. Implemented security and RBAC

* **One** `PlatformRoleAssignments` table behind `IPlatformRoleDirectory` — scope + principal + validity, replacing
  four proposed per-module role tables.
* **Bootstrap-open per company and per scope**, with deliberate exceptions: `payroll-manage` and `confidential-view`
  are **never** bootstrap-open; POS is never open at all (no `BranchUserRole` row ⇒ no access).
* **API-safe authorization.** `ApiPerm` (acc / inv / hr) returns **401 / 403** JSON, never an MVC redirect — because a
  302-to-HTML denial reads as success to a `fetch()` caller.
* **Three genuine holes closed:** unauthorized realtime message delivery (`ChatHub` group join), a cross-company CRM
  owner whitelist, and a cross-company leave approver with decision rights.
* **A defect the fix nearly created, caught:** filtering a foreign approver out of the leave chain would have emptied
  it, and an empty chain **auto-approves**. `ApproverChain.HierarchyDefect` makes `CreateAsync` refuse instead.

## 4. Implemented company isolation

`BusinessContext → IRequestCompanyResolver → validated company → validated business record → authorization →
execution` on all 40 Critical endpoints. Never a company-1 fallback; a mismatched request company is **refused, never
coerced**. The persisted row is always the ownership authority — a foreign record answers exactly like a
non-existent one, so ids cannot be probed.

## 5. Implemented scanner (F1) — and why it mattered

The scanner decided in-body authorization with three hardcoded regexes and an attribute list predating `ApiPerm`, so
it reported **38 of 40 protected endpoints as unprotected**. It now resolves authorization by **call graph**: named
authorities, plus transitive private-helper resolution iterated to a **fixpoint**.

**Verified before its output was trusted:** parses clean · reconciles · all 40 detected · **zero false credits** ·
and it now agrees **exactly** with independent source-derived counts, which it previously missed by 38.

**Limitation:** syntactic call graph over one file, not Roslyn semantic binding. It cannot follow authorization
through an interface, a base class, or another file. That is the Stage 2 entry point (§11).

---

## 6. SQL Server proof (F4) — 14 proofs, all passing

Disposable database created and dropped per run; the fixture **refuses** a connection string naming a real CrossBuy
database. Verified against **SQL Server 17.00.1000**.

| Proof | Result |
|---|---|
| Refused accounting decision → no `JournalEntry`, no lines, no `BusinessEvent`, no `Notification` | ✅ |
| Refused inventory decision → no stock movement, no balance change | ✅ |
| Refused payroll decision → no posting, no leave-balance change | ✅ |
| Write-then-refuse inside a transaction → **complete rollback**, fingerprint identical | ✅ |
| **UPDLOCK** serialises two readers of the same row (second blocked until commit, then sees the committed value) | ✅ |
| **HOLDLOCK** prevents a phantom INSERT into the scanned range | ✅ |
| 10 concurrent refusals → 10 denials, zero rows | ✅ |
| 8 concurrent transactions all rolling back → zero rows | ✅ |
| 8 concurrent locked decrements → **no lost update** (80 − 8×10 = 0 exactly) | ✅ |
| Negative: foreign company · tampered company · tampered employee · unresolved identity | ✅ (4) |
| Performance measured | ✅ |

**No production code was changed by F4** — no defect was found in the authorization or locking paths, so nothing was
"fixed". `StockBalance` locking and its exclusion from global filtering are untouched.

### 6.1 Three real defects F4 exposed — all in tests or tooling, none in production

1. **The Batch C.1 SQL tests had never run.** `CROSSBUY_TEST_SQL` was unset, so they reported *skipped* for two
   batches while I counted them as coverage. Their first execution failed with **twenty `Invalid column name`
   errors**: the `TaskItems` DDL had been hand-written with 9 of the entity's ~28 columns. A schema written to match
   the test means the test agrees with itself. Fixed by deriving the schema from the EF model.
2. **`EnsureCreatedAsync` is a silent no-op** when the database already has tables — it did nothing, and every table
   was absent. Replaced with `GenerateCreateScript()` applied statement-by-statement.
3. **SQL Server rejects the EF model's own cascade graph** — `FK_Branches_CountriesLookup_CountryID`, *"may cause
   cycles or multiple cascade paths"*. Nobody had noticed because production schema is hand-written idempotent SQL
   (never EF migrations) and unit tests run on SQLite, which does not enforce it. **Not a production defect** — no
   database was ever created from that script — but the model's relational configuration is **unvalidated against
   the real engine**. Logged as Stage 2 debt (§10).

Two of my own test expectations were also wrong and the code was right: a company-2 employee acting **in company 2**
is correctly allowed (bootstrap-open applies per company; isolation comes from the row predicate, not the role
check), and `Employee.ID` is an IDENTITY column so ids must be database-assigned.

### 6.2 Performance (measured only, never optimised)

| Path | Measured |
|---|---|
| Authorization decision (cached context, real role rows) | **2.035 ms** |
| Begin + rollback transaction | **1.505 ms** |
| `UPDLOCK`+`HOLDLOCK` read including connect | **2.954 ms** |
| Permission evaluations vs row count | **flat** — 1 for 10 rows, 1 for 500 |
| Full suite | 48 s for 763 tests |

Deliberately **no timing assertion** in the test: a wall-clock threshold on a shared dev instance would be flaky and
would not say what regressed. Numbers are recorded here instead.

---

## 7. Final metrics and evidence (F2 / F8)

Regenerated by the **verified** scanner; no manually edited numbers.

| Metric | Stage 1 start | **Final** |
|---|---|---|
| Mutating actions | 388 | **388** |
| Attribute-protected | 151 | **157** |
| Verified in-body protected | 54 | **88** |
| **Backlog** | 183 | **143** |
| **Critical** | 40 | **0** |
| High | 59 | **59** |
| Medium / Low | ~38 / ~46 | ~38 / ~46 |
| Tests | 112 | **763** (0 skipped) |
| Hard-coded company controllers | 13 | **13** |
| Hard-coded company references | 931 | **908** |

`388 = 157 + 88 + 143` reconciles, and `Stage1PermissionBacklogTests` is pinned to exactly those figures.
**Superseded and not to be quoted: 151/54/183, 152/55/181.**

### 7.1 Final maturity — 57.60 / 100

| Dimension | Weight | Before | **After** | Δ score |
|---|---|---|---|---|
| ERP Business Coverage | 20 | 60 % | 60 % | 0.00 |
| **Security and Company Isolation** | 15 | 70 % | **80 %** | **+1.50** |
| Platform Kernel and Audit | 10 | 90 % | 90 % | 0.00 |
| Business Object Coverage | 10 | 20 % | 20 % | 0.00 |
| Task and Work Management | 10 | 60 % | 60 % | 0.00 |
| **Workflow and Approvals** | 10 | 40 % | **45 %** | **+0.50** |
| Communication and Collaboration | 10 | 50 % | 50 % | 0.00 |
| Search, AI and Intelligence | 7 | 20 % | 20 % | 0.00 |
| **Testing and Reliability** | 5 | 50 % | **75 %** | **+1.25** |
| **Deployment and Operations** | 3 | 60 % | **65 %** | **+0.15** |
| **TOTAL** | 100 | **54.20** | **57.60** | **+3.40** |

**Why the caps are where they are.** Security stops at 80 % because **59 High endpoints are untouched** and the
company source is validated on 40 of 388 actions. Testing stops at 75 % because there is no load harness and no
mutation testing. ERP Business Coverage is **deliberately flat** — Stage 1 was instructed to add no features, and it
added none. **No points were awarded for documentation, classes, attributes without enforcement, or tests alone.**

The modest +3.40 is the honest figure: Stage 1 eliminated an entire risk class (Critical) and quadrupled verified
test coverage, but ERP coverage — the heaviest dimension at 20 — was out of scope by instruction.

---

## 8. Remaining backlog

**143 unprotected mutating actions:** 59 High (HR/identity/privilege, projects non-financial), ~38 Medium
(tasks, communication), ~46 Low (POS setup, admin, brand), **2 anonymous by design** (`AccountController.Login`,
`AuthApiController.Login`).

---

## 9. Remaining company constants

**13 controllers, 908 references.** No controller lost its **last** reference, which is the only thing that removes a
declaration — the honest unit of progress.

| Class | Controllers | Refs |
|---|---|---|
| **A** — financial / stock / payroll | `Inventory` 267 · `Accounting` 201 · `Project` 87 · `Admin` 43 · `Api/InventoryApi` 10 | 608 |
| **B** — business data | `Crm` 97 · `Pos` 46 · `Tasks` 38 · `Currency` 9 · `Brand` 7 | 197 |
| **C** — sanctioned POS catalog exception (correct as-is) | `PosApp` 76 · `HyperPos` 37 · `Hyper` 10 | 123 |

**Services: 2 live constants**, both the sanctioned POS catalog (`PosAccessService.CatalogCompanyId`,
`PosSetupService.PosCompanyId`). A first pass reported 11 — **nine were comments documenting constants Stage 1
removed**. **Presentation layer: 0.**

---

## 10. Known limitations and technical debt

1. **Scanner is syntactic, not semantic** — cannot follow authorization through interfaces, base classes or files.
2. **The EF model's cascade graph is rejected by SQL Server** (§6.1-3). Unvalidated relational configuration.
3. **908 company constants remain.** Only the 40 Critical endpoints have a verified company source. **No claim is
   made that the 157 attribute-protected endpoints are company-safe.**
4. **`leave-manage` is bootstrap-open**, so on a company with no HR role rows `Encash`, `PostLeaveProvision` and
   `RunLeaveCarryOver` remain open. `payroll-manage` is never bootstrap-open.
5. **143 endpoints unprotected** (59 High).
6. **The shared tree does not build** — parallel WIP broke `Views/Accounting/CustomerAnalytics.cshtml` (14 Razor
   errors, none ours). Verification used `-p:RazorCompileOnBuild=false`, so **no view was compiled**.
7. **`PlatformRoleAssignments` / `ProjectMembers` absent from `CrossBuyDB2`** — four Batch C proof endpoints stay
   gated on that deployment. Operator's call.
8. **F3 / F6 / F7 partial** (documentation sweep, test consolidation, load benchmark) — no security consequence.
9. **CORRECTION-006 not yet written** as a standalone record for the scanner-blindness finding.
10. **All parallel-team work remains uncommitted**, so our base can shift invisibly.

---

## 11. Recommended Stage 2 entry point

**Build a Roslyn authorization analyzer first — before Wave 2.**

Stage 1's most expensive lesson, twice over, was that **the instrument was behind the architecture**: the scanner
misread 38 endpoints, and 46 tests reported "skipped" as if it were coverage. Both were measurement failures, not
code failures, and both cost a correction to notice. A compiled analyzer would resolve authorization through
interfaces and base classes, and assert *"every mutating action reaches an access service"* as a **build failure**
rather than a CSV column — making Waves 2–6 self-verifying instead of self-reported.

**Then, in order:** (1) **Wave 2** — 59 High, using the proven gate pattern; (2) the **Class A company-source
sweep** (`Inventory` 267, `Accounting` 201) as its own batch with its own regression surface, never folded into a
wave; (3) **Batch D2** — Security Administration Console, making the RBAC foundation administrable; (4) validate the
EF relational model against SQL Server DDL (§6.1-3).

**Do not start** Workflow, Unified Inbox, AI or any portal until High is zero.

**Also make permanent:** `CROSSBUY_TEST_SQL` must be set in CI. Everything F4 proved is invisible without it, and a
skipped test is not evidence.

---

## 12. Stage status

**Stage 1 = CLOSED.** Stage 1 documents are **frozen**; superseded reports retain pointers and were never rewritten.
Future corrections — including the ten items in §10 — become **Stage 2 work items**.

**Stage 2 has not started. Awaiting approval.**
