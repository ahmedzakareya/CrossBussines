# Post-Stabilization Qualification — Discovery

**Canonical start:** `f932d06` · branch `master` · 2026-09-01

## Work-safety state, recorded before any edit

```
staged            0
tracked-modified  265
untracked         284
stashes           3      (brand-blue WIP; TAB-3's parked Program.cs/CrossDbContext; TAB-0 chat WIP)
worktrees         13
local branches    feature/brand-facebook-blue, integration/final-convergence,
                  reporting/report-studio-v2, stabilization/test-recovery
```

Work was done in a dedicated clean worktree (`wt-qual`). No reset, no clean, no stash dropped, no
worktree deleted, no checkout over foreign modifications, no push.

## Where the foreign InventoryApproval fix actually lives

Searched every worktree by blob hash:

```
CrossBuy (shared tree)   ef1f2229a0a2   fix present   <-- UNCOMMITTED working-tree work
all 12 other worktrees   5cac5c7ef44c   canonical, unfixed
```

It is on **no branch and no commit** — it existed only as uncommitted changes in the shared tree.

## Foreign files touching this batch's subject

| File | State | Relationship |
|---|---|---|
| `CrossBuy/BL/InventoryApprovalService.cs` | modified | **the fix** |
| `CrossBuy.Tests/ApprovalCompanyIsolationTests.cs` | modified | the fix's own suite + constructor update |
| `CrossBuy.Tests/ApprovalInboxAggregatorTests.cs` | modified | constructor update |
| `CrossBuy/Controllers/InventoryController.cs` | modified | **UNRELATED** — a bilingual `NameEn` display fix for work orders. Left untouched. |
| `CrossBuy/BL/Platform/PlatformPermissionProvider.cs` | clean | — |
| `CrossBuy/BL/Platform/RequestCompanyResolver.cs` | clean | — |
| `CrossBuy/BL/Platform/BusinessContextAccessor.cs` | clean | — |

Captured before integration: byte copies and `git hash-object` for all four, the 261-line foreign
diff, the full tracked diff, `git status` and the stash list.

## Integration outcome

The three fix files were applied to the clean worktree and verified **byte-identical** to the
foreign originals. `InventoryController.cs` was deliberately excluded, so its unrelated bilingual
work is preserved untouched in the shared tree.

No DI change was required: `IBusinessContextAccessor` is already registered (`Program.cs:225`), so
the new constructor parameter resolves.
