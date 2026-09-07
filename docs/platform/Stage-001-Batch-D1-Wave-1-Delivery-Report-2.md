# Stage 1 — Batch D1 — Wave 1 — Delivery Report (increment 2)

**Status: PARTIAL. 30 of 40 Critical endpoints protected** (was 2). **10 remain Not Started.**
**Wave 1's completion gate is NOT met.** Wave 2 has not started.

**Suite:** **722 total — 676 passed, 0 failed, 46 skipped.** Was 668/622. **54 tests added this increment.**
**Flaky-test finding: none.** Three consecutive full passes, identical (28 s / 28 s / 44 s).
**Builds:** application 0 errors, tests 0 errors.
**No SQL executed against `CrossBuyDB2` or any database. No calculation, posting, locking or payroll formula changed.**

Prior increment preserved as historical record: `Stage-001-Batch-D1-Wave-1-Delivery-Report.md` (now carries a
pointer here).

---

## 1. Inventory reconfirmation, before any code change

Re-derived from the live tree, not assumed:

* raw grep: **389** verb attributes − **1** commented-out (`AccountingController.cs:183`) = **388** mutating actions
* all **38** claimed remaining endpoints located in source — **0 missing**
* **no concurrent additions or removals** since Phase 1; the count is unchanged at 388

The 38 count was therefore **confirmed correct**, and no corrected count was needed.

---

## 2. Endpoint status — all 40

| # | Controller / action | Permission | Company source | Status |
|---|---|---|---|---|
| 1 | `Accounting.StampInvoiceCustomer` | `AccPerm("post")` | resolved | **Completed** (inc. 1) |
| 2 | `HyperPos.StampInvoiceCustomer` | `IPosAccessService.CanSell` | branch + company validated | **Completed** (inc. 1) |
| 3–25 | `Project` × 23 (see §3) | `ProjectsActions.Billing` / `BudgetManage` **+ accounting `post`** on the 14 GL-reaching ones | resolved, project **row** authoritative | **Completed** |
| 26 | `Accounting.CustomerQuickAdd` | `ApiPerm(acc,"post")` | resolved | **Completed** |
| 27 | `Accounting.VendorQuickAdd` | `ApiPerm(acc,"post")` | resolved | **Completed** |
| 28 | `Inventory.WarehouseQuickAdd` | `ApiPerm(inv,"manage")` | resolved | **Completed** |
| 29 | `Tasks.PostLaborToWO` | `TasksActions.Edit` (+ linked-entity gate) | resolved | **Completed** |
| 30 | `Tasks.GenerateInvoice` | `TasksActions.Edit` **+ accounting `post`** | resolved | **Completed** |
| 31–35 | `Admin` × 5 — `PostFinalSettlement` `PostLeaveProvision` `Encash` `RunLeaveCarryOver` `SaveSalaryPolicy` | — | `HrCompanyId` constant | **Not Started** |
| 36–40 | `Pos` × 5 — `OpenShift` `CloseShift` `PrepareFinished` `PrepareSemi` `CustomerQuickAdd` | — | `DefaultCompanyId` constant | **Not Started** |

### 2.1 The 10 Not Started — reported as the brief requires

**Exact missing work:** production enforcement + tests for `AdminController` payroll 5 and `PosController` 5.

**Reason:** capacity in this increment. Both groups also need dependencies neither controller currently injects
(`AdminController` has no HR access service wired and no `HrPerm` attribute exists; `PosController` injects no
`IPosAccessService` at all), so each is a constructor change plus per-endpoint inspection — not the mechanical
application the other 28 became once their gates existed.

**Affected controllers/services:** `AdminController.cs` → `HrAccessService`, payroll/settlement/provision services;
`PosController.cs` → `IPosAccessService`, `IPosSetupService`, POS production and shift services.

**Financial / stock impact if left:** `PostFinalSettlement` and `PostLeaveProvision` post **payroll journals**;
`Encash` pays cash for leave; `RunLeaveCarryOver` rewrites **leave balances**; `SaveSalaryPolicy` changes the
**payroll basis**; `OpenShift`/`CloseShift` control **cash reconciliation** (close posts a variance JE);
`PrepareFinished`/`PrepareSemi` move **stock** (POS production); `CustomerQuickAdd` creates financial master data.

**Security impact — REAL:** all 10 remain reachable by any signed-in employee, company from a literal. Unchanged
from before this batch.

**Business impact:** none introduced; the 10 behave exactly as before.

**Exact next step:** inject `HrAccessService` + `IRequestCompanyResolver` into `AdminController` and gate the 5
payroll actions on the real HR payroll vocabulary (deriving the right from an existing secured payroll operation,
not inventing one); inject `IPosAccessService` + `IRequestCompanyResolver` into `PosController` and gate the 5 on
real POS roles while preserving `BranchUserRoles` and every existing calculation. Then the SQL Server tests in §6.

