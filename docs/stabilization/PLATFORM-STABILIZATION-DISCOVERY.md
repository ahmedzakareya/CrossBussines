# Platform Stabilization — Discovery

**Starting HEAD:** `dea7bf3` · **Branch:** master · **Date:** 2026-09-01

## Work-safety state, recorded before anything was touched

```
worktrees          12
index              clean (0 staged)
tracked-modified   252     (all foreign; untouched by this batch)
untracked          378
stashes            3       (brand-blue WIP; TAB-3's parked Program.cs/CrossDbContext; TAB-0 chat WIP)
local-only branches feature/brand-facebook-blue, integration/final-convergence,
                    reporting/report-studio-v2
```

Canonical changes were made in a **dedicated clean worktree** (`wt-stab`, branched from master).
The shared tree was never written to. No `reset --hard`, no destructive `clean`, no stash dropped,
no shared file copied over a dirty one.

## Test files outside Git

| | count |
|---|---|
| tracked in canonical Git (`git ls-files CrossBuy.Tests`) | **120** |
| present on the development machine | **226** |
| **present but NOT reachable from Git** | **106** |
| tracked but absent from disk | 0 |

Independently re-measured; the convergence report's figures were correct.

## Untracked PRODUCTION code found during discovery

Nine paths, and two of them explain whole families of failing tests:

* `CrossBuy/BL/Comm/` — a **superseded** outbox dispatcher (3 Aug). Canonical HEAD carries
  `BL/Communication/CommNotificationDispatcher` (19 Aug) instead, which claims with `ClaimedAt`,
  recovers stale claims and is covered by tracked tests.
* `CrossBuy/BL/Construction/`, `CrossBuy/Models/Context/Construction/` — an untracked production
  slice belonging to another work stream. Canonical HEAD does not reference it and builds without it.
* `CrossBuy/Models/DevSeedFixture.cs` — a production remediation that exists only on this machine.
* `EmployeeNames.cs`, `ModulePermAttributes.cs`, three Views.

## Overlapping candidate patches

24 loose `.patch` files under `C:\CrossBuy`, all from batches already landed, plus
`_blue-*.patch` (three open brand handoffs) and `_ar-reversal-*.patch` (AR receipt reversal, still
unlanded and out of scope for a stabilization batch).
