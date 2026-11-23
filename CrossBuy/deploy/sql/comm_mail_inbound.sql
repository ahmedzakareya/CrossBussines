-- Email phase 2 — mail from OUTSIDE the company comes back in (owner, 2026-11-23: "the real mail as it is, nothing
-- external"). Idempotent; safe to re-run.
--
-- No new mail service: the company's existing mailbox (the account in appsettings Smtp) is read over IMAP. Mail for a
-- person arrives there addressed with a TAG — account+e<employee>k<check>@domain (every employee's address for the
-- outside world) or account+m<message>k<check>@domain (the Reply-To of every message we send) — and is delivered to
-- that person's Inbox, in the same conversation.
--   CommMessages.Direction          'In' for mail received from outside; NULL = written here.
--   CommMessages.FromAddress/FromName   who wrote it, for mail from outside (an employee's is CreatedBy).
--   CommMessages.ExternalMessageId  the Message-ID it arrived with: a message is stored once, and a reply to it
--                                   carries In-Reply-To so the outside client threads it.
--   CommMailInbound                 where the reader stopped in the account (UIDVALIDITY + last UID).
IF COL_LENGTH('dbo.CommMessages', 'Direction') IS NULL
    ALTER TABLE dbo.CommMessages ADD Direction nvarchar(3) NULL;
IF COL_LENGTH('dbo.CommMessages', 'FromAddress') IS NULL
    ALTER TABLE dbo.CommMessages ADD FromAddress nvarchar(260) NULL;
IF COL_LENGTH('dbo.CommMessages', 'FromName') IS NULL
    ALTER TABLE dbo.CommMessages ADD FromName nvarchar(200) NULL;
IF COL_LENGTH('dbo.CommMessages', 'ExternalMessageId') IS NULL
    ALTER TABLE dbo.CommMessages ADD ExternalMessageId nvarchar(300) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommMessages_External')
    CREATE UNIQUE INDEX UX_CommMessages_External ON dbo.CommMessages (CompanyID, ExternalMessageId) WHERE ExternalMessageId IS NOT NULL;
GO
IF OBJECT_ID('dbo.CommMailInbound') IS NULL
CREATE TABLE dbo.CommMailInbound (
    Account nvarchar(260) NOT NULL CONSTRAINT PK_CommMailInbound PRIMARY KEY,
    UidValidity bigint NOT NULL,
    LastUid bigint NOT NULL,
    CheckedAt datetime2 NULL,
    Received int NOT NULL CONSTRAINT DF_CommMailInbound_Received DEFAULT 0,
    LastError nvarchar(400) NULL);
GO
