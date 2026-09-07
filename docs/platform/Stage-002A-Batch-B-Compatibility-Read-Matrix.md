# CrossBusiness Platform — Stage 2A Batch B — Compatibility Read Matrix

Source-derived classification of every production path B6 will affect. Implementation-ready, with the unresolved
business decisions flagged rather than guessed.

**Four of the twelve rows require a business decision. B6 must not convert those four automatically.**

---

## 1. The five Mechanism A sites

| # | Site | Governs | Converted by B6 |
|---|---|---|---|
1 | `AccountingAccessService.cs:60` | all 5 Accounting actions | yes |
2 | `InventoryAccessService.cs:48` | all 4 Inventory module actions | yes |
3 | `InventoryAccessService.cs:91` | warehouse scope resolution | yes — **receives no policy row** (§4) |
4 | `CrmAccessService.cs:59` | all 3 CRM actions | yes |
5 | `CrmAccessService.VisibleOwnerIdsAsync` | CRM record owner visibility | yes — via the **typed contract**, not a policy row (§5) |

All five are **unchanged** in this increment.

## 2. The matrix

`Sens.` = data sensitivity · `Mut.` = mutates data · `Bal./Cost/Cust.` = exposes balances / stock costs / customer
data · `Seeded` = has a B3 policy row.

