# ADR-023 — Controlled Company-Isolation Bypass

**Status:** Accepted (Stage 1 Batch B / B3)
**Date:** 2026-08-03
**Supersedes:** nothing. **Depends on:** ADR-022 (BusinessContext resolution), ADR-009 (platform-ops authorization),
`Stage-001-B1-Entity-Isolation-Classification.md`.
**Blocks:** B2 (EF global query filters) — by instruction, no filter is enabled until the bypass exists and its
tests pass.

---

## 1. Context

B2 will install EF global query filters on 12 pilot entities. A global filter is unconditional by construction: it
rewrites every query for that entity, including queries that are *supposed* to see more than one company. B1
identified four such flows, two of them Critical:

| # | Flow | Why a filter breaks it | Severity |
|---|------|------------------------|----------|
| 1 | `BusinessEventDispatchWorker` loads each claimed event **by id** (`BusinessEventDispatchWorker.cs:116`) | `BusinessEventDispatch` is **one queue for every company**, claimed in a single atomic statement. Filtered, the by-id load returns `null` for any event outside the scope's company, and the worker marks a healthy row `Failed` with "Event N no longer exists" while burning an attempt. Every company but one silently stops receiving notifications. | **Critical** |
| 2 | `NotificationProjectionConsumer` reads `Notifications` by `(recipient, dedupKey)` with no company predicate (`NotificationProjectionConsumer.cs:89`) | This is the **only real idempotency guard** — `NotificationService`'s own check suppresses *unread* duplicates only. Filtered, a row belonging to another company is invisible, the guard answers "not sent yet", and every stale-claim redelivery sends a **duplicate** notification. | **Critical** |
| 3 | Business Event Monitor elevated cross-company view | A shipped feature (ADR-009). Filtered without a bypass it would return a grid **narrowed to the operator's own company while the screen says "all companies"** — the failure mode where the screen reads as "nothing is wrong elsewhere". | High |
| 4 | Anonymous storefront (`HomeController.Store`, `StoreController`) | Has **no `BusinessContext` at all** — nobody is signed in, so no company can be resolved from an identity. Filtered, an unresolved scope reads nothing and the storefront goes blank. | High |

These four are not the same kind of operation, and that is the whole design problem.

## 2. Decision

Introduce **`ICompanyIsolationBypass`** as the single sanctioned way to read outside the current company scope,
with **four distinct kinds** rather than one "ignore filters" flag.

```
CompanyBypassKind          AllowsCrossCompany  ReadOnly  Who may hold it
─────────────────────────────────────────────────────────────────────────────────────────────
CrossCompanyAdministration        yes             no     authenticated + resolved employee
                                                         + a PlatformOpsAttribute.AdminRoles role
PlatformDispatch                  yes             no     System / Worker contexts only,
                                                         via BeginPlatformDispatch (structural)
PlatformMonitoring                yes             yes     same as CrossCompanyAdministration
PublicCompanyRead                 NO              yes     nobody — anonymous, via
                                                         BeginPublicCatalogRead only
```

### 2.1 Why four kinds and not one flag

Collapsing them would mean **the anonymous public catalogue and a consolidation report share a permission** — the
weakest caller would define the strongest right. Concretely:

* `PublicCompanyRead` **does not unrestrict at all**. It *pins* the scope to one configured company and stays
  fully filtered. It exists because the storefront is anonymous, not because it needs to cross companies —
  `PublicCompanyRead.AllowsCrossCompany` is `false` and there is no path that makes it true. This is the direct
  implementation of the instruction *"Do not collapse public catalogue access into a cross-company administrative
  permission."*
* `PlatformMonitoring` is separated from `CrossCompanyAdministration` so the monitor's elevated view can be
  **revoked on its own** without also revoking business administration. It is also marked read-only.
* `PlatformDispatch` is the broadest read in the system, so it is reserved for trusted platform code and is
  **unreachable through the public `Begin` entry point**.

### 2.2 The flag a query filter must test

`ICompanyScopeHolder.AllowsCrossCompany` — **not** "is a bypass active". The two differ precisely because
`PublicCompanyRead` is a bypass that pins rather than unrestricts. A filter written against "a bypass is active"
would hand the anonymous storefront every company's data.

### 2.3 Where the state lives

On the **scoped** `ICompanyScopeHolder`. Not a static, not an `AsyncLocal`.

`CompanyScopeHolder` is registered `Scoped` and `CrossDbContext` is `Scoped` with no pooling, so one holder belongs
to exactly one `DbContext` instance. Two concurrent requests therefore have **no shared cell to leak through** —
the isolation is structural rather than a discipline.

An `AsyncLocal` was rejected: it would flow into places nobody intended (a fire-and-forget continuation, a pooled
thread) and would be a genuine global with an ambient value. `Stage1BypassTests` includes the test that
distinguishes them — work started *inside* an active bypass, but holding its own scope, does not inherit it.

