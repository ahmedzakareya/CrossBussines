# Branch protection — the `integration` branch

**These settings live on the remote, not in the repository.** Nothing in `governance/tools` can enforce them; a script can refuse to push, but only the server can refuse to accept. This file records exactly what must be configured, so "protected" is a checked fact rather than an assumption.

Applies to: **`integration`**

---

## Required settings

| Setting | Value | Why |
|---|---|---|
| Require a pull request before merging | **On** | Direct pushes are how the shared tree broke five times |
| Required approvals | **1** (2 for shared-file changes) | A second reader on `Program.cs` and `CrossDbContext.cs` |
| Dismiss stale approvals on new commits | **On** | An approval describes the diff it saw |
| Require status checks to pass | **On** | |
| Required check | **`Integration gate / gate`** | The 12-check gate |
| Require branches to be up to date before merging | **On** | Green against an old base is not green |
| Require conversation resolution | **On** | |
| Allow force pushes | **Off** | |
| Allow deletions | **Off** | |
| Restrict who can push | Integration Owner only | The branch is merged into, not pushed to |
| Include administrators | **On** | A rule that exempts its author is not a rule |

## Merge policy

* **Merge commits**, not squash — the merge commit is what gets reverted when a merge breaks the branch, and reverting one commit is the whole rollback strategy.
* A merge that breaks `integration` is **reverted immediately**, not fixed forward. Fixing forward blocks the other three tabs for the duration of the fix.
* The reverted tab keeps its branch and its history, and re-merges once the gate is green.

## Tab branches

Naming: `tab/tab-1-…`, `tab/tab-2-…`, created by `governance/tools/setup-worktrees.ps1`.

The CI workflow parses the tab id out of the branch name to decide whose ownership rules apply. **A branch that does not follow the convention is checked against `TAB-0`**, which owns only shared files — so it will fail on almost any change. That is deliberate: an unattributable branch should not merge.

## Verifying the protection is real

```bash
gh api repos/:owner/:repo/branches/integration/protection
```

Confirm: `required_status_checks.contexts` contains `Integration gate / gate`; `required_status_checks.strict` is `true`; `enforce_admins.enabled` is `true`; `allow_force_pushes.enabled` is `false`; `allow_deletions.enabled` is `false`.

## Not yet applied

**This has not been configured** — the repository currently has a single `master` branch and no remote protection. Applying it is an Integration Owner action requiring repository-admin rights, and it is listed as an R1 owner action rather than claimed as done.
