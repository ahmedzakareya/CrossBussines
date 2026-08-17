-- =============================================================================================
-- Stage 0 Batch B — PlatformSchemaHistory: a record of which deployment script has actually been
-- applied to THIS database.
--
-- Additive + idempotent. Creates one table and its indexes. Touches no existing table, no GL, no stock.
--
-- WHY THIS EXISTS
--
-- The deployment discipline is "idempotent SQL under deploy/sql, applied before the code, no EF
-- migrations". That is deliberate and it works — but it leaves one question unanswerable: given a
-- database, WHICH scripts have run against it? Today the only way to find out is to inspect the schema
-- object by object and infer. That inference has already been wrong in practice: CrossBuyDB2 turned out
-- to carry slice 1 (applied externally) but not slice 2 or slice 3, and nothing in the database said so.
--
-- This table answers it directly, without becoming a migration framework:
--
--   * It is APPEND-ONLY. Applying an idempotent script twice is legal and produces two rows. The
--     current state of a script is its LATEST row, never a mutated one — so the history of a bad
--     deploy is still there to read.
--   * It records the script's SHA-256. That is what turns "applied" into a checkable claim: if the file
--     in git no longer hashes to what was applied, the script CHANGED after deployment and the database
--     is running older DDL than the repository describes. A name-only log cannot detect that.
--   * It records FAILURES too (Success = 0 + Error). A half-applied script is the most dangerous state
--     a deploy can be in, and it must not be invisible.
--   * It is NOT a gate. Nothing in the application reads this table, and no script refuses to run
--     because of it. It is a reporting surface for an operator, which is why an out-of-band manual
--     apply can be reconciled afterwards with the -Baseline / -Record flags of
--     CrossBuy/deploy/report-schema-history.ps1 instead of leaving the log permanently wrong.
--
-- WHAT IT DELIBERATELY DOES NOT DO
--
--   * No trigger, no constraint, and no procedure enforces ordering. Ordering lives in
--     deploy/sql/manifest.json (the `rank` field) and in the runbook; encoding it here would make this
--     table a migration engine, which is exactly the thing the project decided not to have.
--   * No backfill. A database that already has objects from earlier scripts is reconciled by an
--     EXPLICIT baseline step — see the procedure at the foot of this file — never by guessing from the
--     schema. Guessing is how a log starts lying.
-- =============================================================================================

IF OBJECT_ID('dbo.PlatformSchemaHistory', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PlatformSchemaHistory (
        ID                 INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PlatformSchemaHistory PRIMARY KEY,
        ScriptName         NVARCHAR(260)  NOT NULL,   -- repo-relative path, e.g. CrossBuy/deploy/sql/platform_business_events.sql
        ScriptHash         CHAR(64)       NOT NULL,   -- SHA-256 of the file bytes, lower-case hex
        AppliedAt          DATETIME2      NOT NULL CONSTRAINT DF_PlatformSchemaHistory_AppliedAt DEFAULT SYSUTCDATETIME(),
        AppliedBy          NVARCHAR(200)  NOT NULL,   -- who/what applied it (login, or 'baseline')
        ApplicationVersion NVARCHAR(60)   NULL,       -- app/assembly version at apply time, when known
        Success            BIT            NOT NULL CONSTRAINT DF_PlatformSchemaHistory_Success DEFAULT 1,
        Error              NVARCHAR(2000) NULL        -- first 2000 chars of the failure, NULL on success
    );
    PRINT 'ADDED dbo.PlatformSchemaHistory';
END
ELSE PRINT 'dbo.PlatformSchemaHistory EXISTS';
GO

-- The reporting query is always "latest row for this script name", so that is what the index serves.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PlatformSchemaHistory_Script' AND object_id = OBJECT_ID('dbo.PlatformSchemaHistory'))
BEGIN
    CREATE INDEX IX_PlatformSchemaHistory_Script
        ON dbo.PlatformSchemaHistory (ScriptName, AppliedAt DESC)
        INCLUDE (ScriptHash, Success);
    PRINT 'ADDED IX_PlatformSchemaHistory_Script';
END
ELSE PRINT 'IX_PlatformSchemaHistory_Script EXISTS';
GO

-- Finding every failed apply must be cheap and must not scan the whole log. Filtered, so a healthy
-- database keeps this index empty.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PlatformSchemaHistory_Failed' AND object_id = OBJECT_ID('dbo.PlatformSchemaHistory'))
BEGIN
    CREATE INDEX IX_PlatformSchemaHistory_Failed
        ON dbo.PlatformSchemaHistory (AppliedAt DESC)
        INCLUDE (ScriptName, Error)
        WHERE Success = 0;
    PRINT 'ADDED IX_PlatformSchemaHistory_Failed';
END
ELSE PRINT 'IX_PlatformSchemaHistory_Failed EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PlatformSchemaHistory_Hash')
BEGIN
    -- A 64-character LOWER-CASE hex digest. A truncated or upper-cased hash would silently never match
    -- the manifest, and the report would then call an applied script "changed" forever.
    --
    -- The BIN2 collation is load-bearing, not decoration: CrossBuy's databases use a case-INSENSITIVE
    -- collation, under which '%[^0-9a-f]%' happily accepts 'ABC…' and the case rule would not exist.
    -- Forcing a binary collation on the comparison is what makes "lower-case" an actual guarantee.
    ALTER TABLE dbo.PlatformSchemaHistory ADD CONSTRAINT CK_PlatformSchemaHistory_Hash
        CHECK (LEN(ScriptHash) = 64
               AND ScriptHash COLLATE Latin1_General_BIN2 NOT LIKE '%[^0-9a-f]%' COLLATE Latin1_General_BIN2);
    PRINT 'ADDED CK_PlatformSchemaHistory_Hash';
