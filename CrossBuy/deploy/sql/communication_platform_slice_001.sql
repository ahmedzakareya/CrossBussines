-- =============================================================================================
-- Communication Platform — slice 001. Fourteen new tables, their indexes and their CHECK constraints.
--
-- ADDITIVE + IDEMPOTENT. Safe to run any number of times. It creates ONLY new objects:
--   * NO existing table is altered.
--   * NO existing row is read, written, or migrated. There is no backfill anywhere in this script.
--   * NO GL, stock, accounting, inventory, CRM, authorization or POS object is touched.
-- Every statement is guarded, so a partially-applied earlier run completes cleanly on a re-run.
--
-- SQL BEFORE CODE. Migrations are disabled in this project (CLAUDE.md: "Idempotent SQL in deploy/sql, NOT EF
-- migrations"), and the platform kernel ships its schema the same way (PKS-001 §1). Apply this script BEFORE
-- deploying code that calls AddCommunicationPlatform.
--
-- RUN IT WITH sqlcmd -I  (QUOTED_IDENTIFIER ON). Several indexes below are FILTERED, and SQL Server refuses to
-- create a filtered index when QUOTED_IDENTIFIER is OFF — the same footgun CLAUDE.md records for
-- platform_business_events_slice_002.sql.
--
-- DEPENDENCIES: none. This slice does not reference BusinessEvents, Notifications, CommMessages or any other
-- platform/feature table. It can be applied to a database where no kernel slice has been applied at all — which
-- is deliberate: the audit trail must be available even when the event platform is not (ADR-035).
--
-- FOREIGN KEYS: declared for the parent/child relationships WITHIN this platform only (thread -> comment ->
-- revision/attachment/reaction/mention -> recipient, notification -> delivery). No FK points at Employees,
-- Companies or any business table: an employee id here is a reference to a person, and a hard FK would make a
-- historical audit row block a personnel-record cleanup. Same stance the kernel takes for
-- BusinessEvents.ActorEmployeeId.
--
-- EVERY VOCABULARY COLUMN CARRIES A CHECK CONSTRAINT mirroring the frozen C# set in
-- Models/Communication/CommVocabulary.cs. That mirroring is the point: an unconstrained string column forks into
-- disagreeing vocabularies, which is what ADR-002 exists to prevent.
-- =============================================================================================

SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------------------------------------
-- 1. CommThreads — one conversation anchored to one business record.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommThreads', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CommThreads
    (
        Id                BIGINT         IDENTITY(1,1) NOT NULL,
        CompanyID         INT            NOT NULL,
        BranchID          INT            NULL,
        EntityType        NVARCHAR(60)   NOT NULL,
        EntityId          INT            NOT NULL,
        [Kind]            NVARCHAR(40)   NOT NULL,
        ThreadKey         NVARCHAR(80)   NOT NULL CONSTRAINT DF_CommThreads_ThreadKey DEFAULT (''),
        SubjectAr         NVARCHAR(300)  NULL,
        SubjectEn         NVARCHAR(300)  NULL,
        Visibility        NVARCHAR(40)   NOT NULL,
        IsLocked          BIT            NOT NULL CONSTRAINT DF_CommThreads_IsLocked DEFAULT (0),
        LockedReason      NVARCHAR(300)  NULL,
        LockedBy          INT            NULL,
        LockedAt          DATETIME2      NULL,
        CommentCount      INT            NOT NULL CONSTRAINT DF_CommThreads_CommentCount DEFAULT (0),
        ParticipantCount  INT            NOT NULL CONSTRAINT DF_CommThreads_ParticipantCount DEFAULT (0),
        LastActivityAt    DATETIME2      NULL,
        DeletedAt         DATETIME2      NULL,
        DeletedBy         INT            NULL,
        CreatedBy         INT            NULL,
        CreatedAt         DATETIME2      NULL,
        updatedBy         INT            NULL,
        UpdatedAt         DATETIME2      NULL,
        CONSTRAINT PK_CommThreads PRIMARY KEY CLUSTERED (Id)
    );
    PRINT 'CREATED dbo.CommThreads';
END
ELSE PRINT 'dbo.CommThreads EXISTS';
GO

-- THE identity of a thread. UNIQUE so "get or create the discussion for this invoice" is a race the DATABASE
-- settles: two simultaneous first comments would otherwise create two threads and split the conversation
-- permanently. CommThreadService catches the resulting DbUpdateException and returns the winner.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommThreads_Anchor' AND object_id = OBJECT_ID('dbo.CommThreads'))
BEGIN
    CREATE UNIQUE INDEX UX_CommThreads_Anchor
        ON dbo.CommThreads (CompanyID, EntityType, EntityId, [Kind], ThreadKey);
    PRINT 'ADDED UX_CommThreads_Anchor';
END
ELSE PRINT 'UX_CommThreads_Anchor EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommThreads_Activity' AND object_id = OBJECT_ID('dbo.CommThreads'))
BEGIN
    CREATE INDEX IX_CommThreads_Activity
        ON dbo.CommThreads (CompanyID, EntityType, EntityId, LastActivityAt);
    PRINT 'ADDED IX_CommThreads_Activity';
END
ELSE PRINT 'IX_CommThreads_Activity EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommThreads_Kind')
BEGIN
    ALTER TABLE dbo.CommThreads ADD CONSTRAINT CK_CommThreads_Kind
        CHECK ([Kind] IN ('Discussion','Notes','Review'));
    PRINT 'ADDED CK_CommThreads_Kind';
END
ELSE PRINT 'CK_CommThreads_Kind EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommThreads_Visibility')
BEGIN
    ALTER TABLE dbo.CommThreads ADD CONSTRAINT CK_CommThreads_Visibility
        CHECK (Visibility IN ('Public','Internal','Confidential','Restricted'));
    PRINT 'ADDED CK_CommThreads_Visibility';
