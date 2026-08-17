-- =============================================================================================
-- Stage 1 Batch C — PlatformRoleAssignments: the ONE shared module role-assignment table.
--
-- Additive + idempotent. Safe to run any number of times. NO GL/stock impact, NO data modification,
-- NO change to any existing table, NO backfill. Creates one new table and its indexes only.
--
-- WHY ONE SHARED TABLE
--
-- Four role tables already exist and three of them are byte-for-byte the same shape:
--     AccountingUserRoles (CompanyID, EmployeeId, Role, CreatedAt)
--     CrmUserRoles        (CompanyID, EmployeeId, Role, CreatedAt)   -- identical
--     InventoryUserRoles  (CompanyID, EmployeeId, Role, CreatedAt) + ScopeBranchId
--     BranchUserRoles     (BranchId, EmployeeId, PosRole, IsActive)  -- the outlier, see below
-- They were copied, not designed. Adding HrUserRoles / ProjectUserRoles / TaskUserRoles /
-- CommUserRoles would have been the 4th-7th repetition. This table is that shape, once.
--
-- WHAT IS DELIBERATELY *NOT* HERE
--
--   * POS. BranchUserRoles is keyed by BranchId with NO CompanyID because PosAccessService reads it
--     BEFORE any BusinessContext exists (B1 classifies it SecuritySensitive for exactly that reason).
--     Folding it in would mean either giving POS login a company semantic it does not have, or making
--     CompanyID nullable here and weakening the isolation key for every module. POS stays out — a
--     documented PERMANENT exception, like StockBalance is to query filtering.
--   * Accounting / Inventory / CRM. Their tables are NOT migrated by Batch C: that would change the
--     authorization path of three working modules. The fold-in is a later, dual-read batch.
--   * Field-level permissions. No field columns: field security needs serialization, UI masking, API
--     response shaping, Search and AI decisions first. Planned, not guessed at here.
--   * Delegation. ValidFrom/ValidTo give temporal VALIDITY, which is not a delegation RECORD
--     (no DelegatedFrom/To, Reason, ApprovedBy, revocation, audit). Planned separately.
--
-- DEPLOYMENT: apply BEFORE the Batch C code is deployed ("SQL before code"). The application starts
-- without it only in the sense that nothing reads the table until a module scope is asked for; a
-- missing table surfaces as a hard failure from IPlatformRoleDirectory rather than as silent
-- bootstrap-open, because "no roles configured" and "no table" must not look the same.
-- =============================================================================================
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.PlatformRoleAssignments', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PlatformRoleAssignments
    (
        ID              int            IDENTITY(1,1) NOT NULL,

        -- The tenant. REQUIRED and never nullable: this is the isolation key the whole platform enforces.
        CompanyID       int            NOT NULL,

        -- IModuleAccessService.Scope — 'Hr' | 'Projects' | 'Tasks' | 'Communication' | future packs.
        -- Deliberately data, not DDL: a Hospital or Hotel industry pack ships ROWS, never a new table.
        Scope           nvarchar(40)   NOT NULL,

        -- WHO holds the role. Employee-only on day one; the column exists now because retrofitting it
        -- when External Collaboration lands would mean altering the primary access path of every module
        -- at once. Validated centrally in code against the supported set.
        PrincipalType   nvarchar(20)   NOT NULL CONSTRAINT DF_PlatformRoleAssignments_PrincipalType DEFAULT (N'Employee'),
        PrincipalId     int            NOT NULL,

        -- The module's own role vocabulary (HrManager, PayrollOfficer, ProjectManager, …).
        Role            nvarchar(60)   NOT NULL,

        -- Optional narrowing, exactly as InventoryUserRoles.ScopeBranchId already does. NULL = company-wide.
        ScopeBranchId   int            NULL,

        -- Revocation without deleting the row, so the grant history survives. BranchUserRoles already
        -- has this; the other three cannot revoke without destroying the audit trail.
        IsActive        bit            NOT NULL CONSTRAINT DF_PlatformRoleAssignments_IsActive DEFAULT (1),

        -- Temporal validity (holiday cover, secondment). NULL = unbounded. NOT a delegation record.
        ValidFrom       datetime2(7)   NULL,
        ValidTo         datetime2(7)   NULL,

        CreatedBy       int            NULL,
        CreatedAt       datetime2(7)   NOT NULL CONSTRAINT DF_PlatformRoleAssignments_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedBy       int            NULL,
        UpdatedAt       datetime2(7)   NULL,

        CONSTRAINT PK_PlatformRoleAssignments PRIMARY KEY CLUSTERED (ID)
    );
END
GO

-- ---------------------------------------------------------------------------------------------
-- Constraints. Added separately from CREATE TABLE so a database that already has the table (from an
-- earlier partial run) still receives them.
-- ---------------------------------------------------------------------------------------------

-- ValidTo cannot precede ValidFrom. NULLs are permitted on either side (unbounded), and a NULL
-- comparison yields UNKNOWN which a CHECK treats as satisfied — so the constraint only bites when
-- BOTH are supplied, which is exactly the case that can be wrong.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PlatformRoleAssignments_Validity')
    ALTER TABLE dbo.PlatformRoleAssignments WITH CHECK
        ADD CONSTRAINT CK_PlatformRoleAssignments_Validity
        CHECK (ValidFrom IS NULL OR ValidTo IS NULL OR ValidTo >= ValidFrom);
GO

