-- Communication Hub P3: internal Teams-style chat tables. Idempotent, additive. Safe to re-run.
IF OBJECT_ID('Conversations','U') IS NULL
BEGIN
    CREATE TABLE Conversations (
        ID                 INT IDENTITY(1,1) PRIMARY KEY,
        CompanyID          INT           NOT NULL,
        Kind               NVARCHAR(20)  NOT NULL,   -- Direct | Group
        Title              NVARCHAR(160) NULL,
        CreatedByEmployeeId INT          NOT NULL,
        CreatedAt          DATETIME2     NOT NULL,
        LastMessageAt      DATETIME2     NULL,
        LastMessagePreview NVARCHAR(260) NULL
    );
    CREATE INDEX IX_Conv_Company_Kind ON Conversations (CompanyID, Kind);
END
GO

IF OBJECT_ID('ConversationMembers','U') IS NULL
BEGIN
    CREATE TABLE ConversationMembers (
        ID                INT IDENTITY(1,1) PRIMARY KEY,
        ConversationId    INT          NOT NULL,
        EmployeeId        INT          NOT NULL,
        Role              NVARCHAR(20) NOT NULL DEFAULT 'Member',
        LastReadMessageId INT          NOT NULL DEFAULT 0,
        MutedUntil        DATETIME2    NULL,
        JoinedAt          DATETIME2    NOT NULL
    );
    CREATE INDEX IX_ConvMem_Emp ON ConversationMembers (EmployeeId, ConversationId);
    CREATE INDEX IX_ConvMem_Conv ON ConversationMembers (ConversationId, EmployeeId);
END
GO

IF OBJECT_ID('ChatMessages','U') IS NULL
BEGIN
    CREATE TABLE ChatMessages (
        ID               INT IDENTITY(1,1) PRIMARY KEY,
        ConversationId   INT           NOT NULL,
        SenderEmployeeId INT           NOT NULL,
        Body             NVARCHAR(MAX) NULL,
        AttachmentPath   NVARCHAR(400) NULL,
        AttachmentName   NVARCHAR(260) NULL,
        AttachmentType   NVARCHAR(20)  NULL,
        ReplyToId        INT           NULL,
        EditedAt         DATETIME2     NULL,
        DeletedAt        DATETIME2     NULL,
        CreatedAt        DATETIME2     NOT NULL
    );
    CREATE INDEX IX_ChatMsg_Conv ON ChatMessages (ConversationId, ID);
END
GO

IF OBJECT_ID('ChatReactions','U') IS NULL
BEGIN
    CREATE TABLE ChatReactions (
        ID         INT IDENTITY(1,1) PRIMARY KEY,
        MessageId  INT          NOT NULL,
        EmployeeId INT          NOT NULL,
        Emoji      NVARCHAR(16) NOT NULL,
        CreatedAt  DATETIME2    NOT NULL
    );
    CREATE INDEX IX_ChatReact_Msg ON ChatReactions (MessageId);
END
GO