END
ELSE PRINT 'CK_CommThreads_Visibility EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 2. CommThreadPermissions — thread-level grants (modules 23, 24).
--
-- A row here can only ADD access WITHIN what the anchor entity already allows: entity-level View stays a
-- precondition evaluated by IPlatformPermissionProvider before this table is consulted (ADR-033).
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommThreadPermissions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CommThreadPermissions
    (
        Id             BIGINT        IDENTITY(1,1) NOT NULL,
        CompanyID      INT           NOT NULL,
        ThreadId       BIGINT        NOT NULL,
        PrincipalKind  NVARCHAR(40)  NOT NULL,
        PrincipalId    INT           NULL,
        PrincipalKey   NVARCHAR(120) NULL,
        [Level]        NVARCHAR(40)  NOT NULL,
        RevokedAt      DATETIME2     NULL,
        RevokedBy      INT           NULL,
        CreatedBy      INT           NULL,
        CreatedAt      DATETIME2     NULL,
        updatedBy      INT           NULL,
        UpdatedAt      DATETIME2     NULL,
        CONSTRAINT PK_CommThreadPermissions PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_CommThreadPermissions_Thread FOREIGN KEY (ThreadId)
            REFERENCES dbo.CommThreads (Id)
    );
    PRINT 'CREATED dbo.CommThreadPermissions';
END
ELSE PRINT 'dbo.CommThreadPermissions EXISTS';
GO

-- Filtered to LIVE grants: a revoked grant must never influence a decision, and it would otherwise bloat the
-- authorization index forever.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommThreadPermissions_Thread' AND object_id = OBJECT_ID('dbo.CommThreadPermissions'))
BEGIN
    CREATE INDEX IX_CommThreadPermissions_Thread
        ON dbo.CommThreadPermissions (ThreadId, PrincipalKind, PrincipalId)
        WHERE RevokedAt IS NULL;
    PRINT 'ADDED IX_CommThreadPermissions_Thread';
END
ELSE PRINT 'IX_CommThreadPermissions_Thread EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommThreadPermissions_Kind')
BEGIN
    ALTER TABLE dbo.CommThreadPermissions ADD CONSTRAINT CK_CommThreadPermissions_Kind
        CHECK (PrincipalKind IN ('Employee','Team','Department','Role'));
    PRINT 'ADDED CK_CommThreadPermissions_Kind';
END
ELSE PRINT 'CK_CommThreadPermissions_Kind EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommThreadPermissions_Level')
BEGIN
    ALTER TABLE dbo.CommThreadPermissions ADD CONSTRAINT CK_CommThreadPermissions_Level
        CHECK ([Level] IN ('Read','Comment','Moderate'));
    PRINT 'ADDED CK_CommThreadPermissions_Level';
END
ELSE PRINT 'CK_CommThreadPermissions_Level EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 3. CommComments — the one comment table. Internal and public NOTES differ by Visibility, replies differ by
--    ParentCommentId. Separate tables would duplicate revisions, mentions, attachments and reactions.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommComments', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CommComments
    (
        Id                 BIGINT         IDENTITY(1,1) NOT NULL,
        CompanyID          INT            NOT NULL,
        ThreadId           BIGINT         NOT NULL,
        EntityType         NVARCHAR(60)   NOT NULL,
        EntityId           INT            NOT NULL,
        ParentCommentId    BIGINT         NULL,
        Depth              INT            NOT NULL CONSTRAINT DF_CommComments_Depth DEFAULT (1),
        Body               NVARCHAR(MAX)  NOT NULL,
        BodyFormat         NVARCHAR(40)   NOT NULL,
        Visibility         NVARCHAR(40)   NOT NULL,
        AuthorEmployeeId   INT            NOT NULL,
        RevisionCount      INT            NOT NULL CONSTRAINT DF_CommComments_RevisionCount DEFAULT (0),
        EditedAt           DATETIME2      NULL,
        EditedBy           INT            NULL,
        DeletedAt          DATETIME2      NULL,
        DeletedBy          INT            NULL,
        BodyAnalysisJson   NVARCHAR(MAX)  NULL,
        MentionCount       INT            NOT NULL CONSTRAINT DF_CommComments_MentionCount DEFAULT (0),
        AttachmentCount    INT            NOT NULL CONSTRAINT DF_CommComments_AttachmentCount DEFAULT (0),
        ReactionCount      INT            NOT NULL CONSTRAINT DF_CommComments_ReactionCount DEFAULT (0),
        ReplyCount         INT            NOT NULL CONSTRAINT DF_CommComments_ReplyCount DEFAULT (0),
        DedupKey           NVARCHAR(160)  NULL,
        CreatedBy          INT            NULL,
        CreatedAt          DATETIME2      NULL,
        updatedBy          INT            NULL,
        UpdatedAt          DATETIME2      NULL,
        CONSTRAINT PK_CommComments PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_CommComments_Thread FOREIGN KEY (ThreadId)
            REFERENCES dbo.CommThreads (Id),

        -- Self-reference for the reply chain. NO CASCADE, deliberately: deletion in this platform is always a
        -- soft delete, so a cascade path would never fire — and declaring one would imply row deletion is a
        -- supported operation, which it is not.
        CONSTRAINT FK_CommComments_Parent FOREIGN KEY (ParentCommentId)
            REFERENCES dbo.CommComments (Id)
    );
    PRINT 'CREATED dbo.CommComments';
END
ELSE PRINT 'dbo.CommComments EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommComments_Thread' AND object_id = OBJECT_ID('dbo.CommComments'))
BEGIN
    CREATE INDEX IX_CommComments_Thread ON dbo.CommComments (ThreadId, Id);
    PRINT 'ADDED IX_CommComments_Thread';
END
ELSE PRINT 'IX_CommComments_Thread EXISTS';
GO

-- The record read, used by the timeline aggregator without touching CommThreads — which is why EntityType and
-- EntityId are denormalised onto this table.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommComments_Entity' AND object_id = OBJECT_ID('dbo.CommComments'))
BEGIN
    CREATE INDEX IX_CommComments_Entity ON dbo.CommComments (CompanyID, EntityType, EntityId, CreatedAt);
    PRINT 'ADDED IX_CommComments_Entity';
END
ELSE PRINT 'IX_CommComments_Entity EXISTS';
GO

-- Filtered to ACTUAL replies: most comments are top-level, so an unfiltered index would be mostly NULLs.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommComments_Parent' AND object_id = OBJECT_ID('dbo.CommComments'))
BEGIN
    CREATE INDEX IX_CommComments_Parent ON dbo.CommComments (ParentCommentId)
        WHERE ParentCommentId IS NOT NULL;
    PRINT 'ADDED IX_CommComments_Parent';
