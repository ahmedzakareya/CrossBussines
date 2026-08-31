-- =============================================================================================
-- Project Billing Batch 3 — project closeout evidence.
--
-- ONE TABLE, and deliberately not four columns on dbo.Projects.
--
-- A project can be closed, reopened because a late invoice arrived, and closed again. Columns on
-- Projects record only the last of those, so the second close overwrites the evidence of the first.
-- A closeout is a decision somebody took, and decisions accumulate.
--
-- It also leaves dbo.Projects untouched. That table is read by nearly every screen in the module,
-- and adding columns to it means every one of those queries selects a column that does not exist
-- until this script has run. A new table cannot break a query that does not know about it.
--
-- Projects.Status still moves to 'Completed' on close - that column already exists - but the
-- evidence lives here.
--
-- ADDITIVE AND IDEMPOTENT. Every statement is guarded; running it twice changes nothing.
-- NOT EXECUTED by the batch that authored it.
-- =============================================================================================

SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.ProjectCloseouts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProjectCloseouts
    (
        ID              int             IDENTITY(1,1) NOT NULL,

        -- The isolation key. NOT NULL and no default: a closeout that does not know its company is
        -- not a closeout, and a default would quietly make it company 1's.
        CompanyID       int             NOT NULL,
        ProjectId       int             NOT NULL,

        ClosedAt        datetime2(3)    NOT NULL,
        ClosedBy        int             NOT NULL,

        -- Mandatory at the database as well as in the service. A closeout with no stated grounds is
        -- an audit trail that records only that somebody had the right to do it.
        Reason          nvarchar(1000)  NOT NULL,

        -- What the readiness check saw when this closed, so a later reader can tell a clean close
        -- from one that accepted warnings.
        ReadinessNote   nvarchar(1000)  NULL,

        ReopenedAt      datetime2(3)    NULL,
        ReopenedBy      int             NULL,
        ReopenReason    nvarchar(1000)  NULL,

        IsCurrent       bit             NOT NULL CONSTRAINT DF_ProjectCloseouts_IsCurrent DEFAULT (1),

        CONSTRAINT PK_ProjectCloseouts PRIMARY KEY CLUSTERED (ID)
    );
END
GO

-- A reason that is whitespace is not a reason. Enforced here as well as in the service, because the
-- table can be written by a migration or a script that never goes through the service.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ProjectCloseouts_ReasonNotEmpty')
    ALTER TABLE dbo.ProjectCloseouts
        ADD CONSTRAINT CK_ProjectCloseouts_ReasonNotEmpty
        CHECK (LEN(LTRIM(RTRIM(Reason))) > 0);
GO

-- A reopen is all three facts or none of them: a row claiming it was reopened without saying when,
-- by whom, or why is worse than one that does not claim it.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ProjectCloseouts_ReopenComplete')
    ALTER TABLE dbo.ProjectCloseouts
        ADD CONSTRAINT CK_ProjectCloseouts_ReopenComplete
        CHECK (
            (ReopenedAt IS NULL AND ReopenedBy IS NULL AND ReopenReason IS NULL)
            OR
            (ReopenedAt IS NOT NULL AND ReopenedBy IS NOT NULL
             AND ReopenReason IS NOT NULL AND LEN(LTRIM(RTRIM(ReopenReason))) > 0)
        );
GO

-- A reopened closeout is history and must not still be current. The service sets IsCurrent = 0 when
-- it reopens; this makes the invariant true of the DATA rather than of the code that writes it.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ProjectCloseouts_ReopenedNotCurrent')
    ALTER TABLE dbo.ProjectCloseouts
        ADD CONSTRAINT CK_ProjectCloseouts_ReopenedNotCurrent
        CHECK (ReopenedAt IS NULL OR IsCurrent = 0);
GO

-- AT MOST ONE CURRENT CLOSEOUT PER PROJECT.
--
-- This is the concurrency control, not merely an index. Two administrators closing the same project
-- in the same instant both pass the readiness check; the service re-checks inside its transaction,
-- and this constraint is what makes that check binding rather than advisory. Filtered, so the
-- historical rows a project accumulates do not collide.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_ProjectCloseouts_CurrentPerProject'
               AND object_id = OBJECT_ID(N'dbo.ProjectCloseouts'))
    CREATE UNIQUE INDEX UX_ProjectCloseouts_CurrentPerProject
        ON dbo.ProjectCloseouts (CompanyID, ProjectId)
        WHERE IsCurrent = 1;
GO

-- The history read: every closeout for one project, newest first.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProjectCloseouts_ProjectHistory'
               AND object_id = OBJECT_ID(N'dbo.ProjectCloseouts'))
    CREATE INDEX IX_ProjectCloseouts_ProjectHistory
        ON dbo.ProjectCloseouts (CompanyID, ProjectId, ClosedAt DESC)
        INCLUDE (ClosedBy, IsCurrent);
GO
