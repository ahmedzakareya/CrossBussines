# SQL Deployment Runbook

**Applies to:** every `deploy/sql/*.sql` script, and specifically to the two approved for controlled manual
deployment to **CrossBuyDB2**: `platform_business_events_slice_002.sql` and `comm_outbox_slice_003.sql`.

**Status of those two at the time of writing (verified 2026-08-03 against the live database):**

| Script | Applied to CrossBuyDB2? | Evidence |
|---|---|---|
`platform_business_events.sql` (slice 1) | **Yes** — applied externally | `BusinessEvents` and `BusinessEventDispatch` exist; 5 event rows present |
`platform_business_events_slice_002.sql` | **No** | `Notifications.EntityType` does not exist |
`comm_outbox_slice_003.sql` | **No** | `CommMessages.ClaimedAt` does not exist |
`platform_schema_history.sql` | **No** | `PlatformSchemaHistory` does not exist |

**Nothing in this repository has been executed against CrossBuyDB2.** All SQL verification was performed against
disposable scratch databases that were created and dropped (see *Verification already performed*). Applying these
scripts to CrossBuyDB2 is a manual, deliberate act, performed by a human, after a full backup.

---

## 1. Why the discipline is what it is

CrossBuy does **not** use EF migrations. `Migrations/` is a dead snapshot. Schema changes ship as **additive,
idempotent `.sql` scripts applied BEFORE the code that needs them**. Three consequences follow, and they are the
whole reason this runbook exists:

1. **Order matters, and nothing enforces it.** `platform_business_events_slice_002.sql` requires slice 1. Ordering
   lives in `deploy/sql/manifest.json` (`rank`), not in the scripts.
2. **Re-running must be safe.** A deploy can be interrupted; a script can be applied twice by two people. Every
   deployable script is verified re-runnable — see `manifest.json` and
   [CORRECTION-002](../architecture/CORRECTION-002-Evidence-Scanner-Defects.md).
3. **The database does not remember what ran.** That is what `PlatformSchemaHistory` fixes, and it is the first
   thing to deploy.

### The one hard coupling to know before touching anything

`JournalEntryService.ReverseAsync` — the **only** accounting-correction primitive — calls
`IBusinessEventService.RecordAsync` in-transaction before commit, with no swallowing catch. So **every correction
path** (edit sales/purchase invoice, edit returns, cancel a paid POS order, reopen a fiscal year, FX-revaluation
reverse, manual reversal) **fails completely if `BusinessEvents` is missing or its schema changed.** Normal posting
(`CreateAndPost` / `Post`) is *not* coupled — only reversal.

Slice 1 is already applied to CrossBuyDB2, so this is currently satisfied. It is stated here because it is the
reason "slice 1 first, always" is not a preference.

---

## 2. Pre-flight

Run these **before** touching the database. Each has a stop condition.

### 2.1 Regenerate and read the manifest

```powershell
powershell -NoProfile -File CrossBuy/deploy/scan-sql-manifest.ps1
```

**STOP if** `review (UNREAD)` is greater than 0. That means a script contains a mutating batch with no re-run guard
that nobody has read. Read it, then record a verdict with evidence in the `$reviewLedger` in that script. Do **not**
widen the guard regex to make the warning go away.

Expected today: `guarded 88 · guarded-by-predicate 7 · review 0 · not-deployable 1 · no-mutation 14`.

### 2.2 Note the name collisions

The manifest reports **4 file names that exist in both `deploy/sql/` and `CrossBuy/deploy/sql/` with different
content**: `pos_setup.sql`, `pos_quickmenu.sql`, `pos_hold_recall.sql`, `pos_order_guests.sql`.

**"Apply pos_setup.sql" is therefore an ambiguous instruction.** Always use the full repo-relative path. This is
recorded, not fixed: reconciling the two trees is a separate change with its own risk.

### 2.3 Confirm the script you are about to run

```powershell
Get-FileHash CrossBuy/deploy/sql/platform_business_events_slice_002.sql -Algorithm SHA256
```

Compare against `sha256` for that path in `manifest.json`. If they differ, the manifest is stale — re-run 2.1.

### 2.4 Read the script

Not a formality. Confirm for yourself that it is additive, that every column it adds is nullable, and that it
contains no `DROP`, no `DELETE`, no `UPDATE` outside a guard, and no backfill you did not expect. Both approved
scripts add nullable columns and indexes only.