END
ELSE PRINT 'IX_CommComments_Parent EXISTS';
GO

-- Idempotency, per company. Same filtered-unique construction as UX_BusinessEvents_DedupKey and for the same
-- reason: the key is optional, and NULLs must not collide with each other.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommComments_DedupKey' AND object_id = OBJECT_ID('dbo.CommComments'))
BEGIN
    CREATE UNIQUE INDEX UX_CommComments_DedupKey ON dbo.CommComments (CompanyID, DedupKey)
        WHERE DedupKey IS NOT NULL;
    PRINT 'ADDED UX_CommComments_DedupKey';
END
ELSE PRINT 'UX_CommComments_DedupKey EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommComments_BodyFormat')
BEGIN
    -- HTML is deliberately absent from the vocabulary — see CommBodyFormat.
    ALTER TABLE dbo.CommComments ADD CONSTRAINT CK_CommComments_BodyFormat
        CHECK (BodyFormat IN ('Markdown','PlainText'));
    PRINT 'ADDED CK_CommComments_BodyFormat';
END
ELSE PRINT 'CK_CommComments_BodyFormat EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommComments_Visibility')
BEGIN
    ALTER TABLE dbo.CommComments ADD CONSTRAINT CK_CommComments_Visibility
        CHECK (Visibility IN ('Public','Internal','Confidential','Restricted'));
    PRINT 'ADDED CK_CommComments_Visibility';
END
ELSE PRINT 'CK_CommComments_Visibility EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommComments_Depth')
BEGIN
    ALTER TABLE dbo.CommComments ADD CONSTRAINT CK_CommComments_Depth CHECK (Depth >= 1);
    PRINT 'ADDED CK_CommComments_Depth';
END
ELSE PRINT 'CK_CommComments_Depth EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 4. CommCommentRevisions — the PREVIOUS body of an edited comment (modules 28, 30). Append-only.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommCommentRevisions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CommCommentRevisions
    (
        Id                   BIGINT         IDENTITY(1,1) NOT NULL,
        CompanyID            INT            NOT NULL,
        CommentId            BIGINT         NOT NULL,
        RevisionNo           INT            NOT NULL,
        Body                 NVARCHAR(MAX)  NOT NULL,
        BodyFormat           NVARCHAR(40)   NOT NULL,
        Visibility           NVARCHAR(40)   NOT NULL,
        EditedByEmployeeId   INT            NOT NULL,
        EditedAt             DATETIME2      NULL,
        Reason               NVARCHAR(300)  NULL,
        CreatedBy            INT            NULL,
        CreatedAt            DATETIME2      NULL,
        updatedBy            INT            NULL,
        UpdatedAt            DATETIME2      NULL,
        CONSTRAINT PK_CommCommentRevisions PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_CommCommentRevisions_Comment FOREIGN KEY (CommentId)
            REFERENCES dbo.CommComments (Id)
    );
    PRINT 'CREATED dbo.CommCommentRevisions';
END
ELSE PRINT 'dbo.CommCommentRevisions EXISTS';
GO

-- UNIQUE so a concurrent double-edit cannot write two revision 3s and make the history unorderable.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommCommentRevisions_Comment' AND object_id = OBJECT_ID('dbo.CommCommentRevisions'))
BEGIN
    CREATE UNIQUE INDEX UX_CommCommentRevisions_Comment ON dbo.CommCommentRevisions (CommentId, RevisionNo);
    PRINT 'ADDED UX_CommCommentRevisions_Comment';
END
ELSE PRINT 'UX_CommCommentRevisions_Comment EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommCommentRevisions_RevisionNo')
BEGIN
    ALTER TABLE dbo.CommCommentRevisions ADD CONSTRAINT CK_CommCommentRevisions_RevisionNo
        CHECK (RevisionNo >= 1);
    PRINT 'ADDED CK_CommCommentRevisions_RevisionNo';
END
ELSE PRINT 'CK_CommCommentRevisions_RevisionNo EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 5. CommCommentAttachments — module 7.
--
-- StorageKey is an OPAQUE handle into the deployment's existing file store. This platform stores no bytes,
-- resolves no URL, and deletes nothing from the store when a row is soft-deleted (ADR-030 §8).
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommCommentAttachments', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CommCommentAttachments
    (
        Id                     BIGINT         IDENTITY(1,1) NOT NULL,
        CompanyID              INT            NOT NULL,
        CommentId              BIGINT         NOT NULL,
        ThreadId               BIGINT         NOT NULL,
        FileName               NVARCHAR(260)  NOT NULL,
        ContentType            NVARCHAR(180)  NOT NULL,
        SizeBytes              BIGINT         NOT NULL,
        StorageKey             NVARCHAR(400)  NOT NULL,
        PreviewKind            NVARCHAR(40)   NOT NULL,
        CanInline              BIT            NOT NULL CONSTRAINT DF_CommCommentAttachments_CanInline DEFAULT (0),
        ThumbnailStorageKey    NVARCHAR(400)  NULL,
        UploadedByEmployeeId   INT            NOT NULL,
        DeletedAt              DATETIME2      NULL,
        DeletedBy              INT            NULL,
        CreatedBy              INT            NULL,
        CreatedAt              DATETIME2      NULL,
        updatedBy              INT            NULL,
        UpdatedAt              DATETIME2      NULL,
        CONSTRAINT PK_CommCommentAttachments PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_CommCommentAttachments_Comment FOREIGN KEY (CommentId)
            REFERENCES dbo.CommComments (Id),
        CONSTRAINT FK_CommCommentAttachments_Thread FOREIGN KEY (ThreadId)
            REFERENCES dbo.CommThreads (Id)
    );
    PRINT 'CREATED dbo.CommCommentAttachments';
END
ELSE PRINT 'dbo.CommCommentAttachments EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommCommentAttachments_Comment' AND object_id = OBJECT_ID('dbo.CommCommentAttachments'))
BEGIN
    CREATE INDEX IX_CommCommentAttachments_Comment ON dbo.CommCommentAttachments (CommentId);
    PRINT 'ADDED IX_CommCommentAttachments_Comment';
