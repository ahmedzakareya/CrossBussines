-- ============================================================================
-- CRM 3-7b(ii) — automation rules (trigger → action). Idempotent. No GL impact.
-- ============================================================================

IF OBJECT_ID('dbo.CrmAutomationRules','U') IS NULL
CREATE TABLE dbo.CrmAutomationRules (
    ID           int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID    int NOT NULL,
    Name         nvarchar(200) NOT NULL,
    TriggerType  nvarchar(30) NOT NULL,                 -- LeadCreated | OpportunityStageChanged
    StageFilter  nvarchar(100) NULL,
    ActionType   nvarchar(20) NOT NULL,                 -- Notify | CreateActivity
    ActivityType nvarchar(20) NULL,
    Subject      nvarchar(300) NULL,
    DueInDays    int NOT NULL CONSTRAINT DF_CrmAuto_Due DEFAULT(0),
    NotifyTitle  nvarchar(200) NULL,
    NotifyBody   nvarchar(1000) NULL,
    IsActive     bit NOT NULL CONSTRAINT DF_CrmAuto_Active DEFAULT(1),
    SortOrder    int NOT NULL CONSTRAINT DF_CrmAuto_Sort DEFAULT(0)
);
GO
IF OBJECT_ID('dbo.CrmAutomationRules','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CrmAuto_Trigger' AND object_id=OBJECT_ID('dbo.CrmAutomationRules'))
    CREATE INDEX IX_CrmAuto_Trigger ON dbo.CrmAutomationRules(CompanyID, TriggerType, IsActive);
GO