| Scope | Action | Caller / endpoint | Current behaviour | Current source | Company | Branch | Warehouse | Owner/Team | Sens. | Mut. | Bal. | Cost | Cust. | **Classification** | Policy state | Expiry | Review | Future B6 behaviour | Seeded | Seed identity | Required tests | Decision needed |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
Accounting | `read` | every `[AccPerm("read")]` screen | `"read" => true` for any authenticated user in the company; bootstrap at `:60` returns before the switch | unconditional authenticated | ✅ `context.CompanyId` | ❌ none | — | — | **High** | no | **yes** | ❌ | ❌ | **LegacyCompatibilityRead** | LegacyCompatibility | none | **yes** | allowed via policy, source `BootstrapLegacyCompatibility` | ✅ | `Accounting.read` | reader allow + source; matrix row | **⚠ YES — §6.1** |
Accounting | `post` | journals, sales/purchase invoices | `chief \|\| acct`; bootstrap allows when unconfigured | role, bootstrap-bypassable | ✅ | ❌ | — | — | Critical | **yes** | yes | ❌ | ❌ | **NeverBootstrapOpen** | n/a | n/a | n/a | denied unless a real role allows | ❌ | — | `Every_Never_action_denies…` | no |
Accounting | `pay` | receipts, payments, transfers | `chief \|\| acct \|\| cashier` | role, bootstrap-bypassable | ✅ | ❌ | — | — | Critical | **yes** | yes | ❌ | ❌ | **NeverBootstrapOpen** | n/a | n/a | n/a | denied unless a real role allows | ❌ | — | same | no |
Accounting | `manage` | period close; `AssignAccRole`; PlatformOps fallback | `chief` | role, bootstrap-bypassable | ✅ | ❌ | — | — | Critical | **yes** | yes | ❌ | ❌ | **NeverBootstrapOpen** | n/a | n/a | n/a | denied unless a real role allows | ❌ | — | same | no |
Accounting | `currency-override` | non-branch-currency documents | `chief \|\| acct` | role, bootstrap-bypassable | ✅ | ❌ | — | — | Critical | **yes** | yes | ❌ | ❌ | **NeverBootstrapOpen** | n/a | n/a | n/a | denied unless a real role allows | ❌ | — | same | no |
Inventory | `read` | every `[InvPerm("read")]` screen | `"read" => true` for any authenticated user in the company | unconditional authenticated | ✅ | ❌ none | ❌ none | — | **High** | no | ❌ | **yes** | ❌ | **LegacyCompatibilityRead** | LegacyCompatibility | none | **yes** | allowed via policy, source `BootstrapLegacyCompatibility` | ✅ | `Inventory.read` | reader allow + source | **⚠ YES — §6.2** |
Inventory | `doc` | movement, transfer, count, assembly, landed, sales | `mgr \|\| keeper` | role, bootstrap-bypassable | ✅ | ❌ | partial | — | Critical | **yes** | ❌ | yes | ❌ | **NeverBootstrapOpen** | n/a | n/a | n/a | denied unless a real role allows | ❌ | — | same | no |
Inventory | `purchase` | `CreatePurchaseOrder`, `GeneratePO`, **`CreateGoodsReceipt`**, **`ConvertPoToInvoice`** | `mgr \|\| purch` | role, bootstrap-bypassable | ✅ | ❌ | ❌ | — | Critical | **yes** | yes | yes | ❌ | **NeverBootstrapOpen** | n/a | n/a | n/a | denied unless a real role allows — **whole action closed, vocabulary defect §6.3** | ❌ | — | same | no |
Inventory | `manage` | master data; `AssignRole(…, scopeBranchId)` | `mgr` | role, bootstrap-bypassable | ✅ | ❌ | ❌ | — | Critical | **yes** | ❌ | yes | ❌ | **NeverBootstrapOpen** | n/a | n/a | n/a | denied unless a real role allows | ❌ | — | same | no |
Inventory | `warehouse-access` | warehouse-scope resolution (`:91`) | unconfigured ⇒ **all warehouses**; `InventoryManager` or unscoped keeper ⇒ all | second bootstrap site | ✅ | **exact when configured** | **exact when configured** | — | Critical | no | ❌ | **yes** | ❌ | **NeverBootstrapOpen** | n/a | n/a | n/a | denied unless a real role allows — **must not widen to company scope** | ❌ | — | branch/warehouse exactness | no |
CRM | `read` | every `[CrmPerm("read")]` screen | `"read" => true`; bootstrap at `:59` returns first | unconditional authenticated | ✅ | ❌ | — | via `VisibleOwnerIdsAsync` | **High** | no | ❌ | ❌ | **yes** | **LegacyCompatibilityRead** | LegacyCompatibility | none | **yes** | allowed via policy, source `BootstrapLegacyCompatibility` | ✅ | `Crm.read` | reader allow + source | **⚠ YES — §6.4** |
CRM | `edit` | account/lead/opportunity/contact writes | configured: `mgr \|\| rep \|\| mkt`; **unconfigured: allowed** by the `:59` return | bootstrap-only allow | ✅ | ❌ | — | via `VisibleOwnerIdsAsync` | **High** | **yes** | ❌ | ❌ | **yes** | **LegacyCompatibilityRead** *(mutating — see note)* | LegacyCompatibility | none | **yes** | allowed via policy, `ReviewExpected` | ✅ | `Crm.edit` | reader allow + seed reconciliation | **⚠ YES — §6.4** |
CRM | `manage` | `AssignCrmRole`, pipeline config | `mgr` | role, bootstrap-bypassable | ✅ | ❌ | — | — | Critical | **yes** | ❌ | ❌ | yes | **NeverBootstrapOpen** | n/a | n/a | n/a | denied unless a real role allows | ❌ | — | same | no |
CRM | *(owner scope)* | `VisibleOwnerIdsAsync` | `null` ⇒ **unrestricted, including when unconfigured** | nullable sentinel | ✅ | ❌ | — | **the filter itself** | **High** | no | ❌ | ❌ | **yes** | **RequiresBusinessDecision** | n/a — typed contract | n/a | **yes** | `CrmVisibleOwnerScopeResult`, provenance carried | ❌ | — | 22 contract tests | **⚠ YES — §6.5** |

`Crm.edit` is classified `LegacyCompatibilityRead` because that is the policy state that preserves today's behaviour,
**not** because it is a read. It mutates. The label is the compatibility category; the `Mut.` column is the truth, and
the entry carries `ReviewExpected = true` precisely so the mismatch is visible.

## 3. Reconciliation — 41 / 32 / 4 / 5 / 15