END
ELSE PRINT 'IX_CommCommentAttachments_Comment EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommCommentAttachments_Thread' AND object_id = OBJECT_ID('dbo.CommCommentAttachments'))
BEGIN
    CREATE INDEX IX_CommCommentAttachments_Thread ON dbo.CommCommentAttachments (CompanyID, ThreadId);
    PRINT 'ADDED IX_CommCommentAttachments_Thread';
END
ELSE PRINT 'IX_CommCommentAttachments_Thread EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommCommentAttachments_PreviewKind')
BEGIN
    ALTER TABLE dbo.CommCommentAttachments ADD CONSTRAINT CK_CommCommentAttachments_PreviewKind
        CHECK (PreviewKind IN ('Image','Pdf','Text','Office','Archive','None'));
    PRINT 'ADDED CK_CommCommentAttachments_PreviewKind';
END
ELSE PRINT 'CK_CommCommentAttachments_PreviewKind EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommCommentAttachments_SizeBytes')
BEGIN
    ALTER TABLE dbo.CommCommentAttachments ADD CONSTRAINT CK_CommCommentAttachments_SizeBytes
        CHECK (SizeBytes >= 0);
    PRINT 'ADDED CK_CommCommentAttachments_SizeBytes';
END
ELSE PRINT 'CK_CommCommentAttachments_SizeBytes EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 6. CommReactions — module 6. Key set is FROZEN, not free-text emoji: an open unicode range makes "how many
--    people flagged a concern" unanswerable (see CommReactionKeys).
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommReactions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CommReactions
    (
        Id            BIGINT        IDENTITY(1,1) NOT NULL,
        CompanyID     INT           NOT NULL,
        CommentId     BIGINT        NOT NULL,
        ThreadId      BIGINT        NOT NULL,
        EmployeeId    INT           NOT NULL,
        ReactionKey   NVARCHAR(40)  NOT NULL,
        CreatedBy     INT           NULL,
        CreatedAt     DATETIME2     NULL,
        updatedBy     INT           NULL,
        UpdatedAt     DATETIME2     NULL,
        CONSTRAINT PK_CommReactions PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_CommReactions_Comment FOREIGN KEY (CommentId)
            REFERENCES dbo.CommComments (Id)
    );
    PRINT 'CREATED dbo.CommReactions';
END
ELSE PRINT 'dbo.CommReactions EXISTS';
GO

-- One reaction key per person per comment. Enforced by the DATABASE so a double-click cannot produce two "like"
-- rows and a count of 2 from one person.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommReactions_Unique' AND object_id = OBJECT_ID('dbo.CommReactions'))
BEGIN
    CREATE UNIQUE INDEX UX_CommReactions_Unique ON dbo.CommReactions (CommentId, EmployeeId, ReactionKey);
    PRINT 'ADDED UX_CommReactions_Unique';
END
ELSE PRINT 'UX_CommReactions_Unique EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommReactions_Key')
BEGIN
    ALTER TABLE dbo.CommReactions ADD CONSTRAINT CK_CommReactions_Key
        CHECK (ReactionKey IN ('like','celebrate','insightful','thanks','question','concern'));
    PRINT 'ADDED CK_CommReactions_Key';
END
ELSE PRINT 'CK_CommReactions_Key EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 7. CommParticipants — followers, watchers, participants, owners (modules 4, 5). One table for all four:
--    they differ only in what notification they earn (CommParticipantRole.NotifiedPerActivity).
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommParticipants', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CommParticipants
    (
        Id           BIGINT        IDENTITY(1,1) NOT NULL,
        CompanyID    INT           NOT NULL,
        ThreadId     BIGINT        NOT NULL,
        EntityType   NVARCHAR(60)  NOT NULL,
        EntityId     INT           NOT NULL,
        EmployeeId   INT           NOT NULL,
        [Role]       NVARCHAR(40)  NOT NULL,
        [Source]     NVARCHAR(40)  NOT NULL,
        MutedAt      DATETIME2     NULL,
        RemovedAt    DATETIME2     NULL,
        RemovedBy    INT           NULL,
        CreatedBy    INT           NULL,
        CreatedAt    DATETIME2     NULL,
        updatedBy    INT           NULL,
        UpdatedAt    DATETIME2     NULL,
        CONSTRAINT PK_CommParticipants PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_CommParticipants_Thread FOREIGN KEY (ThreadId)
            REFERENCES dbo.CommThreads (Id)
    );
    PRINT 'CREATED dbo.CommParticipants';
END
ELSE PRINT 'dbo.CommParticipants EXISTS';
GO

-- One LIVE participation per (thread, employee). Filtered on RemovedAt so somebody who left and rejoined has one
-- live row and a full history of both.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommParticipants_Live' AND object_id = OBJECT_ID('dbo.CommParticipants'))
BEGIN
    CREATE UNIQUE INDEX UX_CommParticipants_Live ON dbo.CommParticipants (ThreadId, EmployeeId)
        WHERE RemovedAt IS NULL;
    PRINT 'ADDED UX_CommParticipants_Live';
END
ELSE PRINT 'UX_CommParticipants_Live EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommParticipants_Employee' AND object_id = OBJECT_ID('dbo.CommParticipants'))
BEGIN
    CREATE INDEX IX_CommParticipants_Employee ON dbo.CommParticipants (CompanyID, EmployeeId, [Role])
        WHERE RemovedAt IS NULL;
    PRINT 'ADDED IX_CommParticipants_Employee';
END
ELSE PRINT 'IX_CommParticipants_Employee EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommParticipants_Role')
BEGIN
    ALTER TABLE dbo.CommParticipants ADD CONSTRAINT CK_CommParticipants_Role
        CHECK ([Role] IN ('Owner','Participant','Follower','Watcher'));
    PRINT 'ADDED CK_CommParticipants_Role';
END
ELSE PRINT 'CK_CommParticipants_Role EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommParticipants_Source')
BEGIN
    ALTER TABLE dbo.CommParticipants ADD CONSTRAINT CK_CommParticipants_Source
        CHECK ([Source] IN ('Author','Mention','Explicit','Auto'));
    PRINT 'ADDED CK_CommParticipants_Source';
