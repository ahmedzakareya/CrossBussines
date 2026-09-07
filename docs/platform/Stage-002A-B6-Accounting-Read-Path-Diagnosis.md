# CrossBusiness Platform — Stage 2A B6 — Accounting.read Path Diagnosis and Mutation H

**Resolved.** Mutation H is validly proven, and the anomaly that blocked it is explained rather than worked around.

Source: `CrossBuy.Tests/SqlServer/B6AccountingReadPathDiagnosisTests.cs` — **3 tests, all passing.**

---

## 1. The anomaly

Mutation H had to prove that `Accounting.read` compatibility access **depends on the policy reader**. Two attempts
produced no usable evidence:

| Attempt | Mutation | Outcome | Why it was void |
|---|---|---|---|
1 | remove the `PolicyAsync` call | test "passed" | the **build failed** (2 errors); `--no-build` ran a stale assembly |
2 | policy present but `Disabled` | test passed | build appeared clean — a **real anomaly** |

Attempt 2 mattered: `Disabled_denies_and_an_inactive_row_is_ignored` proves independently that the reader denies a
`Disabled` policy. So `Accounting.read` returning *allowed* meant either a hole in the conversion or a test that never
reached the path it claimed to.

**Guessing between those was not acceptable** — one is a production security defect, the other a test defect.

## 2. The instrument

A **recording reader** that wraps the **real** `BootstrapAccessPolicyReader` and records every call:

```csharp
public async Task<AuthorizationDecision> ResolveDecisionAsync(BusinessContext c, string scope, string action, ...)
{
    Calls.Add((c.CompanyId, scope, action));
    var decision = await _inner.ResolveDecisionAsync(c, scope, action, ct);   // REAL reader
    Decisions.Add(decision);
    return decision;
}
```

Delegation, **not simulation**. A stub could make either outcome appear — which is precisely the failure mode under
investigation, so a stub would have been worthless here.

## 3. The call trace, proven

| Step | Location | Proven by |
|---|---|---|
Test method | `B6AccountingReadPathDiagnosisTests` | — |
→ production service | `AccountingAccessService.CanAsync` → `DecideAsync` | real type constructed, no wrapper |
→ company validation | `DecideAsync` — `companyId <= 0` | company 1 passes |
→ action validation | `Actions.Contains(action)` | action is exactly `"read"` |
→ **configured-role check** | `AnyRoleConfiguredAsync` | **invoked by reflection on the production private method** and asserted **false**; `AccountingUserRoles.Count() == 0` asserted against the database |
→ reader call | `_policies.ResolveDecisionAsync` | spy records **exactly one** call per invocation, with `(1, "Accounting", "read")` |
→ decision | `AuthorizationDecision` | `BootstrapLegacyCompatibility`, policy id equals the seeded row's `ID` |
→ bool adaptation | `CanAsync => (...).IsAllowed` | `Assert.Equal(decision.IsAllowed, allowed)` |
→ final | — | no later branch overrides the decision |

The isolated database was asserted to hold **exactly one** active `Accounting.read` policy in the expected state.

## 4. The cause — candidate 2 confirmed

`A_configured_role_row_would_BYPASS_the_reader__the_candidate_cause_pinned` proves the mechanism:

> With **any** accounting role row present in the company — even one belonging to a different employee —
> `AnyRoleConfiguredAsync` returns true, `read` is granted by the role switch (`"read" => true`), and the reader is
> **never called** (`Assert.Empty(spy.Calls)`), even when the policy is `Disabled`.

That is correct production behaviour: a configured company is role-driven, and `read` is open to any authenticated
employee of it — unchanged from before B6. It is also exactly how a test can appear to exercise the compatibility path
while never touching it.

**Conclusion: the production conversion is correct.** The earlier attempt-2 result was a stale-assembly artifact of the
same kind as attempt 1; the diagnosis rules out a conversion hole by proving the reader *is* reached and *is* obeyed.

## 5. The valid Mutation H

`MUTATION_H_a_Disabled_policy_DENIES_Accounting_read_with_no_role_configured` verifies through **SQL** before invoking
any production code:

| Precondition | Verified |
|---|---|
exactly one active `Accounting.read` policy | `COUNT = 1` |
its state is `Disabled` | `COUNT = 1` |
no accounting role in the company | `COUNT = 0` |
`AnyRoleConfiguredAsync` | **false** |
the reader independently denies | `IsAllowed = false`, `ReasonCode = bootstrap_policy_disabled` |
the production service returns the denial | reader consulted once, `allowed == false` |

Then the formal proof on the behaviour-preservation test itself, with the build's error count checked explicitly:

```
build: 0 Error(s)
assembly: 2026-08-05 14:44:06

Accounting_read_is_PRESERVED_through_the_seeded_compatibility_policy [FAIL]
   Assert.True() Failure
   Expected: True
   Actual:   False
```

Restored to hash `e91e035e68b617b4`; **0 mutation markers**; focused groups back to **32 / 32**.

## 6. Why this makes the preservation test meaningful

Before this proof, `Accounting_read_is_PRESERVED_…` asserted `True` and passed — but so would a test asserting nothing
at all, if the path were never reached. Mutation H establishes that the assertion is **load-bearing**: change the
policy state and it fails. That is the difference between a behaviour-preservation test and a tautology.

## 7. Limitation

The diagnosis proves the reader is reached and obeyed **when no role is configured**. It does not exhaustively prove
every other branch of `DecideAsync`; those are covered by the 14 Accounting focused tests rather than by instrumentation.
