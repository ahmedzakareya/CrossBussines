# ADR-012 — Deployment state is tracked by an append-only log with hashes, and ordering lives in a generated manifest

**Status:** Accepted, implemented in Stage 0 (Slice-003) Batch B.

## Context

CrossBuy does not use EF migrations. `Migrations/` is a dead snapshot. Schema ships as **additive, idempotent `.sql`
applied before the code that needs it**. That discipline is deliberate and it works — it survives partial deploys,
it is reviewable, and it does not fight a 218-DbSet model.

What it does not do is remember anything. Three specific consequences, all recorded as gaps in the discovery pass:

- **B5 (High):** two `deploy/sql` folders, 110 scripts, no manifest, no applied-scripts table. *"Is this database
  current?"* is unanswerable.
- **A2 (Critical):** kernel SQL merged while its code was live. Verified afterwards: CrossBuyDB2 has slice 1 (applied
  externally by someone, with no record) but **not** slice 2 or slice 3. Nothing in the database said so; it took
  querying `COL_LENGTH` on three tables to find out.
- **A8 (Medium):** nine scripts believed non-idempotent — which turned out to be a scanner defect
  ([CORRECTION-002](../architecture/CORRECTION-002-Evidence-Scanner-Defects.md)), but nobody could tell, because the
  verdict had no derivation anyone could re-run.

The default answer to all of this is "adopt a migration framework". That is rejected below.

## Decision

### 1. Ordering and idempotency live in a **generated** manifest

`deploy/sql/manifest.json`, produced by `CrossBuy/deploy/scan-sql-manifest.ps1`. Per script: repo-relative path,
SHA-256, detected encoding, dependency `rank`, guards found, mutations found, the list of `GO`-batches with **no**
guard, an idempotency verdict, and whether it is excluded from deployment and why.

**Generated, not hand-written.** A hand-written manifest goes stale silently, and the entire value of the manifest is
being trustworthy about what is deployable.

**Idempotency is judged per `GO`-batch, not per file.** A file-level scan calls a script safe because *some other*
batch in it happened to contain a guard.

**The generator refuses to call an unread script safe.** It flags every mutating batch with no recognised guard as
`review`, and a human records a verdict **with evidence** in a review ledger inside the generator. Anything flagged
and not in the ledger stays `review`, and the report command prints "do not deploy". This is the direct
counter-measure to how the previous SQL verdict went wrong: the temptation is to widen the guard regex until nothing
is flagged, and that is how a scanner starts lying. Current state: 88 `guarded`, 7 `guarded-by-predicate`
(read and judged — e.g. `UPDATE t SET c = x WHERE c IS NULL`, which no textual guard will ever recognise),
14 `no-mutation`, 1 `not-deployable`, **0 unread**.

**Encoding is detected, not assumed.** `deploy/sql/fresh/03_seed_config.sql` is UTF-16LE; read as UTF-8 it scans as
mojibake and the guard check silently sees an empty file.

**Name collisions are reported.** Four file names exist in both `deploy/sql/` and `CrossBuy/deploy/sql/` **with
different content** (`pos_setup.sql`, `pos_quickmenu.sql`, `pos_hold_recall.sql`, `pos_order_guests.sql`), so
"apply `pos_setup.sql`" is an ambiguous instruction. Reported rather than fixed: reconciling the two trees is a
separate change with its own risk.

### 2. Applied state is an **append-only** log keyed by name **and hash**

`dbo.PlatformSchemaHistory` — `ScriptName`, `ScriptHash` (SHA-256), `AppliedAt`, `AppliedBy`, `ApplicationVersion`,
`Success`, `Error` — plus `vw_PlatformSchemaCurrent` (the latest row per script).

**Append-only.** Applying an idempotent script twice is legal and produces two rows. The current state of a script is
its *latest* row, never a mutated one, so a bad deploy stays readable.

**The hash is the point.** It turns "applied" into a checkable claim. If the file in git no longer hashes to what was
applied, the script **changed after deployment** and the database is running older DDL than the repository describes.
A name-only log cannot detect that, and it is the single most useful thing this table does.

**Failures are recorded too** (`Success = 0` + `Error`). A half-applied script is the worst state a deploy can be left
in, and it must not be invisible.

**Two CHECK constraints, both load-bearing:**
- `ScriptHash` must be 64 lower-case hex characters — with `COLLATE Latin1_General_BIN2` on the comparison, because
  CrossBuy's databases use a **case-insensitive** collation under which `'%[^0-9a-f]%'` happily accepts `ABC…` and
  the rule would not exist. (Verified: without the collation the constraint accepted upper-case hex.)
