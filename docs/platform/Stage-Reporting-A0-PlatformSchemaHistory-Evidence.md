# A0 — 6 · PlatformSchemaHistory evidence

**No new migration-history table was created.** The Reporting slice records through the existing
`dbo.PlatformSchemaHistory` and its `dbo.vw_PlatformSchemaCurrent` view, unchanged.

---

## 1. The existing contract

`CrossBuy/deploy/sql/platform_schema_history.sql` owns:

- `dbo.PlatformSchemaHistory` — append-only log: `ScriptName` (repo-relative path), `ScriptHash` (SHA-256,
  lower-case hex), `AppliedAt`, `AppliedBy`, `ApplicationVersion`, `Success`, `Error`.
- `CK_PlatformSchemaHistory_Hash` — 64 lower-case hex characters, compared under `Latin1_General_BIN2`
  because CrossBuy databases are case-insensitive and the rule would otherwise not exist.
- `CK_PlatformSchemaHistory_Error` — a failure must carry a reason; a success must not.
- `dbo.vw_PlatformSchemaCurrent` — latest row per script, with `ApplyCount`.

A0 uses all of it and changes none of it.

## 2. First apply → `ApplyCount = 1`

`The_reporting_slice_records_through_the_existing_platform_schema_history`:

1. Empty probe database.
2. Apply `platform_schema_history.sql` — the prerequisite, exactly as a deployment would.
3. Apply `reporting_platform.sql`.
4. Record one row: `ScriptName = 'CrossBuy/deploy/sql/reporting_platform.sql'`, `ScriptHash` = the file's
   **real** SHA-256 computed from its bytes at test time (not a literal — a hard-coded digest would go stale
   the moment the slice changed and the test would then pass while describing a different file).
5. `SELECT ScriptName, ApplyCount FROM dbo.vw_PlatformSchemaCurrent WHERE ScriptName LIKE '%reporting_platform.sql'`
   → exactly one row, **`ApplyCount = 1`**.

The same test then proves the hash CHECK is live: inserting the *upper-cased* digest is refused with **547**.
That matters more than it looks — a mis-cased hash would never match the manifest, and the drift report would
call an applied script "changed" forever.

## 3. Second apply → drift is visible

`A_changed_slice_hash_is_visible_as_a_second_apply` records two different hashes for the same script name and
asserts the view reports the **latest** hash and **`ApplyCount = 2`**.

This is what makes the drift report work: if the view collapsed rows by name it could never say "this file
changed since it was applied". Recording is append-only; the view derives current state.

## 4. Idempotent re-apply adds no schema objects

Covered by the idempotency evidence: the object inventory after four applications is identical to the
inventory after one. Schema history counts **apply attempts**, which is a different fact from schema state —
`ApplyCount = 2` with an unchanged inventory is exactly the expected reading of a re-run.

## 5. Scope

- Applied only to disposable `CrossBuyProbe_*` databases created and dropped by the test fixture.
- **`CrossBuyDB2` was not touched.** Neither the Reporting slice nor `platform_schema_history.sql` was run
  against it, in line with the brief.
