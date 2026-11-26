-- =============================================================================================
-- Communication ↔ Tasks — CommTaskLinks. ONE new table, its indexes and its CHECK constraint.
--
-- WHAT THIS UNBLOCKS. ICommTaskIntegrationService.LinkingAvailable answers FALSE today, because
-- EfCommTaskLinkStore is deliberately not registered while this table does not exist — see
-- CommunicationPlatformRegistration: registering it against a missing table would produce a
-- SQL-208 on first use instead of an honest "linking is unavailable". Applying this slice is the
-- whole of what turns LinkingAvailable true, and the UI reveals "Link an existing task" only on
-- that answer. Until then the shared conversation panel already hides the action.
--
-- ADDITIVE + IDEMPOTENT. Safe to run any number of times. It creates ONLY new objects:
--   * NO existing table is altered.
--   * NO existing row is read, written or migrated. There is no backfill anywhere in this script.
--   * NO GL, stock, accounting, inventory, CRM, authorization or POS object is touched.
-- Every statement is guarded, so a partially-applied earlier run completes cleanly on a re-run.
--
-- SQL BEFORE CODE. Migrations are disabled in this project (CLAUDE.md: "Idempotent SQL in
-- deploy/sql, NOT EF migrations"). Apply this script BEFORE deploying code that registers
-- EfCommTaskLinkStore, exactly as communication_platform_slice_001.sql is applied before
-- AddCommunicationPlatform.
--
-- RUN IT WITH sqlcmd -i AND QUOTED_IDENTIFIER ON. The uniqueness rule below is a FILTERED index,
-- and SQL Server refuses to create a filtered index when QUOTED_IDENTIFIER is OFF — the same
-- footgun communication_platform_slice_001.sql and platform_business_events_slice_002.sql record.
-- The SET at the top of this file makes a plain -i run correct on its own.
--
-- DEPENDENCIES: CommThreads must exist (communication_platform_slice_001.sql). TaskItems is NOT
-- required to exist and is NOT referenced by a foreign key — see FOREIGN KEYS below.
--
-- FOREIGN KEYS: one, to CommThreads, and only because thread and link are the same platform's
-- parent and child. TaskId carries NO foreign key to TaskItems, for the reason the Communication
-- slice already states for employee ids: a hard FK would make an audit row block a cleanup, and
-- the two modules are deployable apart — CommTaskIntegration resolves Tasks through
-- IServiceProvider at call time precisely so a deployment may run either module alone.
-- =============================================================================================

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------------------------------------
-- 1. CommTaskLinks — one deliberate attachment of a task to a conversation.
--
-- EXPLICIT LINKS ONLY. A task that merely shares a business record with a conversation is NOT a
-- row here: that relationship is SharedBusinessContext, it is inferred at read time, and storing
-- it would turn an inference into a claim that somebody linked something.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommTaskLinks', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CommTaskLinks
    (
        Id          BIGINT        IDENTITY(1,1) NOT NULL,
        -- Leads every index. Two companies may each link their own task 7 to their own thread 12,
        -- and those are different links; without CompanyID first the first company to create a
        -- pair would block the second.
        CompanyID   INT           NOT NULL,
        TaskId      INT           NOT NULL,
        ThreadId    BIGINT        NOT NULL,
        -- PROVENANCE, NOT IDENTITY. The message the link came from; NULL means the link was made
        -- against the conversation as a whole. Deliberately OUTSIDE the uniqueness rule below.
        CommentId   BIGINT        NULL,
        -- CommTaskLinkOrigin: CreatedFromMessage | LinkedExisting. Constrained below for the
        -- reason ADR-002 gives: an unconstrained string column forks into disagreeing vocabularies.
        Origin      NVARCHAR(40)  NOT NULL,
        CreatedBy   INT           NULL,
        CreatedAt   DATETIME2     NOT NULL CONSTRAINT DF_CommTaskLinks_CreatedAt DEFAULT (SYSUTCDATETIME()),
        -- Soft removal. An unlink is a decision somebody made, and a row that vanishes takes the
        -- fact that it was ever linked with it.
        RemovedAt   DATETIME2     NULL,
        RemovedBy   INT           NULL,
        CONSTRAINT PK_CommTaskLinks PRIMARY KEY CLUSTERED (Id)
    );
    PRINT 'ADDED dbo.CommTaskLinks';
END
ELSE PRINT 'dbo.CommTaskLinks EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 2. THE UNIQUENESS RULE. (CompanyID, TaskId, ThreadId) WHERE RemovedAt IS NULL.
--
-- FILTERED ON PURPOSE, and the filter is the whole design. A task is either attached to a
-- conversation or it is not, so the LIVE pair must be unique — but an unfiltered unique index
-- would make the soft delete a lie: once a link were removed, the same task could never be
-- attached to the same conversation again, because the removed row would still own the key.
--
-- CommentId is NOT in the key. Putting it there would permit the same task to be linked to the
-- same conversation five times because five messages mentioned it — duplicate relationships
-- wearing different provenance. The first link wins and keeps its provenance.
-- ---------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommTaskLinks_Live' AND object_id = OBJECT_ID('dbo.CommTaskLinks'))
BEGIN
    CREATE UNIQUE INDEX UX_CommTaskLinks_Live ON dbo.CommTaskLinks (CompanyID, TaskId, ThreadId)
        WHERE RemovedAt IS NULL;
    PRINT 'ADDED UX_CommTaskLinks_Live';
END
ELSE PRINT 'UX_CommTaskLinks_Live EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 3. The read path. "Which tasks does this conversation carry" is the question the panel asks on
--    every open, and it asks it by thread within a company, live rows only.
-- ---------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommTaskLinks_Thread' AND object_id = OBJECT_ID('dbo.CommTaskLinks'))
BEGIN
    CREATE INDEX IX_CommTaskLinks_Thread ON dbo.CommTaskLinks (CompanyID, ThreadId)
        INCLUDE (TaskId, CommentId, Origin) WHERE RemovedAt IS NULL;
    PRINT 'ADDED IX_CommTaskLinks_Thread';
END
ELSE PRINT 'IX_CommTaskLinks_Thread EXISTS';
GO

-- The reverse question — "which conversations does this task belong to" — is what a task screen
-- asks to offer the way back to the discussion. Same company scope, same live filter.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommTaskLinks_Task' AND object_id = OBJECT_ID('dbo.CommTaskLinks'))
BEGIN
    CREATE INDEX IX_CommTaskLinks_Task ON dbo.CommTaskLinks (CompanyID, TaskId)
        INCLUDE (ThreadId, CommentId, Origin) WHERE RemovedAt IS NULL;
    PRINT 'ADDED IX_CommTaskLinks_Task';
END
ELSE PRINT 'IX_CommTaskLinks_Task EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 4. The vocabulary constraint, mirroring CommTaskLinkOrigin exactly.
-- ---------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommTaskLinks_Origin')
BEGIN
    ALTER TABLE dbo.CommTaskLinks ADD CONSTRAINT CK_CommTaskLinks_Origin
        CHECK (Origin IN (N'CreatedFromMessage', N'LinkedExisting'));
    PRINT 'ADDED CK_CommTaskLinks_Origin';
END
ELSE PRINT 'CK_CommTaskLinks_Origin EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 5. The one foreign key: thread and link are parent and child inside this platform.
--    NO ACTION on delete, deliberately: a thread with live links is not silently removable, the
--    same stance the Communication slice takes between comment and revision.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommThreads', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_CommTaskLinks_Thread')
BEGIN
    ALTER TABLE dbo.CommTaskLinks ADD CONSTRAINT FK_CommTaskLinks_Thread
        FOREIGN KEY (ThreadId) REFERENCES dbo.CommThreads (Id);
    PRINT 'ADDED FK_CommTaskLinks_Thread';
END
ELSE PRINT 'FK_CommTaskLinks_Thread EXISTS OR CommThreads ABSENT';
GO
