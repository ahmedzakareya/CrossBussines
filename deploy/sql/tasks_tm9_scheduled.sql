-- TM-9-أ: scheduled-task criteria on TaskItems. Idempotent, additive, nullable. A normal task is unaffected.
IF COL_LENGTH('TaskItems','IsScheduled')        IS NULL ALTER TABLE TaskItems ADD IsScheduled BIT NOT NULL DEFAULT 0;
GO
IF COL_LENGTH('TaskItems','ExpectedEntityType') IS NULL ALTER TABLE TaskItems ADD ExpectedEntityType NVARCHAR(40) NULL;
IF COL_LENGTH('TaskItems','ExpectedPartyType')  IS NULL ALTER TABLE TaskItems ADD ExpectedPartyType  NVARCHAR(20) NULL;
IF COL_LENGTH('TaskItems','ExpectedPartyId')    IS NULL ALTER TABLE TaskItems ADD ExpectedPartyId    INT NULL;
IF COL_LENGTH('TaskItems','ExpectedFrom')       IS NULL ALTER TABLE TaskItems ADD ExpectedFrom       DATETIME2 NULL;
IF COL_LENGTH('TaskItems','ExpectedTo')         IS NULL ALTER TABLE TaskItems ADD ExpectedTo         DATETIME2 NULL;
IF COL_LENGTH('TaskItems','MatchedAt')          IS NULL ALTER TABLE TaskItems ADD MatchedAt          DATETIME2 NULL;
GO
-- helps the TM-9-ب matcher find still-scheduled tasks quickly (filtered index needs these SET options)
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_TaskItems_Scheduled' AND object_id = OBJECT_ID('TaskItems'))
    CREATE INDEX IX_TaskItems_Scheduled ON TaskItems (CompanyId, IsScheduled, MatchedAt) WHERE IsScheduled = 1;
GO
