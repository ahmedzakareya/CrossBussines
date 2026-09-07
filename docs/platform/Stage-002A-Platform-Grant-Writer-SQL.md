# Stage 2A — Platform Grant Writer — SQL

`CrossBuy/deploy/sql/platform_role_assignments_slice_002.sql`. Additive · idempotent · no backfill · no column
removed or retyped · no existing index dropped · no data modified · no GL/stock impact.

Deploy with **`sqlcmd -I`** (QUOTED_IDENTIFIER ON) so the filtered index builds. SQL before code.

---

## 1. What it adds

| Object | Type | Why |
|---|---|---|
`RevokedAt` | `datetime2(7)` NULL | slice 1 has `IsActive`, which records *that* a grant is off but not when |
`RevokedBy` | `int` NULL | …or by whom. An audit line that cannot answer the only question ever asked of it |
`Reason` | `nvarchar(400)` NULL | why the grant was created, revoked or re-dated |
`SourceSystem` | `nvarchar(40)` NULL | which writer produced the row (A16 foundation) |
`MigrationBatchId` | `uniqueidentifier` NULL | groups one migration run's rows. **Nothing writes it in Batch A** |
`IdempotencyKey` | `nvarchar(120)` NULL | the caller's request id |
`UX_PlatformRoleAssignments_Idempotency` | unique filtered index | idempotency as a **database** guarantee |
`CK_PlatformRoleAssignments_Revocation` | CHECK | revoked ⇒ inactive |

All columns **nullable**: slice-1 rows predate them, and inventing values for those rows would be fabricated history.

## 2. Why the active-grant unique index is NOT created here

It already exists, from slice 1, with exactly the key this batch requires:

```sql
UX_PlatformRoleAssignments_ActiveGrant
  ON (CompanyID, Scope, PrincipalType, PrincipalId, Role, ScopeBranchId) WHERE IsActive = 1
```

Creating a second index with the same key would cost writes and prove nothing. The slice **verifies** it instead and
`THROW`s if it is absent (50001), if its key has drifted (50002), or if it is not unique-and-filtered (50003).

A missing duplicate-prevention index is a **security property missing**, not a performance detail — so it fails the
deployment rather than being discovered later by a duplicate active grant. The filter is what makes revoke-then-regrant
possible without deleting the audit row; the uniqueness is what stops two live copies of the same grant.

## 3. Why the idempotency index is per-company and filtered

**Per company:** an idempotency key is a caller's request id. Two companies' administrators must not be able to collide
— worse, a cross-company key collision would return **another company's grant** as "the original result", which is a
cross-tenant disclosure.

**Filtered to non-null:** the key is optional, and SQL Server treats every `NULL` as equal in a unique index.
Unfiltered, exactly **one keyless grant per company** would be permitted in total.

## 4. Verified twice on a disposable database

Both slices, both passes, on `CrossBuyProbe_SliceCheck_*` — never against `CrossBuyDB2`:

| | slice 1 | slice 2 |
|---|---|---|
**Pass 1** | table ✓, 5 CHECKs, 6 indexes | all 6 columns ✓, both indexes ✓, 6 CHECKs |
**Pass 2** | table ✓, 6 CHECKs, 7 indexes | identical — **nothing re-created** |

Pass 2's higher counts are slice 2's own additions from pass 1, still present. Re-running added nothing, changed
nothing and threw nothing. Probe dropped; `sys.databases` count 0.

## 5. Rollback

Every object is new and nullable, so rollback loses no pre-existing data — but it **does** destroy the audit history
the writer produced, which is why it is documented rather than scripted:

```sql
DROP INDEX UX_PlatformRoleAssignments_Idempotency ON dbo.PlatformRoleAssignments;
ALTER TABLE dbo.PlatformRoleAssignments DROP CONSTRAINT CK_PlatformRoleAssignments_Revocation;
ALTER TABLE dbo.PlatformRoleAssignments
    DROP COLUMN RevokedAt, RevokedBy, SourceSystem, MigrationBatchId, IdempotencyKey, Reason;
```

**Order matters when reversing: code first, then schema.** Rolling back the schema while the code still runs leaves
the writer unable to revoke, which fails loudly (SQL 207) rather than silently skipping a revocation.

## 6. EF model

`CrossDbContext` gained the three string lengths (mirroring the DDL exactly — a model that disagreed would truncate on
one path and not the other) and the idempotency index with the same filter. The EF model is what
`CreateProbeDatabaseAsync` materialises, so the acceptance tests run against the same shape; the test asserts all six
columns are present before any grant is created, so a missing column surfaces as a schema failure rather than as an
unrelated grant defect.

## 7. Not done

* **No migration.** `MigrationBatchId` exists and stays null.
* **No legacy role table touched.**
* **No `rowversion`.** Concurrency uses the locked read; see the implementation plan F8 for the proposal and trigger.
