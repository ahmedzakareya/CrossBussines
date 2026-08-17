-- =============================================================================================
-- Stage 2A Batch A — PlatformRoleAssignments slice 2: the audit and idempotency columns the
-- production Grant Writer needs.
--
-- Additive + idempotent. Safe to re-run any number of times. NO data modification, NO backfill,
-- NO column removed, NO column retyped, NO existing index dropped or replaced, NO GL/stock impact.
-- Creates six nullable columns and one filtered unique index.
--
-- Requires QUOTED_IDENTIFIER ON for the filtered index — deploy with sqlcmd -I, as the other
-- filtered-index slices do.
--
-- ROLLBACK
--   Every object added here is new and nullable, so rollback is a drop and loses no pre-existing
--   data. The columns carry audit history the writer produced, so dropping them destroys THAT
--   history — which is why rollback is documented rather than scripted:
--
--     DROP INDEX UX_PlatformRoleAssignments_Idempotency ON dbo.PlatformRoleAssignments;
--     ALTER TABLE dbo.PlatformRoleAssignments
--         DROP CONSTRAINT CK_PlatformRoleAssignments_Revocation;
--     ALTER TABLE dbo.PlatformRoleAssignments
--         DROP COLUMN RevokedAt, RevokedBy, SourceSystem, MigrationBatchId, IdempotencyKey, Reason;
--
--   Rolling back the SCHEMA without rolling back the CODE leaves the writer unable to revoke, which
--   fails loudly (SQL 207) rather than silently skipping the revocation. That is the intended order:
--   code first, then schema, when reversing.
--
-- WHY THE ACTIVE-GRANT UNIQUE INDEX IS NOT CREATED HERE
--
--   It already exists, from slice 1, with exactly the key this batch requires:
--       (CompanyID, Scope, PrincipalType, PrincipalId, Role, ScopeBranchId) WHERE IsActive = 1
--   Creating a second index with the same key would be a duplicate that costs writes and proves
--   nothing. This slice VERIFIES it instead, and FAILS if it is absent or its definition has
--   drifted — a missing duplicate-prevention index is a security property missing, not a
--   performance detail, so it must not be discovered later by a duplicate row.
-- =============================================================================================
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.PlatformRoleAssignments', N'U') IS NULL
    THROW 50000, N'dbo.PlatformRoleAssignments does not exist. Apply platform_role_assignments.sql (slice 1) first — SQL before code, slices in order.', 1;
GO

-- ---------------------------------------------------------------------------------------------
-- 1. Revocation trail.
--
-- Slice 1 has IsActive, which records THAT a grant is off but not when, by whom, or why. A
-- revocation with no actor is an audit line that cannot answer the only question ever asked of it.
-- All nullable: existing rows were never revoked, and inventing a value for them would be fabricated
-- history.
-- ---------------------------------------------------------------------------------------------
IF COL_LENGTH('dbo.PlatformRoleAssignments', 'RevokedAt') IS NULL
    ALTER TABLE dbo.PlatformRoleAssignments ADD RevokedAt datetime2(7) NULL;
GO

IF COL_LENGTH('dbo.PlatformRoleAssignments', 'RevokedBy') IS NULL
    ALTER TABLE dbo.PlatformRoleAssignments ADD RevokedBy int NULL;
GO

-- Why the change was made. Required by the writer for revocation and for a past-effective validity
-- change; nullable because slice-1 rows have no reason and never will.
IF COL_LENGTH('dbo.PlatformRoleAssignments', 'Reason') IS NULL
    ALTER TABLE dbo.PlatformRoleAssignments ADD Reason nvarchar(400) NULL;
GO

-- ---------------------------------------------------------------------------------------------
-- 2. Coexistence metadata (Batch A16 foundations — additive and DORMANT).
--
-- SourceSystem records which writer produced a row, so that when the legacy Accounting/Inventory/CRM
-- role tables are eventually folded in, a migrated row is distinguishable from one an administrator
-- created. Without it the fold-in becomes irreversible the moment it runs, because nothing marks what
-- came from where.
--
-- MigrationBatchId groups the rows one migration run produced. Nothing writes it in Batch A — no
-- migration occurs — but the writer stamps SourceSystem, so the pair stays consistent.
-- ---------------------------------------------------------------------------------------------
IF COL_LENGTH('dbo.PlatformRoleAssignments', 'SourceSystem') IS NULL
    ALTER TABLE dbo.PlatformRoleAssignments ADD SourceSystem nvarchar(40) NULL;
GO

IF COL_LENGTH('dbo.PlatformRoleAssignments', 'MigrationBatchId') IS NULL
    ALTER TABLE dbo.PlatformRoleAssignments ADD MigrationBatchId uniqueidentifier NULL;
GO

-- ---------------------------------------------------------------------------------------------
-- 3. Idempotency.
--
-- A retried create must return the ORIGINAL grant, not a second one. Application-level checking is
-- not sufficient: two concurrent retries both read "not found" and both insert. The uniqueness has to
-- be a database constraint, exactly as the active-grant rule is.
-- ---------------------------------------------------------------------------------------------
IF COL_LENGTH('dbo.PlatformRoleAssignments', 'IdempotencyKey') IS NULL
    ALTER TABLE dbo.PlatformRoleAssignments ADD IdempotencyKey nvarchar(120) NULL;
GO

