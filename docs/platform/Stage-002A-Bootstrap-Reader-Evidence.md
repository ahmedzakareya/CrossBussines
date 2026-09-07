# CrossBusiness Platform — Stage 2A Batch B — B4 Bootstrap Policy Reader Evidence

**Complete and evidence-backed.** Real SQL Server only.

| File | SHA-256 (16) |
|---|---|
`CrossBuy/BL/Platform/BootstrapAccessPolicyReader.cs` | `8900589605881eb5` |
`CrossBuy/Models/Platform/BootstrapPolicyContracts.cs` | `9216b19729e7f04c` |
`CrossBuy.Tests/SqlServer/BootstrapPolicyStorageAndReaderTests.cs` | `f4ea49fcaebc629b` |

**44 tests, 0 failed, 0 skipped** (storage + reader share one class).

---

## 1. Purpose and design

The **one** read path for bootstrap policy, mirroring `IPlatformRoleDirectory`'s discipline for the same reason: three
things must be true of every bootstrap decision, and each is a separate mistake waiting to be made once per access
service. Written here once, tested here once.

`IBootstrapAccessPolicyReader` — `GetEffectivePolicyAsync`, `ListPoliciesAsync`, `ResolveDecisionAsync`,
`GetDecisionMetadataAsync`, `IsNeverBootstrapOpenAsync`.

**Session-free.** It takes a `BusinessContext`; it never reads `HttpContext`, `Session` or claims. Proven by
construction — every test runs with no web host, so the reader could not execute at all if it needed one.

## 2. The decision order — and why the order IS the security property

```
1. BusinessContext resolved?      no  -> Deny(CompanyUnresolved)
2. CompanyID > 0?                 no  -> Deny(CompanyUnresolved)
3. Scope known?                   no  -> Deny(UnknownScope)
4. ActionCode present?            no  -> Deny(UnknownAction)
5. NeverBootstrapOpen.Contains?   yes -> Deny(NeverBootstrapOpen)     <-- BEFORE the database is touched
6. Scope == Pos?                  yes -> Deny(PosHasNoBootstrap)
7. query the active policy        none-> Deny(NoPolicyConfigured)
8. state known / Temporary valid  no  -> Misconfigured(ConfigurationError)
9. Disabled / expired             yes -> Deny(PolicyDisabled | PolicyExpired)
10. permitting state              yes -> Allow(source named, policy id attached)
```

Step 5 runs **before the query**, not after. If the policy query ran first, a hand-inserted row would produce an allow
that a later check merely contradicts — and any refactor that returned early on the policy hit would ship that allow.
Placing the classification before the database means no such refactor is possible.

## 3. The raw-SQL subversion proof

`A_hand_inserted_allowing_policy_for_a_Never_action_still_DENIES` is the most important test in the batch.

A row is inserted **with raw SQL**, bypassing `Validate()`, claiming `State = 'ExplicitlyAllowed'` for
**`Accounting.post`** — the single most dangerous action in the system, the one that posts to the general ledger.

* The test first asserts the row **really exists** — otherwise it would pass by proving nothing.
* The reader returns `IsAllowed = false`, `ReasonCode = never_bootstrap_open`, `DecisionSource = Denied`.
* And `BootstrapPolicyId` is **null** — proving the decision never consulted the row at all.

This is what makes it safe for the 15 Never actions to live only in code rather than also in a CHECK constraint: a
subverted database cannot produce an allow.

`Every_Never_action_denies_with_the_Never_reason_code` covers all **14** in-scope entries as a theory
(`Platform.PlatformOps` has no module action vocabulary and is the 15th).
`The_Never_list_holds_exactly_the_confirmed_fifteen_entries` pins the count and the per-scope distribution
(Accounting 4 · Inventory 4 · CRM 1 · Platform 1 · Projects 1 · HR 2 · Tasks 1 · Communication 1) and asserts every
entry carries a reason — a classification with no stated reason cannot be reviewed.

## 4. States, expiry and one clock

