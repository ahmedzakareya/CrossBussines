-- =============================================================================================
-- CrossBuy — schema drift repair: period close/reopen evidence + progress-billing approval stamps
-- (session 2026-09)
--
-- WHY THIS EXISTS. Migrations are disabled in this product: schema ships as idempotent deploy/sql.
-- Two features had their ENTITIES extended without a script to match, so EF generated SELECTs for
-- columns the database does not have and two screens returned HTTP 500:
--
--   /Accounting/Periods  -> Invalid column name 'ClosedAt' / 'ClosedBy' / 'ReopenedAt' /
--                           'ReopenedBy' / 'ReopenReason'   (FiscalPeriods)
--                           and dbo.AccountingPeriodAudits did not exist at all
--   /Approvals/Index     -> Invalid column name 'SubmittedAt'  (ProgressBillings, plus six more
--                           stamps the entity declares and the table lacks)
--
-- Every statement is additive and idempotent. Nothing is dropped, renamed or backfilled: these are
-- all NULL-able evidence columns whose absence of a value means "this never happened", which is the
-- correct reading for every row that already exists.
--
-- Column types and lengths are taken from the EF model configuration in CrossDbContext so the two
-- cannot disagree: FromStatus/ToStatus NVARCHAR(20) NOT NULL, Reason and ReopenReason NVARCHAR(500).
--
-- Run:  sqlcmd -S <server> -d <database> -E -C -i period_close_and_billing_approval_schema_2026-09.sql
-- Pure DDL — plain UTF-8 is fine, no BOM required.
-- =============================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- ---------------------------------------------------------------------------------------------
-- 1) FiscalPeriods: who closed or reopened the period, when, and why.
--
--    A period's state is a financial control, so the actor and the instant are part of the record.
--    Reopen carries a reason and closing does not, which is deliberate: closing is routine and
--    explains itself, re-admitting posting to a closed period never does.
-- ---------------------------------------------------------------------------------------------
IF COL_LENGTH('dbo.FiscalPeriods','ClosedBy') IS NULL
BEGIN
    ALTER TABLE dbo.FiscalPeriods ADD ClosedBy INT NULL;
    PRINT 'ADDED FiscalPeriods.ClosedBy';
END ELSE PRINT 'FiscalPeriods.ClosedBy EXISTS';
GO
IF COL_LENGTH('dbo.FiscalPeriods','ClosedAt') IS NULL
BEGIN
    ALTER TABLE dbo.FiscalPeriods ADD ClosedAt DATETIME2 NULL;
    PRINT 'ADDED FiscalPeriods.ClosedAt';
END ELSE PRINT 'FiscalPeriods.ClosedAt EXISTS';
GO
IF COL_LENGTH('dbo.FiscalPeriods','ReopenedBy') IS NULL
BEGIN
    ALTER TABLE dbo.FiscalPeriods ADD ReopenedBy INT NULL;
    PRINT 'ADDED FiscalPeriods.ReopenedBy';
END ELSE PRINT 'FiscalPeriods.ReopenedBy EXISTS';
GO
IF COL_LENGTH('dbo.FiscalPeriods','ReopenedAt') IS NULL
BEGIN
    ALTER TABLE dbo.FiscalPeriods ADD ReopenedAt DATETIME2 NULL;
    PRINT 'ADDED FiscalPeriods.ReopenedAt';
END ELSE PRINT 'FiscalPeriods.ReopenedAt EXISTS';
GO
IF COL_LENGTH('dbo.FiscalPeriods','ReopenReason') IS NULL
BEGIN
    ALTER TABLE dbo.FiscalPeriods ADD ReopenReason NVARCHAR(500) NULL;
    PRINT 'ADDED FiscalPeriods.ReopenReason';
