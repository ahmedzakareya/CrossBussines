# CrossBusiness Platform — Stage 2A Batch B — B3 Bootstrap Policy Seed Evidence

**Implemented and evidence-backed.** Real SQL Server only.

| File | SHA-256 (16) |
|---|---|
`CrossBuy/BL/Platform/BootstrapAccessPolicySeeder.cs` | `ba307f516a139b98` |
`CrossBuy.Tests/SqlServer/BootstrapPolicySeedTests.cs` | `3992dde719978485` |

**20 tests, 0 failed, 0 skipped.** Plan version `B3.v1`.

---

## 1. Purpose

When B6 replaces Mechanism A, the converted services stop treating *no role configured* as an implicit allow and start
asking `IBootstrapAccessPolicyReader` — which **denies** when no policy exists. Without this seed, B6 would close
Accounting and Inventory reads on every unconfigured company the moment it shipped. This writes today's implicit
openness down, explicitly, **before** B6 can consume it.

## 2. The plan correction — 41 / 32 / 4

| Figure | Status | Meaning |
|---|---|---|
**41** | **superseded** | historical planning figure. Not reproducible from live action vocabularies — 32 / 35 / 45 / 46 were all attempted and none is 41. Retained as history, not authority. |
**32** | **inventory** | complete live bootstrap-eligibility count. Counts eligibility, not intent. |
**4** | **implemented** | the policies B6 will actually read. |

**Why only 4.** B3 is not a catalogue. 28 of the 32 belong to HR, Projects, Tasks and Communication, which already run
through the action-aware `ModuleAccessServiceBase`; B6 does not touch them and they never consult this reader. Seeding
them would write rows no production path reads — misleading configuration and future cleanup debt. Manufacturing has an
adapter delegating to Inventory roles and **no `IModuleAccessService` of its own**, so it has no independent bootstrap
behaviour to preserve.

This was an owner decision after the discrepancy was reported. The number was **not forced** in either direction.

## 3. The four entries, each reconciled to its B6 call site

| # | Identity | State | B6 call site | Source evidence |
|---|---|---|---|---|
1 | `Accounting.read` | `LegacyCompatibility` | `AccountingAccessService.cs:60` | switch line 29 `"read" => true` — *"any authenticated user in this company may view"*; the bootstrap return at `:60` precedes the switch |
2 | `Inventory.read` | `LegacyCompatibility` | `InventoryAccessService.cs:48` | switch line 18 `"read" => true` |
3 | `Crm.read` | `LegacyCompatibility` | `CrmAccessService.cs:59` | switch line 29 `"read" => true` |
4 | `Crm.edit` | `LegacyCompatibility` **ReviewExpected** | `CrmAccessService.cs:59` | switch line 30 `"edit" => mgr \|\| rep \|\| mkt` — see §4 |

Every entry carries a `B6CallSite`, and `ValidatePlan()` **rejects** an entry without one: *a policy no production path
consumes is misleading configuration*. That is enforced, not merely documented.

`InventoryAccessService.cs:91` receives **no row** — `warehouse-access` is Never-Bootstrap-Open, and a compatibility
policy there would widen an exact branch/warehouse restriction to company scope.
`CrmAccessService.VisibleOwnerIdsAsync` receives **no row** — it is not an action but the shape of a filter
(`HashSet<int>?`); a policy row cannot express *unrestricted vs no-one*. It uses the typed contract instead.

## 4. Why `Crm.edit` is seeded, and why it is flagged

The `:59` bootstrap return happens **before** the action switch. The switch comment says *"CrmViewer (or no role) →
read-only"* — but that describes the **configured** path. An unconfigured company never reaches it, so today it
genuinely permits `edit`.

Omitting `Crm.edit` would make B6 a **silent tightening**, not behaviour preservation. Seeding it preserves today's
behaviour exactly and marks it `ReviewExpected = true`, because it is the **only mutating action in the plan** — and
that mismatch should be visible rather than buried.

## 5. Behaviour