| Figure | Meaning |
|---|---|
**41** | **Superseded** historical planning figure. No reading of live source reaches it (32 / 35 / 45 / 46 were all attempted). Retained as history, not authority. |
**32** | Complete live bootstrap-eligibility **inventory** — every action a policy *could* cover. An inventory, not a plan. |
**4** | **Implemented** seed set — the policies B6 will actually read. |
**5** | Mechanism A sites (§1). |
**15** | Never-Bootstrap-Open entries — `NeverBootstrapOpen.All`, one authoritative list, never forked. |

### Why each number is what it is

1. **41 superseded** — not reproducible from live action vocabularies; the frozen matrix predates them.
2. **32 is an inventory** — it counts eligibility, not intent. Seeding all 32 would write rows no production path
   reads.
3. **Only 4 seeded** — 28 of the 32 belong to HR, Projects, Tasks and Communication, which already run through the
   action-aware `ModuleAccessServiceBase`. B6 does not touch them and they never consult this reader.
4. **Inventory warehouse site gets no policy** — `warehouse-access` is Never-Bootstrap-Open. A compatibility policy
   there would widen an exact branch/warehouse restriction to company scope, which is the one thing that path exists
   to prevent.
5. **CRM owner scope uses a typed contract, not a fifth seed row** — it is not an *action*; it is the shape of a
   filter (`HashSet<int>?`). A policy row cannot express "unrestricted vs no-one", and inventing an action code for it
   would create a permission the module never asks about.
6. **`Crm.edit` is seeded and flagged** — the `:59` bootstrap return precedes the action switch, so an unconfigured
   company genuinely permits edit today. Omitting it would make B6 a silent tightening rather than behaviour-preserving.
7. **Manufacturing not seeded** — it has an adapter mapping to Inventory roles and **no `IModuleAccessService` of its
   own**, so it has no independent bootstrap behaviour to preserve.
8. **POS gets no policy** — `BranchUserRoles` is read before any `BusinessContext` exists and POS fails closed without
   a role. A policy row would imply a bootstrap path that does not exist. Enforced by `CK_..._NotPos`.
9. **HR / Projects / Tasks / Communication not seeded for B6** — already action-aware; their exclusions
   (`payroll-manage`, `confidential-view`, `manage`, `outbox-manage`) are preserved by not touching them.
10. **No artificial permission codes** — every classification maps to an action the live source already declares.

## 4. Business decisions required — B6 must NOT convert these four automatically

### 6.1 `Accounting.read` exposes ledger balances to any authenticated employee

* **Source**: `AccountingAccessService.cs:29` — `"read" => true`, commented *"any authenticated user in this company
  may view"*. No branch filter.
* **Exposure**: trial balance, journals, customer/vendor statements, invoice totals — the company's full financial
  position, to anyone who can sign in.
* **Safe default**: seed `LegacyCompatibility` (done) so B6 changes nothing, and decide separately.
* **Options**: (a) keep as compatibility indefinitely; (b) `InstallationOnlyRead` with expiry, forcing a role
  decision; (c) `RoleRequiredRead` — any accounting role suffices.
* **Recommended**: (b). It preserves behaviour now and makes the exposure end by a date rather than by intention.
* **B6 blocks?** No for the conversion, **yes for the classification** — the seeded row keeps behaviour identical, so
  B6 may convert the site, but the row must not be treated as a permanent answer.

### 6.2 `Inventory.read` exposes stock costs with no branch or warehouse filter

* **Source**: `InventoryAccessService.cs:18` — `"read" => true`. The warehouse restriction lives at `:91` and governs
  `warehouse-access`, **not** `read`.
* **Exposure**: item costs, stock valuation, movement history across every branch and warehouse.
* **Options**: (a) compatibility indefinitely; (b) installation-only with expiry; (c) role-required; (d) split
  cost visibility from quantity visibility — a new action, which this increment must not invent.
* **Recommended**: (b) now, (d) as a Stage 2C candidate.
* **B6 blocks?** No for conversion, **yes for classification**.

### 6.3 `Inventory.purchase` — the vocabulary defect, reported and closed whole

