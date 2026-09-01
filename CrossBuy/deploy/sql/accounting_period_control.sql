-- =============================================================================================
-- Accounting period control — close / soft-close / reopen evidence and history.
--
-- ADDITIVE and IDEMPOTENT. Safe to run any number of times. NO data modification, NO backfill,
-- NO change to any existing column, NO GL or stock impact.
--
-- WHAT IT ADDS
--   dbo.FiscalPeriods           ClosedBy, ClosedAt, ReopenedBy, ReopenedAt, ReopenReason
--   dbo.AccountingPeriodAudits  one row per governed transition
--
-- WHY THE EVIDENCE IS ON THE PERIOD *AND* IN A TABLE
--
--   The five columns answer "what is true now" — the state a screen renders and a guard reads. The
--   audit table answers "what happened" — a period that was closed, reopened, corrected and closed
--   again has a story, and a financial control is only worth having if that story survives. Keeping
--   only the latest values would silently overwrite the previous close every time.
--
-- NO BACKFILL, DELIBERATELY. Existing periods get NULLs. A NULL ClosedBy means "this period was
-- closed before the control existed, and nobody knows by whom" — which is the truth. Writing a
-- plausible actor into those rows would manufacture audit evidence, which is worse than admitting
-- the gap.
--
-- =============================================================================================
-- DEPLOYMENT COMPATIBILITY
-- =============================================================================================
--
-- APPLY BEFORE THE APPLICATION ("SQL before code").
--
--   * THE COLUMNS are safe in either order: nullable, no default, and the old application never
--     writes them.
--
--   * THE TABLE must exist before the new application runs, because every governed transition
--     writes a row to it inside the same transaction as the status change. Applied after, the first
--     close would fail — and fail atomically, which is correct but avoidable.
--
--   * NO CHECK CONSTRAINT ON FiscalPeriods.Status IS ADDED. There is none today, and the three
--     values (Open / SoftClosed / Closed) already exist in live rows. Adding one that listed only
--     the values this batch happens to use would deploy cleanly, pass every test against existing
--     data, and then reject whatever a later batch adds. The application owns that vocabulary
--     (AccountingPeriodStatuses) and is the single place it is defined.
--
-- ROLLBACK: dropping the table and leaving the columns restores previous behaviour exactly; nullable
-- unused columns are inert to the old application.
-- =============================================================================================
SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------------------------------------
-- 1) Close / reopen evidence on the period
-- ---------------------------------------------------------------------------------------------
IF COL_LENGTH('dbo.FiscalPeriods', 'ClosedBy') IS NULL
    ALTER TABLE dbo.FiscalPeriods ADD ClosedBy INT NULL;
GO
IF COL_LENGTH('dbo.FiscalPeriods', 'ClosedAt') IS NULL
    ALTER TABLE dbo.FiscalPeriods ADD ClosedAt DATETIME2 NULL;
GO
IF COL_LENGTH('dbo.FiscalPeriods', 'ReopenedBy') IS NULL
    ALTER TABLE dbo.FiscalPeriods ADD ReopenedBy INT NULL;
GO
IF COL_LENGTH('dbo.FiscalPeriods', 'ReopenedAt') IS NULL
    ALTER TABLE dbo.FiscalPeriods ADD ReopenedAt DATETIME2 NULL;
GO
IF COL_LENGTH('dbo.FiscalPeriods', 'ReopenReason') IS NULL
    ALTER TABLE dbo.FiscalPeriods ADD ReopenReason NVARCHAR(500) NULL;
GO

