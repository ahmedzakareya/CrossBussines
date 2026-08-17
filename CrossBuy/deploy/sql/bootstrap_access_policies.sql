-- =============================================================================================
-- Stage 2A Batch B — B2: BootstrapAccessPolicies, the explicit bootstrap policy store.
--
-- Additive + idempotent. Safe to re-run. NO data modification, NO backfill, NO change to any
-- existing table, NO GL/stock impact. Creates one new table, its constraints and its indexes.
--
-- Requires QUOTED_IDENTIFIER ON for the filtered index — deploy with sqlcmd -I, as the other
-- filtered-index slices do.
--
-- WHY THIS IS A SEPARATE TABLE FROM PlatformRoleAssignments
--
-- A grant says "this employee holds this role". A policy says "this company has not configured this
-- scope yet, and here is what that permits until it does". Storing both in one table would make every
-- query answer both questions, and IMP-001's coexistence rule is that authoritative sources are never
-- unioned. It would also mean a policy row could be mistaken for a grant by anything reading the grant
-- store — including the four access services that already read it.
--
-- WHY THE 15 NEVER-BOOTSTRAP-OPEN ACTIONS ARE **NOT** A CHECK CONSTRAINT HERE
--
-- This is the one design decision in this slice worth arguing, so it is argued rather than asserted.
-- A CHECK listing the 15 (scope, action) pairs would be a SECOND copy of NeverBootstrapOpen.All.
-- Two copies of a security list drift, and the copy that drifts is always the one nobody is looking at.
-- Worse, the drift here would be silent in the dangerous direction: if code adds a 16th Never action and
-- the CHECK is not updated, the database would happily accept a permitting policy for it, and the row
-- would sit in an audit table asserting something the reader refuses to honour — a false audit line.
--
-- So the Never list lives in ONE place, code, and is enforced in TWO:
--   * BootstrapAccessPolicy.Validate() refuses to construct a permitting policy for a Never action;
--   * IBootstrapAccessPolicyReader evaluates the Never list BEFORE it ever looks at a policy row, so a
--     hand-inserted row cannot produce an allow. That is a mandatory test, not a hope.
-- The database enforces what the database is good at: uniqueness, state vocabulary, date ordering, and
-- the POS exclusion — facts that do not change when the code's classification does.
--
-- ROLLBACK
--   Everything here is new, so rollback loses no pre-existing data. It DOES lose the policy history the
--   seed produced, which is why it is documented rather than scripted:
--
--     DROP INDEX UX_BootstrapAccessPolicies_ActivePolicy ON dbo.BootstrapAccessPolicies;
--     DROP TABLE dbo.BootstrapAccessPolicies;
--
--   Reverse in this order: CODE first, then SCHEMA. Dropping the table while the reader still queries it
--   fails loudly (SQL 208) instead of silently returning "no policy" — and "no policy" is a DENY in this
--   design, so a schema-first rollback would close modules rather than open them. That is the safe
--   direction, but it is still an outage, so order it correctly.
-- =============================================================================================
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.BootstrapAccessPolicies', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BootstrapAccessPolicies
    (
        ID               int            IDENTITY(1,1) NOT NULL,

        -- The tenant. REQUIRED. There is no company 0, no default company and no fallback to 1.
        CompanyID        int            NOT NULL,

        -- EntityRegistry.PermissionScopes value. 'Pos' is refused by CK_..._NotPos below.
        Scope            nvarchar(40)   NOT NULL,

        -- The module's own action string. PER ACTION is the entire point of this table: Mechanism A could
        -- only answer for a whole module, which is precisely why it could not exclude a dangerous action.
        ActionCode       nvarchar(60)   NOT NULL,

        State            nvarchar(30)   NOT NULL
            CONSTRAINT DF_BootstrapAccessPolicies_State DEFAULT (N'LegacyCompatibility'),

        Reason           nvarchar(400)  NULL,

        EnabledAt        datetime2(7)   NULL,
        EnabledBy        int            NULL,

        -- Mandatory when State = 'Temporary'. See CK_..._TemporaryExpiry.
        ExpiresAt        datetime2(7)   NULL,

        ReviewedAt       datetime2(7)   NULL,
        ReviewedBy       int            NULL,
        AcknowledgedAt   datetime2(7)   NULL,
        AcknowledgedBy   int            NULL,

        CreatedBy        int            NULL,
        CreatedAt        datetime2(7)   NOT NULL
            CONSTRAINT DF_BootstrapAccessPolicies_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedBy        int            NULL,
        UpdatedAt        datetime2(7)   NULL,

        SourceSystem     nvarchar(40)   NULL,
        MigrationBatchId uniqueidentifier NULL,

        -- false = superseded history. Rows are never deleted: the row IS the record of what a company was
        -- permitted and when.
        IsActive         bit            NOT NULL
            CONSTRAINT DF_BootstrapAccessPolicies_IsActive DEFAULT (1),

        CONSTRAINT PK_BootstrapAccessPolicies PRIMARY KEY CLUSTERED (ID)
    );
