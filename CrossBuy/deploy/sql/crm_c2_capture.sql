-- ==========================================================================================
-- CRM C2 — lead capture and assignment.
--   CrmTerritories          a sales territory: which leads/accounts it takes (areas, segments, sources), in priority order
--   CrmTerritoryMembers     the reps in a territory; LastAssignedAt drives the round-robin
--   CrmWebForms             a public lead form a company embeds on its own website (one unguessable key each)
--   CrmWebSubmissions       what a visitor sent, stored as received; turned into a lead inside the company's own scope
--
-- Additive only (four new tables, no change to an existing table). Idempotent (OBJECT_ID guards).
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

IF OBJECT_ID('dbo.Leads','U') IS NULL OR OBJECT_ID('dbo.CrmConsents','U') IS NULL
    THROW 51962, 'Run crm_c1_identity.sql first (and the CRM base tables must exist).', 1;

IF OBJECT_ID('dbo.CrmTerritories','U') IS NULL
BEGIN
    CREATE TABLE dbo.CrmTerritories (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        Name                   nvarchar(100)  NOT NULL,
        NameEn                 nvarchar(100)  NULL,
        Areas                  nvarchar(400)  NULL,   -- comma list; empty = any area
        Segments               nvarchar(200)  NULL,   -- comma list; empty = any segment
        Sources                nvarchar(200)  NULL,   -- comma list; empty = any source
        Priority               int            NOT NULL CONSTRAINT DF_CrmTerritories_Priority DEFAULT (100),
        IsActive               bit            NOT NULL CONSTRAINT DF_CrmTerritories_Active DEFAULT (1),
        CreatedAt              datetime2      NOT NULL,
        CONSTRAINT PK_CrmTerritories PRIMARY KEY (ID)
    );
    CREATE INDEX IX_CrmTerritories_Company ON dbo.CrmTerritories (CompanyId, IsActive, Priority);
END;

IF OBJECT_ID('dbo.CrmTerritoryMembers','U') IS NULL
BEGIN
    CREATE TABLE dbo.CrmTerritoryMembers (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        TerritoryId            int            NOT NULL,
        EmployeeId             int            NOT NULL,
        LastAssignedAt         datetime2      NULL,
        CONSTRAINT PK_CrmTerritoryMembers PRIMARY KEY (ID),
        CONSTRAINT FK_CrmTerritoryMembers_Territory FOREIGN KEY (TerritoryId) REFERENCES dbo.CrmTerritories (ID) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX UX_CrmTerritoryMembers ON dbo.CrmTerritoryMembers (TerritoryId, EmployeeId);
END;

IF OBJECT_ID('dbo.CrmWebForms','U') IS NULL
BEGIN
    CREATE TABLE dbo.CrmWebForms (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        PublicKey              nvarchar(32)   NOT NULL,   -- unguessable; the ONLY thing a visitor's request names
        Name                   nvarchar(100)  NOT NULL,
        Title                  nvarchar(150)  NOT NULL,
        Source                 nvarchar(60)   NOT NULL,   -- written on every lead it creates
        Segment                nvarchar(60)   NULL,
        AskCompany             bit            NOT NULL,
        AskEmail               bit            NOT NULL,
        AskArea                bit            NOT NULL,
        AreaOptions            nvarchar(400)  NULL,
        NeedOptions            nvarchar(400)  NULL,
        ConsentText            nvarchar(300)  NOT NULL,
        ThankYou               nvarchar(300)  NOT NULL,
        AllowedSites           nvarchar(400)  NULL,       -- sites allowed to embed it; empty = any
        IsActive               bit            NOT NULL,
        CreatedAt              datetime2      NOT NULL,
        CreatedByEmployeeId    int            NULL,
        CONSTRAINT PK_CrmWebForms PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX UX_CrmWebForms_Key ON dbo.CrmWebForms (PublicKey);
    CREATE INDEX IX_CrmWebForms_Company ON dbo.CrmWebForms (CompanyId);
END;

IF OBJECT_ID('dbo.CrmWebSubmissions','U') IS NULL
BEGIN
    CREATE TABLE dbo.CrmWebSubmissions (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        FormId                 int            NOT NULL,
        ReceivedAt             datetime2      NOT NULL,
        Name                   nvarchar(150)  NOT NULL,
        Company                nvarchar(150)  NULL,
        Phone                  nvarchar(40)   NULL,
        Email                  nvarchar(150)  NULL,
        Area                   nvarchar(60)   NULL,
        Need                   nvarchar(100)  NULL,
        Message                nvarchar(1000) NULL,
        ConsentGiven           bit            NOT NULL,
        ConsentText            nvarchar(300)  NULL,      -- the exact wording the visitor ticked, frozen
        PageUrl                nvarchar(400)  NULL,
        VisitorKey             nvarchar(64)   NULL,      -- a hash, never the raw address
        Status                 nvarchar(10)   NOT NULL,  -- Pending | Converted | Linked | Spam | Failed
        LeadId                 int            NULL,
        AccountId              int            NULL,      -- set when it matched an existing account instead
        OwnerEmployeeId        int            NULL,
        AssignReason           nvarchar(200)  NULL,
        ProcessedAt            datetime2      NULL,
        Error                  nvarchar(300)  NULL,
        CONSTRAINT PK_CrmWebSubmissions PRIMARY KEY (ID),
        CONSTRAINT FK_CrmWebSubmissions_Form FOREIGN KEY (FormId) REFERENCES dbo.CrmWebForms (ID),
        CONSTRAINT CK_CrmWebSubmissions_Status CHECK (Status IN (N'Pending', N'Converted', N'Linked', N'Spam', N'Failed')),
        CONSTRAINT CK_CrmWebSubmissions_Reach CHECK (Phone IS NOT NULL OR Email IS NOT NULL),
        CONSTRAINT CK_CrmWebSubmissions_Done CHECK (Status = N'Pending' OR ProcessedAt IS NOT NULL)
    );
    CREATE INDEX IX_CrmWebSubmissions_Pending ON dbo.CrmWebSubmissions (CompanyId, Status, ReceivedAt);
    CREATE INDEX IX_CrmWebSubmissions_Form ON dbo.CrmWebSubmissions (FormId, ReceivedAt);
END;

IF (SELECT COUNT(*) FROM (VALUES (N'CrmTerritories'), (N'CrmTerritoryMembers'), (N'CrmWebForms'), (N'CrmWebSubmissions')) t(n) WHERE OBJECT_ID(N'dbo.' + t.n, 'U') IS NOT NULL) <> 4
    THROW 51963, 'crm_c2_capture: verification failed.', 1;

COMMIT TRANSACTION;
