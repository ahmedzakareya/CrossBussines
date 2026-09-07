# Stage 1 — Batch D1 — Wave 1 — COMPLETION REPORT

**Status: Wave 1 endpoint enforcement is COMPLETE — 40 of 40 Critical endpoints protected in production code.**
**Two completion-gate items are NOT met** and are declared in §2: the SQL Server integration tests, and a
newly-found **evidence-tooling gap** that makes the regenerated CSV understate the work. Wave 2 has **not** started.

**Suite:** **749 total — 703 passed, 0 failed, 46 skipped.** Was 722/676. **27 tests added this increment.**
**Flaky-test finding: none.** Three consecutive full passes, identical (35 s / 49 s / 44 s).
**No SQL executed against `CrossBuyDB2` or any database. No calculation, posting, locking, payroll formula or POS
calculation changed.**

Prior increments preserved as historical record (each carries a pointer, none rewritten):
`…Wave-1-Delivery-Report.md` (2 of 40) · `…Wave-1-Delivery-Report-2.md` (30 of 40).

---

## 1. Endpoint inventory — all 40, with evidence

Reconfirmed before any code change: raw grep 389 − 1 commented = **388** mutating actions; all 10 remaining
endpoints located in source; **no concurrent additions**.

| # | Controller.Action | Permission | Company | GL half | Status |
|---|---|---|---|---|---|
| 1 | `Accounting.StampInvoiceCustomer` | `AccPerm("post")` | resolved | — | ✅ |
| 2 | `HyperPos.StampInvoiceCustomer` | `PosAccess.CanSell` | branch+company validated | — | ✅ |
| 3–25 | `Project` × 23 | `billing` (11) / `budget-manage` (12) | resolved, project **row** authoritative | 14 | ✅ |
| 26–27 | `Accounting.CustomerQuickAdd` / `VendorQuickAdd` | `ApiPerm(acc,"post")` | resolved | — | ✅ |
| 28 | `Inventory.WarehouseQuickAdd` | `ApiPerm(inv,"manage")` | resolved | — | ✅ |
| 29–30 | `Tasks.PostLaborToWO` / `GenerateInvoice` | `TasksActions.Edit` + linked-entity gate | resolved | 1 | ✅ |
| 31 | `Admin.PostFinalSettlement` | `HrActions.PayrollManage` | resolved | ✔ | ✅ |
| 32 | `Admin.Encash` | `HrActions.LeaveManage` | resolved | ✔ | ✅ |
| 33 | `Admin.PostLeaveProvision` | `HrActions.LeaveManage` | resolved | ✔ | ✅ |
| 34 | `Admin.RunLeaveCarryOver` | `HrActions.LeaveManage` | resolved | — | ✅ |
| 35 | `Admin.SaveSalaryPolicy` | `ApiPerm(hr,"payroll-manage")` | via HR gate | — | ✅ |
| 36 | `Pos.OpenShift` | `PosAccess "manage"` + branch | resolved | — | ✅ |
| 37 | `Pos.CloseShift` | `PosAccess "manage"` + branch | resolved | ✔ | ✅ |
| 38–39 | `Pos.PrepareFinished` / `PrepareSemi` | `PosAccess "manage"` + branch | resolved | ✔ | ✅ |
| 40 | `Pos.CustomerQuickAdd` | `ApiPerm(acc,"post")` | resolved | — | ✅ |

**Verified structurally per endpoint:** every one asserted gated, and asserted to contain **no** `DefaultCompanyId` /
`HrCompanyId` in its own method body. Method bodies are extracted by **brace matching** so an assertion cannot read
the next method and report a false pass. The 18 GL-reaching actions are asserted **individually by name** to require
accounting `post`.

**No new authorization model was built.** Every right came from an existing documented vocabulary:
`HrActions.PayrollManage` is defined as *"salary policies, payroll runs"*; `HrActions.LeaveManage` as *"leave
types/policies/**encashment**/**provision**"*; POS `manage` is `IsManager` over `BranchUserRoles`. The one piece of
genuinely new infrastructure is the API-safe guard (§4), which was unavoidable.

---

## 2. Completion-gate items NOT met — declared

### 2.1 The evidence tooling cannot see most of the remediation *(new finding — CORRECTION-006 candidate)*

I regenerated the evidence with `deploy/scan-architecture.ps1` as instructed. **It detected only 2 of the 40
remediations.** Regenerated CSV: `388 = 152 attribute + 55 in-body + 181 backlog`.

The scanner's detectors predate this wave:

* it does **not** recognise **`ApiPerm`** as a permission attribute → the 5 API-guarded endpoints read as unprotected;
* it does **not** recognise the four gate helpers (`GateAsync`, `HrGateAsync`, `PosGateAsync`, `TaskGateAsync`) as
  in-body authorization → the 33 gated call sites read as unprotected. Its `in_body_authorization` detector looks for
  an access-service call **inside the action body**, and the gate deliberately puts that call in a shared private
  method — the very refactor that stops authorization predicates being copied into 33 places.

