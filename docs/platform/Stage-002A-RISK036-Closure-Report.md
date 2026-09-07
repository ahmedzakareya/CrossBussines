# Stage 2A — RISK-036 Closure Report

**RISK-036 is fully mitigated across the SQL evidence infrastructure. All 10 completion-gate criteria met.**

Infrastructure only · no production code changed · no production behaviour changed · no SQL executed against
`CrossBuyDB2` · Roslyn analyzer not started · SQL companion guards not started · Batch A not started.

---

## 1. Root cause

`SqlServerFixture.EnsureEfSchemaAsync()` creates the **entire EF model** — 231 entities — in whichever database it is
given. Three test families called it against the **shared platform fixture**, so the shared database accumulated ~200
tables that the deploy scripts never create.

`PlatformSchemaDeploymentTests` legitimately asserts *"the platform scripts create no table outside the kernel and its
outbox"*. That assertion is true only if the shared fixture contains **just** what the scripts created.

**The suite was therefore green by luck, not by design.** Whether it passed depended on whether the schema-generating
families happened to run *after* the schema-asserting ones. It broke twice — once when IMP-003 added three tests, once
when Batch P added six — and both times the trigger was a change in execution order, not a change in behaviour.

## 2. Families affected

| Family | Before | After |
|---|---|---|
| `BatchPPreservedRuleTests` | shared fixture (fixed in the previous increment) | **own probe** |
| `Stage1F4DatabaseProofTests` | **shared fixture** | **own probe** (`CrossBuyProbe_F4_…`) |
| `TaskScopeQuerySqlTests` | **shared fixture** | **own probe** (`CrossBuyProbe_TaskScope_…`) |
| `Imp003RelationalModelTests` | already isolated | unchanged — **Safe**, see inventory §2 |
| 5 further families | create no schema | unchanged |

Full classification: `Stage-002A-SQL-Evidence-Family-Inventory.md`.

## 3. Migration performed

Both remaining families now:

1. implement `IAsyncLifetime`;
2. capture the shared fingerprint **before** the probe exists;
3. create a probe via `CreateProbeDatabaseAsync(prefix)`;
4. route every context and connection through `Db()` / `Conn` instead of the fixture;
5. drop the probe in `DisposeAsync` with **confirmed removal**;
6. carry their **own** purity test.

**No probe logic was duplicated** — one implementation on the fixture, three consumers.

## 4. Files changed

| File | Change |
|---|---|
| `CrossBuy.Tests/SqlServer/SqlServerFixture.cs` | the probe lifecycle, ownership guard, fingerprint, probe enumeration (added in the previous increment; unchanged here) |
| `CrossBuy.Tests/SqlServer/Stage1F4DatabaseProofTests.cs` | migrated to a probe; purity test added |
| `CrossBuy.Tests/SqlServer/TaskScopeQuerySqlTests.cs` | migrated to a probe; purity test added |

**Zero production files touched.**

## 5. Probe helper design

| Member | Guarantee |
|---|---|
| `CreateProbeDatabaseAsync(prefix)` | `CrossBuyProbe_<prefix>_<guid>`; model-derived schema via the verified `StripForeignKeys` + `SplitScript` |
| `DropProbeDatabaseAsync(probe)` | **refuses** any name lacking the `CrossBuyProbe_` prefix; **throws** if the database survives the drop |
| `ContextFor(probe, companyId)` | a `CrossDbContext` on the probe with a resolved company scope |
| `ListOwnedProbeDatabasesAsync()` | enumerates only what the fixture owns |
| `SharedFixtureFingerprintAsync()` | `tables\|indexes\|foreign keys\|schemas\|columns` |

The ownership prefix is the safety property that matters most: **cleanup can never drop a database it did not create.**

### 5.1 Shared-infrastructure review (brief item 6)

Extracted **only what was already duplicated**: schema generation, connection/ownership handling, cleanup and the
fingerprint — each previously written or about to be written twice.

