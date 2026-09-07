# CrossBusiness Platform — Stage 2A B6 — Mechanism Site Reconciliation

Semantic re-derivation of all five Mechanism A sites after restoring from the verified artifact. **Every
`AnyRoleConfiguredAsync` occurrence is tied to its enclosing method and its semantic role** — grep counts alone are
not used as proof, because a grep count is exactly what misled the previous increment (§4).

---

## 1. All five sites, by method and behaviour

| # | Site | File · line | Enclosing method | Form | Semantic role | Status |
|---|---|---|---|---|---|---|
1 | Accounting module | `AccountingAccessService.cs:101` | `DecideAsync` | `if (await AnyRoleConfiguredAsync(...))` | **role gate** — enters the configured-role branch | **CONVERTED** |
2 | Inventory module | `InventoryAccessService.cs:87` | `DecideAsync` | `if (await AnyRoleConfiguredAsync(...))` | **role gate** | **CONVERTED** |
3 | Inventory warehouse | `InventoryAccessService.cs:184` | `CanUseWarehouseAsync` | `if (!await AnyRoleConfiguredAsync(...))` → reader block | **narrowed** — asks the reader instead of returning true | **CONVERTED** |
4 | CRM module | `CrmAccessService.cs:59` | `CanAsync` | `if (!await ...) return true;` | **original bootstrap allow** | **UNCHANGED** ✅ |
5 | CRM visible-owner | `CrmAccessService.cs:98` | `VisibleOwnerIdsAsync` | `if (!await ...) return null;` | **original unrestricted scope** | **UNCHANGED** ✅ |

## 2. The polarity change that matters

The conversion **inverted the sense** of the call at sites 1 and 2:

```
BEFORE:  if (!await AnyRoleConfiguredAsync(...)) return true;   // negative — bootstrap allow
AFTER:   if ( await AnyRoleConfiguredAsync(...)) { ...roles... } // positive — role gate
```

The same helper is still called, at the same point in the flow, but it now *selects the role branch* rather than
*granting everything*. That is why the literal string `AnyRoleConfiguredAsync(context.CompanyId, cancellationToken)) return true`
correctly **disappears** from both converted files — its absence is the intended outcome, not damage.

Site 3 keeps the negative form because the question there is still "is this company unconfigured?" — but the answer
now routes to `IBootstrapAccessPolicyReader` instead of returning `true`.

## 3. Non-bootstrap occurrences, accounted for

| File · line | Occurrence | Why it is not a site |
|---|---|---|
`AccountingAccessService.cs:67` | inside a `//` comment | documents what was removed |
`AccountingAccessService.cs:168` | the private helper **declaration** | the method itself |
`InventoryAccessService.cs:61`, `:172` | inside `//` comments | document what was removed |
`InventoryAccessService.cs:218` | the private helper **declaration** | the method itself |
`CrmAccessService.cs:115` | the private helper **declaration** | the method itself |

## 4. Reader wiring — counted per file

| File | `_policies.ResolveDecisionAsync` calls | Expected |
|---|---|---|
`AccountingAccessService.cs` | **1** | 1 (site 1) |
`InventoryAccessService.cs` | **2** | 2 (site 2 + site 3) |
`CrmAccessService.cs` | **0** | 0 — CRM is not converted |

## 5. Inventory role-path allows — both preserved

| Line | Statement | Semantic role |
|---|---|---|
`206` | `if (roles.Any(r => r.Role == "InventoryManager")) return true;` | **role path 1** — an inventory manager reaches any warehouse |
`210` | `if (keeperScopes.Any(s => s == null)) return true;` | **role path 2** — an unscoped keeper reaches any warehouse |

Both are **untouched**. Neither is a bootstrap path: each is reached only *after* the role query, and each is
predicated on a real role row. Sweeping them into the conversion would have been the over-tightening the brief warns
against.

## 6. Correction to the previous increment's diagnosis

The previous increment reported that `InventoryAccessService.cs` had been damaged by a bad restore, on the strength of
`grep -c 'AnyRoleConfiguredAsync(context.CompanyId, cancellationToken)) return true'` returning **0** for both
converted files.

**That reading was wrong.** Zero is the correct post-conversion count — the literal string is supposed to be gone.
Hash comparison against the verified archive proves both production files were **already byte-identical** to the
archive before any restore was attempted:

| File | Hash before restore | Archive hash | Identical |
|---|---|---|---|
`AccountingAccessService.cs` | `2660fe293f7a0518` | `2660fe293f7a0518` | **yes** |
`InventoryAccessService.cs` | `c4e83c0f1d1da748` | `c4e83c0f1d1da748` | **yes** |
`B6BootstrapConversionTests.cs` | `841827a3a109e46b` | `e91e035e68b617b4` | **NO** |

So the production conversions were never corrupted. The **test file** was — my Mutation H restore left it differing
from the archive. That is the real cause of the 6 failures, and it is a materially different fault from the one
previously reported: a damaged test, not damaged authorization code.

**The lesson, recorded:** a grep count is a proxy. A hash against a verified artifact is the fact. The previous
increment reached for the proxy under time pressure and drew the wrong conclusion from it.

## 7. Verdict

All five sites reconcile as intended: **three converted, two CRM unchanged, both Inventory role paths preserved, no
unrelated role logic removed, no over-tightening.**

This reconciliation is **static source analysis** and required no build — which is why it could be completed while the
shared tree is broken by other tabs (see the recovery delivery report).
