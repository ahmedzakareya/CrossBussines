# ADR-038 — Parallel execution model: per-tab worktrees and a protected integration branch

**Status:** Accepted (R1) · **Owner:** Integration Owner · **Area:** Governance

---

## Context

Four tabs build CrossBusiness Platform concurrently in **one shared mutable working tree**. During Stage 2A this produced **five build breaks from three different tabs** — Reporting (`ReportParameterSet`, `IReportService`), Communication (`ICommActorDirectory`), Construction (`CrossBuy.BL.Construction`, `BoqLineInput`).

In every case the change was legitimate work by its author. Nothing was careless. What was missing is structural: **a shared mutable tree has no mechanism to reject a broken state.** The break is discovered by whoever builds next.

Two compounding conditions:

* `git ls-files` returns **empty** for the entire `Platform/` tree — every tab's base can shift invisibly, and selective commits diff against nothing stable.
* One acceptance run had to be performed on an older binary and declared as such, because a parallel tab broke the tree immediately after the build.

## Decision

**Per-tab git worktrees, with a protected `integration` branch as the single place they combine.**

Each tab develops in its own worktree on its own branch (`tab/tab-1-…`). A tab merges to `integration` only after the 12-check integration gate passes in its own worktree.

> **No tab may leave the shared integration branch unbuildable.**

A merge that breaks `integration` is **reverted, not fixed forward.**

## Alternatives considered

| Model | Isolation | Break containment | Verdict |
|---|---|---|---|
| Single shared tree (status quo) | None | None — one tab breaks all four | **Rejected.** This is the observed failure. |
| Sequential tabs | Total | Total | **Rejected.** Four tabs at a quarter speed. |
| Per-tab clone | High | High | **Rejected** — see below. |
| **Per-tab worktree** | High | High | **Accepted.** |
| Integration branch | — | — | **Accepted alongside**, not instead. |

**Why worktrees rather than clones.** The four tabs share one SQL slice registry and one ADR registry. A second clone lets those registries diverge exactly the way `deploy/sql` already has — two copies of one concept, drifting apart, with no mechanism to notice. Worktrees share one object store, so the registries stay single-sourced while each tab still gets a real directory it can build in.

**Why revert rather than fix forward.** A fix-forward leaves the other three tabs blocked for however long the fix takes. Reverting one merge commit restores everyone in seconds and costs the breaking tab only a re-merge. This is also why the merge policy is merge-commits rather than squash: the merge commit is the unit of rollback.

## Consequences

**Positive.** A broken state is rejected at the boundary instead of discovered by the next person to build. Ownership becomes checkable. Each tab gets a stable base to diff against.

**Negative.** Disk cost of four working directories. Every tab must adopt simultaneously — a partial adoption leaves the shared tree in play and yields nothing. Branch protection requires repository-admin rights the tabs may not hold.

**Neutral.** Merge conflicts move from "silent breakage in a shared tree" to "explicit conflict at merge". That is strictly better, but it is not less work.

## Implementation

* `governance/tools/setup-worktrees.ps1` — creates the branch and the worktrees. **Dry run by default**, and it **refuses a dirty tree** (`-AllowDirty` to override), because creating branches around uncommitted work is how work gets lost.
* `governance/tools/integration-gate.ps1` — the 12 checks.
* `.github/workflows/integration-gate.yml` — runs the *same* script in CI, so CI cannot drift from what developers check locally.
* `governance/branch-protection.md` — the remote settings, which no script can enforce.
* `governance/registry/tab-ownership.json` — generated from the roadmap canonical dataset.

## Compliance

The gate reports a **SKIP as loudly as a FAIL**, and CI passes `-RequireAll` so a check that could not run fails the build. This follows the existing platform rule that coverage is never claimed from a skipped test.

## Open

**Two tabs are unassigned.** `TAB-5` (Tasks and Calendar) has two live registered hosted services and no owner; `TAB-0` (Integration Owner) is not appointed. Ownership *boundaries* are decided (D-32, D-33); the owners are not named. Until they are, those paths are enforced as unclaimed rather than protected.
