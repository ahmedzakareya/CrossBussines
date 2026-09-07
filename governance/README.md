# CrossBusiness Platform — Repository Governance (R1)

**One rule sits above the rest:**

> **No tab may leave the shared integration branch unbuildable.**

This directory is the machinery that makes that rule enforceable rather than aspirational.

---

## Why this exists

During Stage 2A, **five cross-tab build breaks came from three different tabs**. In every case the change was legitimate work by its author. What was missing was any mechanism that noticed a file belonged to somebody else, or that a build was red, before the tree went down for everyone.

Alongside that: the SQL deployment tree had split into two roots whose four overlapping files all differ, an unreviewed mutating script sat in the tree, and the entire platform source was uncommitted.

R1 does not add a feature. It makes the repository committed, owned and deployable so that everything built afterwards does not inherit those conditions.

## Layout

```
governance/
  README.md                     <- you are here
  branch-protection.md          <- the remote settings that make "protected" real
  registry/                     <- GENERATED. Do not hand-edit.
    tab-ownership.json          <- who owns which paths
    shared-files.json           <- files no single tab may edit freely
    adr-registry.json           <- ADR numbers, reserved before writing
    sql-slices.json             <- slice claims, canonical root, applied registry
    manifest-facts.json         <- snapshot of the authored registry at R1 design time
  tools/
    integration-gate.ps1        <- the 12-check gate. Run this before you merge.
    build-gate.ps1              <- Debug/Release/TestRun with EXPLICIT error counts
    check-file-ownership.ps1    <- may this tab change these files?
    validate-sql-governance.ps1 <- authored-side SQL governance (G1-G7)
    apply-sql-slices.ps1        <- the deployment pipeline (dry run by default)
    setup-worktrees.ps1         <- per-tab worktrees + integration branch (dry run by default)
```

The registries are **generated** from the roadmap canonical dataset (`docs/roadmap-v2/_generator/`). One source, two renderings — documents for people, JSON for gates. A registry that drifts from the roadmap would be the same defect the two SQL trees demonstrate.

## The everyday commands

```powershell
# before you merge - run this
powershell -File governance/tools/integration-gate.ps1 -TabId TAB-1 -Base origin/integration

# just the build, with error counts captured rather than inferred
powershell -File governance/tools/build-gate.ps1 -Clean

# am I allowed to change these files?
powershell -File governance/tools/check-file-ownership.ps1 -TabId TAB-2 -Base origin/integration

# is the SQL tree in a deployable state?
powershell -File governance/tools/validate-sql-governance.ps1

# what would a deployment do? (nothing is executed without -Apply)
powershell -File governance/tools/apply-sql-slices.ps1 -ConnectionString "Server=.;Database=CrossBuyProbe_X;Trusted_Connection=True;TrustServerCertificate=True"
```

## The 12 integration-gate checks

| # | Check | Fails when |
|---|---|---|
| 1–3 | Debug / Release / TestRun build | any configuration reports errors, **or no error-count summary is found** |
| 4 | Full suite green | any test failed; the skip count is always reported |
| 5 | CBA001 / CBA004 / CBA006 | any analyzer authorization error |
| 6 | Authorization debt | debt count and entry count disagree |
| 7 | Suppressions | a CBA pragma or authorization SuppressMessage appears |
| 8 | Owned paths only | a file belongs to another tab |
| 9 | Shared files | a shared file is touched without `-AllowShared` |
| 10 | SQL governance | G1–G7 (below) |
| 11 | Hosted services | a registered hosted service is absent from the DI wiring test |
| 12 | Residue | a `MUTATION A-H` marker was left behind |

**A skip is reported as loudly as a failure.** Coverage is never claimed from a skip; CI passes `-RequireAll` so a check that could not run fails the build.

## SQL governance conditions

The **applied** side — applied / changed / failed / pending / missing — is covered by `CrossBuy/deploy/report-schema-history.ps1`, which compares the database against the manifest. It is deliberately not duplicated.

`validate-sql-governance.ps1` covers the **authored** side, which a database-side report cannot see:

