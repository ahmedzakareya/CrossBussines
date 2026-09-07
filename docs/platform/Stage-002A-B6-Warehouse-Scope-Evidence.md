# CrossBusiness Platform — Stage 2A B6 — Warehouse Scope Site Evidence

**Site 3 · `InventoryAccessService.cs:91` (`CanUseWarehouseAsync`) · CONVERTED · 10 / 10**

**This conversion deliberately NARROWS behaviour and is approved as such.**

---

## 1. What was removed

```csharp
if (!await AnyRoleConfiguredAsync(context.CompanyId, ct)) return true;  // "not configured yet -> open"
```

An unconfigured company received access to **every warehouse** — the exact opposite of what this path exists to
enforce.

## 2. Why there is no seed row

`warehouse-access` is **Never-Bootstrap-Open**. A compatibility allowance here does not merely open a read: it
**widens an exact branch/warehouse restriction to company scope**, which is the one thing keeper scoping is for. So
B3 seeds nothing for it, and no policy state can open it.

The reader is still consulted rather than a hard-coded denial, so the refusal carries a decision source and reason
code like every other decision — and a future decision to introduce a warehouse policy would flow through the same
path instead of needing this branch rewritten.

## 3. Results

| Case | Result |
|---|---|
| No configured role | **denied** (previously: every warehouse) |
| Policy `LegacyCompatibility` / `Installation` / `ExplicitlyAllowed` / `ReviewRequired`, inserted with **raw SQL** | **denied** — all four |
| `InventoryManager` — **role path 1** | allowed, any warehouse — **unchanged** |
| Unscoped `WarehouseKeeper` — **role path 2** | allowed, any warehouse — **unchanged** |
| Branch-scoped keeper | own branch **only**; another branch denied — **exact** |
| Cross-company warehouse | denied even for an unscoped keeper |

## 4. Both role paths preserved

```csharp
if (roles.Any(r => r.Role == "InventoryManager")) return true;   // path 1, untouched
if (keeperScopes.Any(s => s == null)) return true;               // path 2, untouched
```

Neither is a bootstrap path — each is reached only *after* the role query and is predicated on a real role row.
Sweeping them into the conversion would have been over-tightening.

## 5. Structured logging

Company, warehouse, allowed, source, reason code, plus an explicit note that warehouse access is
Never-Bootstrap-Open and cannot be opened by policy.