| Property | Implementation | Test |
|---|---|---|
**Dry run** | `PreviewAsync` **forces** `DryRun` regardless of the command's mode, so preview cannot write because a field was set wrong | dry-run writes nothing |
**Transaction** | one `ScopedTx` per company; all rows commit together | rollback leaves zero rows |
**Conflict** | any conflict ⇒ **nothing written**. A half-seeded company is worse than an unseeded one: B6 would find compatibility for some actions and not others, and the difference would look deliberate | conflict reported, no rows |
**Idempotency** | second apply inserts nothing; equivalent active row reported as existing | applies twice |
**Different state** | reported as a **conflict**, never overwritten — re-running a seed must not revert an administrator's decision | covered |
**Disabled history** | a disabled row **blocks** re-seeding and is reported as a conflict — its existence means somebody turned compatibility off on purpose | covered |
**Company isolation** | company predicate throughout; no `CompanyID = 1` fallback anywhere | two-company isolation |
**Company mismatch** | request-supplied company validated against the resolved context, never trusted | covered |
**Actor** | must be a real, **active** employee of the target company | covered |
**Never filtering** | consults `NeverBootstrapOpen.All` — never a copy — and reports `SkippedNeverCount` + identities | covered |
**POS filtering** | consults `EntityRegistry.ScopePos`; reports `SkippedPosCount` | covered |
**Concurrency** | duplicate/deadlock (2601/2627/1205) classified as a lost race, not a fault | covered |

Typed throughout: `SeedResultCode` distinguishes `Success`, `DryRun`, `ValidationFailed`, `Forbidden`, `Conflict`,
`CompanyMismatch`, `CompanyNotFound`, `InvalidPlan`, `PartialFailure`, `AlreadyApplied`.

## 6. A defect found and fixed during implementation

**Two invalid `with`-expressions.** `PreviewAsync` and the concurrency-loss path were written using `with` syntax on
types that are **classes with init-only properties, not records**, so the code did not compile. Fixed by plain
construction — and `PreviewAsync` was made stronger in the process: it now *forces* `DryRun` rather than trusting the
caller's mode, so "preview" cannot write even if a field is set wrong.

**A false-receipt hazard avoided by design.** The seeder never reports `InsertedCount > 0` on a dry run: the count is
computed as `dryRun ? 0 : inserted.Count`, and `InsertedIdentities` is empty for a preview. A preview that reported
insertions would be the worst possible output — a caller would believe a company was seeded when nothing was written.

## 7. Mutation D

An allowing policy for a Never action was injected into the plan. **Result: seed tests FAILED**, naming the prohibited
identity. Restored; `grep -c MUTATION` → **0**. `NeverBootstrapOpen.All` was **not** modified.

The proof matters because the filter reads the authoritative list at runtime: had the seeder forked the list, the
mutation would have passed.

## 8. Safe invocation seam

Registered as an **internal scoped service only**:

* ❌ no controller · ❌ no public API · ❌ no startup hook · ❌ no background job · ❌ no all-company enumeration
* ✅ explicit `CompanyID`, actor, source, reason, migration batch, and an explicit mode

Registering it does not run it. Future B6 rollout sequence: preview → review → apply → verify the four identities →
enable B6 for that company/scope → compare behaviour → roll back B6 on mismatch. **Not activated in this increment.**

## 9. Known limitations

* **The seed changes no authorization outcome today.** B6 is inactive; the rows are inert. That is the intended state
  and also means the seed's *effect* is unproven end-to-end until B6 converts a site.
* **`ExpiresAt` is always null** for these four entries — `LegacyCompatibility` records what *is*, so it is unbounded.
  RISK-045 therefore has no expiry evidence from the seed itself, only from the storage constraint.
* **No production company has been seeded.** All evidence is from disposable companies in isolated probes.
* Plan version `B3.v1` is a constant, not enforced against seeded rows — a company seeded under a future `B3.v2`
  would be indistinguishable without inspecting the `Reason` text.

## 10. Environment

`CrossBuyDB2` — **present and untouched**; no SQL executed against it. Evidence from
`CrossBuyProbe_BootstrapSeed*` probes, dropped with confirmed removal. **0 probes remain.**
