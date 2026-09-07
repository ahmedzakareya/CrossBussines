# Stage 2A — Batch B — Implementation Plan, B0 Closure and B1 Source Delta

**Delivered in this increment: B0 (complete) and B1 (complete).** B2–B19 are planned here and **not implemented** —
see §5 for why that boundary was drawn and what it costs.

---

## 1. B0 — the Grant Writer concurrency gap is CLOSED

The one gap the Batch A delivery report declared outstanding. Batch A proved the uniqueness race was *handled*; no
test ran two writers at once, so the claim rested on reading the code.

`PlatformGrantWriterConcurrencyTests` — **5 tests, all passing, stable across 5 consecutive runs**, on an isolated
probe (`CrossBuyProbe_GrantConcurrency_*`).

| Test | Proves |
|---|---|
`Two_concurrent_creates_of_the_same_grant_produce_exactly_one_active_row` | 1 Success · 1 Duplicate/Conflict · **1** active row · **1** `Created` event |
`Eight_concurrent_creates_still_produce_exactly_one_active_row` | 1 Success · 7 refused — two threads can pass by scheduling luck, eight make the index do the work |
`Concurrent_revoke_and_validity_update_do_not_lose_an_update_or_split_the_audit` | both orderings are coherent; row ends revoked; **1** `Revoked` event; no half-written audit |
`Two_concurrent_revokes_revoke_once_and_report_AlreadyRevoked_once` | `RevokedBy` still names the writer that actually did it |
`Concurrent_retries_with_the_same_idempotency_key_create_one_row_and_one_event` | a retry storm converges on **one** grant id |

A `Barrier` makes the threads start together. Without it the first task usually finishes before the second begins, and
the test would prove nothing while passing. Each thread gets **its own** `CrossDbContext` and writer — a `DbContext` is
not thread-safe, and sharing one would fail for a reason unrelated to the concurrency under test.

### 1.1 Two real defects the concurrency proof found

**(a) A deadlock was reported to the caller as an unhandled exception.** The first group run failed once, then passed
twice. A concurrency test that fails once and passes twice is a **flake, and a flake is not evidence** — so instead of
accepting the intermittent green, the cause was traced: several writers contending on the same unique index make SQL
Server pick a **deadlock victim (1205)** rather than failing the insert with a duplicate-key error. The writer only
classified 2601/2627, so 1205 propagated as a raw `DbUpdateException`.

Fixed by widening the classification to a named `IsConcurrencyLoss`. Anything outside {2601, 2627, 1205} still throws —
swallowing an unknown database error would turn a real fault into a "conflict" the caller retries forever.

**(b) Idempotency held for sequential retries and NOT for concurrent ones.** The pre-insert replay check cannot help a
retry storm: every concurrent caller reads "not found", one insert survives, and the losers received `Conflict`. That
breaks the guarantee B0.3 states — *the same command and key return the original result*.

That is the worst shape a guarantee can have: **it holds in the test you write first.** Fixed by re-reading by key
*after* the engine has arbitrated and returning `IdempotentReplay`, so a client resending while the first request is
still in flight gets the same answer either way.

Neither change alters the writer's contract; both are defect fixes, which is what B0 permits.

## 2. B1 — live-source inspection

### 2.1 Mechanism A is exactly as IMP-002 described — confirmed, not assumed

| Service | Line | Shape |
|---|---|---|
`AccountingAccessService` | **60** | `if (!await AnyRoleConfiguredAsync(context.CompanyId, ct)) return true;` |
`InventoryAccessService` | **48**, **91** | same, **twice** — the yes/no path *and* a second warehouse-scope path |
`CrmAccessService` | **59** | same |

All three sit **before** the action switch, so no action can be excluded. Backing probes are
`AccountingUserRoles` / `InventoryUserRoles` / `CrmUserRoles` respectively, each `AnyAsync(r => r.CompanyID == …)`.

