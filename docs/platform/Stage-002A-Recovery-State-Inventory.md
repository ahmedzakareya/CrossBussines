# Stage 2A — Recovery State Inventory

**Phase 1 only. Phase 2 (B2/B3/B4) is NOT STARTED — no source file for it exists on disk.**

Every row below was verified by reading the file system and `git`, not from memory. That is the point of this
document: the previous increment reported progress that disk did not support.

---

## 1. The entanglement that governs everything here

`git status` shows **73 untracked paths**, and they are **not all mine**. The parallel team's entire Platform Kernel
and Comm/Calendar/Library modules are also uncommitted, in the same directories as my work:

| Untracked path | Owner |
|---|---|
`CrossBuy/BL/Platform/` | **parallel team** — their kernel. My `PlatformGrantWriter.cs` sits inside it |
`CrossBuy/Models/Context/Platform/` | **parallel team** — my slice-2 fields are inside *their* `PlatformRoleAssignment.cs` |
`CrossBuy/BL/{Comm,CommService,Announcement,Calendar,DocComment,FileManager}*` | parallel team |
`CrossBuy/Models/Context/{Comm,Calendar,Library}/` | parallel team |
`docs/platform/ADR-*` | parallel team |
`CrossBuy.Tests/` | **mixed** — the project is theirs (kernel tests); the `SqlServer/` evidence suite is largely mine |

**Consequence:** `git add` on any of those directories would commit the parallel team's uncommitted work, which
CLAUDE.md forbids outright (*"Commit only our files"*). That is the blocking reason recorded in the preservation
report, and it is why a bundle was produced instead of a commit.

## 2. My files — verified present, compiling, covered

| Path | Tracked | Role | Compiles | Test coverage | Preserve |
|---|---|---|---|---|---|
`CrossBuy.Analyzers/` (8 files) | untracked | Roslyn authorization analyzer | ✅ 0 errors | 93 analyzer tests | ✅ |
`CrossBuy.Analyzers.Tests/` (7 files) | untracked | analyzer + reconciliation suite | ✅ | self | ✅ |
`.editorconfig` | untracked | CBA001/004/006 = error | n/a | proven by build | ✅ |
`CrossBuy/Models/Platform/PlatformGrantContracts.cs` | untracked | Batch A commands/outcomes | ✅ | acceptance suite | ✅ |
`CrossBuy/Models/Platform/BootstrapPolicyContracts.cs` | untracked | **B5 + B7**: states, 15 Never entries, `AuthorizationDecision` | ✅ | **none yet** | ✅ |
`CrossBuy/BL/Platform/PlatformGrantWriter.cs` | untracked *(in their dir)* | production Grant Writer | ✅ | 17 acceptance + 5 concurrency | ✅ |
`CrossBuy/Controllers/Api/PlatformGrantsApiController.cs` | untracked | 5 admin endpoints | ✅ | analyzer-protected | ✅ |
`CrossBuy/deploy/sql/platform_role_assignments_slice_002.sql` | untracked | Batch A audit columns | n/a | probe-applied | ✅ |
`CrossBuy.Tests/SqlServer/` — 9 of my files | untracked | SQL evidence + guards + Batch A/B0 proofs | ✅ | self | ✅ |
`CrossBuy.Tests/Batch00EvidenceGuardTests.cs` | untracked | baseline + manifest guard | ✅ | self | ✅ |
`engineering/` (2 files) | untracked | authorization baseline · evidence manifest | n/a | enforced by guards | ✅ |
`docs/platform/Stage-002A-*.md` (22 files) | untracked | Stage 2A record | n/a | n/a | ✅ |
`docs/architecture/evidence/Roslyn-*.csv`, `SQL-Evidence-Summary.md` | untracked | generated evidence | n/a | regenerated per run | ✅ |

**64 files** total in the preservation bundle.

### My line-edits inside files I do not own