-- Scoped PER COMPANY: an idempotency key is a caller's request id, and two companies' administrators
-- must not be able to collide with each other — worse, a key collision across companies would return
-- ANOTHER company's grant as the "original result", which is a cross-tenant disclosure.
--
-- FILTERED to non-null keys, because the key is optional. An unfiltered unique index would treat every
-- NULL as equal in SQL Server and permit exactly one keyless grant per company in total.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PlatformRoleAssignments_Idempotency'
               AND object_id = OBJECT_ID(N'dbo.PlatformRoleAssignments'))
    CREATE UNIQUE INDEX UX_PlatformRoleAssignments_Idempotency
        ON dbo.PlatformRoleAssignments (CompanyID, IdempotencyKey)
        WHERE IdempotencyKey IS NOT NULL;
GO

-- ---------------------------------------------------------------------------------------------
-- 4. Revocation coherence.
--
-- A row cannot be active AND revoked. Without this, a bug that sets RevokedAt but forgets IsActive
-- leaves a grant that reads as live to the directory while the audit trail says it was revoked — the
-- authorization outcome and the audit record would disagree, and the audit record is the one nobody
-- checks until afterwards.
--
-- NOT CHECK-validated against existing rows: no existing row has RevokedAt, so WITH CHECK is safe,
-- but it is written WITH CHECK explicitly rather than relying on that being true.
-- ---------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PlatformRoleAssignments_Revocation')
    ALTER TABLE dbo.PlatformRoleAssignments WITH CHECK
        ADD CONSTRAINT CK_PlatformRoleAssignments_Revocation
        CHECK (
            -- revoked ⇒ inactive, and carries a timestamp
            (RevokedAt IS NULL AND RevokedBy IS NULL)
            OR (RevokedAt IS NOT NULL AND IsActive = 0)
        );
GO

-- ---------------------------------------------------------------------------------------------
-- 5. VERIFY the slice-1 duplicate-prevention index. Not created here — asserted.
--
-- If it is missing or its key has drifted, the writer's duplicate rule would rest on application
-- logic alone, which two concurrent requests defeat. That is a security property, so its absence
-- fails the deployment loudly instead of being noticed later by a duplicate active grant.
-- ---------------------------------------------------------------------------------------------
DECLARE @activeGrantKey nvarchar(400);

SELECT @activeGrantKey = STUFF((
    SELECT ',' + c.name
    FROM sys.index_columns ic
    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE ic.object_id = OBJECT_ID(N'dbo.PlatformRoleAssignments')
      AND ic.index_id = (SELECT index_id FROM sys.indexes
                         WHERE object_id = OBJECT_ID(N'dbo.PlatformRoleAssignments')
                           AND name = N'UX_PlatformRoleAssignments_ActiveGrant')
      AND ic.is_included_column = 0
    ORDER BY ic.key_ordinal
    FOR XML PATH('')), 1, 1, '');

IF @activeGrantKey IS NULL
    THROW 50001, N'UX_PlatformRoleAssignments_ActiveGrant is MISSING. Duplicate active grants would be prevented only by application logic, which two concurrent requests defeat. Re-apply platform_role_assignments.sql (slice 1).', 1;

IF @activeGrantKey <> N'CompanyID,Scope,PrincipalType,PrincipalId,Role,ScopeBranchId'
    THROW 50002, N'UX_PlatformRoleAssignments_ActiveGrant key has DRIFTED from the effective grant identity. The duplicate-prevention guarantee no longer matches what the writer enforces.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.PlatformRoleAssignments')
                 AND name = N'UX_PlatformRoleAssignments_ActiveGrant'
                 AND is_unique = 1 AND has_filter = 1)
    THROW 50003, N'UX_PlatformRoleAssignments_ActiveGrant is not a UNIQUE FILTERED index. Revoke-then-regrant requires the filter; duplicate prevention requires the uniqueness.', 1;
GO

-- =============================================================================================
-- Verification. Printed so a deploy log shows what happened rather than silence.
-- =============================================================================================
SELECT
    RevokedAt        = CASE WHEN COL_LENGTH('dbo.PlatformRoleAssignments','RevokedAt')        IS NULL THEN 0 ELSE 1 END,
    RevokedBy        = CASE WHEN COL_LENGTH('dbo.PlatformRoleAssignments','RevokedBy')        IS NULL THEN 0 ELSE 1 END,
    Reason           = CASE WHEN COL_LENGTH('dbo.PlatformRoleAssignments','Reason')           IS NULL THEN 0 ELSE 1 END,
    SourceSystem     = CASE WHEN COL_LENGTH('dbo.PlatformRoleAssignments','SourceSystem')     IS NULL THEN 0 ELSE 1 END,
    MigrationBatchId = CASE WHEN COL_LENGTH('dbo.PlatformRoleAssignments','MigrationBatchId') IS NULL THEN 0 ELSE 1 END,
    IdempotencyKey   = CASE WHEN COL_LENGTH('dbo.PlatformRoleAssignments','IdempotencyKey')   IS NULL THEN 0 ELSE 1 END,
    ActiveGrantIndex = (SELECT COUNT(*) FROM sys.indexes
                        WHERE object_id = OBJECT_ID(N'dbo.PlatformRoleAssignments')
                          AND name = N'UX_PlatformRoleAssignments_ActiveGrant'),
    IdempotencyIndex = (SELECT COUNT(*) FROM sys.indexes
                        WHERE object_id = OBJECT_ID(N'dbo.PlatformRoleAssignments')
                          AND name = N'UX_PlatformRoleAssignments_Idempotency'),
    CheckConstraints = (SELECT COUNT(*) FROM sys.check_constraints
                        WHERE parent_object_id = OBJECT_ID(N'dbo.PlatformRoleAssignments'));
GO
