# Stage 2A — Batch B — Never-Bootstrap-Open Re-Derivation

**STOPPED per B7. The re-derivation yields 15 actions, not 14, and the difference is not a counting error — it is a
substantive disagreement about two Inventory actions and about two Platform items that have no live action at all.**

B7 states: *"Confirm the total remains 14. If the total differs: stop; list the exact actions; explain source delta
versus prior measurement; do not force 14."* This document is that stop.

Nothing was implemented in this increment. No access service, policy storage, seed, reader or decision contract was
touched. The tree is exactly Batch A + the closed B0 concurrency gap.

---

## 1. The live action vocabularies

Read from source, not from the frozen matrix.

| Scope | Actions declared in live source |
|---|---|
**Accounting** (`AccountingAccessService`) | `read` · `post` · `pay` · `manage` · `currency-override` — **5** |
**Inventory** (`InventoryAccessService`) | `read` · `manage` · `doc` · `purchase` — **4**, plus a separate warehouse-scope path |
**CRM** (`CrmAccessService`) | `read` · `edit` · `manage` — **3** |

That matters immediately: the brief's Inventory bullets name **three** things ("receipts, issues and transfers"), but
live Inventory has **one** action covering all three — `doc`, commented *"stock documents
(movement/transfer/count/assembly/landed/sales)"*. Three policy bullets map to one enforceable action.

## 2. The derived list — 15 actions

| # | Scope | Action | Evidence for Never |
|---|---|---|---|
1 | Accounting | `post` | GL posting. Named in B7 |
2 | Accounting | `pay` | receipts, payments, bank/cash transfers. Named in B7 |
3 | Accounting | `manage` | **gates role assignment** — `AccPerm("manage")` guards `AssignAccRole` / `RemoveAccRole` (AccountingController:1586) — **and** is `PlatformOpsAttribute`'s fallback authority. Both halves of B7's "where it grants security or exposes PlatformOps" |
4 | Accounting | `currency-override` | Named in B7 |
5 | Inventory | `doc` | stock movement / transfer / count / assembly / landed / sales. Covers B7's receipt **and** issue **and** transfer in one action |
6 | Inventory | `purchase` | **⚠ see §3** — purchase orders *and receipts*; a receipt is a stock-receipt mutation |
7 | Inventory | `manage` | **⚠ see §3** — `InvPerm("manage")` guards `AssignRole(employeeId, role, scopeBranchId)` (InventoryController:2352): role assignment **including branch scope** |
8 | Inventory | `warehouse-access` | Named in B7; compatibility here would bypass the branch/warehouse restriction |
9 | CRM | `manage` | `CrmPerm("manage")` guards `AssignCrmRole` (CrmController:705). Named in B7 |
10 | Platform | `PlatformOps` | Named in B7; must not inherit an Accounting bootstrap allow (RISK-042) |
11 | Projects | `billing` | delegates to the Accounting decision (`ProjectsAccessService:100-101`: *"Being an administrator of projects is not a right over the ledger"*). Closes transitively once item 1 closes |
12 | HR | `payroll-manage` | already excluded by `HrAccessService`; **preserve** |
13 | HR | `confidential-view` | already excluded; **preserve** |
14 | Tasks | `manage` | already excluded; **preserve** |
15 | Communication | `outbox-manage` | already excluded; **preserve** |

**Total: 15.**

## 3. Where the difference is, precisely

Two independent discrepancies. They do not cancel out, and one of them means the two totals are **not comparable**.

### 3.1 Two Inventory actions I include and the prior count appears not to (+2)

**`Inventory.purchase`** — B7's Inventory bullet is *"document mutations affecting stock receipt, issue and
transfer"*. In live source `purchase` is commented *"purchase orders / receipts"*. A purchase **receipt** is a stock
receipt. If the prior measurement mapped that bullet only onto `doc`, it missed the second action that also receives
stock.

