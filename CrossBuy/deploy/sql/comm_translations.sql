-- Communication — message translation (owner, 2026-11-19). Idempotent.
--   ChatTranslations : a message's text in the other language (ar | en), made once on request by the local model and
--                      shown to every reader who asks for it. One row per message and language.
IF OBJECT_ID('dbo.ChatTranslations') IS NULL
CREATE TABLE dbo.ChatTranslations (
    MessageId int NOT NULL,
    Lang nvarchar(8) NOT NULL,
    CompanyId int NOT NULL,
    Text nvarchar(max) NOT NULL,
    RequestedByEmployeeId int NOT NULL,
    CreatedAt datetime2 NOT NULL,
    CONSTRAINT PK_ChatTranslations PRIMARY KEY (MessageId, Lang));
