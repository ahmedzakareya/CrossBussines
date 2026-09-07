# Stage 1 — Batch D1 — Wave 1 — Delivery Report

> **SUPERSEDED IN PART — see `Stage-001-Batch-D1-Wave-1-Delivery-Report-2.md`** for the second increment
> (30 of 40 endpoints). This file remains the record of the first increment (2 of 40) as reported at the time.

**Status: PARTIAL.** Priority items 1, 2 and 4 are **Completed**. Priority items 3 and 5 — the remaining 38 of the
40 Critical Wave 1 endpoints — are **Not Started**.
**Wave 1's completion gate is NOT met.** No claim of Wave 1 completion is made, and Wave 2 has not started.

**Suite:** **668 total — 622 passed, 0 failed, 46 skipped.** Baseline was 645/599. **23 tests added.**
**Flaky-test finding:** none. Three consecutive full passes, identical results (36 s / 39 s / 31 s).
**Build:** application 0 errors, tests 0 errors.
**No SQL executed against `CrossBuyDB2` or any database. No calculation or posting logic changed.**

---

## 1. Requirement status

| Priority | Item | Status |
|---|---|---|
| 1 | **CORRECTION-005 — validated company source** | **Completed** (mechanism, structural guard, documented) |
| 2 | **`AccountingController.StampInvoiceCustomer`** | **Completed** |
| 3 | **Other Critical financial / stock / payroll endpoints (37)** | **Not Started** |
| 4 | **`HyperPosController.StampInvoiceCustomer`** | **Completed** |
| 5 | Remaining Wave 1 High endpoints | **Not Started** |
| — | API-safe guard | **Not Started** — no Wave 1 endpoint delivered so far is a JSON API |
| — | `TaskPerm` / `CommunicationPerm` | **Not Started** — correctly; no delivered Wave 1 endpoint needs them |
| — | SQL Server integration tests for Wave 1 financial/locking behaviour | **Not Started** — see §6 |
| — | Wave 2 | **Not Started** |

### 1.1 The Partial, reported as the brief requires

**Exact missing work:** production enforcement plus tests for 38 endpoints —
`ProjectController` 23 (`PostBilling` `ApproveBilling` `DeleteBilling` `SaveBilling` `PostSubBilling`
`ApproveSubBilling` `DeleteSubBilling` `SaveSubBilling` `ReceiveAdvance` `ReleaseRetention` `ReleaseSubRetention`
`PostEquipmentDepreciation` `DeleteEquipmentDepreciation` `SaveEquipmentDepreciation` `PostLabor` `PostMaterialIssue`
`SaveMaterialIssue` `DeleteMaterialIssue` `ApproveVariationOrder` `SaveVariationOrder` `DeleteVariationOrder`
`SaveBoq` `SaveSubcontract`) · `AdminController` 5 (`PostFinalSettlement` `PostLeaveProvision` `Encash`
`RunLeaveCarryOver` `SaveSalaryPolicy`) · `PosController` 5 (`OpenShift` `CloseShift` `PrepareFinished` `PrepareSemi`
`CustomerQuickAdd`) · `AccountingController` 2 (`CustomerQuickAdd` `VendorQuickAdd`) · `TasksController` 2
(`GenerateInvoice` `PostLaborToWO`) · `InventoryController` 1 (`WarehouseQuickAdd`).

**Reason:** capacity within this delivery, not a technical obstacle. The two endpoints delivered were the two the
brief ranked first after CORRECTION-005, and each required individual inspection — which is the point: the two
`StampInvoiceCustomer` actions share a name and needed **different** answers (§3). Applying the remaining 38
without that inspection would be the bulk remediation the brief forbids.

**Affected files and endpoints:** `ProjectController.cs`, `AdminController.cs`, `PosController.cs`,
`AccountingController.cs` (2 further actions), `TasksController.cs`, `InventoryController.cs` — as listed above.

**Security impact — REAL and stated.** All 38 remain exactly as before this wave: reachable by any signed-in
employee with a session, with the company taken from a compile-time constant. Concretely, today: any signed-in
employee can post project billing and subcontractor billing to the GL, issue project material (a **stock**
movement), post equipment depreciation, receive advances, release retention, post a **final settlement** and a
**leave provision** (payroll GL), run leave carry-over, change a **salary policy**, open and close POS **shifts**,
run POS production (stock), and create customers, vendors and warehouses. This is unchanged by Wave 1 and is the
reason Wave 1 must continue before Wave 2.

**Business impact of the gap:** none introduced. Nothing regressed; the 38 behave as they did.

**Exact next step:** continue Wave 1 in the order `ProjectController` financial (23, largest GL/stock surface) →
`AdminController` payroll (5) → `PosController` (5) → `AccountingController` quick-adds (2) → `TasksController` (2)
→ `InventoryController` (1). `ProjectsAccessService` and `AccountingAccessService` already carry the required
vocabulary, and `IRequestCompanyResolver` is now in place, so each endpoint is an attribute + a resolved company +
tests — no further infrastructure.