END
GO

-- ---------------------------------------------------------------------------------------------
-- Constraints, added separately so a database that already has the table still receives them.
-- ---------------------------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BootstrapAccessPolicies_Company')
    ALTER TABLE dbo.BootstrapAccessPolicies WITH CHECK
        ADD CONSTRAINT CK_BootstrapAccessPolicies_Company CHECK (CompanyID > 0);
GO

-- The state vocabulary. Mirrors BootstrapPolicyStates so a hand-inserted row cannot introduce a state no
-- reader knows how to evaluate — and an unevaluable state would read as "no policy", which is a deny here
-- but would be indistinguishable from a correct denial in every log.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BootstrapAccessPolicies_State')
    ALTER TABLE dbo.BootstrapAccessPolicies WITH CHECK
        ADD CONSTRAINT CK_BootstrapAccessPolicies_State
        CHECK (State IN (N'LegacyCompatibility', N'Installation', N'Temporary',
                         N'ExplicitlyAllowed', N'Disabled', N'ReviewRequired'));
GO

-- POS may never receive a bootstrap policy. BranchUserRoles is keyed by BranchId with no CompanyID because
-- PosAccessService reads it BEFORE any BusinessContext exists, and it fails closed without a role. A policy
-- row here would imply POS has a bootstrap path; it does not, and inventing one would be a new exposure
-- created by the batch meant to close one.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BootstrapAccessPolicies_NotPos')
    ALTER TABLE dbo.BootstrapAccessPolicies WITH CHECK
        ADD CONSTRAINT CK_BootstrapAccessPolicies_NotPos CHECK (Scope <> N'Pos');
GO

-- A temporary exception with no end date is a permanent one wearing a different name. That is RISK-045,
-- and it is enforced by the engine rather than by remembering to set a field.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BootstrapAccessPolicies_TemporaryExpiry')
    ALTER TABLE dbo.BootstrapAccessPolicies WITH CHECK
        ADD CONSTRAINT CK_BootstrapAccessPolicies_TemporaryExpiry
        CHECK (State <> N'Temporary' OR ExpiresAt IS NOT NULL);
GO

-- Date ordering. NULL on either side is unbounded and a NULL comparison yields UNKNOWN, which a CHECK
-- treats as satisfied — so this bites only when both are supplied, which is the case that can be wrong.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BootstrapAccessPolicies_Dates')
    ALTER TABLE dbo.BootstrapAccessPolicies WITH CHECK
        ADD CONSTRAINT CK_BootstrapAccessPolicies_Dates
        CHECK (ExpiresAt IS NULL OR EnabledAt IS NULL OR ExpiresAt >= EnabledAt);
GO

-- Non-empty text: an empty Scope or ActionCode would be a policy that matches nothing while looking like
-- configuration — and under bootstrap that is the worst possible shape, because "configured" is what ends
-- compatibility.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BootstrapAccessPolicies_Text')
    ALTER TABLE dbo.BootstrapAccessPolicies WITH CHECK
        ADD CONSTRAINT CK_BootstrapAccessPolicies_Text
        CHECK (LEN(LTRIM(RTRIM(Scope))) > 0 AND LEN(LTRIM(RTRIM(ActionCode))) > 0
               AND LEN(LTRIM(RTRIM(State))) > 0);
GO

-- ---------------------------------------------------------------------------------------------
-- ONE active policy per (company, scope, action).
--
-- FILTERED on IsActive, for the same reason the grant store's duplicate guard is filtered: superseded
-- history must survive. A full unique key would force deleting the old row to change a policy, and the
-- old row is the answer to "why was this open in March".
-- ---------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_BootstrapAccessPolicies_ActivePolicy'
               AND object_id = OBJECT_ID(N'dbo.BootstrapAccessPolicies'))
    CREATE UNIQUE INDEX UX_BootstrapAccessPolicies_ActivePolicy
        ON dbo.BootstrapAccessPolicies (CompanyID, Scope, ActionCode)
        WHERE IsActive = 1;