**Source delta found (not in the frozen design):** `InventoryAccessService` has **two** bootstrap-open returns, not
one — line 91 governs warehouse scope resolution independently. IMP-002 counted one per module. B6 must therefore
convert **four** call sites across three services, and the Inventory warehouse path needs its own decision because
widening it would bypass branch/warehouse restrictions (B6's explicit requirement). Two further unconditional
`return true` statements at `InventoryAccessService:72` and `CrmAccessService:81` are **not** bootstrap — they are
role-path allows — and must not be swept into the refactor.

### 2.2 Mechanism B is the model, and it is already action-aware

`ModuleAccessServiceBase` computes `bootstrapOpen` and passes it *into* `EvaluateAsync`, so the module decides per
action. `HrAccessService` already excludes `confidential-view` and `payroll-manage`; those exclusions must not widen.

### 2.3 Confirmed unchanged since the frozen design

`PlatformPermissionProvider` (scope adapters) · `PlatformOpsAttribute` (Identity roles **OR** `acc.CanAsync("manage")`
— the RISK-042 inheritance, still present) · `IPlatformRoleDirectory` as the sole grant reader ·
`IAccountingApiAuthorization` (Hotfix A.1) · `BusinessEventService` contract · `ScopedTx`.

### 2.4 Concurrent changes

None affecting Batch B. The parallel team's uncommitted work does not touch the three financial access services,
`ModuleAccessServiceBase`, or the permission provider. **Nothing was overwritten.**

## 3. Planned design for B2–B19 (not implemented)

Recorded so the next increment starts from a decision rather than a blank page.

* **B2 storage** — `BootstrapAccessPolicies`, additive slice, separate from `PlatformRoleAssignments` (a policy is not
  a grant). Filtered unique index on `(CompanyID, Scope, ActionGroup)` where `IsActive = 1`, mirroring the grant
  store's proven shape. States: `LegacyCompatibility · Installation · Temporary · ExplicitlyAllowed · Disabled ·
  ReviewRequired`, validated in code **and** by a CHECK constraint. **No POS rows permitted** — enforced by a CHECK,
  not by convention.
* **B3 seed** — records today's *implicit* behaviour as explicit `LegacyCompatibility` per company+scope where the
  module is bootstrap-open now. Never seeds `Disabled`; deployment must not close a module.
* **B4/B5** — `IBootstrapPolicyReader` and an additive `AuthorizationDecision` carrying `DecisionSource`, so
  `CanAsync` delegates and returns `IsAllowed`. **One engine**, not two.
* **B6** — the four call sites in §2.1 move to the action-aware pattern. One shared mechanism, not three copies.
* **B7** — the 14 Never-Bootstrap-Open actions, re-derived from the accepted matrix against live source before any
  behaviour changes. No state may override a `Never` classification.
* **B10** — `IBootstrapPolicyWriter`, separate from `IPlatformGrantWriter`, reusing the Batch A authority tiers and
  ceiling. **Bootstrap management is itself never bootstrap-open** — the same rule that already protects the first
  grant in a company.

## 4. Verification of this increment

| Run | Result |
|---|---|
Application suite, SQL enabled | **849 passed · 0 failed · 0 skipped** |
…composition | 844 + **5** new concurrency tests |
Concurrency tests, 5 consecutive runs | **5 / 5 every time** — stable, not intermittent |
Application build (TestRun) | 0 errors |
Manifest | reconciled, **126 enforced tests**, 0 unregistered |
Leftover probe databases | **0** |
`CrossBuyDB2` | present and untouched |

## 5. What is NOT done, and the honest reason

**B2 through B19 are not implemented.**

B6 rewrites the bootstrap decision in `AccountingAccessService`, `InventoryAccessService` and `CrmAccessService` —
the code that decides who may post a journal entry, take a payment, move stock and reassign CRM ownership. B7 then
closes 14 of those actions. Done correctly that is a large, carefully sequenced change with roughly sixty tests behind
it, a behaviour-preserving seed, and a new reader, writer, API and query surface.

I stopped at the boundary where the remaining work no longer fitted the care it requires. Delivering a partial rewrite
of financial authorization — three services converted, the seed or the Never-list unfinished — would leave the system
in a state that is neither the old behaviour nor the new one, on the exact code path where a mistake is a silent
privilege grant. That is worse than an unstarted batch, and it is the one place in this programme where "most of it
works" is not a defensible outcome.

**Nothing is half-changed.** No access service, no reader, no policy storage and no production authorization behaviour
was touched in this increment. The tree is exactly Batch A plus a closed concurrency gap.

### Risk register

**Not updated.** RISK-040, 041, 042, 043, 044 and 045 all remain **Open** at unchanged severity, because B19 permits a
change only on implementation evidence and no bootstrap governance was implemented. RISK-037 and RISK-039 remain
**Mitigated** — B0 strengthens their evidence (the concurrency proof) but does not close them.

### Recommended next increment

B2 → B3 → B4/B5 → B6 → B7, in that order, as one focused batch. The order matters: storage before seed, seed before
reader, reader before the refactor, and the refactor before any action is closed — because closing an action before the
explicit policies exist is precisely what RISK-043 warns against.