**`Inventory.manage`** — B7 names `manage` for Accounting ("where it grants security") and for CRM ("where roles can
be assigned or revoked") but **not** for Inventory. Yet `InvPerm("manage")` guards `AssignRole` with a
`scopeBranchId` parameter, so on an unconfigured company it would let anyone assign an Inventory role *and choose its
branch scope*. By the same test applied to Accounting and CRM, this qualifies. Omitting it looks like an oversight in
the matrix rather than a decision — but **it is not mine to decide.**

### 3.2 Two Platform items in the brief have no live action (−2, and this breaks comparability)

B7 lists three Platform items: `PlatformOps`, *migration administration*, *restricted security diagnostics*.

In live source there is **one** enforceable thing: the `[PlatformOps]` attribute. Migration administration does not
exist (no migration has been built — Batch A deliberately left `MigrationBatchId` dormant), and restricted security
diagnostics are *already* gated by `[PlatformOps]` rather than by a distinct action of their own.

So those two items are **aspirations in the matrix, not classifiable actions today**. If the accepted 14 counted them,
then its 14 contained two entries with nothing to enforce, and the two totals are measuring different things.

### 3.3 Reconciliation arithmetic

| Reading | Total |
|---|---|
My derivation, live-enforceable actions only | **15** |
…excluding `Inventory.purchase` and `Inventory.manage` | 13 |
…excluding those two, plus counting the 2 aspirational Platform items | **15** again |
Brief's bullets read literally (Inventory's 3 document bullets as 3 actions, Platform as 3) | 17 |

**No reading produces 14.** That is why this is a stop and not an adjustment: I cannot tell which two items the
accepted matrix's 14 comprised, and guessing would mean choosing which financial actions stay open.

## 4. Why this blocks B2 and B3 as well, not just B7

The mandatory order is B2 → B3 → B4/B5 → B6 → B7, so it would be reasonable to expect B2–B5 delivered regardless. It
is not, and the reason is in the brief's own requirements:

* **B2 requirement 5** — *"Never-Bootstrap-Open actions cannot be represented by an allowing policy."* The storage
  layer's guard needs the list. Building it against a provisional list would bake a guess into a CHECK constraint and
  into the code that enforces it.
* **B3** — the behaviour-preserving seed must write `LegacyCompatibility` *"where current behavior is implicitly
  open"* and must **not** seed compatibility for a Never action. A seed is data written into every company. Seeding it
  from a provisional classification, then revising the classification, means either a data migration or a company
  silently carrying a policy that contradicts the final list.

Both are the kind of decision that is cheap now and expensive after it ships. So the list is genuinely upstream of the
storage, not merely of the enforcement.

## 5. The decision I need

**Confirm the Never-Bootstrap-Open set.** Specifically:

1. **`Inventory.purchase`** — Never, or not? It receives stock. *(My recommendation: Never.)*
2. **`Inventory.manage`** — Never, or not? It assigns roles with branch scope, exactly as Accounting and CRM `manage`
   do. *(My recommendation: Never — the same test that closes the other two closes this one.)*
3. **Migration administration** and **restricted security diagnostics** — confirm these are **not** separate
   classifiable actions today and collapse into `PlatformOps`, or name the live action each should map to.

With those three answers the list is fixed and B2 → B7 can be implemented in one pass without a provisional
classification anywhere in it.

## 6. What is unchanged

| | |
|---|---|
Production authorization behaviour | **unchanged** — Mechanism A still stands at all four call sites |
`BootstrapAccessPolicies` | not created |
Policy reader · decision contract · seed | not created |
Application suite | **849 / 849 · 0 failed · 0 skipped** |
Analyzer | CBA001 / CBA004 / CBA006 = **0 / 0 / 0**; debt **143** |
Endpoint totals | 391 = 157 + 91 + 143 |
Leftover probe databases | **0** |
`CrossBuyDB2` | present and untouched |
Risk register | **not updated** — RISK-040/041/042/043/044/045 all remain Open at unchanged severity, since B19 permits movement only on implementation evidence |

The four bootstrap call sites confirmed in B1 remain exactly as found: `AccountingAccessService:60`,
`InventoryAccessService:48`, `InventoryAccessService:91`, `CrmAccessService:59`.