END
ELSE PRINT 'CK_CommParticipants_Source EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 8. CommMentions — one @mention TOKEN as authored (modules 3, 15).
--
-- Label and ResolvedRecipientCount are FROZEN AT AUTHORING TIME: a department renamed or grown next month was
-- not the department that was mentioned, and re-resolving on read would rewrite history.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommMentions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CommMentions
    (
        Id                       BIGINT         IDENTITY(1,1) NOT NULL,
        CompanyID                INT            NOT NULL,
        ThreadId                 BIGINT         NOT NULL,
        CommentId                BIGINT         NOT NULL,
        EntityType               NVARCHAR(60)   NOT NULL,
        EntityId                 INT            NOT NULL,
        TargetKind               NVARCHAR(40)   NOT NULL,
        TargetId                 INT            NULL,
        TargetKey                NVARCHAR(120)  NULL,
        LabelAr                  NVARCHAR(200)  NULL,
        LabelEn                  NVARCHAR(200)  NULL,
        ResolvedRecipientCount   INT            NOT NULL CONSTRAINT DF_CommMentions_ResolvedRecipientCount DEFAULT (0),
        MentionedByEmployeeId    INT            NOT NULL,
        CreatedBy                INT            NULL,
        CreatedAt                DATETIME2      NULL,
        updatedBy                INT            NULL,
        UpdatedAt                DATETIME2      NULL,
        CONSTRAINT PK_CommMentions PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_CommMentions_Comment FOREIGN KEY (CommentId)
            REFERENCES dbo.CommComments (Id),
        CONSTRAINT FK_CommMentions_Thread FOREIGN KEY (ThreadId)
            REFERENCES dbo.CommThreads (Id)
    );
    PRINT 'CREATED dbo.CommMentions';
END
ELSE PRINT 'dbo.CommMentions EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommMentions_Comment' AND object_id = OBJECT_ID('dbo.CommMentions'))
BEGIN
    CREATE INDEX IX_CommMentions_Comment ON dbo.CommMentions (CommentId);
    PRINT 'ADDED IX_CommMentions_Comment';
END
ELSE PRINT 'IX_CommMentions_Comment EXISTS';
GO

-- The same target must not be mentioned twice in one comment. The service dedupes the union of parsed and
-- explicit mentions; this index is what makes that a guarantee rather than best-effort.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommMentions_PerComment' AND object_id = OBJECT_ID('dbo.CommMentions'))
BEGIN
    CREATE UNIQUE INDEX UX_CommMentions_PerComment
        ON dbo.CommMentions (CommentId, TargetKind, TargetId, TargetKey);
    PRINT 'ADDED UX_CommMentions_PerComment';
END
ELSE PRINT 'UX_CommMentions_PerComment EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommMentions_TargetKind')
BEGIN
    -- 'Role' is DECLARED here so the vocabulary is frozen now, even though no principal source resolves it yet
    -- (ADR-032 §5). The service refuses it at write time; the constraint keeps the spelling.
    ALTER TABLE dbo.CommMentions ADD CONSTRAINT CK_CommMentions_TargetKind
        CHECK (TargetKind IN ('Employee','Team','Department','Role'));
    PRINT 'ADDED CK_CommMentions_TargetKind';
END
ELSE PRINT 'CK_CommMentions_TargetKind EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 9. CommMentionRecipients — one RESOLVED recipient of one mention. This is what makes "where have I been
--    mentioned" a single indexed read, including mentions received through a team or department.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommMentionRecipients', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CommMentionRecipients
    (
        Id           BIGINT        IDENTITY(1,1) NOT NULL,
        CompanyID    INT           NOT NULL,
        MentionId    BIGINT        NOT NULL,
        CommentId    BIGINT        NOT NULL,
        ThreadId     BIGINT        NOT NULL,
        EmployeeId   INT           NOT NULL,
        ViaKind      NVARCHAR(40)  NOT NULL,
        ReadAt       DATETIME2     NULL,
        CreatedBy    INT           NULL,
        CreatedAt    DATETIME2     NULL,
        updatedBy    INT           NULL,
        UpdatedAt    DATETIME2     NULL,
        CONSTRAINT PK_CommMentionRecipients PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_CommMentionRecipients_Mention FOREIGN KEY (MentionId)
            REFERENCES dbo.CommMentions (Id),
        CONSTRAINT FK_CommMentionRecipients_Comment FOREIGN KEY (CommentId)
            REFERENCES dbo.CommComments (Id)
    );
    PRINT 'CREATED dbo.CommMentionRecipients';
END
ELSE PRINT 'dbo.CommMentionRecipients EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommMentionRecipients_Employee' AND object_id = OBJECT_ID('dbo.CommMentionRecipients'))
BEGIN
    CREATE INDEX IX_CommMentionRecipients_Employee ON dbo.CommMentionRecipients (CompanyID, EmployeeId, Id);
    PRINT 'ADDED IX_CommMentionRecipients_Employee';
END
ELSE PRINT 'IX_CommMentionRecipients_Employee EXISTS';
GO

-- A group mention and a direct mention in the same comment must resolve to ONE row per employee, or the mention
-- badge counts them twice.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommMentionRecipients_Unique' AND object_id = OBJECT_ID('dbo.CommMentionRecipients'))
BEGIN
    CREATE UNIQUE INDEX UX_CommMentionRecipients_Unique ON dbo.CommMentionRecipients (MentionId, EmployeeId);
    PRINT 'ADDED UX_CommMentionRecipients_Unique';
END
ELSE PRINT 'UX_CommMentionRecipients_Unique EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommMentionRecipients_Unread' AND object_id = OBJECT_ID('dbo.CommMentionRecipients'))
BEGIN
    CREATE INDEX IX_CommMentionRecipients_Unread ON dbo.CommMentionRecipients (CompanyID, EmployeeId, ReadAt)
        WHERE ReadAt IS NULL;
    PRINT 'ADDED IX_CommMentionRecipients_Unread';
END
ELSE PRINT 'IX_CommMentionRecipients_Unread EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommMentionRecipients_ViaKind')
BEGIN
    ALTER TABLE dbo.CommMentionRecipients ADD CONSTRAINT CK_CommMentionRecipients_ViaKind
        CHECK (ViaKind IN ('Employee','Team','Department','Role'));
    PRINT 'ADDED CK_CommMentionRecipients_ViaKind';
