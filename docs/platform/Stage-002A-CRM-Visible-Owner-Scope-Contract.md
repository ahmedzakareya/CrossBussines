# CrossBusiness Platform — Stage 2A Batch B — CRM Visible-Owner Scope Contract

**Additive only. `ICrmAccessService.VisibleOwnerIdsAsync` keeps its signature and every production caller is
unchanged.** This contract exists so the mapping is proven by tests before B6 depends on it.

Source: `CrossBuy/Models/Platform/CrmVisibleOwnerScope.cs` · Tests: `CrossBuy.Tests/CrmVisibleOwnerScopeTests.cs`
(**22 tests, 0 skipped**).

---

## 1. The current semantics, exactly as the source states them

`ICrmAccessService.cs:27-28`:

```csharp
/// Owner-id whitelist the current user may SEE; null = unrestricted (viewer/marketing/unconfigured).
Task<HashSet<int>?> VisibleOwnerIdsAsync();
```

Three different answers, two representations, one of them a nullable reference:

| Legacy value | Meaning | Breadth |
|---|---|---|
`null` | see **every** owner's records in the company | **widest possible** |
empty set | see **nobody's** records | **narrowest possible** |
populated set | see exactly these owners' records | in between |

## 2. Why this is a security problem and not a style problem

**The two extremes are one keystroke apart, and both mistakes compile silently.**

* `?? new HashSet<int>()` — the reflex when a compiler warns about a nullable — converts *see everything* into
  *see nothing*.
* `if (ids == null || !ids.Any()) return all;` — the reflex when a list comes back empty — converts *see nothing*
  into *see everything*, on a CRM database.

Neither produces an error. Neither produces a log line. One breaks a screen; the other discloses every customer
record in the company.

**And note the third word in that comment: `unconfigured`.** A company with no CRM role configured receives `null`
through the bootstrap path — unrestricted, company-wide owner visibility. That is the exposure B6 must preserve
*deliberately* rather than inherit accidentally, and it is why this row is classified **RequiresBusinessDecision**
in the compatibility matrix.

## 3. The typed model

```csharp
enum CrmVisibleOwnerScopeState
{
    UnrestrictedCompanyScope,   // explicit — a value you can see in a debugger and a log
    RestrictedOwnerIds,         // requires ≥ 1 id
    NoAccess,                   // denies
    ConfigurationError,         // denies, and says an administrator must act
}
```

`CrmVisibleOwnerScopeResult` carries `State`, `CompanyID`, `OwnerIds`, `DecisionSource`, `ReasonCode`,
`BootstrapPolicyId`, `IsBootstrap` and `IsAllowed` — shaped to sit alongside `AuthorizationDecision` so B6 can carry a
bootstrap decision's provenance straight through to the owner filter.

### 3.1 Mapping table

| Legacy | Typed state | Allowed | Notes |
|---|---|---|---|
`null` | `UnrestrictedCompanyScope` | ✅ | carries **no** ids — "unrestricted" is not "all ids" |
empty set | `NoAccess` | ❌ | |
populated set | `RestrictedOwnerIds` | ✅ | duplicates normalised, deterministically ordered |
populated with a non-positive id | `ConfigurationError` | ❌ | **fails closed** — see §4 |
populated with a cross-company id | `ConfigurationError` | ❌ | when the company owner set is supplied |
company ≤ 0 | `ConfigurationError` | ❌ | no fallback to company 1 |

### 3.2 The reverse map fails closed

`ToLegacy` maps `ConfigurationError` → **empty set**, never `null`. Mapping a misconfiguration to `null` would turn a
fault into company-wide CRM visibility — the single worst available outcome for this type, and the reason the reverse
direction is written explicitly rather than left to a caller.

## 4. Invariants, and the one that is counter-intuitive

1. `UnrestrictedCompanyScope` is explicit, never inferred from absence.
2. `RestrictedOwnerIds` requires at least one id; "restricted to nobody" is `NoAccess`, because an empty restricted set
   would recreate the exact ambiguity this type removes.
3. `NoAccess` is distinct from an accidental empty state.
4. `ConfigurationError` denies, and is distinct from `NoAccess`.
5. `null` and empty are mechanically non-equivalent — asserted directly.
6. Duplicate ids normalise without changing the visible set.
7. **An invalid id fails the whole scope rather than being filtered out.** This is the counter-intuitive one and it is
   deliberate: silently dropping a bad id narrows the scope *and hides the defect that produced it*. A caller that
   built a set it did not validate has a bug, so the scope is suspect in its entirety.
8. Cross-company ids produce `ConfigurationError`, not a filtered set — same reasoning.
9. `CompanyID` is explicit; there is no company-1 fallback.
10. No `Session`, no `HttpContext` — proven by construction, since the whole test file runs with no web host.
11. `ReasonCode` is a lowercase machine token ≤ 60 chars, asserted structurally so a future change cannot start
    interpolating a customer name into it.

## 5. B6 integration plan

```csharp
// B6, when approved — not wired in this increment:
var decision = await _policies.ResolveDecisionAsync(context, "Crm", "read", ct);
var scope    = CrmVisibleOwnerScopeAdapter.FromAuthorizationDecision(decision);
// scope.State == UnrestrictedCompanyScope  -> today's behaviour, but now identifiable as compatibility
// scope.BootstrapPolicyId                  -> which policy permitted it
// scope.IsBootstrap                        -> true, so a CRM list built under compatibility is not
//                                             mistaken for role-authorized access
```

`FromAuthorizationDecision` reproduces today's behaviour for an allowed bootstrap decision (unrestricted) while
attaching the policy id and source. A denied decision becomes `NoAccess`; a `ConfigurationError` decision stays a
`ConfigurationError`. A **Never** action can never produce a visible scope — asserted for `Crm.manage`.

## 6. Tests — 22, all passing

Three legacy shapes · null-vs-empty non-equivalence · exact id preservation · duplicate normalisation · invalid id
fails closed · cross-company rejection · restricted-to-nobody becomes NoAccess · unresolved company (×2) with no
company-1 fallback · both denials deny · metadata preserved · role scope not reported as bootstrap · reason codes carry
no customer data · `ToLegacy` round trip · `ToLegacy` never returns null for a misconfiguration · allowed bootstrap
decision → unrestricted with provenance · denied → NoAccess · misconfigured → ConfigurationError · Never action →
never visible · no Session required · **production `VisibleOwnerIdsAsync` still returns `Task<HashSet<int>?>`**,
asserted by reflection so a premature signature change fails here.

## 7. Limitations

* **Not wired in.** No production CRM caller uses this type. The adapter is proven, not applied.
* **`companyOwnerIds` is optional.** Cross-company detection only runs when the caller supplies the company's owner
  set. Without it, a cross-company id maps to `RestrictedOwnerIds` unchecked — B6 must pass the set.
* **No caching.** Deferred until B10 provides an invalidation signal.
* **The policy decision is not made.** §6.5 of the compatibility matrix records the three options; B6 is **blocked**
  on this path until an owner chooses.
