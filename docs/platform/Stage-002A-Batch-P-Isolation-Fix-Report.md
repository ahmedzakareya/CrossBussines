# Stage 2A — Batch P — Isolation Fix Report

**Complete. All 20 completion-gate criteria met.**

Batch P's preserved-rule tests now **execute** rather than being skipped. No production code changed · no production
behaviour changed · no SQL executed against `CrossBuyDB2` · Roslyn analyzer not started · Batch A not started.

---

## 1. Problem

The six Batch P preserved-rule tests existed, passed, and were mutation-proven — but the class was **skipped**, so by
the project's own standard (*a skipped test is not evidence*) the three rules governing money and quantity remained
protected by prose alone.

## 2. Root cause

`BatchPPreservedRuleTests.ArrangeAsync` called `_sql.EnsureEfSchemaAsync()` against the **shared platform fixture**.
That created the full EF schema in the shared database, which broke three `PlatformSchemaDeploymentTests` asserting the
platform scripts create **no table outside the kernel and its outbox**.

**This is RISK-036 exactly — committed by the batch that documented it.** The rule existed, I wrote it, and I then
violated it in the next increment.

## 3. Why skipped tests were not evidence

A skip is invisible in a pass count. Stage 1 lost two batches to precisely this: 46 SQL tests reported *Skipped* while
being counted as coverage, and their first real execution failed with twenty `Invalid column name` errors. Parking the
class kept the suite honest (green with a visible reason) but left the entry gate unmet, because criteria 3–5 require
the rules to be **mechanically enforced**, not merely written.

## 4. Isolation design

Extracted into `SqlServerFixture` — option 2 of the brief's preference order, because IMP-003 needed the same thing and
the rule should live in **one helper** rather than being remembered per class:

| Member | Responsibility |
|---|---|
| `CreateProbeDatabaseAsync(prefix)` | creates `CrossBuyProbe_<prefix>_<guid>` and applies the **model-derived** schema (reusing the verified `StripForeignKeys` + `SplitScript` from the F4 work) |
| `DropProbeDatabaseAsync(probe)` | drops **and confirms removal**, throwing if the database survives |
| `ContextFor(probe, companyId)` | a `CrossDbContext` bound to the probe, with a resolved company scope |
| `ListOwnedProbeDatabasesAsync()` | enumerates what the fixture owns, for cleanup assertions |
| `SharedFixtureFingerprintAsync()` | `tables\|indexes\|foreign keys\|schemas\|columns` counts, for purity proof |

**Ownership marker:** every probe carries the `CrossBuyProbe_` prefix, and `DropProbeDatabaseAsync` **refuses** to drop
anything without it — so the fixture can never delete a database it did not create.

The class implements `IAsyncLifetime`: probe created in `InitializeAsync`, dropped in `DisposeAsync`, and the shared
fingerprint captured **before** the probe exists so the purity assertion covers everything the class does.

## 5. Files changed

| File | Change |
|---|---|
| `CrossBuy.Tests/SqlServer/SqlServerFixture.cs` | added the probe lifecycle, ownership guard, fingerprint and probe enumeration |
| `CrossBuy.Tests/SqlServer/BatchPPreservedRuleTests.cs` | rewritten to own a probe; skip removed; purity test added; mutation seams isolated into two named methods |
| `engineering/required-evidence-manifest.json` | Batch P group updated to 7 tests with an `isolation` note |

**No production file was touched.** `AccountingAccessService.cs` and `CrmAccessService.cs` show as modified in git, but
their timestamps (18:50 and 12:09) precede this increment (20:00) — they are Stage 1 work, not changes from here.

Per the brief's instruction, the C# was **written directly**, not patched through Python string replacement. That
instruction was warranted: the previous attempt corrupted regex literals exactly that way.

### 5.1 A second defect found and fixed during the fix

The first probe implementation named the database with a computed slice:

```csharp
var name = $"CrossBuyProbe_{prefix}_{Guid.NewGuid():N}"[..Math.Min(120, 16 + prefix.Length + 33)];
```

The computed length (55) exceeded the actual string (53), throwing `ArgumentOutOfRangeException` and failing five
tests. Replaced with plain concatenation. A clever one-liner where straightforward construction was correct.

## 6. Test execution

| Suite | Result |
|---|---|
| `BatchPPreservedRuleTests` | **7 passed · 0 failed · 0 skipped** |
| `PlatformSchemaDeploymentTests` | **9 passed · 0 failed** (was 3 failing) |
| Full suite, run 1 | **779 passed · 0 failed · 0 skipped** |
| Full suite, run 2 | **779 passed · 0 failed · 0 skipped** |

Batch P grew from 6 to **7** tests: the added one is the shared-fixture purity proof (§9), which is the assertion that
makes the fix self-policing rather than a one-time correction.

