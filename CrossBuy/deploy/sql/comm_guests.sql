-- Communication — guests from outside the company (owner, 2026-11-22). Idempotent.
--   ChatGuests     : one invited outsider in one group conversation. The link's secret is shown once and kept only as
--                    its SHA-256. A guest reads the conversation from the moment they were invited (never its history)
--                    and writes in it; nothing else in the system is reachable with the link. Expires; revocable.
--   ChatGuestPosts : which message a guest wrote (posted in the inviter's name, shown with the guest's own name).
IF OBJECT_ID('dbo.ChatGuests') IS NULL
CREATE TABLE dbo.ChatGuests (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatGuests PRIMARY KEY,
    CompanyId int NOT NULL,
    ConversationId int NOT NULL,
    Name nvarchar(80) NOT NULL,
    TokenHash char(64) NOT NULL,
    InvitedByEmployeeId int NOT NULL,
    FirstMessageId int NOT NULL,
    CreatedAt datetime2 NOT NULL,
    ExpiresAt datetime2 NOT NULL,
    LastSeenAt datetime2 NULL,
    RevokedAt datetime2 NULL,
    RevokedByEmployeeId int NULL);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ChatGuests_TokenHash')
CREATE UNIQUE INDEX UX_ChatGuests_TokenHash ON dbo.ChatGuests (TokenHash);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ChatGuests_Conversation')
CREATE INDEX IX_ChatGuests_Conversation ON dbo.ChatGuests (CompanyId, ConversationId);

IF OBJECT_ID('dbo.ChatGuestPosts') IS NULL
CREATE TABLE dbo.ChatGuestPosts (
    MessageId int NOT NULL CONSTRAINT PK_ChatGuestPosts PRIMARY KEY,
    GuestId int NOT NULL,
    CompanyId int NOT NULL,
    CreatedAt datetime2 NOT NULL);