END ELSE PRINT 'FiscalPeriods.ReopenReason EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 2) AccountingPeriodAudits: the transition log behind the columns above.
--
--    NOT NULL on the five facts that make a transition meaningful - a row that does not say who
--    moved what, from where to where and when is not evidence of anything. Reason stays optional
--    because only a reopen requires one.
--
--    The index name is the one the EF mapping declares (IX_AccountingPeriodAudits_ByPeriod), so a
--    model that is ever allowed to create this table produces the same object as this script.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.AccountingPeriodAudits','U') IS NULL
BEGIN
    CREATE TABLE dbo.AccountingPeriodAudits(
        ID               INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AccountingPeriodAudits PRIMARY KEY,
        CompanyID        INT NOT NULL,
        FiscalPeriodId   INT NOT NULL,
        FromStatus       NVARCHAR(20) NOT NULL,
        ToStatus         NVARCHAR(20) NOT NULL,
        ActorEmployeeId  INT NOT NULL,
        OccurredAt       DATETIME2 NOT NULL,
        Reason           NVARCHAR(500) NULL
    );
    CREATE INDEX IX_AccountingPeriodAudits_ByPeriod
        ON dbo.AccountingPeriodAudits(CompanyID, FiscalPeriodId, OccurredAt);
    PRINT 'CREATED AccountingPeriodAudits';
END ELSE PRINT 'AccountingPeriodAudits EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 3) ProgressBillings: the approval stamps.
--
--    The table already carries CreatedAt/CreatedBy and PostedAt. The entity declares four more
--    pairs for the Draft -> Submitted -> Approved -> Posted lifecycle, and PostedBy to go with the
--    PostedAt that is already there. All NULL-able: a draft has not been submitted, and NULL says so.
-- ---------------------------------------------------------------------------------------------
IF COL_LENGTH('dbo.ProgressBillings','UpdatedAt') IS NULL
BEGIN
    ALTER TABLE dbo.ProgressBillings ADD UpdatedAt DATETIME2 NULL;
    PRINT 'ADDED ProgressBillings.UpdatedAt';
END ELSE PRINT 'ProgressBillings.UpdatedAt EXISTS';
GO
IF COL_LENGTH('dbo.ProgressBillings','UpdatedBy') IS NULL
BEGIN
    ALTER TABLE dbo.ProgressBillings ADD UpdatedBy INT NULL;
    PRINT 'ADDED ProgressBillings.UpdatedBy';
END ELSE PRINT 'ProgressBillings.UpdatedBy EXISTS';
GO
IF COL_LENGTH('dbo.ProgressBillings','SubmittedAt') IS NULL
BEGIN
    ALTER TABLE dbo.ProgressBillings ADD SubmittedAt DATETIME2 NULL;
    PRINT 'ADDED ProgressBillings.SubmittedAt';
END ELSE PRINT 'ProgressBillings.SubmittedAt EXISTS';
GO
IF COL_LENGTH('dbo.ProgressBillings','SubmittedBy') IS NULL
BEGIN
    ALTER TABLE dbo.ProgressBillings ADD SubmittedBy INT NULL;
    PRINT 'ADDED ProgressBillings.SubmittedBy';
END ELSE PRINT 'ProgressBillings.SubmittedBy EXISTS';
GO
IF COL_LENGTH('dbo.ProgressBillings','ApprovedAt') IS NULL
BEGIN
    ALTER TABLE dbo.ProgressBillings ADD ApprovedAt DATETIME2 NULL;
    PRINT 'ADDED ProgressBillings.ApprovedAt';
END ELSE PRINT 'ProgressBillings.ApprovedAt EXISTS';
GO
IF COL_LENGTH('dbo.ProgressBillings','ApprovedBy') IS NULL
BEGIN
    ALTER TABLE dbo.ProgressBillings ADD ApprovedBy INT NULL;
    PRINT 'ADDED ProgressBillings.ApprovedBy';
END ELSE PRINT 'ProgressBillings.ApprovedBy EXISTS';
GO
IF COL_LENGTH('dbo.ProgressBillings','PostedBy') IS NULL
BEGIN
    ALTER TABLE dbo.ProgressBillings ADD PostedBy INT NULL;
    PRINT 'ADDED ProgressBillings.PostedBy';
END ELSE PRINT 'ProgressBillings.PostedBy EXISTS';
GO

PRINT 'cb_period_close_and_billing_approval done';
GO
