-- CRM C8 — service: knowledge base, the ticket's public thread (staff ↔ customer, also on the portal) and the
-- satisfaction survey after resolution. Additive only: three new tables, nothing existing is altered.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.CrmKbArticles') IS NULL
CREATE TABLE dbo.CrmKbArticles (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmKbArticles PRIMARY KEY,
    CompanyId int NOT NULL,
    Title nvarchar(200) NOT NULL,
    TitleEn nvarchar(200) NULL,
    Body nvarchar(max) NOT NULL,
    BodyEn nvarchar(max) NULL,
    Category nvarchar(100) NULL,
    Keywords nvarchar(400) NULL,
    IsPublic bit NOT NULL CONSTRAINT DF_CrmKbArticles_Public DEFAULT 0,      -- shown to customers on the portal
    Status varchar(10) NOT NULL CONSTRAINT CK_CrmKbArticles_Status CHECK (Status IN ('Draft', 'Published', 'Archived')),
    Views int NOT NULL CONSTRAINT DF_CrmKbArticles_Views DEFAULT 0,
    Helpful int NOT NULL CONSTRAINT DF_CrmKbArticles_Helpful DEFAULT 0,
    NotHelpful int NOT NULL CONSTRAINT DF_CrmKbArticles_Not DEFAULT 0,
    UsedInTickets int NOT NULL CONSTRAINT DF_CrmKbArticles_Used DEFAULT 0,
    UpdatedByEmployeeId int NULL,
    UpdatedAt datetime2 NOT NULL CONSTRAINT DF_CrmKbArticles_Upd DEFAULT SYSUTCDATETIME(),
    CreatedAt datetime2 NOT NULL CONSTRAINT DF_CrmKbArticles_At DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('dbo.CrmTicketMessages') IS NULL
CREATE TABLE dbo.CrmTicketMessages (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmTicketMessages PRIMARY KEY,
    CompanyId int NOT NULL,
    TicketId int NOT NULL,
    Direction varchar(4) NOT NULL CONSTRAINT CK_CrmTicketMessages_Dir CHECK (Direction IN ('In', 'Out')),   -- In = the customer, Out = our team
    Body nvarchar(4000) NOT NULL,
    ByEmployeeId int NULL,
    ByPortalUserId int NULL,
    ArticleId int NULL,                -- a reply that shared a knowledge-base article
    At datetime2 NOT NULL CONSTRAINT DF_CrmTicketMessages_At DEFAULT SYSUTCDATETIME(),
    -- who wrote it is never ambiguous
    CONSTRAINT CK_CrmTicketMessages_Author CHECK ((Direction = 'Out' AND ByEmployeeId IS NOT NULL AND ByPortalUserId IS NULL) OR (Direction = 'In' AND ByEmployeeId IS NULL))
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CrmTicketMessages_T')
    CREATE INDEX IX_CrmTicketMessages_T ON dbo.CrmTicketMessages (CompanyId, TicketId, ID);

IF OBJECT_ID('dbo.CrmTicketSurveys') IS NULL
CREATE TABLE dbo.CrmTicketSurveys (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmTicketSurveys PRIMARY KEY,
    CompanyId int NOT NULL,
    TicketId int NOT NULL CONSTRAINT UQ_CrmTicketSurveys_T UNIQUE,      -- one survey per ticket
    TokenHash char(64) NOT NULL CONSTRAINT UQ_CrmTicketSurveys_Tok UNIQUE,
    SentAt datetime2 NOT NULL,
    SentTo nvarchar(150) NULL,         -- the address it was e-mailed to, when it was
    Score tinyint NULL CONSTRAINT CK_CrmTicketSurveys_Score CHECK (Score BETWEEN 1 AND 5),
    Comment nvarchar(1000) NULL,
    AnsweredAt datetime2 NULL,
    CONSTRAINT CK_CrmTicketSurveys_Answer CHECK ((Score IS NULL AND AnsweredAt IS NULL) OR (Score IS NOT NULL AND AnsweredAt IS NOT NULL))
);

COMMIT TRANSACTION;
