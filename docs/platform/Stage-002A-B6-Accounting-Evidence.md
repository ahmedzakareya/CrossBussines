# CrossBusiness Platform — Stage 2A B6 — Accounting Site Evidence

**Site 1 · `AccountingAccessService.cs:60` · CONVERTED · 14 / 14**

---

## 1. What was removed

```csharp
if (!await AnyRoleConfiguredAsync(context.CompanyId, ct)) return true;   // BEFORE the action switch
```

One line, ahead of the action switch, granting **every** accounting action — `post`, `pay`, `manage`,
`currency-override` — to any authenticated employee of a company that had not configured roles. It could not exclude
an action, because it ran before the action was examined, and no log distinguished its allow from a real role's.

## 2. What replaced it

`DecideAsync` returns an `AuthorizationDecision`; `CanAsync` is now `=> (await DecideAsync(...)).IsAllowed`.

1. company `<= 0` → deny `company_unresolved` — **no fallback to company 1**
2. unknown action → deny `unknown_action` — never falls through to `read`
3. **roles configured → the original switch, unchanged**, reporting `LegacyRole` with the role list as evidence
4. no roles → `IBootstrapAccessPolicyReader.ResolveDecisionAsync`, which evaluates `NeverBootstrapOpen` **before**
   querying any policy row

The polarity of the `AnyRoleConfiguredAsync` call inverted: it now *selects the role branch* rather than *granting
everything*.

## 3. Results

| Action | No role configured | Configured role |
|---|---|---|
| `read` | **allowed** via seeded policy · source `BootstrapLegacyCompatibility` · policy id returned | allowed (`LegacyRole`) |
| `post` | **denied** `never_bootstrap_open` | `ChiefAccountant`/`Accountant` only — unchanged |
| `pay` | **denied** | `Chief`/`Accountant`/`Cashier` — unchanged |
| `manage` | **denied** | `ChiefAccountant` only — unchanged |
| `currency-override` | **denied** | `Chief`/`Accountant` — unchanged |

Denials hold **even with a permitting `read` policy present**, proving the Never check is per-action.
A **hand-inserted** `ExplicitlyAllowed` row for `post` still denies — the reader never consults it.

`read` denies on **missing**, **expired** and **disabled** policy — three separate assertions.

Configured-role preservation: `ChiefAccountant` full · `Cashier` keeps `pay`, refused `post`/`manage`/
`currency-override` · `Auditor` read-only. Company mismatch and unresolved context deny.

## 4. PlatformOps — RISK-042

`PlatformOpsAttribute` falls back to `acc.CanAsync("manage")`. `Accounting.manage` is Never, so a compatibility policy
cannot hand out platform operations. Asserted directly.

## 5. Structured logging

Bootstrap decisions log at **Information** — compatibility is temporary and must be visible (RISK-041); role decisions
log at Debug. Fields: company, action, allowed, source, reason code, policy id. **No balance, account value or amount**
is logged.

## 6. Scope limitation — stated

This change replaces the **permission source only**. No query filter, row-level rule or returned data changed.
**`Accounting.read` still exposes ledger balances with no branch filter** — recorded as an unresolved business
decision in the Compatibility Read Matrix, deliberately not addressed here.