END
ELSE PRINT 'CK_CommMentionRecipients_ViaKind EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 10. CommReadReceipts — module 14. LastReadCommentId is a WATERMARK, not a count: "unread" is then a single
--     comparison, and it only ever moves forward.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommReadReceipts', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CommReadReceipts
    (
        Id                  BIGINT      IDENTITY(1,1) NOT NULL,
        CompanyID           INT         NOT NULL,
        ThreadId            BIGINT      NOT NULL,
        EmployeeId          INT         NOT NULL,
        LastReadCommentId   BIGINT      NULL,
        LastReadAt          DATETIME2   NULL,
        CreatedBy           INT         NULL,
        CreatedAt           DATETIME2   NULL,
        updatedBy           INT         NULL,
        UpdatedAt           DATETIME2   NULL,
        CONSTRAINT PK_CommReadReceipts PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_CommReadReceipts_Thread FOREIGN KEY (ThreadId)
            REFERENCES dbo.CommThreads (Id)
    );
    PRINT 'CREATED dbo.CommReadReceipts';
END
ELSE PRINT 'dbo.CommReadReceipts EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommReadReceipts_Unique' AND object_id = OBJECT_ID('dbo.CommReadReceipts'))
BEGIN
    CREATE UNIQUE INDEX UX_CommReadReceipts_Unique ON dbo.CommReadReceipts (ThreadId, EmployeeId);
    PRINT 'ADDED UX_CommReadReceipts_Unique';
END
ELSE PRINT 'UX_CommReadReceipts_Unique EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 11. CommNotifications — the notification DECISION (module 19). Rendered text is stored in both languages so a
--     notification sent last year does not re-render with today's template wording.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommNotifications', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CommNotifications
    (
        Id                    BIGINT         IDENTITY(1,1) NOT NULL,
        CompanyID             INT            NOT NULL,
        BranchID              INT            NULL,
        TemplateKey           NVARCHAR(80)   NOT NULL,
        Category              NVARCHAR(40)   NOT NULL,
        Priority              NVARCHAR(40)   NOT NULL,
        LegacyType            NVARCHAR(40)   NOT NULL,
        EntityType            NVARCHAR(60)   NOT NULL,
        EntityId              INT            NOT NULL,
        ThreadId              BIGINT         NULL,
        CommentId             BIGINT         NULL,
        MentionId             BIGINT         NULL,
        RecipientEmployeeId   INT            NOT NULL,
        ActorEmployeeId       INT            NULL,
        TitleAr               NVARCHAR(300)  NOT NULL,
        TitleEn               NVARCHAR(300)  NOT NULL,
        BodyAr                NVARCHAR(MAX)  NOT NULL,
        BodyEn                NVARCHAR(MAX)  NOT NULL,
        Url                   NVARCHAR(400)  NULL,
        DedupKey              NVARCHAR(160)  NOT NULL,
        ReadAt                DATETIME2      NULL,
        CreatedBy             INT            NULL,
        CreatedAt             DATETIME2      NULL,
        updatedBy             INT            NULL,
        UpdatedAt             DATETIME2      NULL,
        CONSTRAINT PK_CommNotifications PRIMARY KEY CLUSTERED (Id)

        -- NO foreign key to CommThreads/CommComments, deliberately: a notification is a durable record of
        -- something a person was told, and it must survive independently of whether the thread it referred to
        -- was later removed by a data cleanup. The ids are nullable references, not enforced relationships.
    );
    PRINT 'CREATED dbo.CommNotifications';
END
ELSE PRINT 'dbo.CommNotifications EXISTS';
GO

-- TRUE idempotency, per company — not the legacy NotificationService's unread-noise guard. A retried transaction
-- produces zero extra rows whether or not the first one has been read.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommNotifications_DedupKey' AND object_id = OBJECT_ID('dbo.CommNotifications'))
BEGIN
    CREATE UNIQUE INDEX UX_CommNotifications_DedupKey ON dbo.CommNotifications (CompanyID, DedupKey);
    PRINT 'ADDED UX_CommNotifications_DedupKey';
END
ELSE PRINT 'UX_CommNotifications_DedupKey EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommNotifications_Inbox' AND object_id = OBJECT_ID('dbo.CommNotifications'))
BEGIN
    CREATE INDEX IX_CommNotifications_Inbox
        ON dbo.CommNotifications (CompanyID, RecipientEmployeeId, ReadAt, Id);
    PRINT 'ADDED IX_CommNotifications_Inbox';
END
ELSE PRINT 'IX_CommNotifications_Inbox EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommNotifications_Category')
BEGIN
    ALTER TABLE dbo.CommNotifications ADD CONSTRAINT CK_CommNotifications_Category
        CHECK (Category IN ('Mentions','Comments','Reactions','Participation','Moderation'));
    PRINT 'ADDED CK_CommNotifications_Category';
END
ELSE PRINT 'CK_CommNotifications_Category EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 12. CommNotificationDeliveries — one delivery ATTEMPT per (notification, channel).
--
-- The two-table split is the point: a notification delivered in-app but failing over email has no single status,
-- and the retry that fixes email must not resend the in-app one. Same per-consumer split ADR-003 chose for
-- BusinessEventDispatch.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommNotificationDeliveries', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CommNotificationDeliveries
    (
        Id                  BIGINT         IDENTITY(1,1) NOT NULL,
        CompanyID           INT            NOT NULL,
        NotificationId      BIGINT         NOT NULL,
        Channel             NVARCHAR(40)   NOT NULL,
        [Status]            NVARCHAR(40)   NOT NULL,
        Attempts            INT            NOT NULL CONSTRAINT DF_CommNotificationDeliveries_Attempts DEFAULT (0),
        ClaimedAt           DATETIME2      NULL,
        SentAt              DATETIME2      NULL,
        [Error]             NVARCHAR(1000) NULL,
        ExternalReference   NVARCHAR(200)  NULL,
        DedupKey            NVARCHAR(160)  NOT NULL,
        CreatedBy           INT            NULL,
        CreatedAt           DATETIME2      NULL,
        updatedBy           INT            NULL,
        UpdatedAt           DATETIME2      NULL,
        CONSTRAINT PK_CommNotificationDeliveries PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_CommNotificationDeliveries_Notification FOREIGN KEY (NotificationId)
            REFERENCES dbo.CommNotifications (Id)
    );
    PRINT 'CREATED dbo.CommNotificationDeliveries';