So there are now **two honest figures**, and neither should be quoted alone:

| | Mutating | Attribute | In-body | **Backlog** |
|---|---|---|---|---|
| **Regenerated evidence (scanner)** | 388 | 152 | 55 | **181** |
| **Source-derived (verified by test)** | 388 | 157 | 88 | **143** |

`388 = 152 + 55 + 181` and `388 = 157 + 88 + 143` both reconcile. The difference is **38 endpoints the scanner
cannot see** — the exact set enumerated in §1 and asserted by `D1Wave1GateTests` / `D1Wave1HrPosGateTests`.

**The pinned test was updated to 152 / 55 / 181** — the figures the committed evidence actually supports — because a
pinned test asserting numbers the CSV does not contain would be a test asserting a claim, not a fact. **It also means
the pinned test currently understates protection**, and that must not be read as the backlog.

*Missing work:* teach the scanner (a) `ApiPerm` in its attribute list, (b) gate-helper recognition, then regenerate
and re-pin to the source-derived figures. *Reason:* it is a tooling change to a Stage 0 script; doing it unreviewed
in the same pass that changed 40 endpoints would mean changing the measuring instrument and the thing measured
together — which is how CORRECTION-004 happened. *Security impact:* none (the enforcement is real and tested); the
**metric** understates it. *Next step:* a scoped scanner change reviewed on its own, before Wave 2's figures are quoted.

The scanner did correctly record one improvement: lane-guarded-actions-without-a-role-check fell **5 → 4**
(`HyperPos.StampInvoiceCustomer` gained one), and the pinned list now names only
`HyperPos.PriceCheck`, `HyperPos.Start`, `PosApp.AddCustomer`, `PosApp.Start`.

### 2.2 SQL Server integration tests — Not Started

*Missing work:* real-server tests for same-row stock concurrency, `UPDLOCK/HOLDLOCK` behaviour under a denial,
rejected cross-company write, and warehouse-access denial — for `PostMaterialIssue`, `SaveMaterialIssue`,
`DeleteMaterialIssue`, `PrepareFinished`, `PrepareSemi`. *Reason:* capacity; these need a scratch-DB fixture with
stock tables, which `SqlServerFixture` does not yet create. *Impact:* the endpoints **are** gated in production code
and covered by access-service and structural tests, so the authorization claim holds; what is unproven is
**concurrency and lock behaviour under refusal**. `StockBalance`'s locking and its exclusion from global filtering
were **not touched**. *Next step:* extend `SqlServerFixture` with the stock tables and add the four cases.

### 2.3 A build break in the shared tree — declared per the HM-D52 rule

Mid-increment the tree stopped compiling with **14 Razor errors** in
`Views/Accounting/CustomerAnalytics.cshtml` — a tracked file I never touched, modified at 13:52:29, **28 seconds
before** my next edit, carrying parallel-team `M` status. **Zero** of the 14 errors are in `Controllers/`, `BL/` or
`Models/`.

I did not fix their file. Instead I proved my own code compiles by building with
`-p:RazorCompileOnBuild=false`, which yields **0 errors**, and ran the suite the same way. **What this did not
cover:** Razor view compilation, so no view in the application was compiled during this increment's verification.
No view was changed by this increment (server-side enforcement only), so nothing in my change set depends on it —
but the application as a whole **will not build** until the parallel team fixes that view, and that is theirs to fix.

---

## 3. Company isolation — CORRECTION-005 continued

Every one of the 40 follows: `BusinessContext → IRequestCompanyResolver → validated company → validated business
record → authorization → execution`. Never a fallback to company 1, never a coerced mismatch, never a trusted
request company.