**Nesting is refused, not stacked.** Two overlapping bypasses in one scope would make "which right is in force"
ambiguous at the exact moment it matters. A refused nested request leaves the outer grant intact.

### 2.4 The deliberate deviation: `BeginPlatformDispatch` takes no `BusinessContext`

The B3 requirement says a bypass requires an explicit `BusinessContext`. The dispatcher is the one exception, and
the reason is recorded rather than glossed:

> The dispatcher has no identity and — crucially — **no single company**. `BusinessEventDispatch` is one queue for
> every company, so the pass legitimately spans companies. To give this method a `BusinessContext` we would have to
> **invent** a `CompanyId`, and the only value available would be a constant — which is precisely the silent
> company-1 fallback Stage 1 Batch A deleted. Faking a context to satisfy a signature would be worse than not
> having one.

Its authorization is therefore **structural**, and deliberately narrow:

1. it is its own method, so it cannot be reached through `Begin()`;
2. `CompanyBypassPolicy` refuses `PlatformDispatch` through `Begin()` for any interactive context;
3. the only production call sites are `BusinessEventDispatchWorker` (the batch) and the two consumers that run
   inside it;
4. every grant *and release* is audited, with the consumer named in the reason, and the audit line says
   `actor=(anonymous) scope=(unresolved)` rather than implying an identity or a company that does not exist.

### 2.5 No test-only allowance in the policy

`CompanyBypassPolicy` originally short-circuited on `BusinessContextSource.Test`. **That was removed in B3.** A
security rule that exempts a context source tests can construct is a rule the tests cannot prove. Tests now reach
each right the same way production does — an admin role for the administrative kinds, `BeginPlatformDispatch` for
the dispatcher. `Stage1BypassTests.The_policy_has_no_test_only_allowance` pins this.

Consequence, stated because it changed existing tests: two Stage 0 monitor tests passed `crossCompany: true` with an
ordinary context. They now present `PlatformTestHost.AdminContext()`. That is the intended behaviour, not a fixture
inconvenience — a test asking for the elevated view must hold the elevated right.

### 2.6 Audit

`ICompanyBypassAudit` records **Granted / Refused / Released**, each carrying kind, actor, scope company, pinned
company, reason, correlation id and timestamp. `LoggingCompanyBypassAudit` logs Granted and Released at
**Information** (a cross-company read is an event an auditor must be able to find) and Refused at **Warning** (a
refused bypass is exactly what an auditor wants to see — the same level ADR-008 uses for a refused retry).

Releases are audited, not only grants: an audit that records grants alone cannot answer *"was it still open when X
happened?"*.

## 3. Consequences

**Positive**

* B2 can install filters on the pilot entities without breaking the four flows — they already hold their rights.
* The four flows are now *visible*: each has a named kind, a reason string and an audit line, where previously
  "this query reads every company" was an unstated property of the code.
* The storefront's `private const int StoreCompanyId = 1`, duplicated in two controllers, is now one configured
  value (`Store:StoreCompanyId`) with a test.
* A missing context denies. Nothing anywhere resolves to company 1 implicitly.

**Negative / accepted**

* Every future flow that legitimately crosses companies must take a bypass explicitly. That is friction by design,
  but it is friction: a developer who does not know the mechanism exists will see a filtered read and may
  "fix" it by removing the filter. Mitigated by the B1 classification document and by this ADR.
* `Begin` throws rather than returning a null-object lease. A caller that forgets `using` holds the bypass for the
  rest of the scope. Mitigated by refusing nesting (a second grant fails loudly rather than compounding) and by
  the release audit line making a long-held grant visible.
* The bypass is **read-widening only**. It does not authorize writes, and `IsReadOnly()` is advisory today —
  nothing enforces it. **Open item for B4:** write-side enforcement must refuse a write under a read-only kind.

## 4. What B3 does NOT claim

* **No entity is filtered yet.** B3 built and proved the bypass; B2 installs the filters. Nothing in B3 makes the
  system company-isolated, and no maturity score moves on the strength of it.
* The tests prove the authorization rules, the lifetime, the leak-freedom, and the state a filter will read. Where
  an assertion depends on a filter, it is written against `NotificationCompanyPolicy.QueryFilter` — the exact
  predicate B2 will install — and each such test says so. There is no test claiming a row vanished because of a
  filter that does not exist.
* `IsReadOnly()` is recorded, not enforced (above).

## 5. Tests

`CrossBuy.Tests/Stage1BypassTests.cs` — **42 tests** (39 facts/theories, 42 executed cases), all passing on SQLite
and on SQL Server (`CROSSBUY_TEST_SQL`, scratch database, dropped afterwards).

