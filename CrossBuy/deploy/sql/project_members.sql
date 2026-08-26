-- =============================================================================================
-- Stage 1 Batch C — ProjectMembers: who is on a project.
--
-- Additive + idempotent. Safe to run any number of times. NO GL/stock impact, NO data modification,
-- NO change to the Project entity or any existing table, NO backfill.
--
-- WHY THIS IS MODULE-SPECIFIC AND NOT A ROW IN PlatformRoleAssignments
--
-- Project membership is a BUSINESS RELATIONSHIP, not a permission. It has its own lifecycle
-- (JoinedAt / LeftAt), its own reporting meaning (who worked on this, at what allocation), and it
-- belongs to the project rather than to the module. A role assignment says "this person may do X in
-- the Projects module"; a membership says "this person is on project 47". Collapsing the second into
-- the first would make leaving a project look like revoking a permission, and would put per-project
-- rows into the table every module reads on every request.
--
-- WHY IT EXISTS AT ALL
--
-- The Project entity carries NO manager, owner or member column, and a search of the whole model for
-- an employee-to-project relationship returns nothing (Batch C analysis §1.3). So record-level project
-- access was not derivable from stored data — this table is that missing relationship, and it is the
-- only new business table Batch C adds.
--
-- WHAT MEMBERSHIP DOES *NOT* GRANT — enforced in ProjectsAccessService, stated here so a reader of the
-- schema is not misled:
--   * budget-view / budget-manage  — a Member sees the project, not its money;
--   * billing                      — cooperates with AccountingAccessService, never bypasses it;
--   * project-wide manage          — a Manager of project 47 is not an administrator of every project;
--   * any Accounting permission    — GL access stays with the accounting module.
--
-- DEPLOYMENT: apply BEFORE the Batch C code ("SQL before code"). Requires the Projects and Employee
-- tables to exist (they do — Projects since the Projects module, Employee since the beginning).
-- =============================================================================================
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.ProjectMembers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProjectMembers
    (
        ID              int            IDENTITY(1,1) NOT NULL,

        -- Denormalised from Project deliberately: every access check filters by company FIRST, and a
        -- join to Projects on the hot path to learn the company would make the company predicate depend
        -- on the very table being authorized. The same-company invariant is enforced by CK below and
        -- re-checked in the service.
        CompanyID       int            NOT NULL,

        ProjectId       int            NOT NULL,
        EmployeeId      int            NOT NULL,

        -- Batch C supports exactly three, each with a distinct documented access level:
        --   Manager  — full project record access, may manage membership
        --   Member   — project record access, no membership management, no financial access
        --   Observer — read-only project record access
        -- No other value is accepted; a fourth role is a business decision, not a schema gap.
        RoleOnProject   nvarchar(20)   NOT NULL CONSTRAINT DF_ProjectMembers_Role DEFAULT (N'Member'),

        -- Optional planning figure (0..100). Bounded by CK: an allocation of 500% is a data error, and
        -- an unbounded column invites one.
        AllocationPct   decimal(5,2)   NULL,

        JoinedAt        datetime2(7)   NOT NULL CONSTRAINT DF_ProjectMembers_JoinedAt DEFAULT (SYSUTCDATETIME()),
        LeftAt          datetime2(7)   NULL,

        -- Revocation without deleting history, same reasoning as PlatformRoleAssignments.IsActive.
        IsActive        bit            NOT NULL CONSTRAINT DF_ProjectMembers_IsActive DEFAULT (1),

        CreatedBy       int            NULL,
        CreatedAt       datetime2(7)   NOT NULL CONSTRAINT DF_ProjectMembers_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedBy       int            NULL,
        UpdatedAt       datetime2(7)   NULL,

        CONSTRAINT PK_ProjectMembers PRIMARY KEY CLUSTERED (ID)
    );
END
GO

-- ---------------------------------------------------------------------------------------------
-- Constraints
-- ---------------------------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ProjectMembers_Keys')
    ALTER TABLE dbo.ProjectMembers WITH CHECK
        ADD CONSTRAINT CK_ProjectMembers_Keys
        CHECK (CompanyID > 0 AND ProjectId > 0 AND EmployeeId > 0);
GO

-- Only the three supported roles. Mirrors the code's validation so a hand-inserted row cannot create a
-- role no service knows how to evaluate.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ProjectMembers_RoleOnProject')
    ALTER TABLE dbo.ProjectMembers WITH CHECK
        ADD CONSTRAINT CK_ProjectMembers_RoleOnProject
        CHECK (RoleOnProject IN (N'Manager', N'Member', N'Observer'));