---

## 3. The project financial gate — 23 endpoints

Before: **all 23** carried `[SessionValidation]` and nothing else, and **every one** passed `DefaultCompanyId` into
its service. Any signed-in employee could post progress and subcontractor billing to the GL, issue project material
(**stock**), post equipment depreciation, receive advances and release retention.

**`ProjectController.GateAsync(action, projectId, requireAccountingPost)`** resolves the company (CORRECTION-005) and
**asks the two approved access services**. No predicate lives in the controller: `ProjectsAccessService` loads the
**project row** and compares **its** `CompanyID`, so the project — not the request, not a membership row — is the
ownership authority. One denial message for every refusal reason, so an unresolved identity, a missing right, a
foreign project and a non-existent project are indistinguishable.

**The action per endpoint was chosen individually, not per controller:**

* `billing` (11) — progress billing (4), subcontractor billing (4), advance receipt, retention release, sub-retention
  release. Customer- and vendor-facing money.
* `budget-manage` (12) — equipment depreciation (3), labour posting, material issue (3), variation orders (3), BOQ,
  subcontract. Cost, contract value and the budget baseline.

**The GL half, and why it is not redundant.** The 14 GL-reaching actions additionally require accounting `post`.
A finding that changed the design: `ProjectsAccessService` **already** requires accounting `post` for `billing`
(Batch C's rule that "stops project administration becoming a back door into accounting") — so for billing the
gate's second half is belt-and-braces. But `budget-manage` does **not** delegate to accounting, so for
`PostLabor`, `PostMaterialIssue`, `PostEquipmentDepreciation` and the two matching deletes **the gate is the only
accounting check in the path**. Without it, a project budget right alone would post journals. Both facts are
asserted by test.

Each of the 23 also had `DefaultCompanyId` replaced with the validated company **inside its own method body only** —
23 references. The controller's other **61** references are untouched, which is why no claim is made that
`ProjectController` is company-safe.

---

## 4. The API-safe guard — now required, and built

`AccountingController.CustomerQuickAdd`/`VendorQuickAdd` and `InventoryController.WarehouseQuickAdd` are MVC actions
that return `Json(...)` to inline dropdown widgets. `AccPerm`/`InvPerm` deny by **redirecting**, so a `fetch()`
caller would receive **302 → 200 with an HTML login page**: the browser sees a success status and an unparseable
body. The denial would read as success.

`Models/ApiPermAttribute.cs` — the same decision, a different response. It asks the **same** access service for the
**same** action (no second permission engine), and answers in the project's own `{ ok = false, error = … }` shape:
**401 unauthenticated, 403 authorized-but-denied**, identical body for both, carrying no authorization detail.
It differs from the MVC attributes in one deliberate way: a **missing access service denies**, where `AccPerm`
calls `next()` — a fail-open this guard does not copy.

**The rights are derived, not invented.** `SaveCustomer` and `SaveVendor` in the same controller already carry
`AccPerm("post")`, and the quick-adds create the **same entities**, so they take the same right.
`WarehouseQuickAdd` takes `InvPerm`-equivalent `manage`, matching the full warehouse maintenance action.

---

## 5. Tasks — two financial endpoints

`PostLaborToWO` writes manufacturing **cost** via `ManufService`; `GenerateInvoice` **creates a sales invoice** via
`ReceivableService`. Both now gate on `TasksActions.Edit` for the specific task, which does two things at once: the
record rule (assignee / creator / company-intersected manager), and — because these tasks are WO-linked — the
**linked-entity check through `IPlatformPermissionProvider`**, so someone who may not see the work order cannot reach
it through the task. `GenerateInvoice` additionally requires accounting `post`, because a task right must not become
a way to issue invoices. `GenerateInvoice` now also records the **resolved** employee as the actor instead of the
session-parsed one.

### 5.1 A silent failure my own tests caught — disclosed

My first attempt to gate these two **did nothing**. I applied the edit with a string replace and, unlike the other
patches in this increment, **without an assertion** — the anchor did not match, the replace was a no-op, the build
stayed green, and the helper method was added while both action bodies still used `DefaultCompanyId` and no gate.
Had I not written the structural tests, this would have shipped as "protected" with **zero** enforcement. The
structural assertions (§7) exist for exactly this class of error and are the reason it was caught rather than
delivered.

---

## 6. Tests — 54 added

`D1Wave1GateTests` (49 incl. theory cases) and additions alongside the existing `D1Wave1CompanySourceTests` (18) and
`Correction005StructuralTests` (5).

**Bootstrap-open safety, per the brief.** Every allow/deny assertion seeds **real** role rows
(`AccountingUserRoles` + `PlatformRoleAssignments`) and uses the **real** access services — never a permissive stub.
The trap is pinned for **both** modules this wave depends on:

* `Unseeded_accounting_allows_a_cashier_to_post…` — asserts permission **is** granted with no role rows. That is
  the finding.
* `Unseeded_projects_allows_billing…` — the same for the projects scope.
* `Removing_the_seeded_role_row_changes_the_answer` — the diagnostic: denied when configured, allowed again when the
  row is deleted.

**Covered:** project role required; **project row's company** authoritative (a company-2 project refused to a
company-1 finance role); **record-id tampering** (nonexistent id refused identically to a foreign one); the GL half
denies a project-only right; unresolved context refuses **with company 0, never 1**; API guard 401 vs 403 with **no
redirect**, authorized caller reaches the action, unknown module and missing service both deny.