END
ELSE PRINT 'CK_PlatformSchemaHistory_Hash EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PlatformSchemaHistory_Error')
BEGIN
    -- A failure with no reason recorded is not a usable record, and a success carrying an error message
    -- is a contradiction. Both are rejected rather than stored.
    ALTER TABLE dbo.PlatformSchemaHistory ADD CONSTRAINT CK_PlatformSchemaHistory_Error
        CHECK ((Success = 1 AND Error IS NULL) OR (Success = 0 AND Error IS NOT NULL));
    PRINT 'ADDED CK_PlatformSchemaHistory_Error';
END
ELSE PRINT 'CK_PlatformSchemaHistory_Error EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- Current state, one row per script: the latest apply attempt.
--
-- A view rather than a table so it can never drift from the log, and so the log stays append-only.
--
-- CREATE OR ALTER (SQL Server 2016 SP1+) rather than DROP-then-CREATE: a DROP followed by a CREATE in
-- the next batch is not re-runnable as one unit — if the CREATE batch fails, the previous run's view is
-- already gone and the database is left worse than before the script ran. The same engine floor already
-- applies to the kernel's ISJSON constraint, so this adds no new requirement.
-- ---------------------------------------------------------------------------------------------
CREATE OR ALTER VIEW dbo.vw_PlatformSchemaCurrent
AS
SELECT h.ScriptName, h.ScriptHash, h.AppliedAt, h.AppliedBy, h.ApplicationVersion, h.Success, h.Error,
       h.ID AS HistoryId,
       (SELECT COUNT(*) FROM dbo.PlatformSchemaHistory a WHERE a.ScriptName = h.ScriptName) AS ApplyCount
  FROM dbo.PlatformSchemaHistory h
 WHERE h.ID = (SELECT MAX(b.ID) FROM dbo.PlatformSchemaHistory b WHERE b.ScriptName = h.ScriptName);
GO

-- =============================================================================================
-- BASELINE PROCEDURE — reconciling a database that was built before this table existed.
--
-- CrossBuyDB2 is exactly this case: scripts have been applied to it manually over months, and there is
-- no record of which. Do NOT try to infer the answer from the schema, and do NOT mark everything
-- applied to make the report look green. Both produce a log that lies.
--
-- The procedure is:
--
--   1. Apply THIS script (it only adds the table, the view and their indexes).
--
--   2. Decide, per script, whether it is genuinely already applied. The only acceptable evidence is a
--      direct check of the objects that script creates — e.g. for slice 1:
--          SELECT OBJECT_ID('BusinessEvents'), OBJECT_ID('BusinessEventDispatch');
--      For anything you cannot establish, leave it unrecorded. "Pending" for a script that is in fact
--      applied is harmless — the scripts are idempotent, so re-running it is a no-op. "Applied" for a
--      script that is not is the dangerous direction, because the report then hides real work.
--
--   3. Record ONLY what step 2 established, with AppliedBy = 'baseline':
--          powershell -File CrossBuy/deploy/report-schema-history.ps1 `
--              -ConnectionString "<conn>" `
--              -Baseline "CrossBuy/deploy/sql/platform_business_events.sql"
--      The -Baseline flag exists precisely so a baseline row is DISTINGUISHABLE from a real apply: it
--      records the hash the file has NOW and stamps AppliedBy = 'baseline', so a later reader can see
--      the row was asserted rather than observed.
--
--   4. Run the report and read what is left:
--          powershell -File CrossBuy/deploy/report-schema-history.ps1 -ConnectionString "<conn>"
--      Everything still Pending is now an explicit to-do list, not an unknown.
--
-- From that point on, every apply is recorded at the time it happens, and 'changed' becomes meaningful:
-- it means the file in git was edited after it was applied here.
-- =============================================================================================