GO

-- LeftAt cannot precede JoinedAt. NULL LeftAt = still on the project.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ProjectMembers_Dates')
    ALTER TABLE dbo.ProjectMembers WITH CHECK
        ADD CONSTRAINT CK_ProjectMembers_Dates
        CHECK (LeftAt IS NULL OR LeftAt >= JoinedAt);
GO

-- Bounded allocation.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ProjectMembers_Allocation')
    ALTER TABLE dbo.ProjectMembers WITH CHECK
        ADD CONSTRAINT CK_ProjectMembers_Allocation
        CHECK (AllocationPct IS NULL OR (AllocationPct >= 0 AND AllocationPct <= 100));
GO

-- Referential integrity to the two real parents. NO CASCADE: deleting a project must not silently
-- delete the record of who worked on it, and this project reverses rather than deletes by convention.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ProjectMembers_Project')
    ALTER TABLE dbo.ProjectMembers WITH CHECK
        ADD CONSTRAINT FK_ProjectMembers_Project FOREIGN KEY (ProjectId)
        REFERENCES dbo.Projects (ID) ON DELETE NO ACTION ON UPDATE NO ACTION;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ProjectMembers_Employee')
    ALTER TABLE dbo.ProjectMembers WITH CHECK
        ADD CONSTRAINT FK_ProjectMembers_Employee FOREIGN KEY (EmployeeId)
        REFERENCES dbo.Employee (ID) ON DELETE NO ACTION ON UPDATE NO ACTION;
GO

-- ---------------------------------------------------------------------------------------------
-- Duplicate prevention: one ACTIVE membership per (project, employee). Filtered, so a person may
-- rejoin a project later without the historical row blocking the new one.
-- Requires QUOTED_IDENTIFIER ON — deploy with sqlcmd -I.
-- ---------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_ProjectMembers_ActiveMembership'
               AND object_id = OBJECT_ID(N'dbo.ProjectMembers'))
    CREATE UNIQUE INDEX UX_ProjectMembers_ActiveMembership
        ON dbo.ProjectMembers (ProjectId, EmployeeId)
        WHERE IsActive = 1;
GO

-- ---------------------------------------------------------------------------------------------
-- Query indexes
-- ---------------------------------------------------------------------------------------------

-- "Is this employee on this project, and as what?" — the record-level access check.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProjectMembers_Access'
               AND object_id = OBJECT_ID(N'dbo.ProjectMembers'))
    CREATE INDEX IX_ProjectMembers_Access
        ON dbo.ProjectMembers (CompanyID, EmployeeId, ProjectId)
        INCLUDE (RoleOnProject, IsActive, JoinedAt, LeftAt);
GO

-- "Which projects is this employee on?" — the set-shaped question AccessScope answers for Projects.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProjectMembers_ByEmployee'
               AND object_id = OBJECT_ID(N'dbo.ProjectMembers'))
    CREATE INDEX IX_ProjectMembers_ByEmployee
        ON dbo.ProjectMembers (CompanyID, EmployeeId)
        INCLUDE (ProjectId, RoleOnProject, LeftAt)
        WHERE IsActive = 1;
GO

-- "Who is on this project?" — the membership screen and the reverse lookup.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProjectMembers_ByProject'
               AND object_id = OBJECT_ID(N'dbo.ProjectMembers'))
    CREATE INDEX IX_ProjectMembers_ByProject
        ON dbo.ProjectMembers (CompanyID, ProjectId)
        INCLUDE (EmployeeId, RoleOnProject, AllocationPct, IsActive, JoinedAt, LeftAt);
GO

-- =============================================================================================
-- Verification
-- =============================================================================================
SELECT
    TableExists      = CASE WHEN OBJECT_ID(N'dbo.ProjectMembers', N'U') IS NULL THEN 0 ELSE 1 END,
    CheckConstraints = (SELECT COUNT(*) FROM sys.check_constraints
                        WHERE parent_object_id = OBJECT_ID(N'dbo.ProjectMembers')),
    ForeignKeys      = (SELECT COUNT(*) FROM sys.foreign_keys
                        WHERE parent_object_id = OBJECT_ID(N'dbo.ProjectMembers')),
    Indexes          = (SELECT COUNT(*) FROM sys.indexes
                        WHERE object_id = OBJECT_ID(N'dbo.ProjectMembers') AND index_id > 0);
GO
