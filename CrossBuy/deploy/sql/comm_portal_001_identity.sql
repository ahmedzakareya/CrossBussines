-- =============================================================================================
-- Client Portal — slice comm_portal_001_identity.
--
-- AUTHORED, NOT EXECUTED. No DDL ran for this batch: no production write and no CrossBuyDev change.
--
-- THE comm_ PREFIX IS A GOVERNANCE ARTEFACT, NOT A CLAIM. deploy/sql/comm_* and documents_*.sql are
-- the SQL globs this work stream owns; portal_* belongs to another tab. The table is PortalUsers,
-- which is what it is. A rename once a portal glob is granted is a rename with no behavioural
-- change, and is recorded as debt rather than taken by claiming a path not granted.
--
-- WHAT THIS TABLE IS. The link between an Identity login and exactly ONE customer of exactly ONE
-- company. It is the entire external identity model, and it is deliberately small.
--
-- WHAT IT IS NOT: an Employee. That is the point of the whole batch. Every internal access service
-- resolves its company through BusinessContextFactory, which reads the "Employee" session blob and
-- REFUSES when there is none. A portal user has no Employee row, so an external caller reaching an
-- internal service arrives unresolved and is refused by machinery that already exists. The boundary
-- is structural: "a customer cannot become an employee" holds because the customer does not have the
-- thing employees are identified by, not because a check somewhere remembers to say no.
--
-- NO FK TO AspNetUsers. UserId is nullable so a contact can be INVITED before their login exists,
-- which is how an invitation flow works without creating a placeholder account. A hard FK would make
-- provisioning depend on Identity insert order for no safety gain - the column is validated by the
-- unique filtered index below and by the resolver, which requires an active row to return a context.
--
-- ADDITIVE + IDEMPOTENT. Safe to run any number of times. It creates ONE new table and its two
-- indexes:
--   * NO existing table is altered.
--   * NO existing row is read, written or migrated.
--   * NO Customer, Employee, Identity, GL, stock, accounting, CRM or POS object is modified.
--
-- RUN IT WITH sqlcmd -I (QUOTED_IDENTIFIER ON). The UserId index below is FILTERED, and SQL Server
-- refuses to create a filtered index when QUOTED_IDENTIFIER is OFF.
-- =============================================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID('dbo.PortalUsers') IS NULL
BEGIN
    CREATE TABLE dbo.PortalUsers
    (
        Id                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PortalUsers PRIMARY KEY,

        -- The tenant, and the customer within it. BOTH are the scope: company alone is not enough,
        -- because two customers of one company must not see each other, and that is the failure a
        -- "tenant-scoped" portal ships with on the day its second customer signs in.
        CompanyID           int            NOT NULL,
        CustomerId          int            NOT NULL,

        -- AspNetUsers key. Nullable: a contact may be provisioned before their login exists.
        UserId              nvarchar(450)      NULL,

        -- The person. Deliberately NOT a link to Employee - a customer contact is not staff, and
        -- modelling them as one would put an outsider into org charts and approval chains.
        ContactName         nvarchar(200)  NOT NULL,
        Email               nvarchar(200)      NULL,
        Phone               nvarchar(64)       NULL,

        -- Comma-separated portal capabilities. NULL means the read-only default, never "everything":
        -- a blank column is a row nobody configured, and the safe reading of that is the minimum.
        Capabilities        nvarchar(400)      NULL,

        -- Revocation without deletion, so a former contact keeps their audit trail and loses access.
        IsActive            bit            NOT NULL CONSTRAINT DF_PortalUsers_IsActive DEFAULT (1),

        CreatedAt           datetime2      NOT NULL CONSTRAINT DF_PortalUsers_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CreatedByEmployeeId int                NULL,
        LastSeenAt          datetime2          NULL
    );
    PRINT 'PortalUsers created.';
END
ELSE PRINT 'PortalUsers already present.';
GO

IF OBJECT_ID('dbo.PortalUsers') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PortalUsers_UserId')
BEGIN
    -- ONE login resolves to ONE portal identity. Without this, a second link for the same account
    -- would make the customer boundary depend on which row the query plan returned first - a tenant
    -- boundary decided by the optimiser is not a boundary.
    CREATE UNIQUE INDEX UX_PortalUsers_UserId
        ON dbo.PortalUsers (UserId) WHERE UserId IS NOT NULL;
    PRINT 'UX_PortalUsers_UserId created.';
END
GO

IF OBJECT_ID('dbo.PortalUsers') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PortalUsers_Company_Customer')
BEGIN
    -- The administrative listing, and the scope predicate itself.
    CREATE INDEX IX_PortalUsers_Company_Customer
        ON dbo.PortalUsers (CompanyID, CustomerId) INCLUDE (IsActive);
    PRINT 'IX_PortalUsers_Company_Customer created.';
END
GO

-- NO SEEDED PORTAL USER. Provisioning an external login is a business act with a real person on the
-- other end of it; a seeded row here would be a live credential shipped in source control.
PRINT 'comm_portal_001_identity complete. Structure only: no portal user seeded, no existing row touched.';
GO
