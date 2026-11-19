-- Communication — speech to text (owner, 2026-11-19). Idempotent.
--   ChatTranscripts : the words of a voice message, transcribed once (on request) and shown to everyone who may
--                     read that message. One row per message; a failure is kept so the same file is not retried
--                     on every click.
IF OBJECT_ID('dbo.ChatTranscripts') IS NULL
CREATE TABLE dbo.ChatTranscripts (
    MessageId int NOT NULL CONSTRAINT PK_ChatTranscripts PRIMARY KEY,
    CompanyId int NOT NULL,
    Status nvarchar(16) NOT NULL,                 -- done | failed | too_long
    Text nvarchar(max) NULL,
    Language nvarchar(8) NULL,
    Model nvarchar(40) NULL,
    AudioSeconds decimal(9,2) NULL,
    WorkSeconds decimal(9,2) NULL,
    Error nvarchar(200) NULL,
    RequestedByEmployeeId int NOT NULL,
    CreatedAt datetime2 NOT NULL);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ChatTranscripts_Company' AND object_id = OBJECT_ID('dbo.ChatTranscripts'))
CREATE INDEX IX_ChatTranscripts_Company ON dbo.ChatTranscripts (CompanyId, CreatedAt);
