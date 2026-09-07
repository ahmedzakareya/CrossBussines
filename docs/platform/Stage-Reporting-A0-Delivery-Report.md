# A0 — Reporting Database Deployment · Delivery Report

**Tab:** 2 — Reporting Platform · **Date:** 2026-08-09

---

## 1. Headline

**The Reporting schema already existed.** It was authored, reviewed, hashed into the manifest and registered
as SliceId `SQL-06` — and never applied to the development database. `Invalid object name 'ReportShares'`
was a **deployment state**, not a missing artifact.

My previous report said the opposite:

> *"deploy/sql has 58 files and not one creates a Reporting table. The Reporting platform has never had a
> deployment script."*

That was wrong. I searched the legacy repo-root `deploy/sql/` and never looked at the canonical
`CrossBuy/deploy/sql/`. The brief's "do not immediately write a new schema" rule is the only reason A0 did
not produce a second, divergent Reporting schema.

## 2. What A0 actually closed

| Gap | Closed by |
|---|---|
| No **re-runnable** proof the slice applies — the earlier from-empty proof was a one-off document, and a document cannot fail when someone edits an entity | `ReportingSchemaDeploymentTests`, 13 tests on disposable SQL Server databases |
| No **mechanical** EF↔SQL parity — nothing failed when a Reporting entity was added without SQL | 3 parity tests, ownership derived from the CLR namespace, one of them needing **no database** |
| **No CHECK constraints** — vocabulary was C#-only, as the architecture had recorded | 7 constraints, each proven to reject |
| Nothing distinguished `EnsureCreated()` from deployment evidence | `ReportingDeploymentGovernanceTests` (ungated) + a warning block in `ReportingTestHost` |

## 3. Changes

| File | Change |
|---|---|
| `CrossBuy/deploy/sql/reporting_platform.sql` | **+1 section**: 7 CHECK constraints, outside the CREATE guards so they reach existing databases. Nothing else altered. |
| `CrossBuy/deploy/sql/manifest.json` | regenerated with the **existing** generator; hash refreshed |
| `CrossBuy.Tests/SqlServer/ReportingSchemaDeploymentTests.cs` | new — 13 tests |
| `CrossBuy.Tests/ReportingDeploymentGovernanceTests.cs` | new — 7 ungated tests |
| `CrossBuy.Tests/ReportingTestHost.cs` | the "not deployment evidence" warning |

**No new SQL root. No second manifest. No new history table. No EF migration.** The legacy tree was not
moved or deleted. The POS name collisions the generator reports are pre-existing and belong to their owners.

## 4. Verification

| Check | Result |
|---|---|
| A0 proofs (deployment + governance), SQL enabled | **24 / 24** |
| All `SqlServer` tests | **48 / 48** |
| Reporting suite | **304 / 304** |
| Full suite, SQL enabled | 1746 total · 1744 passed · **2 failed** |
| Debug / Release / TestRun | **0 `CS` errors in all three** |
| Scratch databases remaining | **0** |
| `CrossBuyDB2` | **untouched** |

## 5. Honest limits

**Two suite failures.** Enabling `CROSSBUY_TEST_SQL` turned 183 skipped tests into real runs and 2 fail.
They are not in the `SqlServer` namespace (48/48 alone) and not Reporting (304/304 in the same session) —
pre-existing SQL-gated tests elsewhere that had never actually executed. **Gate item 16 is therefore not
met**, and I have not attributed them because attribution without the failure text would be a guess.

**Builds.** All three configurations compile with 0 errors, but `MSB3021`/`MSB3027` file-copy failures occur
in each because the running IIS Express worker and Visual Studio hold `bin/<config>/net8.0/CrossBuy.dll`. An
environment lock, not a code defect; clearing it means stopping your running app.

**Not applied anywhere real.** The runbook is prepared and unexecuted. `/Reports/Viewer` will keep returning
500 on the dev database until the slice is applied there — that is now a one-command action with a validated
artifact behind it.

## 6. Verdict

Eighteen of the twenty gate conditions are met. Two are not:

- **#16 full suite 0 failures** — 2 pre-existing SQL-gated failures, unattributed (§5).
- **#14 Debug/Release/TestRun succeed** — they compile cleanly; the *copy* step fails on locked DLLs (§5).

Both are environmental or pre-existing rather than defects in the Reporting deployment, and every
Reporting-specific condition (#1–#13, #17–#20) is met. On the brief's own terms that is not a clean pass, so
the verdict is stated as blocked with the exact reason rather than rounded up.

## 7. Open items

| # | Item | Owner |
|---|---|---|
| **B1** | Identify and attribute the 2 SQL-gated failures exposed by running with a database for the first time | needs a labelled run; not Reporting |
| **B2** | Apply `reporting_platform.sql` to the dev database (and later, with approval, to real environments) — see the runbook | owner |
| **B3** | Stop IIS Express / Visual Studio to obtain a lock-free Release and TestRun build | owner |
| A1 | `IReportAuthorizationService` → `AuthorizationSurface.AuthorityTypes`, unblocking the 7 write endpoints | TAB 1 |
