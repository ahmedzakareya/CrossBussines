# A0 — 4 · Idempotency evidence

**Test:** `ReportingSchemaDeploymentTests.Applying_the_slice_twice_leaves_an_identical_object_inventory`
**Result: PASS.** Four applications total; inventory identical after the first.

---

## 1. What is compared

Not a table count. A **full ordered object inventory**, read from the system catalogs after each pass:

| Kind | Captured |
|---|---|
| `TABLE` | name |
| `COLUMN` | table.column, type, max_length, is_nullable |
| `INDEX` | table.index, is_unique, is_filtered |
| `FK` | table.constraint |
| `CHECK` | table.constraint |
| `DEFAULT` | table.constraint |

The assertion is full sequence equality. **Counting would pass if one index vanished while another
appeared**; comparing the ordered inventory cannot. A second assertion proves no entry is duplicated within
a snapshot, which is the failure a non-guarded `CREATE INDEX` would produce.

## 2. Sequence

1. Empty probe database created (`CrossBuyProbe_rptIdem_<guid>`), `applyModelSchema: false`.
2. Apply the slice → snapshot A.
3. Apply it **twice more** → snapshot B.
4. `Assert.Equal(A, B)` — full inventory, element by element.
5. `Assert.Equal(B.Distinct().Count(), B.Count)` — nothing duplicated.
6. Probe dropped, removal confirmed.

The third and fourth applications are deliberate: a defect that only appears on the *second* re-run — a
guard that consumes its own precondition — would survive a single re-apply test.

## 3. Why it is idempotent

- Every table: `IF OBJECT_ID(N'dbo.X', N'U') IS NULL BEGIN CREATE TABLE … CREATE INDEX … END`. Indexes sit
  **inside** the table guard, so they are created exactly once with their table.
- Every CHECK constraint: its own `IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = …)`.
- The closing verification `SELECT` is a read.
- **No DROP, no ALTER COLUMN, no data statement anywhere in the file.**

The manifest generator classifies it independently: `"idempotency": "guarded"`, guards
`object-null`, `if-not-exists`, `sys-catalog`, and `unguardedBatches: []`.

## 4. The from-empty proof

`The_reporting_slice_creates_its_whole_schema_in_an_empty_database` asserts the starting state is empty
before applying — a proof that begins on a database somebody already prepared proves only that it stayed
prepared. It then checks all twelve tables are present and that the count is exactly twelve, plus
`ReportShares`' ten columns and its two authorization indexes by name.

## 5. `sqlcmd -I`

The file creates filtered indexes, which require `QUOTED_IDENTIFIER ON`. The test harness runs each batch
through `SqlCommand`, where that is the default; a manual apply must pass `-I`. The runbook states this.
