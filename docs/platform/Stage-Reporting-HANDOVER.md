# Reporting Platform — Handover Package

**Tab:** 2 — Reporting Platform · **Date:** 2026-08-09
**Status:** **REPORTING WAITING FOR PLATFORM** · Reporting is frozen. No further Reporting implementation is
authorized. Report Studio remains blocked.

---

## 1. Reporting completion summary

| Increment | Delivered |
|---|---|
| **R1 — Activation** | Business Events dataset (`Platform.BusinessEvents.Log`), three-tier visibility permissions, registration, `Program.cs` permission mapping |
| **R2 — Reports Center backend** | `api/reports` — 11 GET endpoints: catalog, categories, describe, datasets, preview, export (HTML/CSV/XLSX), history, history parameters, archive, archive download, saved, favourites |
| **R3 — UI** | Reports Center (`/Reports`) and Report Viewer (`/Reports/Viewer/{code}`), built from Inventory's own components; Business Events pilot filters (delivery state, correlation id, event type, branch, consumer); Workspace contribution via `IWorkspaceReportSource` |
| **A0 — Database deployment** | SQL-06 verified deployable, idempotent, model-parity enforced, vocabulary constrained |

**Visual authority:** the Inventory module. `_LayoutInventory.cshtml` unmodified; Metronic components only;
the bespoke shell, stylesheet and its resources were **deleted**. `/Reports` and `/Inventory/Index` load the
same seven stylesheets, byte for byte.

**Held back, not missing:** seven write endpoints (`save`, `fork`, `set default`, `delete`, `favourite`,
`unfavourite`, `reorder`) are written in full at
`CrossBuy/Controllers/Api/ReportsCenterWriteEndpoints.cs.pending` and are not compiled. `CBA001` blocks them
correctly. The Viewer renders their controls **disabled with a stated reason**. Activation is three steps and
no code change — see `Stage-Reporting-UI-03-Write-Endpoint-Evidence.md`.

---

## 2. SQL-06 deployment evidence

| | |
|---|---|
| **File** | `CrossBuy/deploy/sql/reporting_platform.sql` |
| **SliceId** | `SQL-06` (canonical root `CrossBuy/deploy/sql`) |
| **Bytes** | `32228` |
| **SHA-256** | `53db921a777c4612ee6a1a2943dd4ac8090df1df106e1e996af277f63ed75199` |
| **Manifest** | registered, hash fresh, `idempotency: guarded`, `excludedFromDeploy: false` |

Executed on disposable database `CrossBuyReportingProof_20260809112420`, exactly as the runbook documents:

| Step | Result |
|---|---|
| Reporting tables before apply | **0** |
| `sqlcmd -I -b -i reporting_platform.sql` | **exit 0** — 12 tables `ok`, 7 constraints `ADDED` |
| Tables · `ReportShares` columns | **12** · **14** |
| `ReportShares` indexes | `PK_ReportShares`, `UX_ReportShares_Unique`, `IX_ReportShares_Lookup` |
| CHECK constraints · foreign keys | **7** · **3** |
| Second apply | **exit 0** — 7 × `EXISTS`, **0 × `ADDED`** |
| Inventory after second apply | **12 tables · 37 indexes · 7 checks · 3 FKs** — unchanged |
| Database dropped | confirmed; **0 proof databases remain** |

**Not applied to any real environment.** `CrossBuyDB2`, prelive and production are untouched. The runbook is
prepared and unexecuted and requires explicit owner approval.

**Consequence:** `/Reports/Viewer/{code}` will continue to return `Invalid object name 'ReportShares'` on the
development database until SQL-06 is applied there. That is now a one-command action behind a validated
artifact.

---

## 3. Reporting verification evidence

| Check | Result |
|---|---|
| Reporting test suite | **304 / 304** |
| A0 deployment + governance proofs | **24 / 24** |
| All `SqlServer` tests (isolated) | **48 / 48** |
| `SqlCompanionGuardTests` after my two fixes | **46 / 46** |
| Debug · Release · TestRun compile | **0 `CS` errors in all three** |
| Release with isolated output path | **0 errors, 0 lock failures**, artifacts produced |
| `CBA001` | **zero** |
| Scratch databases on the instance | **0** |
| Preservation archives | R3: 27 members, 0 mismatches · A0: 17 members, 0 mismatches |

**Two failures in the final full run were mine and are fixed:**

- `Guard6_every_SQL_evidence_test_in_the_assembly_is_registered_in_the_manifest` — 11 new SQL evidence tests
  were unregistered. Added to `engineering/required-evidence-manifest.json`; `enforcedTestCount` 217 → 228.
- `Guard3_no_leftover_scratch_database_exists_on_the_real_instance` — scratch databases survived backgrounded
  and killed runs. Dropped; zero remain.

**`EnsureCreated()` is no longer accepted as deployment evidence.** `ReportingTestHost` carries the warning;
`ReportingDeploymentGovernanceTests` (ungated, 7 tests) fails if Reporting production code creates its own
schema, if the slice leaves the canonical root, if a second copy appears, if the manifest hash goes stale, or
if a Reporting entity is added without matching SQL.

---

## 4. Remaining external blockers

| Test | Owner | Reason |
|---|---|---|
| `CrossBuy.Tests.TaskDependencyTests.A_two_node_cycle_is_refused` | TAB 4 — Tasks/Calendar | `Assert.False() Failure — Expected: False, Actual: True` (`TaskEcosystemTests.cs:160`) |
| `CrossBuy.Tests.TaskDependencyTests.A_long_cycle_is_refused_and_the_offending_chain_is_named` | TAB 4 — Tasks/Calendar | `Assert.False() Failure — Expected: False, Actual: True` (`TaskEcosystemTests.cs:186`) |
| `CrossBuy.Tests.TaskDependencyTests.The_same_edge_cannot_be_added_twice` | TAB 4 — Tasks/Calendar | `SqliteException 19: UNIQUE constraint failed: TaskDependencies.CompanyId, TaskDependencies.PredecessorTaskId, TaskDependencies.SuccessorTaskId` |
| `CrossBuy.Tests.TaskChecklistTests.Reordering_keeps_line_identity_and_its_done_state` | TAB 4 — Tasks/Calendar | `Assert.Equal() Failure — Expected: [3, 2, 1], Actual: [4, 5, 6]` (`TaskEcosystemTests.cs:375`) |
| `CrossBuy.Tests.SqlServer.WorkerLeaseTests.With_the_requirement_disabled_both_processes_run_workers` | Platform | `SqlException: Login failed for user 'AHMEDZAKAREYA\Lenovo'` — transient; the class passes in isolation |

The four TAB 4 failures reproduce deterministically with `CROSSBUY_TEST_SQL` both set and unset, in class
isolation: **4 failed / 14 passed** either way.

---

## 5. Freeze

Reporting is frozen. Report Studio remains blocked until Platform declares the repository green.