| State | Outcome | Decision source |
|---|---|---|
`LegacyCompatibility` | allow | `BootstrapLegacyCompatibility` |
`Installation` | allow | `BootstrapInstallation` |
`Temporary` | allow before expiry, **deny after** | `BootstrapTemporary` |
`ExplicitlyAllowed` | allow | `BootstrapExplicitlyAllowed` |
`ReviewRequired` | **allow**, and metadata flags `RequiresReview` | `BootstrapLegacyCompatibility` |
`Disabled` | deny | — |
unknown | **`ConfigurationError`** | — |

`ReviewRequired` reports as compatibility rather than inventing a fifth bootstrap source the future console would have
to learn. It still permits — it means *somebody has been asked to look*, not *this is off*.

**One clock per call** (`UtcNow()` read once), matching `PlatformRoleDirectory`. Two expiry comparisons inside one
decision must not straddle a tick, or a policy expiring "exactly now" becomes non-deterministic.

A `Temporary` row with no expiry that **bypassed the CHECK** (inserted with the constraint disabled) returns
`ConfigurationError` / `temporary_without_expiry` — not an ordinary denial, because an administrator must fix it and no
role assignment will.

## 5. Decision sources and reason codes

13 sources: `DirectGrant`, `LegacyRole`, four `Bootstrap*`, `BusinessMembership`, `Ownership`, `Hierarchy`,
`SystemPolicy`, `PosBranchRole`, `Denied`, `ConfigurationError`.

Reason codes are lowercase machine tokens: `never_bootstrap_open`, `pos_has_no_bootstrap`, `no_bootstrap_policy`,
`bootstrap_policy_disabled`, `bootstrap_policy_expired`, `temporary_policy_without_expiry`,
`unknown_bootstrap_policy_state`, `unknown_scope`, `unknown_action`, `company_unresolved`, `bootstrap_policy_permits`.

This is RISK-041's mechanism: a bootstrap allow currently looks identical to a role allow in every log. Now every
decision names its source, and a caller never has to reconstruct the reason from tables.

## 6. The inversion — a missing policy DENIES

`A_missing_policy_DENIES_which_is_the_inversion_Batch_B_exists_to_make`.

Today the **absence** of configuration means open. Once B6 consults this reader, absence means **closed**. That
inversion is the entire point of Batch B, and the B3 seed is what makes it safe — it records today's implicit openness
explicitly before B6 runs.

Also proven: an **inactive** row is ignored even when its state is permitting
(`Disabled_denies_and_an_inactive_row_is_ignored`), and company isolation holds — a policy in company 2 does not answer
company 1 (`Company_isolation_holds…`).

## 7. Mutation A — and a weak mutation caught first

| Attempt | Mutation | Result |
|---|---|---|
1 | Never gate moved to just **before** the allow return | **tests still passed** — the gate still executed, so the mutation was ineffective, **not** the test |
2 | Never gate **removed** from the decision path (placing it after a `return` is dead code) | **15 tests FAILED** |

Restored; `grep -c MUTATION` → **0**. The first attempt is recorded because it is the more interesting result: a
mutation proof can fail to prove anything, and the honest response was to strengthen the mutation rather than accept
the green.

## 8. No caching — a decision, not an omission

Deferred until B10 provides an invalidation signal. A cached bootstrap decision would survive a policy being disabled
or expiring, so the cache would keep a module open after an administrator closed it. Adding caching before there is a
way to invalidate it would make the expiry guarantee (§4) unenforceable.

## 9. Known limitations

* **Nothing consumes the reader.** B6 is inactive, so no production authorization outcome depends on it. The reader is
  proven in isolation, not in the decision path.
* **`ListPoliciesAsync` has no paging** — capped by nothing. Acceptable at four rows per company; not at scale.
* **`GetEffectivePolicyAsync` applies no Never check** by design: it returns the row, not a decision. A caller that
  mistook it for a decision would bypass step 5. Only `ResolveDecisionAsync` and `GetDecisionMetadataAsync` decide.
* **No metrics or event emission.** Bootstrap allows are logged at Information; aggregate exposure counting is B12.

## 10. Environment

`CrossBuyDB2` — **present and untouched**; no SQL executed against it. Evidence from
`CrossBuyProbe_BootstrapPolicy_*`, dropped with confirmed removal. **0 probes remain.**