| File | Tracked | My change | Concurrent-owner risk | Action |
|---|---|---|---|---|
`CrossBuy.sln` | tracked, `M` | +63 (2 projects, TestRun config) | **high** — parallel WIP in same file | captured as patch |
`CrossBuy/CrossBuy.csproj` | tracked, `M` | +65 (analyzer ref, AdditionalFiles, baseline guard target) | **high** | captured as patch |
`CrossBuy/Program.cs` | tracked, `M` | +193/−7 *(mine ≈ 4 lines: grant writer + admin identity DI)* | **high** | captured as patch |
`CrossBuy/Models/Context/CrossDbContext.cs` | tracked, `M` | +158/−1 *(mine ≈ 12 lines: slice-2 mapping + idempotency index)* | **high** | captured as patch |
`CrossBuy/BL/Platform/EntityRegistry.cs` | **untracked, theirs** | +1 constant, +1 `EntityDefinition` | **high** | full copy retained |
`CrossBuy/Models/Context/Platform/PlatformRoleAssignment.cs` | **untracked, theirs** | +6 fields, +`PlatformGrantSources` | **high** | full copy retained |

The Program.cs and CrossDbContext.cs line counts are dominated by the parallel team's changes, not mine. The patch
captures the file's whole working-tree diff, so reapplying it restores *their* changes as well — that is stated plainly
in the preservation report rather than implied, because it means the patch is a **snapshot of a shared file**, not an
isolation of my lines.

## 3. Parallel-work source changes recorded

No parallel change was overwritten in this increment. Nothing in the parallel team's files was edited beyond the two
additive changes listed above (`EntityRegistry.cs`, `PlatformRoleAssignment.cs`), both of which were made in earlier
approved increments and are unchanged since.

## 4. What does NOT exist — Not Started

| Item | Status |
|---|---|
B2 `BootstrapAccessPolicy` entity · DbSet · EF configuration | **absent** |
B2 SQL slice | **absent** |
B3 seed service | **absent** |
B4 `IBootstrapAccessPolicyReader` | **absent** |
B2/B3/B4 tests | **absent** |
CRM visible-owner scope contract | **absent** |
Compatibility read matrix | **absent** |

Confirmed by `grep -rl` across the repository: no source declares `BootstrapAccessPolicy`,
`IBootstrapAccessPolicyReader`, or any seed type. The 106 leaked probe databases named `BootstrapStorage`,
`BootstrapSeed` and `BootstrapReader` prove such code **ran** at some point in this session; its source is not on disk
and is not recoverable from the databases.

`BootstrapPolicyContracts.cs` is the one Batch B artefact that survived, and it **has no test coverage** — it is
declarations only, consumed by nothing.

## 5. Cleanup verification

| Check | Result |
|---|---|
`CrossBuyProbe_BootstrapStorage_*` | **0** |
`CrossBuyProbe_BootstrapSeed_*` | **0** |
`CrossBuyProbe_BootstrapReader_*` | **0** |
All `CrossBuyProbe_*` | **0** |
`CrossBuyPlatformTest_*` | **0** |
`CrossBuyImp003_*` | **0** |
`CrossBuyDB2` | **present (1), untouched** |
Orphan mutation markers in source | **0** |
Orphan `.cs` in the scratch directory | **0** |

106 leaked databases were dropped with **per-name ownership re-verification** before each drop, so nothing outside the
three owned prefixes could be touched. They were found by `SqlCompanionGuardTests.Guard3_no_leftover_scratch_database_exists_on_the_real_instance`
— the Batch 00-C guard doing exactly what it was built for. Without it this would have been invisible and the next run
would have inherited state its tests did not create.

## 6. Verification of the preserved state

| Run | Result |
|---|---|
Application suite, SQL enabled | **849 passed · 0 failed · 0 skipped** |
Analyzer suite | **93 passed · 0 failed · 0 skipped** |
TestRun build | 0 errors |
CBA001 / CBA004 / CBA006 | **0 / 0 / 0** |
Authorization debt | **143**, unchanged |
Endpoint totals | **391 = 157 + 91 + 143** |
Mechanism A sites | **unchanged** — `AccountingAccessService:60`, `InventoryAccessService:48`, `:91`, `CrmAccessService:59` |
Production authorization behaviour | **unchanged** |