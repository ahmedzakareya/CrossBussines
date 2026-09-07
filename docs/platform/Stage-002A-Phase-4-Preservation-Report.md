# CrossBusiness Platform — Stage 2A Batch B — Phase-4 Preservation Report

**A Phase-4 preservation point exists and was verified by restoring it.**

---

## 1. The artifact

| | |
|---|---|
Archive | `stage2a-phase4.tar.gz` |
**SHA-256** | `2b13f7b0d72958cdbdb6c090ed648a11467ad67574026324d782e51c2a914214` |
File count | **12** |
Location | `%LOCALAPPDATA%\Temp\claude\stage2a-phase4\` |
Baseline commit | `ef4b869` (unmoved — no commit was created) |

The archive was **rebuilt** after the three evidence documents were written, so the recorded hash covers the complete
Phase-4 set rather than an earlier partial one. Two earlier intermediate hashes are superseded and recorded so they are not confused with the final one:
`e5929d17ff3c…` (6 files) and `7e8f550e0b4a…` (9 files). The archive was rebuilt twice — after the three evidence
documents, then after this report and the delivery report — so the recorded hash covers the complete set.

## 2. File inventory

| # | Path | Category |
|---|---|---|
1 | `CrossBuy/Models/Platform/CrmVisibleOwnerScope.cs` | source |
2 | `CrossBuy.Tests/CrmVisibleOwnerScopeTests.cs` | test |
3 | `docs/platform/Stage-002A-Batch-B-Compatibility-Read-Matrix.md` | document |
4 | `docs/platform/Stage-002A-CRM-Visible-Owner-Scope-Contract.md` | document |
5 | `docs/platform/Stage-002A-Bootstrap-Seed-Plan-Reconciliation.md` | document |
6 | `docs/platform/Stage-002A-Bootstrap-Policy-Storage-Evidence.md` | document |
7 | `docs/platform/Stage-002A-Bootstrap-Policy-Seed-Evidence.md` | document |
8 | `docs/platform/Stage-002A-Bootstrap-Reader-Evidence.md` | document |
9 | `engineering/required-evidence-manifest.json` | evidence |
10 | `docs/platform/Stage-002A-Phase-4-Preservation-Report.md` | document (this file) |
11 | `docs/platform/Stage-002A-Batch-B-Phase-4-Delivery-Report.md` | document |
12 | `docs/platform/Stage-002A-Phase-4-Disk-State-Manifest.csv` | manifest |

Per-file sizes and SHA-256 values: `docs/platform/Stage-002A-Phase-4-Disk-State-Manifest.csv` (9 source/document rows;
the three files above were added to the archive after the manifest was generated, so the manifest covers 9 of the 12).

**B2/B3/B4 source files are deliberately not in this archive** — they are already preserved in the Phase-2 and Phase-3
artifacts and re-archiving them would create two authorities for the same file. Their hashes are recorded in the three
evidence documents so the correspondence is checkable.

## 3. Restore procedure and result

```
mkdir  <disposable>
tar -xzf stage2a-phase4.tar.gz -C <disposable>
# per-file sha256 compared against the working tree
```

| Check | Result |
|---|---|
Files archived | **12** |
Files extracted | **12** |
Per-file SHA-256, extracted vs working tree | **0 mismatches** |
Archive integrity | lists and extracts without error |

An unverified archive is not a preservation point, so the restore was performed rather than assumed.

## 4. Earlier artifacts — intact, not overwritten

| Phase | SHA-256 (12) | Status |
|---|---|---|
1 | `2e0cfd558679` | intact |
2 | `aa1ce5f1258b` | intact |
3 | `b66aff246366` | intact |
4 | `2b13f7b0d729` | **new** |

Each phase has its own directory; no archive was written over another.

## 5. Shared-file limitation — unchanged and still the main risk

No commit was created, and the reason is the same one recorded in the Phase-1 report: **the parallel team's
uncommitted kernel is interleaved with this work in the same untracked directories.** `CrossBuy/BL/Platform/` is theirs
and holds `BootstrapAccessPolicyReader.cs`, `BootstrapAccessPolicySeeder.cs` and `PlatformGrantWriter.cs`;
`CrossBuy/Models/Context/Platform/` is theirs and holds `BootstrapAccessPolicy.cs`.

`git add` on those paths would commit their unreleased kernel under this batch's message. Per-file adds would produce a
tree that does not build. So the shared-file edits remain captured as the Phase-1 patch
(`stage2a-shared-edits.tar.gz`, `5919d9b3…`), which is a snapshot of *shared* files — reapplying it restores their
changes too, because the edits are interleaved line-by-line.

**This is the standing risk.** It has already caused one episode of real work loss (the B2/B3/B4 source that ran, leaked
106 probe databases, and was not on disk afterwards). Asking the parallel team to commit their kernel remains an owner
action.

## 6. Exclusions

Excluded from the archive: `bin/`, `obj/`, build output, probe databases, generated evidence CSVs that are rewritten on
every run, secrets, and any temporary or mutation file. Nothing binary beyond the archive itself.

## 7. Mutation-artifact and probe verification

| Check | Result |
|---|---|
`grep -rl "MUTATION [A-H]"` across `CrossBuy/` and `CrossBuy.Tests/` | **0 files** |
Owned probe databases (`CrossBuyProbe_*`, `CrossBuyPlatformTest_*`, `CrossBuyImp003_*`) | **0** |
`CrossBuyDB2` | **present and untouched** |

Mutations A, B, C and D were all applied and restored during Batch B; no marker survives in any source or SQL file.

**Guard 3 fired twice this increment** and was correct both times — it caught a leaked `CrossBuyPlatformTest_*` fixture
database and a `CrossBuyProbe_BatchP_*` probe left behind when test runs were interrupted mid-flight and `DisposeAsync`
never ran. Both were dropped with per-name ownership re-verification. Without that guard the next run would have
inherited state its tests did not create.
