# Stage R1 — Final Delivery Report

## Repository, Deployment and Governance — engineering closure

**Infrastructure, build, deployment, CI, SQL governance and documentation only. No business feature implemented. No production SQL executed. `CrossBuyDB2` untouched.**

---

## 1. Delivered this increment

| # | Deliverable | Artefact |
|---|---|---|
| 1 | Reporting authorization-surface onboarding | `AuthorizationSurface.cs` + `ReportingAuthoritySurfaceTests.cs` (9 tests) |
| 2 | Ownership reconciliation | 8 tabs registered; 280 / 392 changed files claimed |
| 3 | Registry reconciliation | 44 unique ADRs; manifest regenerated (123 scripts); one authored + one applied registry |
| 4 | Integration baseline | Measured and **honestly reported as red** |
| 5 | Governance verification | Gates proved to fire in both directions |
| 6 | Preservation | Archive + restore-test |
| 7 | Owner-action handover | 4 groups, sequenced |

Documents: `Stage-R1-Reporting-Authorization-Surface-Evidence.md` · `-Ownership-Reconciliation.md` · `-Registry-Reconciliation.md` · `-Integration-Baseline.md` · `-Owner-Actions.md` · this report · `Stage-R1-Disk-State-Manifest.csv` · `Stage-R1-Preservation-Report.md`.

## 2. The Reporting authority — added correctly, and not further

`IReportAuthorizationService` / `ReportAuthorizationService` were added to the declared surface **after reading what they do**: they fail closed on an unresolved company, resolve ownership and scope, are wired to real roles in `Program.cs`, and no endpoint accepts a caller-supplied `companyId`.

**Seven of the nine blocked endpoints reach that seam** — `Preview`, `Export`, `SaveReport`, `ForkSavedReport`, `SetDefaultSavedReport`, `DeleteSavedReport`, `AddFavorite`; the template writes reach it transitively through `LoadForAccessAsync`.

**Two do not.** `RemoveFavorite` and `ReorderFavorites` authorize by row ownership only. They are **not** credited, and that is the correct outcome — forcing all nine green would be the false credit CORRECTION-004 exists to prevent. They stay visible to CBA001 for TAB-2 to resolve.

No suppression. No baseline addition. No controller exception. No unrelated Reporting type declared — pinned by a test that fails if anyone adds one.

### A mistake I made and corrected

My first pass concluded *"SaveAsync contains no authorization call"* from a direct-call grep. That was wrong in the dangerous direction: it would have under-credited a path that does authorize through a private helper. The mirror-image trap was also present — `ReportAuthorizationService.CodeCompanyUnresolved` is a **constant reference**, and counting substring matches would have over-credited it. Both were resolved by distinguishing an awaited invocation from a type reference. It is the reason the analyzer is semantic rather than textual.

## 3. Integration baseline — red, and not R1's doing

| Gate | Result |
|---|---|
| Debug build | **0 Error(s)** |
| Release build | **2 Error(s)** — FAIL |
| TestRun build | **4 Error(s)** — FAIL |
| Analyzer build | **0 Error(s)** |
| New authority tests | **9 / 9** (isolation harness) |
| Full application suite | **NOT RUN** — needs a TestRun build |
| CBA001/004/006 | **Not measurable** — the analyzer cannot classify an incomplete compilation |
| SQL governance | **FAIL, exit 2** — 4 divergent duplicates, 2 undeployable |
| Probe databases | **0** · `CrossBuyDB2` untouched |

All three compile errors are in **TAB-2's** `ReportsCenterPresenter.cs` and `ReportingRegistration.cs`, attributed by the R1 ownership tool rather than by inspection. Their implementation was **not repaired**.

Debug builds clean while Release and TestRun do not — the signature of a configuration whose compilation symbols differ, which CLAUDE.md already records as a defect class that cost a whole acceptance cycle. Flagged to TAB-2, not investigated further.

**No green integration baseline is claimed, and no coverage is claimed from the suites that could not run.**

## 4. Gate verification

| Gate | Proved by |
|---|---|
| Ownership — denies another tab | TAB-1 → `ReportEngine.cs` **DENIED**, attributed TAB-2 |
| Ownership — permits the owner | TAB-2 → same file, **PASS** |
| Ownership — shared files | TAB-1 → `Program.cs` **FAIL** without `-AllowShared`, cites SHF-01 |
| SQL G1/G2/G3 | Blocks 4 duplicates, 1 `review`, 1 `not-deployable` |
| SQL G6/G7 | **Caught the manifest stale on first run**, plus 6 claimed slices missing from it |
| Pipeline — production refusal | `CrossBuyDB`, `CrossBuyDB2`, `CrossBuy` all refused with `-Apply -Bootstrap` |
| Pipeline — governance block | Refuses to execute anything while the backlog stands |
| Schema history | Applied, idempotent (appends, `ApplyCount = 2`), CHECK constraints reject malformed **and upper-case** hashes, `changed` drift detected |
| Registries | Regenerate **byte-identical** |

## 5. Defects found in my own work by testing it

| Defect | Consequence had it shipped |
|---|---|
| `-Files a,b` arrived as one joined string | The gate compared `"a,b"` against every glob, matched nothing, and reported **PASS on files it must deny** |
| Ownership globs were not repo-relative | `BL/*AccessService.cs` never matches `CrossBuy/BL/...` — every owned file fell through as unclaimed |
| Annotated globs (`governance/** (R1 tools)`) matched nothing | A rule that matches nothing is a rule that is not enforced |
| `TAB-0`'s Platform Kernel claim silently did not apply | A `sed` target that no longer matched; caught by re-running the sweep, not by reading the diff |
| PowerShell 5.1 reads `.ps1` as ANSI without a BOM | Em-dashes produced parser errors; all tooling is now ASCII-only |

Every one of these was found by running the tool against a case whose answer I already knew. None would have been found by reading the code.

## 6. Completion gate

| Condition | Status |
|---|---|
| Reporting authority added through the official analyzer process | **Yes** |
| Analyzer tests prove the boundary | **Yes** — 9/9, 3 recognised / 4 refused / 2 surface-discipline |
| No suppressions or baseline additions | **Yes** — none |
| Ownership registries reflect all four tabs | **Yes** — eight tabs, including three registered as UNASSIGNED |
| ADR references valid | **Yes** — 44 unique; 030–036 Communication, 037 Reporting, 038/039 R1 |
| SQL manifest remains canonical | **Yes** — regenerated; no competing registry |
| Integration build honestly reported | **Yes — reported RED** |
| Owner actions separated from engineering | **Yes** — four groups, sequenced |
| No business feature modified | **Yes** |
| No SQL executed against `CrossBuyDB2` | **Yes** — 0 probes remain |
| Preservation restores with 0 mismatches | **Yes** |

## 7. Honest position

R1's engineering is closed. What R1 could build alone — the registries, the tools, the gates, the CI, the ADRs, the pipeline, the authority onboarding — is delivered and each part was proved to fire rather than merely written.

What R1 cannot do alone remains open: **the platform tree is still uncommitted**, no Integration Owner is appointed, three tabs are unassigned, branch protection is a remote action, and the SQL backlog needs owners who can decide what the schema should be.

The rule R1 exists to enforce — *no tab may leave the shared integration branch unbuildable* — is, today, tooling a tab may choose to run. It becomes enforcement when the work is committed, the branch exists, and the remote refuses a merge that fails the gate. Those three steps are B1, B9 and C1 in the owner-action package.

**Stopping here for owner actions.**
