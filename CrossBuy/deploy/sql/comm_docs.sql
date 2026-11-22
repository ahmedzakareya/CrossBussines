-- Communication — shared documents inside a conversation (owner, 2026-11-22). Idempotent.
--   ChatDocs        : one document of one conversation; every member reads and edits it. Version counts the saves.
--   ChatDocVersions : every saved version (who, when, the whole text): the history, and the base a concurrent save is
--                     merged against.
IF OBJECT_ID('dbo.ChatDocs') IS NULL
CREATE TABLE dbo.ChatDocs (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatDocs PRIMARY KEY,
    CompanyId int NOT NULL,
    ConversationId int NOT NULL,
    Title nvarchar(150) NOT NULL,
    Content nvarchar(max) NOT NULL,
    Version int NOT NULL,
    MessageId int NULL,
    FilePath nvarchar(400) NULL,
    CreatedByEmployeeId int NOT NULL,
    CreatedAt datetime2 NOT NULL,
    UpdatedByEmployeeId int NOT NULL,
    UpdatedAt datetime2 NOT NULL,
    DeletedAt datetime2 NULL);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ChatDocs_Conversation')
CREATE INDEX IX_ChatDocs_Conversation ON dbo.ChatDocs (CompanyId, ConversationId);

IF OBJECT_ID('dbo.ChatDocVersions') IS NULL
CREATE TABLE dbo.ChatDocVersions (
    DocId int NOT NULL,
    Version int NOT NULL,
    Content nvarchar(max) NOT NULL,
    Title nvarchar(150) NOT NULL,
    EmployeeId int NOT NULL,
    CreatedAt datetime2 NOT NULL,
    Merged bit NOT NULL CONSTRAINT DF_ChatDocVersions_Merged DEFAULT 0,
    CONSTRAINT PK_ChatDocVersions PRIMARY KEY (DocId, Version));