END
ELSE PRINT 'dbo.CommNotificationDeliveries EXISTS';
GO

-- One delivery per (notification, channel). Without it a retried DECIDE step creates a second Pending row and
-- the recipient gets two emails.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommNotificationDeliveries_Channel' AND object_id = OBJECT_ID('dbo.CommNotificationDeliveries'))
BEGIN
    CREATE UNIQUE INDEX UX_CommNotificationDeliveries_Channel
        ON dbo.CommNotificationDeliveries (NotificationId, Channel);
    PRINT 'ADDED UX_CommNotificationDeliveries_Channel';
END
ELSE PRINT 'UX_CommNotificationDeliveries_Channel EXISTS';
GO

-- THE CLAIM INDEX. Column order matches the claim predicate (Status first, then the counters and clocks it
-- compares), and it is FILTERED to rows that can still be worked — a Sent or Skipped row leaves the index
-- entirely, so the queue index stays small no matter how much is delivered. Exactly the construction
-- comm_outbox_slice_003.sql chose for IX_CommMessages_Dispatch.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommNotificationDeliveries_Dispatch' AND object_id = OBJECT_ID('dbo.CommNotificationDeliveries'))
BEGIN
    CREATE INDEX IX_CommNotificationDeliveries_Dispatch
        ON dbo.CommNotificationDeliveries ([Status], Attempts, ClaimedAt, UpdatedAt)
        INCLUDE (CompanyID, NotificationId, Channel)
        WHERE [Status] <> 'Sent' AND [Status] <> 'Skipped';
    PRINT 'ADDED IX_CommNotificationDeliveries_Dispatch';
END
ELSE PRINT 'IX_CommNotificationDeliveries_Dispatch EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommNotificationDeliveries_Channel')
BEGIN
    -- 'Email', 'Push' and 'WhatsApp' are DECLARED here even though no adapter is registered. A delivery row for
    -- an unwired channel is parked as 'Skipped' with a reason by the dispatcher — never left Pending forever.
    ALTER TABLE dbo.CommNotificationDeliveries ADD CONSTRAINT CK_CommNotificationDeliveries_Channel
        CHECK (Channel IN ('InApp','Email','Push','WhatsApp'));
    PRINT 'ADDED CK_CommNotificationDeliveries_Channel';
END
ELSE PRINT 'CK_CommNotificationDeliveries_Channel EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommNotificationDeliveries_Status')
BEGIN
    ALTER TABLE dbo.CommNotificationDeliveries ADD CONSTRAINT CK_CommNotificationDeliveries_Status
        CHECK ([Status] IN ('Pending','Claimed','Sent','Failed','Skipped'));
    PRINT 'ADDED CK_CommNotificationDeliveries_Status';
END
ELSE PRINT 'CK_CommNotificationDeliveries_Status EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommNotificationDeliveries_Attempts')
BEGIN
    ALTER TABLE dbo.CommNotificationDeliveries ADD CONSTRAINT CK_CommNotificationDeliveries_Attempts
        CHECK (Attempts >= 0);
    PRINT 'ADDED CK_CommNotificationDeliveries_Attempts';
END
ELSE PRINT 'CK_CommNotificationDeliveries_Attempts EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 13. CommNotificationPreferences — module 22.
--
-- ABSENCE OF A ROW IS NOT "OFF": it means "use the deployment default". That is why this table is NOT seeded —
-- seeding it would freeze today's default for every existing employee and stop a future operator's change from
-- reaching anyone who never opened the preferences screen.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommNotificationPreferences', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CommNotificationPreferences
    (
        Id           BIGINT        IDENTITY(1,1) NOT NULL,
        CompanyID    INT           NOT NULL,
        EmployeeId   INT           NOT NULL,
        Category     NVARCHAR(40)  NOT NULL,
        Channel      NVARCHAR(40)  NOT NULL,
        Mode         NVARCHAR(40)  NOT NULL,
        CreatedBy    INT           NULL,
        CreatedAt    DATETIME2     NULL,
        updatedBy    INT           NULL,
        UpdatedAt    DATETIME2     NULL,
        CONSTRAINT PK_CommNotificationPreferences PRIMARY KEY CLUSTERED (Id)
    );
    PRINT 'CREATED dbo.CommNotificationPreferences';
END
ELSE PRINT 'dbo.CommNotificationPreferences EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommNotificationPreferences_Unique' AND object_id = OBJECT_ID('dbo.CommNotificationPreferences'))
BEGIN
    CREATE UNIQUE INDEX UX_CommNotificationPreferences_Unique
        ON dbo.CommNotificationPreferences (CompanyID, EmployeeId, Category, Channel);
    PRINT 'ADDED UX_CommNotificationPreferences_Unique';
END
ELSE PRINT 'UX_CommNotificationPreferences_Unique EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommNotificationPreferences_Mode')
BEGIN
    ALTER TABLE dbo.CommNotificationPreferences ADD CONSTRAINT CK_CommNotificationPreferences_Mode
        CHECK (Mode IN ('Immediate','Digest','Off'));
    PRINT 'ADDED CK_CommNotificationPreferences_Mode';
END
ELSE PRINT 'CK_CommNotificationPreferences_Mode EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommNotificationPreferences_Channel')
BEGIN
    ALTER TABLE dbo.CommNotificationPreferences ADD CONSTRAINT CK_CommNotificationPreferences_Channel
        CHECK (Channel IN ('InApp','Email','Push','WhatsApp'));
    PRINT 'ADDED CK_CommNotificationPreferences_Channel';
END
ELSE PRINT 'CK_CommNotificationPreferences_Channel EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommNotificationPreferences_Category')
BEGIN
    ALTER TABLE dbo.CommNotificationPreferences ADD CONSTRAINT CK_CommNotificationPreferences_Category
        CHECK (Category IN ('Mentions','Comments','Reactions','Participation','Moderation'));
    PRINT 'ADDED CK_CommNotificationPreferences_Category';