| Code | Condition | Behaviour |
|---|---|---|
| G1 | Same filename in both trees, different content | **BLOCK** — `apply <name>.sql` is ambiguous |
| G2 | Script classified `review` | **BLOCK** — unguarded, unread |
| G3 | Script classified `not-deployable` | **BLOCK** |
| G4 | New slice outside the canonical root | Reported; the historical backlog is listed, not failed |
| G5 | Duplicate `SliceId` | **BLOCK** |
| G6 | `manifest.json` older than a `.sql` file | **BLOCK** — the registry is describing the past |
| G7 | Registry names a slice that does not exist | **BLOCK** |

Everything blocks rather than warns. A warning in a deployment pipeline is a message nobody reads at 2 a.m.

## SQL roots

| Root | Role |
|---|---|
| `CrossBuy/deploy/sql` | **Canonical authored root** (D-38). New slices go here. The manifest generator and schema-history reporter live beside it at `CrossBuy/deploy/`. |
| `deploy/` | **Deployment package root.** Generated artifacts (`fresh/`, produced by `gen_fresh_db.ps1`), operational scripts (backup, purge), the server README. **Not an authoring root.** |

53 module slices still sit in the package root. That is a known migration backlog, executed slice by slice with hash verification — not a reason to fail every build today. A gate that fails on day one for historical files gets switched off, and a switched-off gate protects nothing.

## Two registries, not one

| Registry | Answers | Where |
|---|---|---|
| **Authored** | What slices exist, in what order, with what hash and idempotency verdict | `CrossBuy/deploy/sql/manifest.json` (generated by `scan-sql-manifest.ps1`) |
| **Applied** | What has actually run against *this* database, with what hash, when, by whom, and whether it succeeded | `dbo.PlatformSchemaHistory` |

Neither replaces the other. The manifest cannot know what a database contains; the database cannot know what the repository intends. Drift is the disagreement between them.

## Rules

| Rule | Statement |
|---|---|
| **Buildability** | Debug, Release and TestRun all build before merge. The error count is **captured**, never inferred. `--no-build` is never the build. |
| **Commit** | Every tab commits its own work daily. |
| **Shared files** | Edited only inside the tab's marked region, or by request to the Integration Owner. resx files get our keys only via git plumbing — never a whole-file `git add`. |
| **ADR numbers** | Reserved in `registry/adr-registry.json` before the document is written. |
| **SQL slices** | `SliceId` claimed before authoring. Idempotent and additive. Never an EF migration. |
| **Program.cs** | One registration extension method per platform, so the shared file gains one line rather than forty. `AddCrossBusinessReporting` is the pattern. |
| **CrossDbContext** | DbSets added in the tab's marked region. Never reordered, never reformatted — 245 DbSets make whole-file edits certain conflicts. |
| **Rollback** | Revert the merge commit, not the tab's branch. A breaking merge is reverted, **not fixed forward** — fixing forward blocks the other three tabs for the duration. |
| **Stale builds** | Delete outputs before an acceptance build; record the assembly timestamp. A test result from an unverified build is the previous result wearing a new timestamp. |

## Safety properties of the tools

Each was proved to fire, not merely written:

* `apply-sql-slices.ps1` **refuses `CrossBuyDB`, `CrossBuyDB2` and `CrossBuy` under every flag combination.** A deployment tool that can reach production by accident eventually will.
* It is **dry run by default**; `-Apply` is explicit.
* It **blocks on the governance backlog** before executing anything.
* `-Bootstrap` is the one narrow exception — history table only, and only if that script is itself clean — because the applied registry cannot otherwise be established while a backlog exists.
* `setup-worktrees.ps1` is dry run by default and **refuses to run against a dirty tree**, because creating branches around uncommitted work is how work gets lost.

## Related documents

* `ADR-038` — parallel execution model (worktrees + protected integration branch)
* `ADR-039` — canonical SQL root and the two registries
* `docs/deployment/SQL-Deployment-Runbook.md` — the human deployment procedure
* `docs/roadmap-v2/Roadmap-V2-17-SQL-Deployment-Governance.md` — the design this implements
* `docs/roadmap-v2/Roadmap-V2-09-Parallel-Execution-Model.md` — the model and its alternatives