-- A company id must be a real company. There is no company 0 and no default company.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PlatformRoleAssignments_Company')
    ALTER TABLE dbo.PlatformRoleAssignments WITH CHECK
        ADD CONSTRAINT CK_PlatformRoleAssignments_Company CHECK (CompanyID > 0);
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PlatformRoleAssignments_Principal')
    ALTER TABLE dbo.PlatformRoleAssignments WITH CHECK
        ADD CONSTRAINT CK_PlatformRoleAssignments_Principal CHECK (PrincipalId > 0);
GO

-- The supported principal types. A CHECK mirrors the code's central validation so a hand-inserted row
-- cannot introduce a principal kind no service knows how to authorize. Batch C ASSIGNS only 'Employee';
-- the others are listed so the structure is ready without a schema change when portals land.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PlatformRoleAssignments_PrincipalType')
    ALTER TABLE dbo.PlatformRoleAssignments WITH CHECK
        ADD CONSTRAINT CK_PlatformRoleAssignments_PrincipalType
        CHECK (PrincipalType IN (N'Employee', N'CustomerContact', N'VendorContact', N'PartnerContact',
                                 N'ServiceAccount', N'IntegrationClient'));
GO

-- Non-empty text columns: an empty Scope or Role would be a grant that matches nothing and looks like data.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PlatformRoleAssignments_Text')
    ALTER TABLE dbo.PlatformRoleAssignments WITH CHECK
        ADD CONSTRAINT CK_PlatformRoleAssignments_Text
        CHECK (LEN(LTRIM(RTRIM(Scope))) > 0 AND LEN(LTRIM(RTRIM(Role))) > 0
               AND LEN(LTRIM(RTRIM(PrincipalType))) > 0);
GO

-- ---------------------------------------------------------------------------------------------
-- Duplicate prevention.
--
-- A FILTERED unique index on the ACTIVE rows only. Two reasons it is filtered rather than a plain
-- unique constraint:
--   * the same grant may legitimately exist twice in history — one revoked (IsActive = 0) and one live;
--     a full unique key would make revoke-then-regrant impossible without deleting the audit trail;
--   * ScopeBranchId is nullable, and in SQL Server a UNIQUE index treats NULLs as equal, which is the
--     behaviour we want here (one company-wide grant per role) and is stated rather than inherited.
-- Requires QUOTED_IDENTIFIER ON — deploy with sqlcmd -I, as the other filtered-index slices do.
-- ---------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PlatformRoleAssignments_ActiveGrant'
               AND object_id = OBJECT_ID(N'dbo.PlatformRoleAssignments'))
    CREATE UNIQUE INDEX UX_PlatformRoleAssignments_ActiveGrant
        ON dbo.PlatformRoleAssignments (CompanyID, Scope, PrincipalType, PrincipalId, Role, ScopeBranchId)
        WHERE IsActive = 1;
GO

-- ---------------------------------------------------------------------------------------------
-- Query indexes, one per real access path.
-- ---------------------------------------------------------------------------------------------

-- THE hot path: IPlatformRoleDirectory.RolesAsync — one principal, one company, one scope. Covering,
-- so the whole grant list is served from the index without touching the table.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PlatformRoleAssignments_Principal'
               AND object_id = OBJECT_ID(N'dbo.PlatformRoleAssignments'))
    CREATE INDEX IX_PlatformRoleAssignments_Principal
        ON dbo.PlatformRoleAssignments (CompanyID, Scope, PrincipalType, PrincipalId)
        INCLUDE (Role, ScopeBranchId, IsActive, ValidFrom, ValidTo);
GO

-- The bootstrap-open probe: AnyConfiguredAsync(companyId, scope). Filtered to active rows because that
-- is the only question it asks, which keeps it tiny.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PlatformRoleAssignments_ScopeConfigured'
               AND object_id = OBJECT_ID(N'dbo.PlatformRoleAssignments'))
    CREATE INDEX IX_PlatformRoleAssignments_ScopeConfigured
        ON dbo.PlatformRoleAssignments (CompanyID, Scope)
        INCLUDE (ValidFrom, ValidTo)
        WHERE IsActive = 1;
GO

-- Administration: "who holds role X in this company", and the branch-scoped variant.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PlatformRoleAssignments_Role'
               AND object_id = OBJECT_ID(N'dbo.PlatformRoleAssignments'))
    CREATE INDEX IX_PlatformRoleAssignments_Role
        ON dbo.PlatformRoleAssignments (CompanyID, Scope, Role)
        INCLUDE (PrincipalType, PrincipalId, ScopeBranchId, IsActive);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PlatformRoleAssignments_Branch'
               AND object_id = OBJECT_ID(N'dbo.PlatformRoleAssignments'))
    CREATE INDEX IX_PlatformRoleAssignments_Branch
        ON dbo.PlatformRoleAssignments (CompanyID, ScopeBranchId)
        INCLUDE (Scope, PrincipalType, PrincipalId, Role, IsActive)
        WHERE ScopeBranchId IS NOT NULL;
GO

-- =============================================================================================
-- Verification. Prints the created objects so a deploy log shows what happened rather than silence.
-- =============================================================================================
SELECT
    TableExists     = CASE WHEN OBJECT_ID(N'dbo.PlatformRoleAssignments', N'U') IS NULL THEN 0 ELSE 1 END,
    CheckConstraints = (SELECT COUNT(*) FROM sys.check_constraints
                        WHERE parent_object_id = OBJECT_ID(N'dbo.PlatformRoleAssignments')),
    Indexes          = (SELECT COUNT(*) FROM sys.indexes
                        WHERE object_id = OBJECT_ID(N'dbo.PlatformRoleAssignments') AND index_id > 0);
GO