### 2.5 Check the engine version

```sql
SELECT SERVERPROPERTY('ProductVersion'), SERVERPROPERTY('ProductMajorVersion');
```

Major version **13 or above (SQL Server 2016+)** is required for two things: the kernel's `ISJSON` payload
constraint, and `CREATE OR ALTER` in `platform_schema_history.sql`. On an older engine the JSON constraint is
skipped by design (the payload contract is then enforced by `BusinessEventService` alone) and
`platform_schema_history.sql` will fail — do not deploy it there.

### 2.6 Take a full backup

```sql
BACKUP DATABASE CrossBuyDB2
    TO DISK = 'D:\Backups\CrossBuyDB2_preSlice2_20260803.bak'
    WITH INIT, CHECKSUM, STATS = 10;
RESTORE VERIFYONLY FROM DISK = 'D:\Backups\CrossBuyDB2_preSlice2_20260803.bak' WITH CHECKSUM;
```

**STOP if `RESTORE VERIFYONLY` does not succeed.** A backup that has not been verified is not a rollback plan.

Record: file path, size, completion time, and the verify result.

---

## 3. Apply order

Apply in ascending `rank`. For the currently outstanding work:

| # | Script | Rank | Why in this position |
|---|---|---|---|
1 | `CrossBuy/deploy/sql/platform_schema_history.sql` | 40 | Deploy it **first** so every step after it can be recorded. It only adds one table, one view and their indexes. |
2 | *(baseline slice 1)* | — | Record that slice 1 is already applied — see §4. No SQL runs. |
3 | `CrossBuy/deploy/sql/platform_business_events_slice_002.sql` | 11 | Requires slice 1 (already applied). Adds `Notifications.EntityType` / `EntityId` + two filtered indexes. |
4 | `CrossBuy/deploy/sql/comm_outbox_slice_003.sql` | 31 | Requires the existing `CommMessages` table. Adds `ClaimedAt`, a filtered dispatch index, and two CHECK constraints. |

The schema-history script is out of rank order deliberately: its rank describes its *dependencies* (none), while its
position here reflects that you want the log in place before you start.

### How to run one script

```powershell
sqlcmd -S . -d CrossBuyDB2 -E -C -b -f 65001 `
       -i CrossBuy\deploy\sql\platform_business_events_slice_002.sql
```

- `-b` — exit on error. Without it sqlcmd continues past a failed batch and reports success.
- `-f 65001` — UTF-8. Without it, Arabic literals in seed scripts are stored as mojibake. (This bit
  `pos_fix_preset_names.sql` once already; that script exists to repair exactly this mistake.)
- `-E` — Windows auth. `-C` — trust the server certificate.

Each script prints what it did: `ADDED Notifications.EntityType` on a first run, `Notifications.EntityType EXISTS`
on a second. **Read the output.** A clean run of an already-applied script prints only `EXISTS` lines.

### After each script

```powershell
powershell -NoProfile -File CrossBuy/deploy/report-schema-history.ps1 `
    -ConnectionString "Server=.;Database=CrossBuyDB2;Trusted_Connection=True;TrustServerCertificate=True" `
    -Record "CrossBuy/deploy/sql/platform_business_events_slice_002.sql" `
    -ApplicationVersion "<the version being deployed>"
```

If a script **failed**, record that too, with the reason. A half-applied script that is not recorded is the worst
state a deployment can be left in:

```powershell
... -Record "CrossBuy/deploy/sql/comm_outbox_slice_003.sql" -Failed -ErrorText "Msg 4922, Level 16 ..."
```

---

## 4. Baselining CrossBuyDB2 (one time only)

CrossBuyDB2 has had scripts applied to it manually over months with no record. Do **not** infer which ones from the
schema, and do **not** mark everything applied to make the report green. Both produce a log that lies.

1. Apply `platform_schema_history.sql`.
2. For each script you want to claim is already applied, **check the objects it creates.** For slice 1:

   ```sql
   SELECT OBJECT_ID('BusinessEvents')            AS Events,
          OBJECT_ID('BusinessEventDispatch')     AS Dispatch,
          (SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_BusinessEvents_DedupKey') AS DedupIndex;
   ```

   All non-null and 1 ⇒ slice 1 is applied.