## 7. Mutation proof

Both prohibited behaviours were injected into the **test-local seams** — `IsUnitSellableAsync` and
`ResolveCandidatesAsync` — never into production code:

| Mutation | Injected | Result |
|---|---|---|
| **A** — factor-1 fallback (RISK-048) | `IsUnitSellableAsync` returns `true` unconditionally | **2 failed** (both P-01 tests) |
| **B** — first-match resolution (RISK-050) | `ResolveCandidatesAsync` appends `.Take(1)` | **1 failed** (P-02 ambiguity) |
| Restored | seams returned to original | **7 passed** |

`grep "MUTATION A\|MUTATION B"` → **0**. Only the two deliberate `MUTATION SEAM` comments remain, marking where future
proofs should be applied.

## 8. Order-independence proof

* Each test rebuilds its dataset in `ArrangeAsync`, so **none inherits another's rows**.
* The full suite ran **twice** with identical results (779/779 both times) — and this is meaningful precisely because
  the original defect was ordering-dependent: the earlier suite was green by luck, and adding three tests broke it.
* The probe is created and dropped per class, so no state survives between runs.
* `PlatformSchemaDeploymentTests` passes both when run alone and inside the full suite.

## 9. Shared-fixture purity (RISK-036 mitigated)

`This_class_adds_nothing_to_the_shared_platform_fixture` captures the fingerprint before the probe exists, performs the
full schema-and-seed work, then asserts the fingerprint is **byte-identical**. It also asserts the probe name carries
the ownership prefix and is **not** the shared connection string.

**This converts the rule from a documented convention into an enforced one.** RISK-036's mitigation is now mechanical
for this class; the remaining families (`Imp003RelationalModelTests`, `TaskScopeQuerySqlTests`, `Stage1F4DatabaseProofTests`)
should adopt `CreateProbeDatabaseAsync` next — the helper exists for them.

## 10. Cleanup proof

| Check | Result |
|---|---|
| Owned probes remaining (`CrossBuyProbe_%`) | **0** |
| Scratch databases remaining | **0** |
| `CrossBuyDB2` | **present and untouched** |
| Unowned database touched | **none** — the drop refuses any name without the ownership prefix |
| Cleanup confirmation | `DropProbeDatabaseAsync` **throws** if the database survives the drop |

## 11. Remaining entry-gate gaps

Unchanged by this increment, and deliberately out of scope:

* **Roslyn Authorization Analyzer — Not Started.** New unprotected mutating actions still do not fail the build.
* **SQL companion guards — Not Started.** Production-catalog refusal and leftover-scratch detection are verified
  manually each run but are not yet tests. (Shared-fixture purity is now covered for Batch P.)
* **CI wiring — Blocked Pending Platform Decision.** No CI configuration exists in the repository.
* **Batch P's remaining 21 invariants — Not Started.** Three of the 24 are now enforced; the datasets and the other 21
  are the Batch P continuation.

## 12. Final status

| Gate | Status |
|---|---|
| 1 Class no longer skipped | **MET** |
| 2 All six tests execute | **MET** (seven, incl. purity) |
| 3 All pass | **MET** |
| 4 All in the manifest | **MET** — 31 enforced tests, 10 groups |
| 5 Manifest verification passes | **MET** |
| 6 Missing-conversion rejection mechanically enforced | **MET** |
| 7 Ambiguous-barcode rejection mechanically enforced | **MET** |
| 8 `ItemBarcode.UoMId` sold-unit behaviour enforced | **MET** |
| 9 Mutation A fails a test | **MET** — 2 failures |
| 10 Mutation B fails a test | **MET** — 1 failure |
| 11 `PlatformSchemaDeploymentTests` green | **MET** — 9/9 |
| 12 Shared fixture unchanged | **MET** — fingerprint identical, asserted |
| 13 Order does not change results | **MET** — 779/779 twice |
| 14 Probe databases isolated | **MET** |
| 15 Probe databases cleaned up | **MET** — 0 remaining, confirmed removal |
| 16 No production code changed | **MET** |
| 17 No production behaviour changed | **MET** |
| 18 No SQL against `CrossBuyDB2` | **MET** |
| 19 Roslyn analyzer not started | **CONFIRMED** |
| 20 Batch A not started | **CONFIRMED** |

**20 of 20. The manifest count rose from 30 to 31** — the Batch P group gained the purity test; no test was removed and
no count was reduced.

**Three of the 24 Master Data invariants are now mechanically enforced.** Stage 2A Entry Gate remains **Not Met** on the
analyzer, the SQL companion guards and CI.

**Stopping for review. Roslyn analyzer not started. Batch A not started.**
