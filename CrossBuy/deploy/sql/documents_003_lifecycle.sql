-- =============================================================================================
-- Central Document Platform — slice documents_003_lifecycle.
--
-- AUTHORED, NOT EXECUTED. No DDL ran for this batch: no production write, no CrossBuyDev change.
--
-- WHAT IT ADDS, and why each column earns its place:
--
--   DecidedBy / DecidedAt / DecisionNote
--     WHO accepted or refused a submitted document, WHEN, and WHY. On the row rather than
--     reconstructed from an audit trail, because "is this verified" is a question the validity rule
--     answers on every read - an onboarding checklist render must not depend on an event replay.
--
--   'Rejected' joins the status CHECK
--     A refused submission must not vanish. The person who filed it needs to see that it was looked
--     at and why, and their correction is then a new VERSION of the same document rather than a
--     fresh row nobody can relate to the first.
--
-- ADDITIVE + IDEMPOTENT. Safe to run any number of times:
--   * three NULLable columns, guarded by COL_LENGTH checks;
--   * the status CHECK is dropped and recreated in ONE transaction, so the table is never briefly
--     unconstrained - a window with no constraint is a window in which a bad status can be written
--     and then outlive the fix;
--   * NO existing row is read, rewritten or migrated. Every existing document keeps its status and
--     gets NULL decision evidence, which is the truthful value: nobody decided on it under this
--     model;
--   * NO legacy document store is touched.
--
-- WHY THERE IS NO VERIFICATION *TABLE*. A verification here is not an approval request. The
-- approval platform models a request that TRAVELS - submitted, routed to a step, acted on, and the
-- outcome then drives the original transaction. A document verification has no route and no steps:
-- one person with employee-manage looks at a file and says yes or no, and the outcome is a property
-- of the document. Giving it its own request row would create a second object whose lifecycle has to
-- stay in step with the document's, and the first bug would be a verified approval attached to a
-- document that had since been resubmitted. If a deployment later wants multi-step document
-- approval, the approval platform can drive THIS transition rather than duplicate it.
--
-- RUN IT WITH sqlcmd -I (QUOTED_IDENTIFIER ON), for consistency with the other slices in this tree.
-- Requires the Central Document Platform base slice to have been applied first.
-- =============================================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID('dbo.PlatformDocuments') IS NULL
BEGIN
    RAISERROR ('documents_003 REFUSED: PlatformDocuments does not exist. Apply the Central Document Platform base slice first; this slice alters, it does not create.', 16, 1);
END
GO

-- ---- 1. decision evidence ----------------------------------------------------------------------
IF COL_LENGTH('dbo.PlatformDocuments', 'DecidedBy') IS NULL
BEGIN
    ALTER TABLE dbo.PlatformDocuments ADD DecidedBy int NULL;
    PRINT 'PlatformDocuments.DecidedBy added.';
END
ELSE PRINT 'PlatformDocuments.DecidedBy already present.';
GO

IF COL_LENGTH('dbo.PlatformDocuments', 'DecidedAt') IS NULL
BEGIN
    ALTER TABLE dbo.PlatformDocuments ADD DecidedAt datetime2 NULL;
    PRINT 'PlatformDocuments.DecidedAt added.';
END
ELSE PRINT 'PlatformDocuments.DecidedAt already present.';
GO

IF COL_LENGTH('dbo.PlatformDocuments', 'DecisionNote') IS NULL
BEGIN
    ALTER TABLE dbo.PlatformDocuments ADD DecisionNote nvarchar(400) NULL;
    PRINT 'PlatformDocuments.DecisionNote added.';
END
ELSE PRINT 'PlatformDocuments.DecisionNote already present.';
GO

-- ---- 2. 'Rejected' joins the status vocabulary -------------------------------------------------
IF OBJECT_ID('dbo.PlatformDocuments') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE name = 'CK_PlatformDocuments_Status'
                 AND parent_object_id = OBJECT_ID('dbo.PlatformDocuments'))
   AND NOT EXISTS (SELECT 1 FROM sys.check_constraints
                   WHERE name = 'CK_PlatformDocuments_Status'
                     AND parent_object_id = OBJECT_ID('dbo.PlatformDocuments')
                     AND definition LIKE '%Rejected%')
BEGIN
    BEGIN TRANSACTION;
        ALTER TABLE dbo.PlatformDocuments DROP CONSTRAINT CK_PlatformDocuments_Status;
        ALTER TABLE dbo.PlatformDocuments WITH CHECK
            ADD CONSTRAINT CK_PlatformDocuments_Status CHECK
                (Status IN ('Draft','Submitted','Rejected','Active','Expired','Archived'));
    COMMIT TRANSACTION;
    PRINT 'CK_PlatformDocuments_Status widened to include Rejected.';
END
ELSE PRINT 'CK_PlatformDocuments_Status already accepts Rejected (or the table is absent).';
GO

-- ---- 3. the pending queue ----------------------------------------------------------------------
IF OBJECT_ID('dbo.PlatformDocuments') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PlatformDocuments_Pending')
BEGIN
    -- "What is waiting for me to verify" is the one query the HR side will run constantly, and it is
    -- a tiny slice of the table. Filtered, so it stays a seek and does not grow with history.
    CREATE INDEX IX_PlatformDocuments_Pending
        ON dbo.PlatformDocuments (CompanyID, Status, EntityType, EntityId)
        WHERE Status IN ('Submitted','Rejected');
    PRINT 'IX_PlatformDocuments_Pending created.';
END
GO

PRINT 'documents_003_lifecycle complete. Structure only: no document altered, no type seeded, no legacy row touched.';
GO