3. Record **only** what you established:

   ```powershell
   ... -Baseline "CrossBuy/deploy/sql/platform_business_events.sql"
   ```

   `-Baseline` stamps `AppliedBy = 'baseline'`, so the row stays distinguishable from an observed apply forever.
4. Leave everything you could not establish **unrecorded**. Pending-for-an-applied-script is harmless — the scripts
   are idempotent, so re-running is a no-op. Applied-for-an-unapplied-script is the dangerous direction, because the
   report then hides real work.

---

## 5. Smoke tests

Run these **after** the deploy, against CrossBuyDB2, in this order. They are ordered cheapest-first so a failure
costs the least.

### 5.1 Schema present

```sql
-- slice 2
SELECT COL_LENGTH('dbo.Notifications','EntityType') AS EntityType,   -- expect 120
       COL_LENGTH('dbo.Notifications','EntityId')   AS EntityId;     -- expect 4
SELECT name, is_unique, has_filter FROM sys.indexes
 WHERE object_id = OBJECT_ID('dbo.Notifications')
   AND name IN ('IX_Notifications_Recipient_DedupKey','IX_Notifications_Entity');
-- expect 2 rows, is_unique = 0, has_filter = 1 for both

-- slice 3
SELECT COL_LENGTH('dbo.CommMessages','ClaimedAt') AS ClaimedAt;      -- expect 8
SELECT name, filter_definition FROM sys.indexes WHERE name = 'IX_CommMessages_Dispatch';
-- expect a filter mentioning 'Sent'
SELECT name FROM sys.check_constraints
 WHERE name IN ('CK_CommMessages_Status','CK_CommMessages_Attempts');   -- expect 2 rows
```

### 5.2 Nothing was rewritten

```sql
-- Slice 2 performs NO backfill. Every pre-existing notification must still have NULL entity columns.
SELECT COUNT(*) AS ShouldBeZero
  FROM dbo.Notifications
 WHERE CreatedAt < '2026-08-03' AND (EntityType IS NOT NULL OR EntityId IS NOT NULL);
```

### 5.3 Idempotency, on the real database

Re-run **both** scripts. Every line of output must be an `EXISTS` line, and no row count may change:

```sql
SELECT COUNT(*) FROM dbo.Notifications;   -- before and after: identical
SELECT COUNT(*) FROM dbo.CommMessages;    -- before and after: identical
```

### 5.4 The reversal path still works — the highest-value test

This is the one that exercises the hard coupling in §1. In the application, with a **demo/`ZZ-*` document only**:

1. Post a small sales invoice.
2. Edit or cancel it, so `JournalEntryService.ReverseAsync` runs.
3. Confirm it succeeds, and that a reversing journal entry exists (**not** a deleted row).
4. Confirm a `JournalEntry.Reversed` event was written:

   ```sql
   SELECT TOP 5 EventId, EventType, Visibility, DedupKey, CreatedAt
     FROM BusinessEvents WHERE EventType = 'JournalEntry.Reversed' ORDER BY EventId DESC;
   ```

   Expect `Visibility = 'Confidential'` and `DedupKey = 'JournalEntry.Reversed:<id>'`.

**If this fails with SQL error 208 (invalid object name), stop and restore.** It means the kernel schema is not
where the code expects it.

### 5.5 The outbox drains

Open **Administration → System setup → Business event monitor**.

- The runtime banner must show a worker role. `PRIMARY` = this process is dispatching. `STANDBY` = another process
  is (fine — check that one). A red `UNENFORCED` banner means the worker lease could not be evaluated; read the
  warning text.
- Filter `DispatchStatus = Pending`. After one dispatcher poll interval (15 s by default) the count must fall.
- Filter `DispatchStatus = Failed`. Investigate anything there; the error text is on the row.

### 5.6 Email outbox

Only if SMTP is configured. Send one email from the Comm screen and confirm the row reaches `Sent`. The dispatcher
**skips its whole pass** when SMTP is not configured, precisely so it does not burn `Attempts` against a
misconfiguration — so "nothing happened" on an unconfigured host is correct behaviour, not a failure.

### 5.7 Schema history is consistent

```powershell
powershell -NoProfile -File CrossBuy/deploy/report-schema-history.ps1 `
    -ConnectionString "Server=.;Database=CrossBuyDB2;Trusted_Connection=True;TrustServerCertificate=True"