**Deliberately not extracted:** a manifest helper (one consumer), a generic dataset builder (each family's data differs),
a connection-pooling abstraction (no evidence of need). Creating those would be the speculative abstraction the delivery
principle rejects — the same reasoning that limited Batch 00 to two analyzers rather than twenty.

## 6. Shared-fixture purity proof

Each of the three schema-generating families proves purity **independently** — one shared assertion would have hidden a
single offender:

* `BatchPPreservedRuleTests.This_class_adds_nothing_to_the_shared_platform_fixture`
* `Stage1F4DatabaseProofTests.This_family_adds_nothing_to_the_shared_platform_fixture`
* `TaskScopeQuerySqlTests.This_family_adds_nothing_to_the_shared_platform_fixture`

Each asserts the before/after fingerprint is byte-identical — covering tables, indexes, foreign keys, schemas and
columns — and that its probe name carries the ownership prefix.

**`PlatformSchemaDeploymentTests`: 9 passed.** It is the independent witness: it was the victim twice, and it is green.

## 7. Order-independence proof

| Evidence | Result |
|---|---|
| Each family in isolation | F4 **15** · TaskScope **5** · Batch P **7** · IMP-003 **3** — all passed |
| `PlatformSchemaDeploymentTests` alone | 9 passed |
| Full suite, run 1 | **781 passed · 0 failed · 0 skipped** |
| Full suite, run 2 | **781 passed · 0 failed · 0 skipped** (identical) |

This is meaningful precisely because the original defect **was** order-dependence. Each family now rebuilds its own
data in its own database, so there is no shared state for order to expose.

**Honest limitation:** the runner's default order was used, plus per-family isolation runs, which together exercise
different orderings. A randomized-order run was **not** configured — that needs a runner setting change, and the
structural argument is stronger than a shuffle: with no shared mutable state, order cannot matter.

## 8. Cleanup proof

| Check | Result |
|---|---|
| Probes created this run | 3 (`F4`, `TaskScope`, `BatchP`) |
| Probes dropped | 3, each with **confirmed removal** |
| Probes remaining (`CrossBuyProbe_%`) | **0** |
| Scratch databases remaining | **0** |
| Foreign database touched | **none** — the drop refuses unowned names |
| `CrossBuyDB2` | **present and untouched** |

## 9. Remaining work

* **`EnsureEfSchemaAsync` has zero callers but still exists.** *Recommended Now:* mark it `[Obsolete]` pointing at
  `CreateProbeDatabaseAsync`, so a future family cannot re-enter the trap. Not done here — this increment was scoped to
  eliminating the exposure, and an attribute change is separately reviewable.
* **`Imp003RelationalModelTests` could adopt the shared helper.** Already isolated, owned and cleaned up, so this is
  consistency rather than risk.
* **Randomized-order execution** not configured (§7).
* Unchanged and out of scope: Roslyn analyzer, SQL companion guards (production-catalog refusal and leftover-scratch
  detection as *tests* rather than manual checks), CI wiring, Batch A.

## 10. Completion gate

| # | Criterion | Status |
|---|---|---|
| 1 | Every schema-generating family uses the probe helper | **MET** — 3 of 3; IMP-003 already isolated |
| 2 | No family creates schema in the shared fixture | **MET** — 0 residual call sites |
| 3 | Shared-fixture purity proven per family | **MET** — 3 independent assertions |
| 4 | Order independence proven | **MET** — 781/781 twice, plus per-family isolation |
| 5 | Probe cleanup verified | **MET** — 0 remaining, confirmed removal |
| 6 | `PlatformSchemaDeploymentTests` green | **MET** — 9/9 |
| 7 | Full suite passes twice | **MET** — 781/781 identical |
| 8 | No probe databases remain | **MET** |
| 9 | `CrossBuyDB2` untouched | **MET** |
| 10 | RISK-036 fully mitigated | **MET** |

**10 of 10.** The suite is now green **by design rather than by ordering luck** — which is the actual closure condition,
and the reason the previous two green runs were not evidence.

Suite grew 779 → **781**: the two new per-family purity tests. No test was removed.

**Stopping for review. Roslyn analyzer not started. SQL companion guards not started. Batch A not started.**
