# CrossBusiness Platform — R1 Delivery Report

## Repository, Ownership and Deployment Governance

**Infrastructure, build, deployment, CI and documentation only. No business feature implemented, no production source changed, no production SQL applied.**

---

## 1. What was delivered

| Artifact | Purpose |
|---|---|
| `governance/README.md` | Repository governance entry point |
| `governance/branch-protection.md` | The remote settings no script can enforce |
| `governance/registry/tab-ownership.json` | Who owns which paths — **generated** |
| `governance/registry/shared-files.json` | Files no single tab may edit freely — **generated** |
| `governance/registry/adr-registry.json` | ADR numbers, reserved before writing — **generated** |
| `governance/registry/sql-slices.json` | Slice claims, canonical root, applied registry — **generated** |
| `governance/registry/manifest-facts.json` | Authored-registry snapshot for drift detection — **generated** |
| `governance/tools/integration-gate.ps1` | The 12-check gate |
| `governance/tools/build-gate.ps1` | Debug/Release/TestRun with **explicit** error counts |
| `governance/tools/check-file-ownership.ps1` | Tab ownership and shared-file enforcement |
| `governance/tools/validate-sql-governance.ps1` | Authored-side SQL governance, G1–G7 |
| `governance/tools/apply-sql-slices.ps1` | Deployment pipeline — dry run by default |
| `governance/tools/setup-worktrees.ps1` | Worktrees + integration branch — dry run by default |
| `.github/workflows/integration-gate.yml` | CI running the **same** scripts as developers |
| `ADR-038` | Parallel execution model |
| `ADR-039` | Canonical SQL root and the two registries |

The five registries are **generated from the roadmap canonical dataset**, so gates and documents cannot disagree. A hand-maintained second copy would be the same defect the two SQL trees demonstrate.

## 2. What already existed — and was extended, not rebuilt

A substantial part of R1's SQL core was already built (Stage 0 Batch B) and **entirely untracked**:

* `platform_schema_history.sql` — table, view, two indexes, two CHECK constraints, and a documented baseline procedure. Better than the design I had specified.
* `scan-sql-manifest.ps1` — manifest generator with encoding detection and guard analysis.
* `report-schema-history.ps1` — applied/changed/failed/pending/missing with CI exit codes 0/1/2/3.
* `docs/deployment/SQL-Deployment-Runbook.md`.

**None of it was duplicated.** `validate-sql-governance.ps1` covers only what a database-side report structurally cannot see — the authored tree. Rebuilding the applied-side states would have created two implementations of one concept.

## 3. Verification performed

### SQL pipeline — isolated probe `CrossBuyProbe_R1_Gov`, dropped afterwards

| Proof | Result |
|---|---|
| Production catalogue refusal — `CrossBuyDB`, `CrossBuyDB2`, `CrossBuy`, with `-Apply -Bootstrap` | **Refused, exit 2, nothing executed — all three** |
| Governance block without `-Bootstrap` | **Blocked, exit 2, nothing executed** |
| `-Bootstrap` applies only the history table | 1 slice, exit 0 |
| Table + view + 2 indexes + 2 CHECK constraints | All created |
| The apply recorded itself | Real SHA-256, `Success = 1` |
| Second apply idempotent and **appends** | 2 rows, `ApplyCount = 2` |
| `CK_..._Hash` rejects malformed hash | Rejected |
| `CK_..._Hash` rejects **upper-case** hash | Rejected — the `BIN2` collation is load-bearing |
| `CK_..._Error` rejects failure with no reason | Rejected |
| `changed` drift detected | `CHANGED: 1`, exit 1 |
| Probes remaining after cleanup | **0** · `CrossBuyDB2` present and untouched |

### Ownership enforcement — proved in both directions

| Case | Result |
|---|---|
| TAB-1 touching TAB-2's `ReportEngine.cs` | **DENIED**, exit 1, attributed to TAB-2 |
| TAB-2 touching its own `ReportEngine.cs` | PASS |
| TAB-1 touching its own files | PASS, `owned = 2` |
| TAB-1 touching shared `Program.cs` | **FAIL** without `-AllowShared`, cites SHF-01 and its rule |

### SQL governance — G1–G7

Detected on first run: 4 divergent duplicate pairs (G1), 1 unreviewed script (G2), 1 not-deployable (G3), **a stale manifest (G6)** and 6 registry-claimed slices absent from it (G7). Regenerating the manifest cleared G6/G7; G1–G3 are a real backlog requiring their owners.

## 4. Defects found in my own tooling by testing it

Three, all of which would have shipped a gate that silently passed:

1. **`-Files a,b` arrives as one joined string.** The ownership check compared `"a,b"` against every glob, matched nothing, and reported **PASS on files it must deny**. Fixed by splitting; re-proved in both directions.
2. **Ownership globs were not repo-relative.** `BL/*AccessService.cs` never matches `CrossBuy/BL/AccountingAccessService.cs`, so genuinely-owned files fell through as unclaimed. All six tabs normalised.
3. **Annotated globs matched nothing.** `governance/** (R1 tools)` is not a path. The generator now strips the annotation — a rule that matches nothing is a rule that is not enforced.