* **Source**: one action code gates `CreatePurchaseOrder`, `GeneratePO` (pre-stock drafts), `CreateGoodsReceipt`
  (**creates stock**) and `ConvertPoToInvoice` (**accounting effect**).
* **Decision already taken**: keep the entire action closed rather than leave stock receipt open; do not invent a
  split code in this increment.
* **Open item**: whether to split it later into `purchase-order` / `purchase-receipt`. **Not** a B6 blocker — closing
  the whole action is strictly safer than the status quo.

### 6.4 CRM `read` + `edit` expose customer data, and `edit` mutates it

* **Source**: `CrmAccessService.cs:59` returns before the switch, so an unconfigured company gets both.
* **Exposure**: accounts, leads, opportunities, contacts — and unrestricted **write** access to them.
* **Options for `edit`**: (a) compatibility indefinitely (today's behaviour); (b) `Temporary` with a short expiry;
  (c) close it, accepting that unconfigured companies lose CRM editing.
* **Recommended**: (b). A mutating action reachable with no role configured should end on a date.
* **B6 blocks?** **YES for `Crm.edit`.** It is the only *mutating* action in the seed plan. B6 should not convert the
  CRM site until an owner has chosen (a), (b) or (c), because whichever is chosen changes what B6 preserves.

### 6.5 `VisibleOwnerIdsAsync` returns `null` — unrestricted — for an unconfigured company

* **Source**: `ICrmAccessService.cs:27-28` — *"null = unrestricted (viewer/marketing/**unconfigured**)"*.
* **Exposure**: every owner's CRM records company-wide, with no role configured. `null` and empty are one keystroke
  apart and mean opposite things.
* **Delivered**: `CrmVisibleOwnerScopeResult` makes the three states explicit and fails closed on misconfiguration;
  `ToLegacy` maps `ConfigurationError` to an **empty** set, never `null`.
* **Options**: (a) preserve unrestricted under compatibility; (b) restrict to the caller's own + team owners;
  (c) require a CRM role for any owner visibility.
* **Recommended**: (a) for B6 (behaviour-preserving), with (b) as the hardening target.
* **B6 blocks?** **YES.** Converting this filter changes which CRM records users see. The typed contract is ready; the
  policy choice is not made.

## 5. Classification summary

| Classification | Count | Actions |
|---|---|---|
`NeverBootstrapOpen` | **8** | Accounting `post`/`pay`/`manage`/`currency-override`; Inventory `doc`/`purchase`/`manage`/`warehouse-access`; CRM `manage` *(9 rows; `Platform.PlatformOps`, `Projects.billing`, HR ×2, Tasks, Communication complete the 15 outside this matrix's scope)* |
`LegacyCompatibilityRead` | **4** | `Accounting.read`, `Inventory.read`, `Crm.read`, `Crm.edit` — the seeded set |
`RequiresBusinessDecision` | **1** | CRM owner scope |
`ExplicitlyAllowedCompatibilityRead` · `InstallationOnlyRead` · `RoleRequiredRead` | **0** | none assigned — all four compatibility reads start as `LegacyCompatibility` and are candidates for `InstallationOnlyRead` pending §6 |

**No row is labelled `LegacyRole`.** Unconditional authenticated access is *not* role authorization, and calling it
that is what let this exposure sit unexamined.

## 6. B6 readiness verdict

| Site | Ready to convert |
|---|---|
`AccountingAccessService.cs:60` | **Yes** — seeded, behaviour-preserving. Classification decision (§6.1) may follow. |
`InventoryAccessService.cs:48` | **Yes** — seeded. Classification decision (§6.2) may follow. |
`InventoryAccessService.cs:91` | **Yes** — `warehouse-access` is Never; conversion strictly narrows. |
`CrmAccessService.cs:59` | **NO** — blocked on §6.4 (`Crm.edit`). |
`CrmAccessService.VisibleOwnerIdsAsync` | **NO** — blocked on §6.5. |

**Three of five sites are ready. Two are blocked on decisions, both in CRM.**
