# Post-Stabilization Qualification Closure — Final Report

**Start:** `f932d06` · **Commits:** 6 · **Pushed:** no

---

## CLOSED

### Q1 — Inventory approval company isolation is now in canonical Git
The defect was real: a hardcoded company across twelve call sites, five of them writes, on a
production HTTP path. It is fixed — and **not by me**.

Another tab had written a stronger fix that lived only as uncommitted work in the shared tree, on
no branch and no commit. It landed here byte-identical, with provenance recorded rather than
absorbed. Its asymmetry is the point: reads and new submissions resolve from `BusinessContext` and
**throw** when unresolved; approve/reject/replay use the **approval row's own `CompanyID`**, because
the payload was captured against that company's warehouses and vendors.

TAB-1's contribution is the integration and the proof of the write half — 13 tests observing the
company argument each replay actually passes, for all four payload types. Details and the full
invariant table: `INVENTORY-APPROVAL-COMPANY-ISOLATION-PROOF.md`.

### Q2 — Mutation #3 is killed behaviourally
It survived 81 tests because **neither surviving `context.CompanyId` read is the gate**: one is a
deny message, the other a memo key. The gate hands the whole context to the registry.

The memo key is still a real surface — drop the company from it and two companies share an answer,
which is a cross-company grant. Six tests drive two companies through one provider instance; the
mutation now fails three. Details: `PLATFORM-PERMISSION-COMPANY-MUTATION-PROOF.md`.

### Stage1ContextTests: E → A
Its only blocker was that it was right about a fix nobody had committed. Re-evaluated rather than
waved back: compiles, 22/22 pass, no absolute path, no environment read, no HTTP, no sleep, no
wall-clock literal, no duplicate class. The inventory records the transition beneath the original
classification so the history stays auditable. `A + B + C + D + E = 67 + 0 + 2 + 0 + 37 = 106`.

### All 12 mutations killed

| # | Mutation | Killed by |
|---|---|---|
| 1 | runtime company fallback to 1 | `InventoryApprovalReplayCompanyTests` × 3 |
| 2 | worker company scope removed | compilation — the runner is asserted by name |
| 3 | provider ignores `context.CompanyId` | `PlatformPermissionCompanyCacheTests` × 3 |
| 4 | GL guard → literal `"Closed"` | `PeriodPostingChokepointTests`, `AccountingPeriodCloseTests` |
| 5 | stock guard → literal | `PeriodPostingChokepointTests` × 2 (incl. behavioural SoftClosed) |
| 6 | period setter on the interface | compilation — a test stub implements it |
| 7 | period setter on the concrete class | `The_CONCRETE_period_service_exposes_no_status_mutator_either` |
| 8 | uncovered document authority member | `DocumentAuthorityMemberTests`, `DocumentLifecycleUiTests` |
| 9 | expiry idempotency by document id only | `A_renewed_document_may_warn_again_on_its_NEW_expiry_date` |
| 10 | Documents UI computes expiry | `DocumentLifecycleUiTests` × 2 |
| 11 | per-document authorization removed | `A_row_the_caller_may_not_see_is_ABSENT…` |
| 12 | Client Portal cross-client substitution | `Customer_A_never_sees_customer_B…` |

**12 / 12.** #2 and #6 kill by compilation, which §10 permits: both deliberately violate a
compile-time contract.

---

## BLOCKED

Nothing in this batch's scope is blocked.

---

## CARRIED DEBT

1. **`EntityRegistry.ResolveAsync` applies no company filter to `Employee`** — a declared deviation
   commented *"TM-2 behaviour"*. Because `PlatformPermissionProvider.BelongsToCompanyAsync` consults
   `ResolveAsync(...).Found`, **the platform company gate is a no-op for the Employee entity type**.
   Found while writing the mutation coverage. Another tab's file and another tab's declared choice,
   so it is reported rather than changed. **New debt, and the most consequential item here.**
2. **Four UNKNOWN company-1 surfaces**, ~228 references — `AccountingController` (185),
   `AdminController` (24), `Api/InventoryApiController` (10), `CurrencyController` (8). Unchanged:
   nothing in this batch proved any of the four classifications wrong.
3. **37 blocked machine-only test files**, per `MISSING-TEST-INVENTORY.md`.
4. **Ambient-scope fixture limit** — `PlatformTestHost` holds one company scope, so a second live
   company scope is not representable. One assertion was narrowed rather than written against the
   fixture.

---

## FOREIGN WORK PRESERVED

* `InventoryController.cs` — an unrelated bilingual `NameEn` display fix. Deliberately excluded from
  the integration and untouched.
* 3 stashes intact · 13 worktrees intact · no reset, clean, drop or forced branch movement.
* The integration ran in a dedicated worktree; the shared tree was never written to.

---

## MEASURED, NOT COPIED

```
mutating 439 · attributeProtected 156 · inBodyProtected 189 · gaps 94 · controllers 53
gap-set delta 0 · CBA001–CBA006 all 0
DDL executed 0 · CrossBuyDev writes 0 · production writes 0
```

No new mutating endpoint, no new gap. Safety scan of every changed file: no production connection
string, no Azure host, no credential, no token, no absolute developer path, no scratch path.

F-1 remains `F1-UNRESOLVED` and was not reopened. AI was not touched.