Also: PowerShell 5.1 reads `.ps1` as ANSI without a BOM, so em-dashes produced parser errors. All R1 tooling is ASCII-only.

## 5. The tree is RED — and it is not R1's doing

`build-gate.ps1` reports **Debug 24 errors, Release 15, TestRun 15**. It was 0/0/0 earlier the same day.

**Attribution, proven not assumed:**

| Evidence | Finding |
|---|---|
| Files carrying the errors | `CrossBuy/BL/Workspace/WorkspaceService.cs`, `CrossBuy/Controllers/Api/ReportsCenterApiController.cs` |
| Git status of both | **Untracked**, created **10:41** and **10:44** today |
| `.cs` files among R1's changes | **0** — R1 delivered `.ps1`, `.md`, `.json`, `.yml` only |
| New untracked trees | `CrossBuy/BL/Workspace/`, `CrossBuy/Views/Workspace/`, `CrossBuy/Controllers/WorkspaceController.cs`, `CrossBuy/BL/Reporting/`, `CrossBuy/Models/Context/Reporting/` |

Compile failures are against Communication DTOs (`CommActorDto.DisplayName`, `CommMentionHistoryItemDto.MentionedAt`) and Reporting signatures — cross-tab contract drift.

**Nine of the errors are `CBA001`**: the analyzer refusing new mutating endpoints (`Preview`, `Export`, `SaveReport`, `ForkSavedReport`, `SetDefaultSavedReport`, `DeleteSavedReport`, `AddFavorite`, `RemoveFavorite`, `ReorderFavorites`) that carry no authorization and cannot be added to a shrink-only baseline. **That is the authorization gate working exactly as designed.**

This is RSK-05 occurring live, during the phase built to prevent it — the most direct evidence available that R1 is necessary. I did not fix another tab's code: R1 forbids business features, and the files belong to TAB-2 and TAB-6.

**Consequence for this report:** R1's artifacts compile nothing, so their delivery is unaffected. But **no green-build claim is made**, and the integration gate cannot pass on the shared tree until those two tabs' work compiles.

## 6. Scope observation

`WorkspaceController.cs` and `Views/Workspace/` are **R3** work; `ReportsCenterApiController.cs` is **R4**. The owner-approved plan sequences both **after** R1 and R2. Stated as a fact for the owner, not as a judgement — but the ownership registry now claims those paths (TAB-2, and TAB-6 as **UNASSIGNED**) so the gate can attribute future changes instead of letting them land unclaimed.

## 7. R1 exit criteria — honest status

| # | Criterion | Status |
|---|---|---|
| 1 | All four tabs' work committed | **NOT DONE** — owner action; the whole platform tree is still untracked |
| 2 | Canonical authored root declared; `deploy/` labelled the package root | **DONE** — D-38, ADR-039, enforced by G4 |
| 3 | 53 package-tree slices migrated | **NOT DONE** — scheduled; no file moved, as instructed |
| 4 | 4 divergent pairs reconciled | **NOT DONE** — owner action (D-39); detected and blocking |
| 5 | `review` / `not-deployable` scripts resolved | **NOT DONE** — owner action; detected and blocking |
| 6 | `PlatformSchemaHistory` designed, authored, applied first | **DONE and proved** on an isolated probe |
| 7 | Environments stamped and reconciled | **NOT DONE** — requires approved environments |
| 8 | Restore path reconciled with the slice path | **NOT DONE** — owner decision |
| 9 | Drift detection blocks on all conditions | **DONE** — G1–G7 plus the five applied-side states |
| 10 | Worktrees, integration branch and gate in force in CI | **PARTIAL** — tooling and workflow delivered; branch protection is a remote action, and worktree setup **refuses to run against today's dirty tree** |
| 11 | Zero build breaks for one full cadence | **NOT MET** — the tree broke during R1 (§5) |

**R1 is not complete.** Six criteria require owner action or another tab's work. What R1 *can* deliver alone — the registries, the tools, the gates, the CI, the ADRs, the pipeline, and their verification — is delivered and proved.

## 8. Owner actions required

1. **Commit all four tabs' work.** Criterion 1 blocks 3, 7, 10 and 11, and `setup-worktrees.ps1` deliberately refuses a dirty tree.
2. **Appoint the Integration Owner** (TAB-0) and assign **TAB-5** (Tasks/Calendar, 2 live hosted services) and **TAB-6** (Workspace).
3. **Fix the red tree** — TAB-2 and TAB-6 code; 9 CBA001 endpoints need authorization, not a baseline entry.
4. **Reconcile the 4 divergent POS slices** (D-39) — read by their owner, superseded by one slice.
5. **Read or exclude `hm16_rename_grni.sql`** and `script.sql`.
6. **Apply branch protection** on `integration` per `governance/branch-protection.md`.
7. **Decide the restore-vs-slice authority** for new environments.

## 9. Confirmations

No business feature implemented · no production `.cs` changed · no production SQL applied · `CrossBuyDB2` untouched · 0 probe databases remaining · no file moved or deleted in either SQL tree · nothing committed or pushed.

Not begun: Reporting features · Communication features · Workspace · Tasks · Calendar · CRM · Construction · Security Console · Master Data.
