-- =============================================================================================
-- Central Document Platform — slice documents_002_submission.
--
-- AUTHORED, NOT EXECUTED. No DDL ran anywhere for this batch: no production write, and no change to
-- CrossBuyDev. Applying it is a separate, explicit act.
--
-- WHY IT EXISTS. Batch 2 adds two things the schema has to carry:
--
--   1. PlatformDocumentTypes.SelfServiceAllowed
--      Whether the SUBJECT of a record may file this type about themselves. Configuration rather
--      than code, so the words "Passport" and "Civil ID" never appear in a service. Default 0: a
--      type nobody has deliberately opened is HR-only, because that is the safe reading of silence.
--
--   2. 'Submitted' joins the document status vocabulary.
--      A document the subject filed has been RECEIVED, not accepted. Keeping Submitted out of Active
--      is what stops "the employee uploaded something" being mistaken for "HR verified it" — and
--      FindValidDocumentAsync counts only Active, so an onboarding checklist cannot tick itself.
--
-- ADDITIVE + IDEMPOTENT. Safe to run any number of times:
--   * adds ONE nullable-with-default column and WIDENS one CHECK constraint;
--   * NO existing row is read, rewritten or migrated - the new column defaults to 0 for every
--     existing type, which is the restrictive value, so nothing becomes self-servable by upgrade;
--   * NO legacy document store is touched;
--   * NO index is dropped and no column is removed.
--
-- THE CHECK CONSTRAINT IS REPLACED, NOT LOOSENED BY DELETION. It is dropped and recreated in the
-- same guarded block so the table is never left without it: a window with no constraint is a window
-- in which a bad status can be written and then outlive the fix.
--
-- RUN IT WITH sqlcmd -I (QUOTED_IDENTIFIER ON), for consistency with the other slices in this tree.
-- Requires documents_001 / comm_documents_001 to have been applied first.
-- =============================================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

-- ---- 1. self-service policy on the type catalogue ---------------------------------------------
IF OBJECT_ID('dbo.PlatformDocumentTypes') IS NULL
BEGIN
    RAISERROR ('documents_002 REFUSED: PlatformDocumentTypes does not exist. Apply the Central Document Platform base slice first; this slice alters, it does not create.', 16, 1);
END
GO

IF COL_LENGTH('dbo.PlatformDocumentTypes', 'SelfServiceAllowed') IS NULL
BEGIN
    -- NOT NULL with a restrictive default: every type that exists today stays HR-only, and opening one
    -- is a deliberate row update rather than a side effect of deploying.
    ALTER TABLE dbo.PlatformDocumentTypes
        ADD SelfServiceAllowed bit NOT NULL
            CONSTRAINT DF_PlatformDocumentTypes_SelfService DEFAULT (0);
    PRINT 'PlatformDocumentTypes.SelfServiceAllowed added (default 0 - HR-only until opened).';
END
ELSE PRINT 'PlatformDocumentTypes.SelfServiceAllowed already present.';
GO

-- ---- 2. 'Submitted' joins the status vocabulary -----------------------------------------------
IF OBJECT_ID('dbo.PlatformDocuments') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE name = 'CK_PlatformDocuments_Status'
                 AND parent_object_id = OBJECT_ID('dbo.PlatformDocuments'))
   AND NOT EXISTS (SELECT 1 FROM sys.check_constraints
                   WHERE name = 'CK_PlatformDocuments_Status'
                     AND parent_object_id = OBJECT_ID('dbo.PlatformDocuments')
                     AND definition LIKE '%Submitted%')
BEGIN
    BEGIN TRANSACTION;
        ALTER TABLE dbo.PlatformDocuments DROP CONSTRAINT CK_PlatformDocuments_Status;
        ALTER TABLE dbo.PlatformDocuments WITH CHECK
            ADD CONSTRAINT CK_PlatformDocuments_Status CHECK
                (Status IN ('Draft','Submitted','Active','Expired','Archived'));
    COMMIT TRANSACTION;
    PRINT 'CK_PlatformDocuments_Status widened to include Submitted.';
END
ELSE PRINT 'CK_PlatformDocuments_Status already accepts Submitted (or the table is absent).';
GO

-- ---- 3. what this slice deliberately does NOT do ----------------------------------------------
-- No seeded document types. Which types a deployment has, and which of them a person may file about
-- themselves, is a business decision; shipping a "Passport" row here would make the platform opinionated
-- about an HR catalogue it does not own.
--
-- No verification/approval columns. Submission is separated from acceptance by the status alone in this
-- batch. Who verifies, and through which workflow, reuses the existing approval platform rather than
-- growing a second one inside documents - that convergence is a later, explicit decision.
PRINT 'documents_002_submission complete. Structure only: no type seeded, no document altered, no legacy row touched.';
GO