At the time B3 completed the full suite stood at 325 passed / 0 failed / 0 skipped. After B2, B4, B5 and B7 it is
**412 passed / 0 failed / 0 skipped** on SQL Server — see ADR-024.

Mapped to the ten mandatory proofs:

| Required proof | Test(s) |
|---|---|
| unauthorized bypass rejected | `An_ordinary_employee_cannot_obtain_a_cross_company_bypass`, `An_accounting_manager_role_is_not_a_cross_company_right`, `An_interactive_user_cannot_hold_the_dispatcher_right_even_as_an_admin`, `An_unauthenticated_context_is_refused`, `An_identity_without_a_resolved_employee_is_refused`, `A_missing_context_denies_rather_than_becoming_unrestricted`, `A_bypass_without_a_reason_is_refused_on_every_entry_point`, `Public_catalogue_access_is_not_reachable_through_the_administrative_entry_point`, `The_policy_has_no_test_only_allowance` |
| authorized bypass sees cross-company records | `An_authorized_administrative_bypass_unrestricts_the_scope`, `The_B2_predicate_hides_another_companys_row_and_the_bypass_reveals_it`, `The_dispatch_bypass_names_no_company_and_no_actor_rather_than_inventing_them` |
| bypass ends with its scope | `The_bypass_ends_when_its_lease_is_disposed`, `The_bypass_ends_even_when_the_body_throws`, `Disposing_a_lease_twice_is_a_no_op`, `A_second_bypass_cannot_be_nested_inside_the_first`, `Two_scopes_do_not_share_bypass_state` |
| no leakage across parallel tasks/requests | `Bypass_state_does_not_leak_across_parallel_scopes` (32 concurrent scopes × 20 interleaved rounds), `A_child_task_with_its_own_scope_does_not_inherit_an_active_bypass` |
| dispatcher loads events across companies | `The_dispatcher_claims_and_loads_events_from_every_company_in_one_pass` (3 companies, real claim + real by-id load) |
| dedup correct across recipient companies | `The_dedup_guard_finds_an_existing_notification_that_belongs_to_another_company`, `Redelivering_the_same_event_does_not_duplicate_a_notification` |
| monitor elevated view honest | `The_elevated_monitor_view_takes_an_audited_monitoring_bypass_and_returns_other_companies`, `The_ordinary_monitor_view_takes_no_bypass_and_shows_only_the_callers_company`, `A_non_elevated_operator_cannot_widen_the_view_by_posting_another_company_id`, `An_unauthorized_elevated_request_is_refused_loudly_not_narrowed_silently` |
| public catalogue reads only the configured company | `The_public_catalogue_pins_the_configured_company_and_never_crosses_companies`, `No_public_catalogue_entry_point_accepts_a_company_from_the_caller` (structural, by reflection) |
| public scope cannot switch companies | `The_public_scope_cannot_be_applied_over_a_scope_that_is_already_another_company`, `The_public_pin_cannot_be_moved_to_a_second_company_within_one_scope`, `A_disabled_storefront_is_closed_rather_than_unfiltered` |
| company 1 is never an implicit fallback | `The_public_catalogue_uses_the_configured_company_not_company_one`, `An_unconfigured_store_company_is_refused_rather_than_defaulting_to_one`, `A_bypass_over_an_unresolved_scope_leaves_it_unresolved`, `The_B2_predicate_denies_an_unresolved_scope_rather_than_widening_it` |

Plus the `Notification.CompanyID` NULL policy tests — see
`Stage-001-B3-Notification-Company-Policy.md`.

## 6. Files

| File | Change |
|---|---|
| `Models/Platform/CompanyBypass.cs` | new — kinds, grant record, denial exception, `PublicCatalogOptions` |
| `BL/Platform/CompanyIsolationBypass.cs` | new — the bypass, the policy, the audit |
| `BL/Platform/CompanyScopeHolder.cs` | `ActiveBypass`, `AllowsCrossCompany`, `ApplyBypass` (refuses nesting) |
| `BL/Platform/NotificationCompanyPolicy.cs` | new — the NULL-company policy and the B2 predicate |
| `BL/Platform/BusinessEventDispatchWorker.cs` | wraps the batch in `BeginPlatformDispatch` |
| `BL/Platform/NotificationProjectionConsumer.cs` | takes the bypass + holder; wraps the handler |
| `BL/Platform/BusinessEventMonitorService.cs` | takes the bypass; `PlatformMonitoring` on the elevated paths |
| `BL/NotificationService.cs` | takes the bypass + holder; `BeginRecipientScope` |
| `Controllers/StoreController.cs`, `Controllers/HomeController.cs` | `BeginPublicCatalogRead`; company from config |
| `Program.cs` | `Configure<PublicCatalogOptions>("Store")`, policy + audit singletons, scoped bypass |
| `appsettings.json` | `Store` section |