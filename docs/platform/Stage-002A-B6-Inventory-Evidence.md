# CrossBusiness Platform — Stage 2A B6 — Inventory Module Site Evidence

**Site 2 · `InventoryAccessService.cs:48` · CONVERTED · 8 / 8**

---

## 1. What was removed

```csharp
if (!await AnyRoleConfiguredAsync(context.CompanyId, ct)) return true;   // BEFORE the action switch
```

It granted `doc`, `purchase` and `manage` — every stock mutation and role assignment — to any authenticated employee
of an unconfigured company.

## 2. What replaced it

The same action-aware shape as Accounting: company validation → action validation → **configured roles first
(unchanged switch)** → otherwise the policy reader. The row-level warehouse check still applies **on top of** the
action grant for `doc`/`purchase` with a named warehouse, exactly as before.

## 3. Results

| Action | No role configured | Configured role |
|---|---|---|
| `read` | **allowed** via seeded policy · source and policy id returned | allowed |
| `doc` | **denied** `never_bootstrap_open` | `InventoryManager`/`WarehouseKeeper` — unchanged |
| `purchase` | **denied** | `InventoryManager`/`PurchasingOfficer` — unchanged |
| `manage` | **denied** | `InventoryManager` — unchanged |

`read` denies on a missing or expired policy.
`PurchasingOfficer` keeps `purchase`, refused `doc` and `manage`. `InventoryManager` full.

## 4. `purchase` stays ONE closed action — the vocabulary defect

`InvPerm("purchase")` gates four endpoints across three impact classes:

| Endpoint | Effect |
|---|---|
| `CreatePurchaseOrder`, `GeneratePO` | pre-stock draft |
| `CreateGoodsReceipt` | **creates stock** |
| `ConvertPoToInvoice` | **accounting effect** |

Owner decision: keep the whole action closed rather than leave stock receipt open; **do not invent a split permission
code** in this increment. A test asserts the Never entry's reason still contains `VOCABULARY DEFECT`, so the decision
cannot be quietly reversed.

## 5. Structured logging

Company, action, allowed, source, reason code, branch. **No stock cost, quantity or valuation** is logged.

## 6. Scope limitation — stated

Permission source only. **`Inventory.read` still exposes stock costs with no branch or warehouse filter** — recorded
as an unresolved business decision, deliberately not addressed here.
