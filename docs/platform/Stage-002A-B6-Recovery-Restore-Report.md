# CrossBusiness Platform — Stage 2A B6 — Recovery Restore Report

**Restoration succeeded. All three files match the verified artifact byte-for-byte.**

---

## 1. Source artifact

| | |
|---|---|
Archive | `stage2a-phase5-b6-unverified.tar.gz` |
SHA-256 | `c838003b1992c4b8d93cd06c5a3686cf716ec1115f8b81d9a83241dbfd8660e8` |
Contents | exactly 3 files |
Provenance | created before the mutation cycle, restore-verified at creation |

**Only the archive was used.** No `.bak` file, no editor history, no session memory, no manual reconstruction, no
copy/paste from logs — the brief forbids all five and none was used.

## 2. Restored files

| File | Recorded | Restored | Match | Lines | Bytes |
|---|---|---|---|---|---|
`CrossBuy/BL/AccountingAccessService.cs` | `2660fe293f7a0518` | `2660fe293f7a0518` | **YES** | 217 | 11,088 |
`CrossBuy/BL/InventoryAccessService.cs` | `c4e83c0f1d1da748` | `c4e83c0f1d1da748` | **YES** | 269 | 13,054 |
`CrossBuy.Tests/SqlServer/B6BootstrapConversionTests.cs` | `e91e035e68b617b4` | `e91e035e68b617b4` | **YES** | 581 | 26,648 |

## 3. The finding — which file was actually damaged

Hashes taken **before** restoring:

| File | Before restore | Archive | Was it damaged? |
|---|---|---|---|
`AccountingAccessService.cs` | `2660fe293f7a0518` | `2660fe293f7a0518` | **no — already identical** |
`InventoryAccessService.cs` | `c4e83c0f1d1da748` | `c4e83c0f1d1da748` | **no — already identical** |
`B6BootstrapConversionTests.cs` | `841827a3a109e46b` | `e91e035e68b617b4` | **YES** |

**The production authorization code was never corrupted.** Both converted access services were byte-identical to the
verified artifact before any restore was attempted. The damaged file was the **test** file — my Mutation H restore left
it differing from the archive.

This corrects the previous increment's diagnosis, which attributed the 6 failures to a bad restore of
`InventoryAccessService.cs` on the strength of a grep count. See the mechanism-site reconciliation §6 for why that
count was misread.

## 4. Files deliberately NOT restored

| File | Why not |
|---|---|
`CrossBuy.Tests/B6TestWiring.cs` | created **after** the artifact; supplies the two new constructor dependencies to 10 call sites. Restoring the archive cannot affect it and it must survive. |
10 patched test call sites (`BatchCAccessServiceTests`, `D1Wave1*`, `HotfixA1*`, `Stage1Notification*`, `Stage1Permission*`, `Stage1F4*`) | patched after the artifact; reverting them would re-break compilation |
`engineering/`, `docs/`, all B2/B3/B4 source | not in this artifact and not implicated |

## 5. Parallel-tab files — none overwritten

`CrossBuy/BL/Reporting/` (17 files, SECOND TAB) and `CrossBuy/BL/Communication/` (THIRD TAB) are untouched: the archive
contains exactly three paths, none of them theirs. `git status` still shows their directories as untracked and
unmodified by this increment.

## 6. Shared-tree observation

The restore itself succeeded, but the tree **cannot currently be built** — two other tabs have in-progress code with
undefined types:

| Path | Missing type | Owning tab |
|---|---|---|
`CrossBuy/BL/Communication/CommThreadService.cs:59,69` | `ICommActorDirectory` | THIRD TAB — Communication Platform |
`CrossBuy/BL/Reporting/ReportPrintService.cs:155,160` | `IReportService` | SECOND TAB — Report Studio |

Per tab ownership these were **not modified**. The block is recorded, not worked around.
