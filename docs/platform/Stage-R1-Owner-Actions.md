# Stage R1 — Owner Actions

**Engineering is complete where engineering can complete it. Everything below needs an authority engineering does not have.**

Grouped by *who* must act, because that is what determines when each can happen.

---

## A. Engineering-complete — no owner action required

| # | Item | Evidence |
|---|---|---|
| A1 | Reporting authority added through the documented analyzer process | `Stage-R1-Reporting-Authorization-Surface-Evidence.md` · 9/9 boundary tests |
| A2 | Ownership registries cover eight tabs; 280 of 392 changed files claimed | `Stage-R1-Ownership-Reconciliation.md` |
| A3 | ADR registry reconciled — 44 unique, ownership confirmed | `Stage-R1-Registry-Reconciliation.md` |
| A4 | ADR-038 and ADR-039 written | `docs/platform/` |
| A5 | SQL manifest regenerated; authored registry canonical, no competing registry | manifest 123 scripts |
| A6 | `PlatformSchemaHistory` applied and proved on an isolated probe | 10 proofs, ADR-039 §"Verification performed" |
| A7 | Drift detection blocks on G1–G7 plus the five applied-side states | `validate-sql-governance.ps1` |
| A8 | Deployment pipeline refuses production catalogues under every flag | proved for all three names |
| A9 | Build gate captures error counts explicitly rather than inferring them | `build-gate.ps1` |
| A10 | 12-check integration gate + CI workflow running the *same* scripts | `.github/workflows/integration-gate.yml` |
| A11 | Worktree tooling, dry-run by default, refuses a dirty tree | `setup-worktrees.ps1` |

## B. Repository-owner actions

| # | Action | Why it blocks | Blocks |
|---|---|---|---|
| **B1** | **Commit all untracked platform work** — the entire `BL/Platform` tree, `BL/Workspace`, `BL/Reporting`, `BL/TasksCalendar`, `Models/Context/Reporting`, `Views/Workspace`, and the R1 governance tooling itself | Every tab's base shifts invisibly; selective commits diff against nothing stable | B5, C1, and R1 exit criteria 3, 7, 10, 11 |
| **B2** | **Appoint the Integration Owner (TAB-0)** | TAB-0 owns `Program.cs`, `CrossDbContext`, resx, both SQL roots, the Platform Kernel, the ADR and slice registries — and is currently nobody | Every shared-file change |
| **B3** | **Assign TAB-5** (Tasks and Calendar) | Two hosted services run in production wiring with no owner | R5 |
| **B4** | **Assign TAB-6** (Workspace) | R3 code is already in the tree with no owner | R3 |
| **B5** | **Assign TAB-7** (legacy business modules) | Carries most of the 143 authorization debt | R6, R8 |
| **B6** | **TAB-2 to fix 3 Reporting compile errors** and confirm the Debug-vs-Release symbol discrepancy | Release and TestRun do not build | The entire integration baseline |
| **B7** | **TAB-2 to decide `RemoveFavorite` / `ReorderFavorites`** — route through the authorization seam, or accept row ownership and record it | They remain visible to CBA001; not forced green | Reporting activation |
| **B8** | **TAB-3 / TAB-6 to settle the DTO contract** (`CommActorDto.DisplayName`, `CommMentionHistoryItemDto.MentionedAt`, `ReportingWorkspaceSource`) | Cross-tab contract drift. A consumer never edits the producer's DTO | The integration baseline |
| **B9** | **Create per-tab worktrees** — *after* B1 | `setup-worktrees.ps1` deliberately refuses a dirty tree, because creating branches around uncommitted work loses work | The execution model |

## C. Remote-host actions

| # | Action | Why engineering cannot do it |
|---|---|---|
| **C1** | **Apply branch protection to `integration`** per `governance/branch-protection.md` — require the `Integration gate / gate` check, require up-to-date branches, disallow force-push and deletion, include administrators | A script can refuse to push; only the server can refuse to accept. Requires repository-admin rights. |
| **C2** | **Enforce the tab branch-name convention** (`tab/tab-N-…`) | CI derives the tab id from the branch name; an unattributable branch is checked against TAB-0 and will fail |
| **C3** | **Verify protection is real**: `gh api repos/:owner/:repo/branches/integration/protection` | The settings live outside the repository |

## D. Database-owner actions

| # | Action | Detail |
|---|---|---|
| **D1** | **Reconcile the 4 divergent POS slices** — `pos_hold_recall`, `pos_order_guests`, `pos_quickmenu`, `pos_setup` | Each pair diffed and **read by the POS owner**, then superseded by ONE slice with a new `SliceId`. Choosing by timestamp or location is guessing about schema. Governance detects the ambiguity; it cannot decide the schema. |
| **D2** | **Review `hm16_rename_grni.sql`** | Classified `review` — a mutating batch with no re-run guard that nobody has read. Read it, then add a verdict to `$reviewLedger` in `scan-sql-manifest.ps1`. **Do not widen the guard regex to silence it.** |
| **D3** | **Exclude `script.sql` from deployment** | Already `not-deployable` and `excludedFromDeploy` in the manifest. Confirm the exclusion is intended and permanent, or delete the file. |
| **D4** | **Decide backup/restore versus slice replay authority** | `deploy/README.md` states provisioning is BACKUP/RESTORE of `CrossBuyDB2` because EF migrations are broken and ~90 ad-hoc scripts in `C:/temp` built the schema. That is a third source of truth outside the repository. Which is authoritative for a *new* environment? |
| **D5** | **Stamp every existing database** into `PlatformSchemaHistory` using the documented baseline procedure | `AppliedBy = 'baseline'` distinguishes an asserted row from an observed one. **Never infer from the schema**; leave anything unestablished as pending. An unstamped restore is an ungoverned database and may not be promoted. |
| **D6** | **Migrate the 57 package-root slices** to the canonical authored root | Slice by slice, with hash verification. No file was moved in this increment. |

## E. Sequence

```
B1 commit ─┬─> B9 worktrees ─> C1 branch protection ─> gate enforced
           ├─> B2 appoint Integration Owner ─> B3/B4/B5 assign tabs
           └─> B6/B8 fix the tree ─> green integration baseline
D1/D2/D3 ──> SQL gate green ──> D5 stamp ──> D6 migrate ──> R2 activation
```

**B1 is the keystone.** It blocks the worktrees, which block branch protection, which is what makes *"no tab may leave the integration branch unbuildable"* enforceable rather than aspirational. Until then the rule exists as tooling a tab can choose to run.

## F. What must NOT be done

* Do not append to `authorization-baseline.json` to clear the remaining CBA001 endpoints — the baseline may only shrink.
* Do not suppress CBA001.
* Do not widen the manifest guard regex to silence `hm16_rename_grni.sql`.
* Do not pick a divergent duplicate by timestamp.
* Do not run `apply-sql-slices.ps1` against `CrossBuyDB`, `CrossBuyDB2` or `CrossBuy` — it refuses, and that refusal should not be engineered around.
* Do not fix another tab's code to make your own gate pass.
