-- ==========================================================================================
-- CRM C4 — the unified inbox: every conversation with a customer, on every channel, in one place.
--   CrmChannels                a connected channel: a website chat, a WhatsApp Business number, the company mailbox
--   CrmConversations           one conversation with one outside party on one channel, linked to the CRM record
--   CrmConversationMessages    what was said, both ways, plus internal notes the customer never sees
--
-- Additive only (three new tables, no change to an existing table). Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.CrmContacts','U') IS NULL OR OBJECT_ID('dbo.CrmConsents','U') IS NULL
    THROW 51966, 'Run crm_c1_identity.sql first (and the CRM base tables must exist).', 1;

IF OBJECT_ID('dbo.CrmChannels','U') IS NULL
BEGIN
    CREATE TABLE dbo.CrmChannels (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        Kind                   nvarchar(10)   NOT NULL,   -- WebChat | WhatsApp | Email
        Name                   nvarchar(100)  NOT NULL,
        PublicKey              nvarchar(32)   NOT NULL,   -- the only thing an outside request names
        ConfigJson             nvarchar(2000) NULL,       -- non-secret settings (greeting, colour, phone number id, templates)
        SecretProtected        nvarchar(max)  NULL,       -- tokens, encrypted with the application's data-protection key
        VerifyToken            nvarchar(64)   NULL,       -- the WhatsApp webhook handshake
        IsActive               bit            NOT NULL,
        CreatedAt              datetime2      NOT NULL,
        CONSTRAINT PK_CrmChannels PRIMARY KEY (ID),
        CONSTRAINT CK_CrmChannels_Kind CHECK (Kind IN (N'WebChat', N'WhatsApp', N'Email'))
    );
    CREATE UNIQUE INDEX UX_CrmChannels_Key ON dbo.CrmChannels (PublicKey);
    CREATE INDEX IX_CrmChannels_Company ON dbo.CrmChannels (CompanyId, Kind);
END;

IF OBJECT_ID('dbo.CrmConversations','U') IS NULL
BEGIN
    CREATE TABLE dbo.CrmConversations (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        ChannelId              int            NOT NULL,
        ExternalKey            nvarchar(150)  NOT NULL,   -- the party on that channel: a phone, an e-mail, a chat visitor (hashed)
        DisplayName            nvarchar(150)  NULL,
        Phone                  nvarchar(40)   NULL,
        Email                  nvarchar(150)  NULL,
        ContactId              int            NULL,
        LeadId                 int            NULL,
        AccountId              int            NULL,
        OpportunityId          int            NULL,
        TicketId               int            NULL,
        OwnerEmployeeId        int            NULL,
        Status                 nvarchar(8)    NOT NULL,   -- Open | Pending | Closed
        Linked                 bit            NOT NULL,   -- matched to the CRM inside the company's own scope
        UnreadCount            int            NOT NULL CONSTRAINT DF_CrmConversations_Unread DEFAULT (0),
        LastInboundAt          datetime2      NULL,       -- WhatsApp's 24-hour reply window runs from here
        LastMessageAt          datetime2      NOT NULL,
        LastPreview            nvarchar(200)  NULL,
        CreatedAt              datetime2      NOT NULL,
        CONSTRAINT PK_CrmConversations PRIMARY KEY (ID),
        CONSTRAINT FK_CrmConversations_Channel FOREIGN KEY (ChannelId) REFERENCES dbo.CrmChannels (ID),
        CONSTRAINT CK_CrmConversations_Status CHECK (Status IN (N'Open', N'Pending', N'Closed'))
    );
    -- one conversation per party per channel: a returning customer continues the same thread
    CREATE UNIQUE INDEX UX_CrmConversations_Party ON dbo.CrmConversations (ChannelId, ExternalKey);
    CREATE INDEX IX_CrmConversations_Inbox ON dbo.CrmConversations (CompanyId, Status, LastMessageAt);
END;

IF OBJECT_ID('dbo.CrmConversationMessages','U') IS NULL
BEGIN
    CREATE TABLE dbo.CrmConversationMessages (
        ID                     bigint IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        ConversationId         int            NOT NULL,
        Direction              nvarchar(6)    NOT NULL,   -- In | Out | Note | System
        Body                   nvarchar(4000) NOT NULL,
        SentByEmployeeId       int            NULL,
        ExternalId             nvarchar(128)  NULL,       -- the provider's message id (WhatsApp wamid)
        Delivery               nvarchar(10)   NOT NULL,   -- Received | Queued | Sent | Delivered | Read | Failed | Internal
        Error                  nvarchar(300)  NULL,
        At                     datetime2      NOT NULL,
        CONSTRAINT PK_CrmConversationMessages PRIMARY KEY (ID),
        CONSTRAINT FK_CrmConversationMessages_Conv FOREIGN KEY (ConversationId) REFERENCES dbo.CrmConversations (ID),
        CONSTRAINT CK_CrmConversationMessages_Dir CHECK (Direction IN (N'In', N'Out', N'Note', N'System')),
        CONSTRAINT CK_CrmConversationMessages_Delivery CHECK (Delivery IN (N'Received', N'Queued', N'Sent', N'Delivered', N'Read', N'Failed', N'Internal')),
        -- a note and a system line never leave the company; only staff write outbound
        CONSTRAINT CK_CrmConversationMessages_Note CHECK (Direction NOT IN (N'Note', N'System') OR Delivery = N'Internal'),
        CONSTRAINT CK_CrmConversationMessages_Author CHECK (Direction NOT IN (N'Out', N'Note') OR SentByEmployeeId IS NOT NULL)
    );
    CREATE INDEX IX_CrmConversationMessages_Conv ON dbo.CrmConversationMessages (ConversationId, ID);
    -- a provider retrying the same webhook must not write the message twice
    CREATE UNIQUE INDEX UX_CrmConversationMessages_External ON dbo.CrmConversationMessages (ConversationId, ExternalId) WHERE ExternalId IS NOT NULL;
END;

IF (SELECT COUNT(*) FROM (VALUES (N'CrmChannels'), (N'CrmConversations'), (N'CrmConversationMessages')) t(n) WHERE OBJECT_ID(N'dbo.' + t.n, 'U') IS NOT NULL) <> 3
    THROW 51967, 'crm_c4_inbox: verification failed.', 1;

COMMIT TRANSACTION;