- `Success = 1 AND Error IS NULL` **or** `Success = 0 AND Error IS NOT NULL`. A failure with no reason recorded is not
  a usable record; a success carrying an error message is a contradiction.

### 3. Nothing reads it at runtime, and no script gates on it

The application does not read this table, and no script refuses to run because of it. It is a **reporting surface for
an operator**. That is what lets an out-of-band manual apply — which is how CrossBuyDB2 has always been maintained —
be reconciled afterwards instead of leaving the log permanently wrong.

### 4. Reconciliation is an **explicit baseline**, never an inference

`report-schema-history.ps1 -Baseline <path>` records a script as already-applied with `AppliedBy = 'baseline'`, so the
row stays distinguishable from an observed apply forever.

The procedure (in the script's own header and in the runbook) requires checking the objects a script creates, and
**leaving unrecorded anything you cannot establish**. Pending-for-an-applied-script is harmless — the scripts are
idempotent, so re-running is a no-op. Applied-for-an-unapplied-script is the dangerous direction, because the report
then hides real work. Guessing from the schema is forbidden: it is how a log starts lying.

### 5. The report classifies five states and exits distinctly

| State | Meaning | Exit |
|---|---|---|
`applied` | latest apply succeeded and the file still hashes to what was applied | 0 |
`changed` | applied, but the file has been edited since | 1 |
`pending` | no successful apply recorded | 1 |
`missing` | recorded as applied, but no such file in the repository | 1 |
`failed` | the latest recorded attempt failed | **2** |

`failed` exits 2 rather than 1 because a half-applied script is not the same problem as work not yet done, and CI
should be able to tell them apart. `3` means the command itself could not run.

## Consequences

**Ordering is documented, not enforced.** No trigger, constraint or procedure enforces `rank`. Encoding order in the
database would make this a migration engine, which is the thing the project decided not to have. The runbook is the
enforcement.

**`platform_schema_history.sql` requires SQL Server 2016 SP1+** for `CREATE OR ALTER VIEW`. DROP-then-CREATE across
two batches is not re-runnable as a unit: if the CREATE batch fails, the previous run's view is already gone and the
database is left worse than before the script ran. The same engine floor already applies to the kernel's `ISJSON`
constraint, so this adds no new requirement.

**The table must be deployed first**, and the report command **refuses to create it** — so "SQL before code" stays
true even for the tool that tracks SQL.

**The report refuses a connection string with no Initial Catalog** rather than defaulting to `master`, because
`-Baseline` against `master` would silently create the table in the wrong database.

## Alternatives rejected

**Adopt EF migrations.** Rejected. It would require reverse-engineering 218 DbSets into an initial migration that
matches a database nobody can fully characterise, and the first `Database.Migrate()` against CrossBuyDB2 would be a
guess with production data behind it. The existing idempotent-SQL discipline is *safer* here; what it lacked was
memory, and memory is what this ADR adds.

**A third-party migration runner** (DbUp, Flyway, RoundhousE). Rejected for this stage: each brings a
hash-and-ordering model that conflicts with "idempotent scripts may be re-run deliberately". DbUp in particular
treats a script as run-once, which is the opposite of the discipline here. Worth revisiting if the two `deploy/sql`
trees are ever consolidated.

**A `SchemaVersion` integer.** Rejected: it cannot express "these 14 scripts are applied and those 3 are not", which
is the actual shape of every real CrossBuy database.

**Deriving applied-state from the schema** (does `BusinessEvents` exist ⇒ slice 1 applied). Rejected as the basis for
the log — it is fine as *evidence for a human performing a baseline*, but as an automatic inference it is wrong for
any script that alters rather than creates, and it silently fails for a script that was partially applied.

## Verification

- `SqlServer/PlatformSchemaDeploymentTests` (9) — slices 1/2/3 applied repeatedly on a disposable database, with
  every table, PK, FK, CHECK, unique index and **filtered** index asserted from `sys.*`; the `ISJSON` constraint
  proven to reject and to be correctly skipped below major version 13; the dedup index proven unique **per company**
  and to ignore NULL keys; slice 2 proven to perform **no** backfill; and the platform scripts proven to create no
  journal/stock/invoice table.
- `platform_schema_history.sql` and `report-schema-history.ps1` verified end to end on a scratch database that was
  created and dropped: applied twice (table + view + 3 indexes + 2 CHECKs, no duplicates); the hash constraint
  rejects a short hash **and** upper-case hex; a failure with no reason is rejected; `applied`/`pending`/`changed`/
  `failed`/`missing` all detected with the right exit codes; append-only confirmed (`ApplyCount = 2`, latest hash
  wins); `-Failed` without `-ErrorText` refused; a connection string with no database refused.
- **Nothing was executed against CrossBuyDB2.** Its state was read to establish what is and is not applied.