**Remaining hard-coded company references: 908** (was 931; **23 repointed** inside remediated project methods, plus
the earlier increments' replacements).
**Remaining hard-coded company controllers: 13 — unchanged.**

No controller lost its **last** reference, which is the only thing that removes a declaration. `ProjectController`
alone retains **61**, `AccountingController` **198**, `InventoryController` **267**. The structural guard still pins
all 13 by name and fails if a 14th appears **or** if one is removed without the report being updated.

**Standing constraint restated:** the attribute-protected figure is honest about role checks and silent about company
source. **No claim is made that the 152 attribute-protected endpoints are company-safe** — only the 40 in §1 have a
verified company source.

---

## 4. The API-safe guard

`Models/ApiPermAttribute.cs`, modules `acc` / `inv` / `hr`. Five JSON endpoints use it. Unauthenticated → **401**;
authorized-but-denied → **403**; identical generic body in the project's `{ ok = false, error = … }` shape; **never**
a redirect — asserted by test (`Assert.IsNotType<RedirectToActionResult>`). It asks the **same** access service for
the **same** action, so there is no second permission engine. One deliberate divergence from `AccPerm`/`InvPerm`: a
**missing access service denies**, where those call `next()` — a fail-open not worth copying. HR is asked through the
canonical `BusinessContext` path, and an unresolved context **denies**.

---

## 5. Test summary — 27 added this increment (749 total)

**Bootstrap-open safety.** Every allow/deny test seeds real role rows (`AccountingUserRoles`,
`PlatformRoleAssignments`, `BranchUserRoles`) and uses the **real** access services — no permissive stubs. The trap
is pinned for accounting, projects and HR, plus the diagnostic that removing a role row flips the result.

**Two of my own assumptions were wrong, and the code was right — both corrected:**

* **`payroll-manage` and `confidential-view` are NEVER bootstrap-open.** `HrAccessService` refuses them even on an
  unconfigured company, because *"those were unreachable-by-design data before this batch, and opening them by
  default would be a new exposure created by the very batch meant to close one."* So `PostFinalSettlement` and
  `SaveSalaryPolicy` are genuinely **closed by default** — stronger than accounting. **Honest limitation:**
  `leave-manage` *is* bootstrap-open, so on a company with **no HR role rows** `Encash`, `PostLeaveProvision` and
  `RunLeaveCarryOver` remain open. That is the HR module's own compatibility policy, not the gate's, and it is now
  asserted explicitly rather than assumed away.
* **`PayrollOfficer` does not hold `leave-manage`** — leave administration is `HrManager`/`HrOfficer`. My test
  expected otherwise.

**POS is not bootstrap-open at all** — no `BranchUserRole` row means no access. Pinned, because it is the opposite of
the accounting/HR default.

**Also covered:** cross-branch denial (a manager of branch 12 naming branch 11 is refused, and allowed at their own —
so the denial is about the branch, not the person); POS identity is the user id, so an empty `UserId` denies; unknown
action denies; cashier may `sell` but not `manage` (workflow preserved, distinctions intact); the POS gate asks
`_posAccess.CanAsync` and **never** consults `PosLaneActivityGuard`.

---

## 6. Compatibility, performance, rollback

**Compatibility:** no request/response contract changed. The five JSON endpoints keep their existing failure shape;
only the HTTP status on denial is new (401/403 instead of a 302). No view changed. `BranchUserRoles` preserved as the
documented POS exception, and **not** copied into accounting, HR, projects or inventory. Cashier and production
workflows preserved — `CanSell` for the stamp, `manage` only for admin shift/production operations.

**Performance:** each gated action adds at most one context resolution (per-scope cached), one role-directory read,
and for GL actions one accounting-role read — all indexed, all once per request, none per row. Suite duration is
unchanged within noise (28–49 s across six runs), so no measurable regression.

**Rollback:** `git revert` per increment; each is independently revertible. Every change is an added attribute, an
added gate call, constructor parameters, and tests. No schema, no data, no calculation. Nothing to undo in any
database. The two new tables remain absent from `CrossBuyDB2` and are unaffected by this wave.

---

## 7. Maturity

**No maturity record created and no points claimed** — deliberately, despite 40/40 enforcement.

Two gate items are open (§2.1, §2.2), and one of them is a **measurement** problem: the committed evidence cannot
currently see 38 of the 40 protected endpoints. Awarding a maturity increase off source-derived figures while the
project's own instrument reports 181 backlog would be exactly the unreconciled-metric failure CORRECTION-004 exists
to prevent — *"security and isolation metrics require independent reconciliation before publication"*. The record
belongs after the scanner is taught and the two figures agree.

**Remaining risk:** Critical **0** of 40. High **59** (Wave 2). Medium/Low per the Phase 1 plan. Plus the two gate
items above.

---

## 8. Files

**Added (2):** `CrossBuy.Tests/D1Wave1HrPosGateTests.cs` · (earlier) `Models/ApiPermAttribute.cs`
**Changed (5):** `AdminController.cs` (HR gate + 5) · `PosController.cs` (POS gate + 5) ·
`Models/ApiPermAttribute.cs` (HR module) · `CrossBuy.Tests/Stage1PermissionBacklogTests.cs` (re-pinned to
regenerated evidence) · `CrossBuy.Tests/D1Wave1HrPosGateTests.cs` (two corrected expectations)
**Regenerated:** `Endpoint-Inventory.csv`, `Permission-Coverage.csv`, `Controller-Inventory.csv`,
`Worker-Inventory.csv`, `SQL-Script-Inventory.csv`
**Not touched:** `JournalEntryService`, `StockService`, `StockBalance` locking, any calculation/posting/payroll
formula/POS calculation, any SQL script, any EF migration, `CrossDbContext`, any view, any resx.

---

## 9. Stopping point

Wave 1's **endpoint enforcement is complete (40/40)**. Two gate items remain open and are declared in §2 rather than
waived: the SQL Server concurrency/locking tests, and the scanner's inability to see 38 of the 40 protections.
**Wave 2 has not started. Batch D2 has not started.**
