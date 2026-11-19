-- ==========================================================================================
-- Communication v2 (owner, 2026-11-18: "build every gap, completely"): what the leading team-chat systems have and
-- ours did not.
--   messages   : threads (ThreadRootId), forwarding (ForwardedFromId), pinning inside a conversation (PinnedAt/By)
--   conversations: public channels (IsPublic), a description and a picture
--   ChatSaved          : a person's saved messages
--   ChatScheduled      : a message to send later
--   ChatReminders      : "remind me about this message"
--   ChatPolls/Options/Votes : polls
--   ChatStatus         : a person's own status text and do-not-disturb
--   ChatSettings       : message retention, sensitive-data rule (DLP)
--   ChatLegalHolds     : people or conversations exempt from retention
--   ChatExports        : every compliance export, who and what
--
-- Additive only. Idempotent. Needs comm_chat (the chat tables) and comm_pin.sql.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH('dbo.ChatMessages', 'ThreadRootId') IS NULL ALTER TABLE dbo.ChatMessages ADD ThreadRootId int NULL;
IF COL_LENGTH('dbo.ChatMessages', 'ForwardedFromId') IS NULL ALTER TABLE dbo.ChatMessages ADD ForwardedFromId int NULL;
IF COL_LENGTH('dbo.ChatMessages', 'PinnedAt') IS NULL ALTER TABLE dbo.ChatMessages ADD PinnedAt datetime2 NULL;
IF COL_LENGTH('dbo.ChatMessages', 'PinnedByEmployeeId') IS NULL ALTER TABLE dbo.ChatMessages ADD PinnedByEmployeeId int NULL;

IF COL_LENGTH('dbo.Conversations', 'IsPublic') IS NULL ALTER TABLE dbo.Conversations ADD IsPublic bit NOT NULL CONSTRAINT DF_Conversations_IsPublic DEFAULT (0);
IF COL_LENGTH('dbo.Conversations', 'Description') IS NULL ALTER TABLE dbo.Conversations ADD Description nvarchar(400) NULL;
IF COL_LENGTH('dbo.Conversations', 'AvatarPath') IS NULL ALTER TABLE dbo.Conversations ADD AvatarPath nvarchar(400) NULL;

IF OBJECT_ID('dbo.ChatSaved') IS NULL
CREATE TABLE dbo.ChatSaved (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatSaved PRIMARY KEY,
    CompanyId int NOT NULL, EmployeeId int NOT NULL, MessageId int NOT NULL, SavedAt datetime2 NOT NULL);

IF OBJECT_ID('dbo.ChatScheduled') IS NULL
CREATE TABLE dbo.ChatScheduled (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatScheduled PRIMARY KEY,
    CompanyId int NOT NULL, ConversationId int NOT NULL, EmployeeId int NOT NULL,
    Body nvarchar(4000) NOT NULL, SendAt datetime2 NOT NULL,
    SentAt datetime2 NULL, SentMessageId int NULL, CancelledAt datetime2 NULL, Failure nvarchar(200) NULL,
    CreatedAt datetime2 NOT NULL);

IF OBJECT_ID('dbo.ChatReminders') IS NULL
CREATE TABLE dbo.ChatReminders (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatReminders PRIMARY KEY,
    CompanyId int NOT NULL, EmployeeId int NOT NULL, ConversationId int NOT NULL, MessageId int NOT NULL,
    RemindAt datetime2 NOT NULL, NotifiedAt datetime2 NULL, DoneAt datetime2 NULL, CreatedAt datetime2 NOT NULL);

IF OBJECT_ID('dbo.ChatPolls') IS NULL
CREATE TABLE dbo.ChatPolls (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatPolls PRIMARY KEY,
    CompanyId int NOT NULL, MessageId int NOT NULL, Question nvarchar(300) NOT NULL,
    Multi bit NOT NULL, Anonymous bit NOT NULL, ClosesAt datetime2 NULL, ClosedAt datetime2 NULL, CreatedAt datetime2 NOT NULL);
IF OBJECT_ID('dbo.ChatPollOptions') IS NULL
CREATE TABLE dbo.ChatPollOptions (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatPollOptions PRIMARY KEY,
    PollId int NOT NULL, Text nvarchar(150) NOT NULL, Sort int NOT NULL);
IF OBJECT_ID('dbo.ChatPollVotes') IS NULL
CREATE TABLE dbo.ChatPollVotes (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatPollVotes PRIMARY KEY,
    PollId int NOT NULL, OptionId int NOT NULL, EmployeeId int NOT NULL, At datetime2 NOT NULL);

