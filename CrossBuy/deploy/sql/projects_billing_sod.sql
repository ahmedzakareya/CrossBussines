-- =============================================================================================
-- Project Billing — Batch 1: separation of duties + lifecycle actor evidence.
--
-- ADDITIVE and IDEMPOTENT. Safe to run any number of times. NO data modification, NO backfill,
-- NO GL or stock impact, NO change to any other table.
--
-- WHAT IT ADDS
--   Six nullable columns on dbo.ProgressBillings that record WHO moved the billing and WHEN:
--   UpdatedBy/UpdatedAt, SubmittedBy/SubmittedAt, ApprovedBy/ApprovedAt. CreatedBy/CreatedAt and
--   PostedBy/PostedAt already exist and are unchanged.
--
--   ApprovedBy is not audit decoration. The separation-of-duties rule is ApprovedBy != CreatedBy,
--   so the approver's identity is BUSINESS STATE the workflow decides on, not a trail written
--   beside it — which is why it belongs on the row rather than in a generic audit log.
--
-- =============================================================================================
-- DEPLOYMENT COMPATIBILITY — READ BEFORE CHANGING THE ORDER
-- =============================================================================================
--
-- APPLY THIS SCRIPT BEFORE DEPLOYING THE APPLICATION ("SQL before code", the repository's order).
--
-- WHY THAT ORDER IS REQUIRED HERE, and it is about the CHECK constraint rather than the columns:
--
--   * THE COLUMNS are safe in either order. They are NULLABLE with no default, so the OLD
--     application — which never writes them — keeps working against the new schema, and every row
--     written before this batch simply has NULLs. The new application treats NULL as "this never
--     happened", which for a billing created before the batch is exactly true.
--
--   * THE CHECK CONSTRAINT is not. CK_ProgressBillings_Status admits all FIVE lifecycle states,
--     including the two this batch introduces (Submitted, Returned). Applied FIRST, both
--     applications are legal: the old one only ever writes Draft/Approved/Posted, which is a
--     subset, and the new one can write all five. Applied AFTER the application, there is a window
--     in which the new application writes 'Submitted' into a database that still forbids it, and
--     every submit in that window fails.
--
--     The reverse hazard is the one worth naming explicitly, because it is the trap: a CHECK
--     listing only the three OLD states must NEVER be authored. It would deploy cleanly, pass
--     every test against old data, and then reject the first submit.
--
-- ROLLBACK: dropping the constraint restores the previous behaviour exactly; the columns may be
-- left in place, since a nullable unused column is inert to the old application.
--
-- NOTE ON DUPLICATE POSTING: this batch's duplicate-post protection is the ATOMIC TRANSACTION and
-- the UPDLOCK row lock in ProgressBillingService.PostAsync, not a uniqueness constraint. A unique
-- index on SalesInvoiceId was considered and rejected — it would only catch a duplicate AFTER the
-- second invoice had already been written to the ledger, which is precisely the outcome the
-- transaction prevents. See the analysis note in the batch report.
-- =============================================================================================
SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------------------------------------
-- 1) Lifecycle actor columns
-- ---------------------------------------------------------------------------------------------
IF COL_LENGTH('dbo.ProgressBillings', 'UpdatedBy') IS NULL
    ALTER TABLE dbo.ProgressBillings ADD UpdatedBy INT NULL;
GO
IF COL_LENGTH('dbo.ProgressBillings', 'UpdatedAt') IS NULL
    ALTER TABLE dbo.ProgressBillings ADD UpdatedAt DATETIME2 NULL;
GO
IF COL_LENGTH('dbo.ProgressBillings', 'SubmittedBy') IS NULL
    ALTER TABLE dbo.ProgressBillings ADD SubmittedBy INT NULL;
GO
IF COL_LENGTH('dbo.ProgressBillings', 'SubmittedAt') IS NULL
    ALTER TABLE dbo.ProgressBillings ADD SubmittedAt DATETIME2 NULL;
GO
IF COL_LENGTH('dbo.ProgressBillings', 'ApprovedBy') IS NULL
    ALTER TABLE dbo.ProgressBillings ADD ApprovedBy INT NULL;
GO
IF COL_LENGTH('dbo.ProgressBillings', 'ApprovedAt') IS NULL
    ALTER TABLE dbo.ProgressBillings ADD ApprovedAt DATETIME2 NULL;
GO

-- ---------------------------------------------------------------------------------------------
-- 2) The lifecycle states the column may hold.
--
-- WITH NOCHECK is deliberate: existing rows are already Draft, Approved or Posted, so they satisfy
-- this list anyway — but NOCHECK keeps the statement from scanning a large table during a
-- deployment window, and the constraint still governs every subsequent write.
-- ---------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ProgressBillings_Status')
    ALTER TABLE dbo.ProgressBillings WITH NOCHECK
        ADD CONSTRAINT CK_ProgressBillings_Status
        CHECK (Status IN (N'Draft', N'Submitted', N'Returned', N'Approved', N'Posted'));
GO

-- ---------------------------------------------------------------------------------------------
-- 3) Verification
-- ---------------------------------------------------------------------------------------------
SELECT
    UpdatedBy     = CASE WHEN COL_LENGTH('dbo.ProgressBillings','UpdatedBy')   IS NULL THEN 0 ELSE 1 END,
    UpdatedAt     = CASE WHEN COL_LENGTH('dbo.ProgressBillings','UpdatedAt')   IS NULL THEN 0 ELSE 1 END,
    SubmittedBy   = CASE WHEN COL_LENGTH('dbo.ProgressBillings','SubmittedBy') IS NULL THEN 0 ELSE 1 END,
    SubmittedAt   = CASE WHEN COL_LENGTH('dbo.ProgressBillings','SubmittedAt') IS NULL THEN 0 ELSE 1 END,
    ApprovedBy    = CASE WHEN COL_LENGTH('dbo.ProgressBillings','ApprovedBy')  IS NULL THEN 0 ELSE 1 END,
    ApprovedAt    = CASE WHEN COL_LENGTH('dbo.ProgressBillings','ApprovedAt')  IS NULL THEN 0 ELSE 1 END,
    StatusCheck   = (SELECT COUNT(*) FROM sys.check_constraints
                     WHERE name = N'CK_ProgressBillings_Status'),
    IllegalStatus = (SELECT COUNT(*) FROM dbo.ProgressBillings
                     WHERE Status NOT IN (N'Draft', N'Submitted', N'Returned', N'Approved', N'Posted'));
GO