-- ---------------------------------------------------------------------------------------------
-- 2) The transition history
--
-- CompanyID is denormalised from the period's fiscal year on purpose: every read of this table is
-- "this company's history for this period", and joining FiscalPeriods -> FiscalYears on the read
-- path to learn the company would make the company predicate depend on the table being read. The
-- control service resolves the company from the fiscal year before it writes the row.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.AccountingPeriodAudits', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AccountingPeriodAudits
    (
        ID               int            IDENTITY(1,1) NOT NULL,
        CompanyID        int            NOT NULL,
        FiscalPeriodId   int            NOT NULL,

        -- Both ends of the move are recorded. "Closed" alone does not say whether it came from Open
        -- or from SoftClosed, and at audit time that difference is the whole question.
        FromStatus       nvarchar(20)   NOT NULL,
        ToStatus         nvarchar(20)   NOT NULL,

        ActorEmployeeId  int            NOT NULL,
        OccurredAt       datetime2(7)   NOT NULL CONSTRAINT DF_AccountingPeriodAudits_At DEFAULT (SYSUTCDATETIME()),

        -- The reopen reason, or the warning codes a close deliberately overrode. Always populated
        -- when something stood in the way of the transition.
        Reason           nvarchar(500)  NULL,

        CONSTRAINT PK_AccountingPeriodAudits PRIMARY KEY CLUSTERED (ID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_AccountingPeriodAudits_Keys')
    ALTER TABLE dbo.AccountingPeriodAudits WITH CHECK
        ADD CONSTRAINT CK_AccountingPeriodAudits_Keys
        CHECK (CompanyID > 0 AND FiscalPeriodId > 0 AND ActorEmployeeId > 0);
GO

-- A transition must not record itself as going nowhere.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_AccountingPeriodAudits_Moved')
    ALTER TABLE dbo.AccountingPeriodAudits WITH CHECK
        ADD CONSTRAINT CK_AccountingPeriodAudits_Moved
        CHECK (FromStatus <> ToStatus);
GO

-- NO CASCADE: deleting a period must never silently delete the record of who sealed it. This
-- project reverses rather than deletes by convention, and audit rows outlive their subject.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_AccountingPeriodAudits_Period')
    ALTER TABLE dbo.AccountingPeriodAudits WITH CHECK
        ADD CONSTRAINT FK_AccountingPeriodAudits_Period FOREIGN KEY (FiscalPeriodId)
        REFERENCES dbo.FiscalPeriods (ID) ON DELETE NO ACTION ON UPDATE NO ACTION;
GO

-- "What happened to this period?" — newest first, which is how the screen and the report read it.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AccountingPeriodAudits_ByPeriod'
               AND object_id = OBJECT_ID(N'dbo.AccountingPeriodAudits'))
    CREATE INDEX IX_AccountingPeriodAudits_ByPeriod
        ON dbo.AccountingPeriodAudits (CompanyID, FiscalPeriodId, OccurredAt DESC)
        INCLUDE (FromStatus, ToStatus, ActorEmployeeId, Reason);
GO

-- ---------------------------------------------------------------------------------------------
-- 3) Verification
-- ---------------------------------------------------------------------------------------------
SELECT
    ClosedBy      = CASE WHEN COL_LENGTH('dbo.FiscalPeriods','ClosedBy')     IS NULL THEN 0 ELSE 1 END,
    ClosedAt      = CASE WHEN COL_LENGTH('dbo.FiscalPeriods','ClosedAt')     IS NULL THEN 0 ELSE 1 END,
    ReopenedBy    = CASE WHEN COL_LENGTH('dbo.FiscalPeriods','ReopenedBy')   IS NULL THEN 0 ELSE 1 END,
    ReopenedAt    = CASE WHEN COL_LENGTH('dbo.FiscalPeriods','ReopenedAt')   IS NULL THEN 0 ELSE 1 END,
    ReopenReason  = CASE WHEN COL_LENGTH('dbo.FiscalPeriods','ReopenReason') IS NULL THEN 0 ELSE 1 END,
    AuditTable    = CASE WHEN OBJECT_ID(N'dbo.AccountingPeriodAudits', N'U') IS NULL THEN 0 ELSE 1 END,
    CheckCount    = (SELECT COUNT(*) FROM sys.check_constraints
                     WHERE parent_object_id = OBJECT_ID(N'dbo.AccountingPeriodAudits')),
    ForeignKeys   = (SELECT COUNT(*) FROM sys.foreign_keys
                     WHERE parent_object_id = OBJECT_ID(N'dbo.AccountingPeriodAudits')),
    Indexes       = (SELECT COUNT(*) FROM sys.indexes
                     WHERE object_id = OBJECT_ID(N'dbo.AccountingPeriodAudits') AND index_id > 0),
    -- Must be 0: an audit row whose period belongs to another company would mean the denormalised
    -- CompanyID and the period's own fiscal year had drifted apart.
    Mismatched    = (SELECT COUNT(*) FROM dbo.AccountingPeriodAudits a
                     JOIN dbo.FiscalPeriods p ON p.ID = a.FiscalPeriodId
                     JOIN dbo.FiscalYears  y ON y.ID = p.FiscalYearId
                     WHERE y.CompanyID <> a.CompanyID);
GO
