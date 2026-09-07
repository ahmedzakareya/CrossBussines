# A0 — 7 · Runtime smoke evidence

**Test:** `ReportingSchemaDeploymentTests.The_reporting_services_run_against_the_authored_schema`
**Result: PASS**, against a database built solely by `reporting_platform.sql`.

---

## 1. Why table presence was not enough

Asserting the twelve tables exist proves names. It does not prove the **columns are the ones EF binds to** —
and that is precisely the gap that produced twenty `Invalid column name` errors in `TaskScopeQuerySqlTests`
when a hand-written table carried only the columns its assertions touched.

So the smoke test issues real EF queries through `CrossDbContext` against the probe database. A column
mismatch surfaces here as a `SqlException` rather than at a user's first click.

## 2. What was exercised

| Path | Assertion |
|---|---|
| **`ReportShares`** — the query that produced the 500 | `db.ReportShares.Where(s => s.CompanyID == 1 && s.DeletedAt == null)` executes and returns empty. This is the shape `HighestShareLevelAsync` runs on **every** report authorization. |
| All twelve `DbSet`s | each materialises without error |
| Write round-trip | a `ReportTemplate` is inserted with `Scope = Company`, then re-read **through a fresh context** and its `ReportCode` and `Scope` verified |

The round-trip matters: a `SELECT` over an empty table exercises the column list but not the defaults or the
enum→INT conversion. Writing exercises both, and reading back from a *new* context proves the value was
persisted rather than served from the change tracker — the same "prove from a new context" rule the rest of
this codebase follows.

## 3. What this does and does not establish

**Establishes:** the authored SQL produces a schema the real Reporting services can read and write. The
Business Events report's data source reads `BusinessEvents` (kernel-owned, not in this slice), but every
reporting *service* it passes through — authorization, templates, history, archive — binds to tables this
slice creates, and all of them now bind successfully.

**Does not establish:** that any real environment has the slice applied. It has not been. The runbook exists
for that and requires explicit owner approval.

## 4. Held back

Write endpoints remain blocked by `CBA001` pending TAB 1's authorization-surface addition. **The analyzer was
not bypassed to test them.** Their service layer is proven by `ReportingWriteAuthorizationTests`, which runs
on the in-memory host; A0 did not weaken a security guardrail to improve its own coverage.