```

Exit code **0** = everything recorded as applied and unchanged. **1** = something pending/changed/missing.
**2** = a failed apply is recorded — resolve that before anything else. **3** = the command could not run.

---

## 6. Rollback

**There is no "down script", by design.** Both approved scripts are purely additive: they add nullable columns and
indexes. The rollback posture follows from that.

| Situation | Action |
|---|---|
A script failed part-way | Re-run it. It is idempotent, so it completes the remaining steps. Record the failure and then the success. |
The new columns cause a problem | The old code ignores them (they are nullable and additive). Roll back the **application**, leave the schema. |
Something is genuinely wrong with the schema | Restore the verified backup from §2.6. This is the only true rollback. |
An index is causing a performance problem | Drop that index only: `DROP INDEX IX_Notifications_Entity ON dbo.Notifications;`. The scripts recreate it on the next run, so also decide whether to keep re-running them. |

**Never** hand-drop `Notifications.EntityType`/`EntityId` or `CommMessages.ClaimedAt` on a database whose
application code expects them: `NotificationProjectionConsumer` and `SqlCommMessageDispatchStore` both read them,
and the failure surfaces as SQL 207 inside the dispatch worker.

---

## 7. Verification already performed

So the reviewer knows what has and has not been tested, and where.

**On disposable SQL Server databases** (created and dropped per run; the fixture **refuses** a connection string
naming `CrossBuyDB`, `CrossBuyDB2` or `CrossBuy`):

| Test | What it proves |
|---|---|
`PlatformSchemaDeploymentTests.Slice1_is_idempotent_and_creates_every_object_the_kernel_depends_on` | slice 1 applied **three times**; every table, PK, FK, CHECK and index present exactly once, with the right uniqueness and filters |
`…The_claiming_index_is_filtered_so_finished_work_leaves_it` | `IX_BusinessEventDispatch_Pending` really excludes `Done` |
`…The_payload_json_constraint_is_present_and_enforced_on_a_supported_engine` | `ISJSON` constraint rejects non-JSON, accepts NULL and real JSON, and is correctly skipped below major version 13 |
`…The_numeric_floor_constraints_are_enforced_not_decorative` | `PayloadVersion >= 1` and `Attempts >= 0` actually reject |
`…The_dedup_index_is_unique_per_company_and_ignores_null_keys` | idempotency is per tenant; NULL keys coexist |
`…Slice2_is_idempotent_and_makes_notifications_entity_addressable` | slice 2 applied **twice**; both columns nullable with the right types, both indexes non-unique and filtered; kernel tables untouched |
`…Slice2_performs_no_backfill` | a legacy notification row keeps NULL entity columns and its original `(Type, RefId)` |
`…Slice3_comm_outbox_is_idempotent_and_adds_only_what_it_claims` | `ClaimedAt` added once, `UpdatedAt` **not** duplicated, filtered index and both CHECKs present |
`…The_platform_scripts_create_no_table_outside_the_kernel_and_its_outbox` | no journal/stock/invoice table is created — the two-writers rule is not breached by the DDL |
`platform_schema_history.sql` (scripted verification) | applied twice; table + view + 3 indexes + 2 CHECKs; hash constraint rejects a short hash **and** upper-case hex; a failure with no reason is rejected; applied/pending/changed/failed/missing all detected; append-only confirmed; the report refuses a connection string with no database |

**Not performed, and it must be stated:** nothing was run against CrossBuyDB2. Its live state was **read** to
establish the table above, and read only.

---

## 8. Post-deployment

1. Re-run `report-schema-history.ps1` and keep the output with the deployment record.
2. Re-run `scan-architecture.ps1` if any code shipped alongside, so the evidence CSVs match what is deployed.
3. Watch the Business Event Monitor for one full dispatch interval. `Failed` should be 0; anything there is real.
4. Confirm the startup log line for this instance:

   ```
   CrossBuy runtime starting — instance=… pid=… requireSingleWorkerProcess=True lease=CrossBuy.BackgroundWorkers
   ```

   and then either `lease acquired … PRIMARY` or a standby line. A red
   `SINGLE-WORKER ENFORCEMENT IS NOT ACTIVE` means the lease could not be taken — see
   [Single-Worker-Process.md](Single-Worker-Process.md).