---

## 2. CORRECTION-005 — Completed

Full record: `docs/architecture/CORRECTION-005-Company-Source-Measurement.md`.

`CrossBuy/BL/Platform/RequestCompanyResolver.cs` — resolves the company from `BusinessContext`; **fails closed**
(`CompanyId == 0`, never 1) when nothing resolves or when a context carries no company; treats a request-supplied
company as compatibility only, validating it and **refusing a mismatch rather than coercing it**. Returns a result
type rather than an `int`, because a method returning `int` invites `?? 1`.

Registered `AddScoped` in `Program.cs` (one line — our line only, per the selective-commit discipline).

**Structural guard** (`Correction005StructuralTests`, 5 tests) — pins the **13** declaring controllers **by name**
and fails in *both* directions: when a 14th appears, and when one is removed without the report being updated.

### 2.1 The hard-coded company count after Wave 1 — required report

**Still 13 declarations, 931 references.** Neither remediated endpoint's controller could drop its constant, because
both controllers use it elsewhere (`AccountingController` retains 198 other references; `HyperPosController` retains
its `PosCompanyId` for the lane). The count falls only when a controller's **last** reference is repointed. That is
the honest unit of progress, and it is why the count — not an endpoint tally — is what the correction pins.

### 2.2 The constraint this places on every future report

**No claim may be made that the 151 attribute-protected endpoints are company-safe.** The metric is honest about
role checks and silent about company source; until an endpoint's company source is remediated, "protected" means
"a role is checked", nothing more.

---

## 3. The two `StampInvoiceCustomer` endpoints — same name, different answers

### 3.1 `AccountingController.StampInvoiceCustomer` — Critical, Completed

`POST /Accounting/StampInvoiceCustomer` · module Accounting · MVC · `SessionValidation` +
`ValidateAntiForgeryToken` before, neither of which is authorization · company was `DefaultCompanyId` (the constant)
· direct `CrossDbContext` · no `ScopedTx` (single `SaveChangesAsync`, display-only fields).

**Before:** any signed-in employee could rewrite the legal beneficiary **name** and **tax number** on any posted
sales invoice in company 1 — alteration of a printed statutory tax document.

**Added — deliberately only two things:**

1. **`AccPerm("post")`** — the same right that already governs journals and sales/purchase invoices in
   `AccountingAccessService`. Stamping the beneficiary of an issued invoice *is* an invoice mutation, so it takes the
   invoice-mutation right rather than a vocabulary invented for one action.
2. **The resolved company** — `_company.ResolveAsync()`, and the invoice loaded on
   `ID == id && CompanyID == scope.CompanyId`. An invoice in another company answers identically to one that does
   not exist, so an id cannot be probed. The recorded actor is now the **resolved** employee, not the
   session-parsed one.