END
ELSE PRINT 'CK_CommNotificationPreferences_Category EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 14. CommAuditEntries — the APPEND-ONLY audit trail (module 31) and the durable home of every communication
--     event (module 26).
--
-- It exists rather than reusing BusinessEvents for three reasons (ADR-035):
--   AVAILABILITY  BusinessEvents is a kernel slice, and RecordAsync throws when it is missing. Audit must not
--                 depend on a schema this platform does not deploy.
--   COMPLETENESS  The kernel's event log is filtered on READ by visibility and module permission — correct for a
--                 record timeline, wrong for an audit trail.
--   GRANULARITY   Reactions, participation and permission grants are deliberately not bridged to the kernel, but
--                 they ARE audited. Audit is the superset.
--
-- NO DeletedAt COLUMN, and no service exposes a mutation. This is the one append-only table in the platform.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommAuditEntries', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CommAuditEntries
    (
        Id                  BIGINT           IDENTITY(1,1) NOT NULL,
        CompanyID           INT              NOT NULL,
        BranchID            INT              NULL,
        [Action]            NVARCHAR(40)     NOT NULL,
        EventType           NVARCHAR(40)     NULL,
        EntityType          NVARCHAR(60)     NOT NULL,
        EntityId            INT              NOT NULL,
        ThreadId            BIGINT           NULL,
        CommentId           BIGINT           NULL,
        MentionId           BIGINT           NULL,
        NotificationId      BIGINT           NULL,
        ActorEmployeeId     INT              NULL,
        SubjectEmployeeId   INT              NULL,
        Visibility          NVARCHAR(40)     NULL,
        DetailJson          NVARCHAR(MAX)    NULL,
        CorrelationId       UNIQUEIDENTIFIER NULL,
        DedupKey            NVARCHAR(160)    NULL,
        CreatedAt           DATETIME2        NOT NULL,
        CONSTRAINT PK_CommAuditEntries PRIMARY KEY CLUSTERED (Id)

        -- NO foreign keys at all. An audit row must outlive everything it describes: a thread, a comment and a
        -- notification may each be removed by a future data-retention job, and the record that somebody deleted
        -- a comment is precisely the row that must survive that.
    );
    PRINT 'CREATED dbo.CommAuditEntries';
END
ELSE PRINT 'dbo.CommAuditEntries EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommAuditEntries_Entity' AND object_id = OBJECT_ID('dbo.CommAuditEntries'))
BEGIN
    CREATE INDEX IX_CommAuditEntries_Entity ON dbo.CommAuditEntries (CompanyID, EntityType, EntityId, Id);
    PRINT 'ADDED IX_CommAuditEntries_Entity';
END
ELSE PRINT 'IX_CommAuditEntries_Entity EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommAuditEntries_Thread' AND object_id = OBJECT_ID('dbo.CommAuditEntries'))
BEGIN
    CREATE INDEX IX_CommAuditEntries_Thread ON dbo.CommAuditEntries (CompanyID, ThreadId, Id);
    PRINT 'ADDED IX_CommAuditEntries_Thread';
END
ELSE PRINT 'IX_CommAuditEntries_Thread EXISTS';
GO

-- "Everything this person did" — the investigation read.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommAuditEntries_Actor' AND object_id = OBJECT_ID('dbo.CommAuditEntries'))
BEGIN
    CREATE INDEX IX_CommAuditEntries_Actor ON dbo.CommAuditEntries (CompanyID, ActorEmployeeId, Id);
    PRINT 'ADDED IX_CommAuditEntries_Actor';
END
ELSE PRINT 'IX_CommAuditEntries_Actor EXISTS';
GO

-- Joins a comm audit row to the kernel event row from the same request. Both take CorrelationId from
-- BusinessContext, so an investigation can follow one operation across both logs.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommAuditEntries_Correlation' AND object_id = OBJECT_ID('dbo.CommAuditEntries'))
BEGIN
    CREATE INDEX IX_CommAuditEntries_Correlation ON dbo.CommAuditEntries (CorrelationId)
        WHERE CorrelationId IS NOT NULL;
    PRINT 'ADDED IX_CommAuditEntries_Correlation';
END
ELSE PRINT 'IX_CommAuditEntries_Correlation EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommAuditEntries_DedupKey' AND object_id = OBJECT_ID('dbo.CommAuditEntries'))
BEGIN
    CREATE UNIQUE INDEX UX_CommAuditEntries_DedupKey ON dbo.CommAuditEntries (CompanyID, DedupKey)
        WHERE DedupKey IS NOT NULL;
    PRINT 'ADDED UX_CommAuditEntries_DedupKey';
END
ELSE PRINT 'UX_CommAuditEntries_DedupKey EXISTS';
GO

-- The Action vocabulary is NOT constrained by a CHECK, and that is a considered exception to this script's own
-- rule. Reason: the audit log is append-only and permanent, and a CHECK constraint on a 23-value vocabulary that
-- will grow becomes a deployment ordering hazard — a new action in code would fail every write until the next SQL
-- slice ran. CommAuditWriter validates against CommAuditActions and REFUSES an unknown value before it reaches
-- the column, so the vocabulary is enforced where it can fail safely. Recorded in CPS-001 §9.
GO

-- =============================================================================================
-- NOT DONE HERE, deliberately:
--   * NO seeding of CommNotificationPreferences — absence of a row means "use the deployment default"
--     (see table 13). Seeding would freeze today's default for every existing employee.
--   * NO backfill from DocComments into CommComments. The parallel team's DocComment feature stays exactly as it
--     is, still serving _DocTimeline on SalesInvoiceDetail / PurchaseInvoiceDetail / QuotationDetails. Migrating
--     its rows would be a change to a working production feature owned by another work stream, which this phase
--     explicitly excludes. A migration path is proposed in CPS-001 §10 for whoever owns that decision.
--   * NO change to Notifications, CommMessages, BusinessEvents or any other existing table. This platform writes
--     only the fourteen tables above.
--   * NO hosted service, NO Program.cs change, NO UI. AddCommunicationPlatform exists but is not called.
-- =============================================================================================
PRINT 'communication_platform_slice_001.sql COMPLETE';
GO