IF OBJECT_ID('dbo.ChatStatus') IS NULL
CREATE TABLE dbo.ChatStatus (
    EmployeeId int NOT NULL CONSTRAINT PK_ChatStatus PRIMARY KEY,
    CompanyId int NOT NULL, Emoji nvarchar(16) NULL, Text nvarchar(100) NULL, Until datetime2 NULL,
    Dnd bit NOT NULL CONSTRAINT DF_ChatStatus_Dnd DEFAULT (0), DndUntil datetime2 NULL, UpdatedAt datetime2 NOT NULL);

IF OBJECT_ID('dbo.ChatSettings') IS NULL
CREATE TABLE dbo.ChatSettings (
    CompanyId int NOT NULL CONSTRAINT PK_ChatSettings PRIMARY KEY,
    RetentionDays int NOT NULL CONSTRAINT DF_ChatSettings_Retention DEFAULT (0),      -- 0 = kept
    DlpMode varchar(8) NOT NULL CONSTRAINT DF_ChatSettings_Dlp DEFAULT ('off'),      -- off | warn | block
    DlpCards bit NOT NULL CONSTRAINT DF_ChatSettings_DlpCards DEFAULT (1),
    DlpIban bit NOT NULL CONSTRAINT DF_ChatSettings_DlpIban DEFAULT (1),
    DlpNationalId bit NOT NULL CONSTRAINT DF_ChatSettings_DlpId DEFAULT (1),
    DlpWords nvarchar(1000) NULL,
    UpdatedAt datetime2 NULL, UpdatedByEmployeeId int NULL);

IF OBJECT_ID('dbo.ChatLegalHolds') IS NULL
CREATE TABLE dbo.ChatLegalHolds (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatLegalHolds PRIMARY KEY,
    CompanyId int NOT NULL, EmployeeId int NULL, ConversationId int NULL, Reason nvarchar(300) NOT NULL,
    CreatedByEmployeeId int NOT NULL, CreatedAt datetime2 NOT NULL, ReleasedAt datetime2 NULL, ReleasedByEmployeeId int NULL);

IF OBJECT_ID('dbo.ChatExports') IS NULL
CREATE TABLE dbo.ChatExports (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatExports PRIMARY KEY,
    CompanyId int NOT NULL, ByEmployeeId int NOT NULL, Scope nvarchar(200) NOT NULL,
    FromUtc datetime2 NULL, ToUtc datetime2 NULL, Rows int NOT NULL, At datetime2 NOT NULL);

COMMIT TRANSACTION;
GO

-- indexes in their own batch (they name the columns added above)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ChatMessages_Thread' AND object_id = OBJECT_ID('dbo.ChatMessages'))
    CREATE INDEX IX_ChatMessages_Thread ON dbo.ChatMessages (ConversationId, ThreadRootId) WHERE ThreadRootId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ChatMessages_Pinned' AND object_id = OBJECT_ID('dbo.ChatMessages'))
    CREATE INDEX IX_ChatMessages_Pinned ON dbo.ChatMessages (ConversationId) WHERE PinnedAt IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ChatSaved' AND object_id = OBJECT_ID('dbo.ChatSaved'))
    CREATE UNIQUE INDEX UX_ChatSaved ON dbo.ChatSaved (EmployeeId, MessageId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ChatScheduled_Due' AND object_id = OBJECT_ID('dbo.ChatScheduled'))
    CREATE INDEX IX_ChatScheduled_Due ON dbo.ChatScheduled (CompanyId, SendAt) WHERE SentAt IS NULL AND CancelledAt IS NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ChatReminders_Due' AND object_id = OBJECT_ID('dbo.ChatReminders'))
    CREATE INDEX IX_ChatReminders_Due ON dbo.ChatReminders (CompanyId, RemindAt) WHERE NotifiedAt IS NULL AND DoneAt IS NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ChatPolls_Message' AND object_id = OBJECT_ID('dbo.ChatPolls'))
    CREATE UNIQUE INDEX UX_ChatPolls_Message ON dbo.ChatPolls (MessageId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ChatPollVotes' AND object_id = OBJECT_ID('dbo.ChatPollVotes'))
    CREATE UNIQUE INDEX UX_ChatPollVotes ON dbo.ChatPollVotes (PollId, OptionId, EmployeeId);
