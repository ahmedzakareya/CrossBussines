-- CrossBuy AI platform tables (Phase 0.3).
-- Migrations are broken (Trap #1) — run this manually:
--   sqlcmd -S localhost -d CrossBuyDB2 -E -i "<repo>\crossbuy_ai\sql\ai_tables.sql"
-- PascalCase column names = future EF entity property names (EF maps by name).
-- Idempotent: guarded by IF NOT EXISTS so re-running is safe.

SET NOCOUNT ON;

-- Log of every AI interaction (audit + continuous improvement).
IF OBJECT_ID(N'dbo.AiInteractions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiInteractions (
        ID            BIGINT IDENTITY PRIMARY KEY,
        CompanyID     INT NOT NULL,
        UserId        NVARCHAR(450) NULL,        -- AspNetUsers.Id
        Feature       NVARCHAR(60)  NOT NULL,    -- Assistant/Classify/Extract/Forecast/Anomaly...
        InputText     NVARCHAR(MAX) NULL,
        OutputJson    NVARCHAR(MAX) NULL,        -- structured result
        Model         NVARCHAR(60)  NULL,        -- claude-opus-4-8 ...
        InputTokens   INT NULL,
        OutputTokens  INT NULL,
        Confidence    DECIMAL(5,4)  NULL,
        UserDecision  NVARCHAR(20)  NULL,        -- Accepted/Edited/Rejected
        CreatedAt     DATETIME2 NOT NULL
    );
    PRINT 'Created dbo.AiInteractions';
END
ELSE PRINT 'dbo.AiInteractions already exists';

-- Suggestions awaiting human review (unified queue).
IF OBJECT_ID(N'dbo.AiSuggestions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiSuggestions (
        ID            BIGINT IDENTITY PRIMARY KEY,
        CompanyID     INT NOT NULL,
        Feature       NVARCHAR(60) NOT NULL,
        TargetType    NVARCHAR(40) NULL,         -- JournalEntry/PurchaseInvoice/LeaveRequest...
        TargetId      INT NULL,                  -- set once turned into a real Draft
        PayloadJson   NVARCHAR(MAX) NOT NULL,
        Status        NVARCHAR(20) NOT NULL CONSTRAINT DF_AiSuggestions_Status DEFAULT 'Pending', -- Pending/Applied/Dismissed
        CreatedBy     NVARCHAR(450) NULL,
        CreatedAt     DATETIME2 NOT NULL
    );
    PRINT 'Created dbo.AiSuggestions';
END
ELSE PRINT 'dbo.AiSuggestions already exists';

-- RAG knowledge chunks (text + metadata; embeddings live in Qdrant — NOT pgvector).
IF OBJECT_ID(N'dbo.AiKnowledgeChunks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiKnowledgeChunks (
        ID          BIGINT IDENTITY PRIMARY KEY,
        Source      NVARCHAR(200) NOT NULL,      -- UserManual/Policy/COA...
        Lang        CHAR(2) NOT NULL,            -- ar/en
        Content     NVARCHAR(MAX) NOT NULL,
        UpdatedAt   DATETIME2 NOT NULL
    );
    PRINT 'Created dbo.AiKnowledgeChunks';
END
ELSE PRINT 'dbo.AiKnowledgeChunks already exists';
GO
