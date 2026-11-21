-- Communication — incoming webhooks (owner, 2026-11-19). Idempotent.
--   ChatWebhooks     : a group's or channel's incoming address. Another system posts a message to it with the secret
--                      token; the token is shown once and stored only as its SHA-256. A message is posted in the name of
--                      the member who made the hook, marked "via <hook name>", and only while that member is still in
--                      the conversation and active. Revoked hooks stay (who made what, and when it was stopped).
--   ChatWebhookPosts : which message came through which hook (the chat shows "via ...").
IF OBJECT_ID('dbo.ChatWebhooks') IS NULL
CREATE TABLE dbo.ChatWebhooks (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatWebhooks PRIMARY KEY,
    CompanyId int NOT NULL,
    ConversationId int NOT NULL,
    Name nvarchar(60) NOT NULL,
    TokenHash char(64) NOT NULL,
    CreatedByEmployeeId int NOT NULL,
    CreatedAt datetime2 NOT NULL,
    LastUsedAt datetime2 NULL,
    UseCount int NOT NULL CONSTRAINT DF_ChatWebhooks_UseCount DEFAULT 0,
    RevokedAt datetime2 NULL,
    RevokedByEmployeeId int NULL);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ChatWebhooks_TokenHash')
CREATE UNIQUE INDEX UX_ChatWebhooks_TokenHash ON dbo.ChatWebhooks (TokenHash);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ChatWebhooks_Conversation')
CREATE INDEX IX_ChatWebhooks_Conversation ON dbo.ChatWebhooks (CompanyId, ConversationId);

IF OBJECT_ID('dbo.ChatWebhookPosts') IS NULL
CREATE TABLE dbo.ChatWebhookPosts (
    MessageId int NOT NULL CONSTRAINT PK_ChatWebhookPosts PRIMARY KEY,
    WebhookId int NOT NULL,
    CompanyId int NOT NULL,
    CreatedAt datetime2 NOT NULL);