**Structural, per endpoint:** all 23 project actions asserted gated + `DefaultCompanyId`-free; the 14 GL-reaching
ones asserted to require `requireAccountingPost: true`, **named individually**; the 3 JSON endpoints asserted to
carry `ApiPerm` with the right module/action and to use `scope.CompanyId`; both task actions asserted gated. Method
bodies are extracted by **brace matching** so an assertion cannot read the next method and report a false pass.

**Not done — SQL Server integration tests.** The brief requires them for stock concurrency, locking, rejected
cross-company writes and warehouse access denial. **Not Started.** The stock-moving endpoints in this increment
(`PostMaterialIssue`, `SaveMaterialIssue`, `DeleteMaterialIssue`) are gated in production code and covered by
structural and access-service tests, but their **same-row concurrency and UPDLOCK behaviour under a denial is not
yet proven against a real server**. `StockBalance`'s `UPDLOCK/HOLDLOCK` paths and its exclusion from global filtering
were **not touched**. This is the largest remaining test gap and belongs with the POS production endpoints in the
next increment.

---

## 7. Counts — regenerated evidence deliberately NOT produced

Per the brief: final evidence is **not** regenerated until all 38 (now 10) remain. So the pinned
`Stage1PermissionBacklogTests` figures (151 / 54 / 183) still stand against the un-regenerated CSV and still pass.
**Those figures are stale and must not be described as current.** Source-derived reality:

| Metric | Phase 1 | After inc. 1 | **After inc. 2** |
|---|---|---|---|
| Mutating actions | 388 | 388 | **388** |
| Module-attribute protected | 151 | 152 | **155** (+3 `ApiPerm`) |
| Verified in-body protected | 54 | 55 | **80** (+25: 23 project gates, 2 task gates) |
| **Backlog** | 183 | 181 | **153** |
| Critical remaining | 40 | 38 | **10** |
| High remaining | 59 | 59 | **59** |
| Concurrent additions/removals | — | none | **none** |
| Hard-coded company **controllers** | 13 | 13 | **13** |
| Hard-coded company **references** | 931 | 931 | **≈905** (26 repointed inside remediated methods) |

`388 = 155 + 80 + 153` reconciles. The controller count stays **13** because no controller's **last** reference was
repointed — `ProjectController` alone retains 61.

---

## 8. Maturity

**No maturity record created and no points claimed.** 30 of 40 is not Wave 1. The record belongs at completion, per
the brief's own instruction not to claim improvement until Wave 1 is complete.

---

## 9. Files

**Added (2):** `CrossBuy/Models/ApiPermAttribute.cs` · `CrossBuy.Tests/D1Wave1GateTests.cs`
**Changed (5):** `ProjectController.cs` (gate + 23 actions) · `AccountingController.cs` (2 quick-adds) ·
`InventoryController.cs` (quick-add + resolver) · `TasksController.cs` (gate + 2 actions) ·
`CrossBuy.Tests/D1Wave1CompanySourceTests.cs`
**Docs (2):** this report · pointer added to the increment-1 report (its content preserved)
**Not touched:** `JournalEntryService`, `StockService`, `StockBalance` locking, any calculation, posting, payroll
formula or POS calculation, any SQL script, any EF migration, `CrossDbContext`, any view, any resx.

**Rollback:** `git revert` of this increment. Every change is an added attribute, an added gate call, two
constructor parameters, and tests. No schema, no data, no calculation. Nothing to undo in any database.

---

## 10. Stopping point

Wave 1 remains **incomplete**: **10 of 40** Critical endpoints unprotected (`AdminController` payroll 5,
`PosController` 5), with their financial, payroll and stock impact stated in §2.1, plus the SQL Server test gap in
§6. Wave 2 has **not** started. Batch D2 has **not** started.
