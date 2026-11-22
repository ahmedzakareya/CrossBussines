-- =============================================================================================
-- Communication — CommPushTokens. ONE new table, its indexes and its CHECK constraint.
--
-- WHAT THIS UNBLOCKS. Module 21 (Push) is contracts-only and CPS-001 §9 records it as gap G3:
-- "ICommPushTokenStore has no implementation". The reason is this table: there is nowhere to put
-- a device token, so UnwiredCommNotificationChannel.Push() returns Skip with that exact reason
-- and push rows park as Skipped rather than queueing as Failed. This slice is the SCHEMA half of
-- G3. It does not close it on its own — see "WHAT THIS DOES NOT DO" below.
--
-- NOT APPLIED BY AUTHORING IT. This file is prepared, not deployed. Nothing registers an
-- ICommPushTokenStore implementation today, so an unapplied slice changes no behaviour: the push
-- channel keeps answering Skip. That is the same SQL-BEFORE-CODE ordering comm_task_links.sql
-- uses, and the same reason: registering a store against a missing table is a SQL-208 on first
-- use instead of an honest "push is unavailable".
--
-- ADDITIVE + IDEMPOTENT. Safe to run any number of times. It creates ONLY new objects:
--   * NO existing table is altered.
--   * NO existing row is read, written or migrated. There is no backfill anywhere in this script.
--   * NO GL, stock, accounting, inventory, CRM, authorization or POS object is touched.
--
-- RUN IT WITH sqlcmd -i AND QUOTED_IDENTIFIER ON. The uniqueness rule below is a FILTERED index,
-- and SQL Server refuses to create a filtered index when QUOTED_IDENTIFIER is OFF — the footgun
-- communication_platform_slice_001.sql and comm_task_links.sql both record. The SET at the top of
-- this file makes a plain -i run correct on its own.
--
-- DEPENDENCIES: none beyond a CrossBuy database. CommPushTokens is self-contained; it does NOT
-- reference CommNotifications, because a token outlives every notification sent to it.
--
-- FOREIGN KEYS: NONE, deliberately — the same rule communication_platform_slice_001.sql states
-- for employee ids. A hard FK to Employee would make a device registration block an HR cleanup,
-- and EmployeeId is already company-scoped by the predicate every read applies.
--
-- A TOKEN IS A CREDENTIAL, NOT A PREFERENCE. It addresses one person's device: anyone holding it
-- can be pushed to. Three consequences are built into the shape below rather than left to callers:
--   * It is NEVER returned by a read model, a DTO or a report. CommPushToken (the contract) is an
--     adapter-facing type; no inbox or preference screen projects it.
--   * Revocation is a SOFT retire (RevokedAt), not a DELETE, so a token that stops working leaves
--     evidence of when it stopped — a deleted row cannot be distinguished from one never created.
--   * The uniqueness rule is per COMPANY and filtered on live rows, so the same physical device
--     re-registering after revocation is a new row rather than an update that erases the history.
-- =============================================================================================

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------------------------------------
-- 1. CommPushTokens — one registered device endpoint for one employee.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommPushTokens', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CommPushTokens
    (
        Id              BIGINT         IDENTITY(1,1) NOT NULL,

        -- Leads every index, for the reason every other Communication table states: two companies
        -- may legitimately hold the same employee id, and a token is only ever resolved inside one.
        CompanyID       INT            NOT NULL,
        EmployeeId      INT            NOT NULL,

        -- "web" | "android" | "ios" — CommPushToken.Platform. Constrained below so an adapter
        -- cannot be handed a platform it has no transport for.
        Platform        NVARCHAR(20)   NOT NULL,

        -- The provider's device token. 512 is above the longest FCM/APNs/WebPush endpoint in
        -- circulation; a WebPush endpoint is a URL and is the long case.
        Token           NVARCHAR(512)  NOT NULL,

        RegisteredAt    DATETIME2      NULL,
        LastSeenAt      DATETIME2      NULL,

        -- SOFT RETIRE. Set when the provider reports the token dead, or the employee signs the
        -- device out. Never deleted — see the header.
        RevokedAt       DATETIME2      NULL,
        RevokedReason   NVARCHAR(200)  NULL,

        CreatedBy       INT            NULL,
        CreatedAt       DATETIME2      NULL,
        updatedBy       INT            NULL,
        UpdatedAt       DATETIME2      NULL,

        CONSTRAINT PK_CommPushTokens PRIMARY KEY CLUSTERED (Id)
    );
    PRINT 'ADDED dbo.CommPushTokens';
END
ELSE PRINT 'dbo.CommPushTokens EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 2. The live-token uniqueness rule.
--
-- FILTERED ON RevokedAt, so a device that re-registers after revocation inserts a new row instead
-- of colliding with its own retired one. Without the filter, soft revocation would be impossible:
-- the dead row would permanently own the token value.
-- ---------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommPushTokens_Live' AND object_id = OBJECT_ID('dbo.CommPushTokens'))
BEGIN
    CREATE UNIQUE INDEX UX_CommPushTokens_Live
        ON dbo.CommPushTokens (CompanyID, Token)
        WHERE RevokedAt IS NULL;
    PRINT 'ADDED UX_CommPushTokens_Live';
END
ELSE PRINT 'UX_CommPushTokens_Live EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 3. The send path's read: every live token for one recipient in one company.
-- ---------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommPushTokens_Recipient' AND object_id = OBJECT_ID('dbo.CommPushTokens'))
BEGIN
    CREATE INDEX IX_CommPushTokens_Recipient
        ON dbo.CommPushTokens (CompanyID, EmployeeId)
        WHERE RevokedAt IS NULL;
    PRINT 'ADDED IX_CommPushTokens_Recipient';
END
ELSE PRINT 'IX_CommPushTokens_Recipient EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 4. The platform vocabulary, in the schema as well as in code.
--
-- Unlike CommAuditEntries.Action (G7, deliberately unconstrained because its vocabulary grows),
-- this one is CLOSED: a push transport is a deployment capability, not a value a feature adds.
-- ---------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommPushTokens_Platform')
BEGIN
    ALTER TABLE dbo.CommPushTokens ADD CONSTRAINT CK_CommPushTokens_Platform
        CHECK (Platform IN ('web','android','ios'));
    PRINT 'ADDED CK_CommPushTokens_Platform';
END
ELSE PRINT 'CK_CommPushTokens_Platform EXISTS';
GO

-- =============================================================================================
-- WHAT THIS DOES NOT DO, so nobody reads an applied slice as a closed gap:
--
--   * It registers no ICommPushTokenStore. Applying this slice alone changes NOTHING — the push
--     channel still answers Skip, because no implementation is wired.
--   * It writes no registration endpoint. A device has no way to enrol until one exists, and that
--     endpoint is an authenticated write that must bind the token to the CALLER's employee id,
--     never to an id supplied in the request body.
--   * It ships no push adapter. FCM/APNs/WebPush each need credentials held outside this codebase.
--
-- G3 therefore needs THREE more things after this slice: the store, the registration endpoint and
-- one adapter. The order matters — the store and endpoint are useless without the adapter, and the
-- adapter cannot be tested without the store.
-- =============================================================================================
