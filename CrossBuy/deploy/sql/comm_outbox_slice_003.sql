-- =============================================================================================
-- Stage 0 (Slice-003) — CommMessage email outbox dispatch state.
-- Additive + idempotent. Safe to run any number of times. NO GL/stock impact, NO data modification.
--
-- WHY: CommMessages was already outbox-shaped (Status / Attempts / Error / SentAt) but nothing drained it.
-- CommService.SendAsync queued a row and then sent it SYNCHRONOUSLY inside the request; a Failed row stayed
-- Failed forever, and a row queued while SMTP was unconfigured was never retried. A dispatcher needs two things
-- the table did not have: a claim timestamp (which is also the stale-claim clock) and a general UpdatedAt.
--
-- The claim itself reuses the pattern already proven by BusinessEventDispatch (ADR-003/ADR-007):
--   UPDATE TOP (n) ... OUTPUT inserted.* FROM CommMessages WITH (ROWLOCK, READPAST, UPDLOCK)
-- so two workers can never send the same email, and neither blocks the other.
--
-- Status vocabulary after this script: Queued | Claimed | Sent | Failed.
-- "Claimed" is NEW. The CHECK constraint is written to accept every value already present in the table.
-- =============================================================================================

-- ---------------------------------------------------------------------------------------------
-- 1. Dispatch state columns (nullable — every existing row stays valid).
-- ---------------------------------------------------------------------------------------------
IF COL_LENGTH('dbo.CommMessages', 'ClaimedAt') IS NULL
BEGIN
    ALTER TABLE dbo.CommMessages ADD ClaimedAt DATETIME2 NULL;
    PRINT 'ADDED CommMessages.ClaimedAt';
END
ELSE PRINT 'CommMessages.ClaimedAt EXISTS';
GO

-- UpdatedAt is INHERITED from BaseEntity, so it is already a column on this table in any existing database.
-- The guard is kept because the dispatcher depends on the column existing, and a fresh/rebuilt database must get
-- it either way. Expect 'EXISTS' on a real deployment.
IF COL_LENGTH('dbo.CommMessages', 'UpdatedAt') IS NULL
BEGIN
    ALTER TABLE dbo.CommMessages ADD UpdatedAt DATETIME2 NULL;
    PRINT 'ADDED CommMessages.UpdatedAt';
END
ELSE PRINT 'CommMessages.UpdatedAt EXISTS (inherited from BaseEntity)';
GO

-- ---------------------------------------------------------------------------------------------
-- 2. The claiming index. Filtered so it only covers rows that can still be worked: once a message is
--    Sent it leaves the index entirely, so the queue index stays small no matter how much mail is sent.
--    Column order matches the claim predicate: Status first, then the two clocks it compares.
-- ---------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommMessages_Dispatch' AND object_id = OBJECT_ID('dbo.CommMessages'))
BEGIN
    CREATE INDEX IX_CommMessages_Dispatch
        ON dbo.CommMessages (Status, Attempts, ClaimedAt, UpdatedAt)
        INCLUDE (CompanyID)
        WHERE Status <> 'Sent';
    PRINT 'ADDED IX_CommMessages_Dispatch';
END
ELSE PRINT 'IX_CommMessages_Dispatch EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 3. Status vocabulary constraint.
--    Guarded twice: the constraint is only created if it does not exist AND if no existing row would
--    violate it. A pre-existing row with an unexpected status must NOT block deployment — it is reported
--    instead, so the operator can decide.
-- ---------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommMessages_Status')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.CommMessages WHERE Status NOT IN ('Queued','Claimed','Sent','Failed'))
    BEGIN
        PRINT 'SKIPPED CK_CommMessages_Status — existing rows hold a status outside (Queued|Claimed|Sent|Failed).';
        PRINT 'Review with:  SELECT DISTINCT Status FROM dbo.CommMessages;';
    END
    ELSE
    BEGIN
        ALTER TABLE dbo.CommMessages ADD CONSTRAINT CK_CommMessages_Status
            CHECK (Status IN ('Queued','Claimed','Sent','Failed'));
        PRINT 'ADDED CK_CommMessages_Status';
    END
END
ELSE PRINT 'CK_CommMessages_Status EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommMessages_Attempts')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.CommMessages WHERE Attempts < 0)
        PRINT 'SKIPPED CK_CommMessages_Attempts — existing rows hold a negative Attempts value.';
    ELSE
    BEGIN
        ALTER TABLE dbo.CommMessages ADD CONSTRAINT CK_CommMessages_Attempts CHECK (Attempts >= 0);
        PRINT 'ADDED CK_CommMessages_Attempts';
    END
END
ELSE PRINT 'CK_CommMessages_Attempts EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- NOT DONE HERE, deliberately:
--   * No backfill of ClaimedAt/UpdatedAt for historical rows. They are read only by the dispatcher, and a
--     NULL clock is treated as "eligible immediately", which is the correct behaviour for a row that was
--     queued before the dispatcher existed.
--   * No status rewriting. A row stuck at Queued from before this slice becomes eligible the moment the
--     dispatcher starts — which is the intended fix, not a migration.
-- ---------------------------------------------------------------------------------------------