GO

-- The reader's hot path.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BootstrapAccessPolicies_CompanyScope'
               AND object_id = OBJECT_ID(N'dbo.BootstrapAccessPolicies'))
    CREATE INDEX IX_BootstrapAccessPolicies_CompanyScope
        ON dbo.BootstrapAccessPolicies (CompanyID, Scope)
        INCLUDE (ActionCode, State, ExpiresAt, IsActive);
GO

-- Expiry and review sweeps scan across companies.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BootstrapAccessPolicies_Expiry'
               AND object_id = OBJECT_ID(N'dbo.BootstrapAccessPolicies'))
    CREATE INDEX IX_BootstrapAccessPolicies_Expiry
        ON dbo.BootstrapAccessPolicies (ExpiresAt)
        INCLUDE (CompanyID, Scope, ActionCode, State)
        WHERE ExpiresAt IS NOT NULL;
GO

-- =============================================================================================
-- DRIFT DETECTION. A second run must find compatible objects, and must FAIL rather than proceed if the
-- shape has changed underneath it — a duplicate-prevention index whose key has drifted is a security
-- property missing, not a performance detail.
-- =============================================================================================
DECLARE @activeKey nvarchar(400);

SELECT @activeKey = STUFF((
    SELECT ',' + c.name
    FROM sys.index_columns ic
    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE ic.object_id = OBJECT_ID(N'dbo.BootstrapAccessPolicies')
      AND ic.index_id = (SELECT index_id FROM sys.indexes
                         WHERE object_id = OBJECT_ID(N'dbo.BootstrapAccessPolicies')
                           AND name = N'UX_BootstrapAccessPolicies_ActivePolicy')
      AND ic.is_included_column = 0
    ORDER BY ic.key_ordinal
    FOR XML PATH('')), 1, 1, '');

IF @activeKey IS NULL
    THROW 50010, N'UX_BootstrapAccessPolicies_ActivePolicy is MISSING. Duplicate active policies would be prevented only by application logic, which two concurrent seeds defeat.', 1;

IF @activeKey <> N'CompanyID,Scope,ActionCode'
    THROW 50011, N'UX_BootstrapAccessPolicies_ActivePolicy key has DRIFTED from (CompanyID, Scope, ActionCode). The one-active-policy guarantee no longer matches what the reader assumes.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.BootstrapAccessPolicies')
                 AND name = N'UX_BootstrapAccessPolicies_ActivePolicy'
                 AND is_unique = 1 AND has_filter = 1)
    THROW 50012, N'UX_BootstrapAccessPolicies_ActivePolicy is not a UNIQUE FILTERED index. History retention requires the filter; one-active-policy requires the uniqueness.', 1;

IF (SELECT COUNT(*) FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.BootstrapAccessPolicies')) < 6
    THROW 50013, N'BootstrapAccessPolicies is missing one or more CHECK constraints (expected 6: Company, State, NotPos, TemporaryExpiry, Dates, Text).', 1;
GO

-- =============================================================================================
-- Verification, printed so a deploy log shows what happened rather than silence.
-- =============================================================================================
SELECT
    TableExists      = CASE WHEN OBJECT_ID(N'dbo.BootstrapAccessPolicies', N'U') IS NULL THEN 0 ELSE 1 END,
    Columns          = (SELECT COUNT(*) FROM sys.columns
                        WHERE object_id = OBJECT_ID(N'dbo.BootstrapAccessPolicies')),
    CheckConstraints = (SELECT COUNT(*) FROM sys.check_constraints
                        WHERE parent_object_id = OBJECT_ID(N'dbo.BootstrapAccessPolicies')),
    Indexes          = (SELECT COUNT(*) FROM sys.indexes
                        WHERE object_id = OBJECT_ID(N'dbo.BootstrapAccessPolicies') AND index_id > 0),
    ActivePolicyIndex = (SELECT COUNT(*) FROM sys.indexes
                         WHERE object_id = OBJECT_ID(N'dbo.BootstrapAccessPolicies')
                           AND name = N'UX_BootstrapAccessPolicies_ActivePolicy');
GO