**Requirements 6–8 of the brief were ALREADY satisfied and were left untouched.** `OfficialInvoiceHelper.StampCustomer`
already enforces **SET-ONCE** (a printed document's beneficiary cannot change once issued) and **TAX-ZERO ONLY** (a
taxed invoice's beneficiary must be the ledger account holder), and already records actor and timestamp. So no
correction workflow was invented: the missing controls were authorization and company, and only those changed.

**Not supported by the model — reported, not invented:** `SalesInvoice` has **no reason column** for a beneficiary
change, so *"record the reason"* cannot be satisfied without a schema change. Declared here as a gap.

**Totals, tax and posting untouched** — asserted by test (`TaxTotal` unchanged on a refusal).

### 3.2 `HyperPosController.StampInvoiceCustomer` — High, Completed

`POST /hyper/pos/…` · module Retail/POS · `PosLaneActivityGuard` (a lane guard, **not** authorization).

**Preserved unchanged:** the `OfficialInvoice` capability gate, `BranchOwnsInvoiceAsync` (the invoice must be the
pay result of an order on **this** branch), and the company predicate on the invoice row. Those three are why the
brief's downgrade from Critical to High is correct: a caller could only ever reach invoices of their own branch and
company.

**Added:** `_access.CanSell(c.Roles)` — pos-cashier or pos-manager. Before, **every** lane role could stamp,
including `pos-waiter` and `pos-kitchen`, who have no business touching an issued invoice. `CanSell` is the minimum
that is both real and compatible: stamping a walk-in beneficiary is part of the ordinary cashier printing flow, so
requiring a manager would break a working counter workflow rather than secure anything.

**No POS calculation or posting behaviour changed.**

**The POS exception stays in POS.** Lane identity comes from the `HyperCtx` session blob with roles from
`BranchUserRoles` — the documented permanent exception. It is used here and deliberately **not** copied into the
accounting twin, which resolves a real `BusinessContext`.

---

## 4. Test quality — the bootstrap-open trap

The brief required a guard test proving an "authorized" test cannot pass merely because bootstrap-open is active.
It is the most important test in this wave, because it invalidates a whole style of green test.

`AccountingAccessService` returns **true for every action** when no `AccountingUserRole` row exists in the company.
So `With_no_role_rows_even_a_cashier_can_post_which_is_why_every_test_seeds_a_role` **asserts that permission IS
granted** — that is the finding — and its pair, `Once_a_single_role_row_exists_the_cashier_is_denied_posting`, shows
the same caller denied once one role row exists. Every allow/deny test in this wave seeds real
`AccountingUserRole` rows and uses the **real** `AccountingAccessService`, never a permissive stub.

**23 tests added:**

* `D1Wave1CompanySourceTests` (18) — resolver: unresolved → no company and **never 1**; a real context supplies the
  caller's own company (asserted as **2**, not 1); a context with no company refused; matching request company
  accepted; **mismatched request company refused and neither honoured nor swapped for the caller's own**; 0/−1/null
  treated as absent. Permission matrix: chief ✓, accountant ✓, **cashier ✗**, another company's chief ✗ here and ✓
  there, unresolved identity ✗. The stamp: a foreign invoice unreachable by id **and left untouched**; set-once
  preserved; tax-zero preserved; authorized stamp records the resolved actor and leaves a whitespace tax number
  null; **a refused stamp leaves every field and `TaxTotal` unchanged**.
* `Correction005StructuralTests` (5) — the 13-file pin (both directions); the remediated action reads
  `_company.ResolveAsync`/`scope.CompanyId` and **no** `DefaultCompanyId`; it carries `AccPerm("post")`; the POS twin
  keeps branch/capability/lane checks **and** gains the role check. Method bodies are extracted by **brace matching**
  so an assertion cannot read the next method and report a false pass.

---

## 5. Backlog reconciliation

| Metric | Before Wave 1 | After | Δ |
|---|---|---|---|
| Mutating actions | 388 | 388 | 0 |
| Module-attribute protected | 151 | **152** | **+1** (`AccountingController.StampInvoiceCustomer`) |
| Verified in-body protected | 54 | **55** | **+1** (`HyperPosController.StampInvoiceCustomer` — in-body `CanSell`) |
| **Backlog** | 183 | **181** | **−2** |
| Critical remaining | 40 | **38** | −2 |
| High remaining | 59 | 59 | 0 |
| Hard-coded company declarations | 13 | **13** | 0 (§2.1) |
| Concurrent tree additions/removals | — | **none** | — |

`388 = 152 + 55 + 181` reconciles. **The pinned `Stage1PermissionBacklogTests` figures (151/54/183) are NOT yet
updated** — the evidence CSV is regenerated by the PowerShell scanner, which has not been re-run, so the pinned test
still passes against the old CSV. This is a **known inconsistency**, declared rather than hidden: the CSV and the
pinned figures must be regenerated in the same commit that completes Wave 1. Until then the authoritative figures
are the ones in this table, derived from source.

---

## 6. Not done, and why — SQL Server integration tests

The brief requires real SQL Server tests for financial and locking behaviour. **Not Started.** Neither delivered
endpoint posts a journal, moves stock, or takes a lock: `StampInvoiceCustomer` writes four display-only columns on
one row and touches no financial field, no `JournalEntry`, no `StockBalance` and no `BusinessEvent`. There is no
locking or transactional behaviour for a SQL Server test to prove. The 38 remaining endpoints **do** post journals
and move stock, and they are where those tests belong. `StockBalance`'s `UPDLOCK/HOLDLOCK` semantics were not
touched.

---

## 7. Files

**Added (3):** `CrossBuy/BL/Platform/RequestCompanyResolver.cs` ·
`CrossBuy.Tests/D1Wave1CompanySourceTests.cs` · `CrossBuy.Tests/Correction005StructuralTests.cs`
**Changed (3):** `CrossBuy/Controllers/AccountingController.cs` · `CrossBuy/Controllers/HyperPosController.cs` ·
`CrossBuy/Program.cs` (one registration line)
**Docs (2):** `docs/architecture/CORRECTION-005-Company-Source-Measurement.md` · this report
**Not touched:** `JournalEntryService`, `StockService`, any calculation or posting path, any SQL script, any EF
migration, `CrossDbContext`, any view, any resx.

**Rollback:** `git revert` of this wave's commits. Every change is an added attribute, an added resolver call, one DI
line, and tests. No schema, no data, no calculation. Nothing to undo in any database.

---

## 8. Maturity

**No maturity record is created for this wave, and no points are claimed.**

Two endpoints of 40 is 5 % of Wave 1. Maturity increases only when real production paths are protected and
verified, and the correct honest statement is that the **measurement was corrected** (CORRECTION-005) while the
**enforcement surface barely moved**. Awarding a point for the resolver class, the structural test or the correction
document would be awarding points for a class, tests and documentation — all three explicitly excluded. The
maturity record belongs at the end of Wave 1.

---

## 9. Stopping point

Wave 1 is **incomplete**: 38 of 40 Critical endpoints remain unprotected, and their security impact is stated in
full in §1.1. Wave 2 has **not** started. Batch D2 has **not** started.
