# Stage 2A — Recovery Preservation Report

**A preservation point exists and has been verified by restoring it.** It is a **bundle + patch**, not a commit, and
the blocking reason is recorded below rather than worked around.

---

## 1. Why not a commit

The brief permits a commit *"if repository and branch policy permit"*. They do not, for one concrete reason:

**The parallel team's uncommitted work is interleaved with mine in the same untracked directories.**
`CrossBuy/BL/Platform/` is their kernel and contains my `PlatformGrantWriter.cs`. `CrossBuy/Models/Context/Platform/`
is theirs and contains my slice-2 fields inside *their* `PlatformRoleAssignment.cs`. `CrossBuy.Tests/` is their
project holding my evidence suite.

`git add` on any of those paths commits their unreleased kernel. CLAUDE.md is unambiguous: *"Commit only our files.
Shared files get our lines only via git plumbing."* Committing 40+ of their files to preserve 64 of mine would be a
larger error than the one being fixed — and it would put their work under my commit message, on `master`, without
their knowledge.

Per-file `git add` of only my paths was considered and rejected as well: it would commit `PlatformGrantWriter.cs`
into a directory whose *other* files remain uncommitted, producing a tree that does not build for anyone who checks
it out. A partial commit that cannot compile is not a preservation point.

**Recommendation for the owner (not something I should decide):** ask the parallel team to commit their kernel. Every
Stage 1 and Stage 2 report has raised this (HM-D44 onward); this increment is the first time it caused actual work
loss, and it will keep doing so.

## 2. The preservation artefacts

| Artefact | SHA-256 | Contents |
|---|---|---|
`stage2a-verified.tar.gz` | `2e0cfd5586795873fe9b33dd31c9d9783990dbd174047de85ecceba11ddbb3dc` | **64 files** — wholly-mine Stage 2A work |
`stage2a-shared-edits.tar.gz` | `5919d9b3f4bfcd109163a19c24d49bd7c7c0551d2ac23b69d2f4bb6b89bdcf08` | the shared-file diff + 2 parallel-owned files carrying my additive changes |

Location: `%LOCALAPPDATA%\Temp\claude\stage2a\`. Baseline commit: `ef4b869`. Branch: `master` (unmoved — no commit was
created, so no history changed).

Excluded: `bin/`, `obj/`, probe databases, generated build output, secrets. Nothing binary beyond the archives.

### 2.1 What is in the 64-file bundle

`CrossBuy.Analyzers/` (8) · `CrossBuy.Analyzers.Tests/` (7) · `.editorconfig` · `engineering/` (2) ·
4 production source files · 1 SQL slice · 10 test files · 22 `Stage-002A-*.md` · 4 generated evidence artefacts.

### 2.2 What is in the shared-edits archive

* `shared-tracked-edits.patch` — **603 lines**, the working-tree diff of `CrossBuy.sln`, `CrossBuy/CrossBuy.csproj`,
  `CrossBuy/Program.cs`, `CrossBuy/Models/Context/CrossDbContext.cs`.
* `EntityRegistry.cs.parallel-owned-with-my-entity` — full copy; my change is +1 constant and +1 `EntityDefinition`.
* `PlatformRoleAssignment.cs.parallel-owned-with-my-fields` — full copy; my change is +6 nullable fields and
  `PlatformGrantSources`.

**Stated plainly:** that patch is a snapshot of *shared* files. Reapplying it restores the parallel team's changes to
those four files too, because their edits and mine are interleaved line-by-line and the diff cannot separate them
without the plumbing that a real commit would use. It is a recovery aid, not an isolation of my lines.

## 3. Verification — the bundle was restored, not just written

An unverified archive is not a preservation point.

| Check | Result |
|---|---|
Files archived | **64** |
Files extracted to a disposable directory | **64** |
Per-file SHA-256, extracted vs working tree | **0 mismatches** across every file and every `.cs` in every archived directory |
Patch validity | `git apply --check --reverse` **succeeds** — the patch describes the working tree exactly |
Archive integrity | both `.tar.gz` list and extract without error |

The reverse-apply check is the meaningful one: it proves the patch is not merely well-formed but corresponds
byte-for-byte to the current state of those four files.

## 4. Cleanup

| Check | Result |
|---|---|
`CrossBuyProbe_BootstrapStorage_*` · `_BootstrapSeed_*` · `_BootstrapReader_*` | **0 · 0 · 0** |
All `CrossBuyProbe_*` · `CrossBuyPlatformTest_*` · `CrossBuyImp003_*` | **0 · 0 · 0** |
`CrossBuyDB2` | **present, untouched** — no SQL executed against it |
Orphan mutation markers · orphan scratch sources | **0 · 0** |

106 leaked databases dropped, each name re-verified against the three owned prefixes immediately before its drop.

## 5. State after preservation

| | |
|---|---|
Application suite | **849 passed · 0 failed · 0 skipped** |
Analyzer suite | **93 passed · 0 failed · 0 skipped** |
CBA001 / CBA004 / CBA006 | **0 / 0 / 0** |
Debt · endpoints | **143** · **391 = 157 + 91 + 143** |
Five Mechanism A sites | **unchanged** |
Production authorization behaviour | **unchanged** |
B6 · Batch C · Wave 2 · Security Console · Master Data | **Not Started / unchanged** |

## 6. Phase 2 — NOT STARTED

B2 storage, the B2 SQL slice, B3 seed, B4 reader, their tests, the four mutation proofs, the CRM visible-owner
contract and the compatibility read matrix **do not exist on disk** and are not claimed.

The brief's own rule is the reason this report stops here: *"Do NOT report any item as delivered unless its final
source files exist on disk and verification proves them."* Phase 1 was scoped to make the surviving work
unlosable, and that is done and verified. Beginning Phase 2 in the same increment would have produced exactly the
situation this recovery exists to correct — a report of storage, seed and reader work whose files may or may not
survive.

**Recommended next increment:** Phase 2 in one focused pass, starting from the verified bundle, with the artefact
list re-checked against disk at the end of it. `NeverBootstrapOpen.All` is already fixed and confirmed at 15 entries,
so B2's guard and B3's exclusions have a stable input.