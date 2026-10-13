-- ============================================================
-- CrossBuy CRM schema updates (Module 3, items 3-1 .. 3-7).
-- Run ONCE on the production CrossBuyDB2. Every script is idempotent
-- (IF NOT EXISTS / COL_LENGTH guards) so re-running is safe.
--   sqlcmd -S localhost -d CrossBuyDB2 -E -C -b -i crm_schema_updates_3x.sql
-- ============================================================


-- ============================ cb_crm_3_1.sql ============================
-- =====================================================================
-- CrossBuy CRM 3-1: record owner on CRM entities + CRM user roles (idempotent).
-- =====================================================================
SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET NOCOUNT ON;

DECLARE @adds TABLE(id int IDENTITY, tbl sysname, col sysname, def nvarchar(100));
INSERT INTO @adds(tbl,col,def) VALUES
 ('Leads','OwnerEmployeeId','int NULL'),
 ('Opportunities','OwnerEmployeeId','int NULL'),
 ('Activities','OwnerEmployeeId','int NULL'),
 ('Campaigns','OwnerEmployeeId','int NULL');

DECLARE @i int=1, @max int=(SELECT MAX(id) FROM @adds), @t sysname, @c sysname, @d nvarchar(100), @sql nvarchar(max);
WHILE @i<=@max
BEGIN
    SELECT @t=tbl,@c=col,@d=def FROM @adds WHERE id=@i;
    IF COL_LENGTH(@t,@c) IS NULL BEGIN SET @sql='ALTER TABLE ['+@t+'] ADD ['+@c+'] '+@d+';'; EXEC sp_executesql @sql; PRINT 'ADD '+@t+'.'+@c; END
    ELSE PRINT 'SKIP '+@t+'.'+@c;
    SET @i=@i+1;
END

IF OBJECT_ID('CrmUserRoles','U') IS NULL
BEGIN
    CREATE TABLE CrmUserRoles(
        ID         int IDENTITY(1,1) PRIMARY KEY,
        CompanyID  int NOT NULL,
        EmployeeId int NOT NULL,
        Role       nvarchar(40) NOT NULL,     -- SalesRep | SalesManager | Marketing | CrmViewer
        CreatedAt  datetime2 NULL
    );
    CREATE INDEX IX_CrmUserRoles_Emp ON CrmUserRoles(CompanyID, EmployeeId);
    PRINT 'created CrmUserRoles';
END ELSE PRINT 'CrmUserRoles exists';
GO

-- ============================ cb_crm_3_2.sql ============================
-- =====================================================================
-- CrossBuy CRM 3-2: Account (360 party) + Contact + re-point Lead/Opp; migrate existing. Idempotent.
-- NOTE: CRM account table is CrmAccounts (Accounts = GL chart of accounts).
-- =====================================================================
SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET NOCOUNT ON;

IF OBJECT_ID('CrmAccounts','U') IS NULL
BEGIN
    CREATE TABLE CrmAccounts(
        ID              int IDENTITY(1,1) PRIMARY KEY,
        CompanyID       int NOT NULL,
        Name            nvarchar(200) NOT NULL,
        NameEn          nvarchar(200) NULL,
        Industry        nvarchar(100) NULL,
        Phone           nvarchar(50) NULL,
        Email           nvarchar(150) NULL,
        Website         nvarchar(150) NULL,
        Address         nvarchar(400) NULL,
        Source          nvarchar(80) NULL,
        Segment         nvarchar(80) NULL,
        OwnerEmployeeId int NULL,
        CustomerId      int NULL,            -- link to financial Customer (set on Won/convert); one Account <-> one Customer
        IsActive        bit NOT NULL CONSTRAINT DF_CrmAccounts_Active DEFAULT(1),
        Notes           nvarchar(max) NULL,
        CreatedBy       nvarchar(450) NULL,
        CreatedAt       datetime2 NULL
    );
    CREATE INDEX IX_CrmAccounts_Cust ON CrmAccounts(CompanyID, CustomerId);
    PRINT 'created CrmAccounts';
END ELSE PRINT 'CrmAccounts exists';

IF OBJECT_ID('CrmContacts','U') IS NULL
BEGIN
    CREATE TABLE CrmContacts(
        ID              int IDENTITY(1,1) PRIMARY KEY,
        CompanyID       int NOT NULL,
        AccountId       int NOT NULL,
        Name            nvarchar(200) NOT NULL,
        Title           nvarchar(120) NULL,
        Phone           nvarchar(50) NULL,
        Email           nvarchar(150) NULL,
        IsPrimary       bit NOT NULL CONSTRAINT DF_CrmContacts_Primary DEFAULT(0),
        OwnerEmployeeId int NULL,
        Notes           nvarchar(max) NULL,
        CreatedAt       datetime2 NULL
    );
    CREATE INDEX IX_CrmContacts_Acc ON CrmContacts(CompanyID, AccountId);
    PRINT 'created CrmContacts';
END ELSE PRINT 'CrmContacts exists';

IF COL_LENGTH('Leads','AccountId') IS NULL BEGIN ALTER TABLE Leads ADD AccountId int NULL; PRINT 'ADD Leads.AccountId'; END ELSE PRINT 'SKIP Leads.AccountId';
IF COL_LENGTH('Opportunities','AccountId') IS NULL BEGIN ALTER TABLE Opportunities ADD AccountId int NULL; PRINT 'ADD Opportunities.AccountId'; END ELSE PRINT 'SKIP Opportunities.AccountId';

-- ---- migration: one CrmAccount per existing financial Customer (linked), then backfill Lead/Opp.AccountId ----
INSERT INTO CrmAccounts(CompanyID, Name, NameEn, Phone, Email, Segment, CustomerId, IsActive, CreatedAt)
SELECT c.CompanyID, c.Name, c.NameEn, c.Phone, c.Email, c.Segment, c.ID, 1, SYSUTCDATETIME()
FROM Customers c
WHERE NOT EXISTS (SELECT 1 FROM CrmAccounts a WHERE a.CustomerId = c.ID);
PRINT 'CrmAccounts migrated from Customers: ' + CAST(@@ROWCOUNT AS varchar);

UPDATE o SET o.AccountId = a.ID
FROM Opportunities o JOIN CrmAccounts a ON a.CustomerId = o.CustomerId
WHERE o.AccountId IS NULL AND o.CustomerId IS NOT NULL;
PRINT 'Opportunities re-pointed to AccountId: ' + CAST(@@ROWCOUNT AS varchar);

UPDATE l SET l.AccountId = a.ID
FROM Leads l JOIN CrmAccounts a ON a.CustomerId = l.CustomerId
WHERE l.AccountId IS NULL AND l.CustomerId IS NOT NULL;
PRINT 'Leads re-pointed to AccountId: ' + CAST(@@ROWCOUNT AS varchar);
GO

-- ============================ cb_crm_3_3a.sql ============================
-- =====================================================================
-- CrossBuy CRM 3-3a: configurable pipelines + stages; opportunity pipeline/stage/win-loss. Idempotent.
-- (Seed of the default pipeline + stages + backfill is done via /api/dev/crm-seed-pipeline — Arabic-safe.)
-- =====================================================================
SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET NOCOUNT ON;

IF OBJECT_ID('CrmPipelines','U') IS NULL
BEGIN
    CREATE TABLE CrmPipelines(
        ID        int IDENTITY(1,1) PRIMARY KEY,
        CompanyID int NOT NULL,
        Name      nvarchar(120) NOT NULL,
        NameEn    nvarchar(120) NULL,
        IsDefault bit NOT NULL CONSTRAINT DF_CrmPipelines_Def DEFAULT(0),
        IsActive  bit NOT NULL CONSTRAINT DF_CrmPipelines_Act DEFAULT(1),
        CreatedAt datetime2 NULL
    );
    PRINT 'created CrmPipelines';
END ELSE PRINT 'CrmPipelines exists';

IF OBJECT_ID('CrmPipelineStages','U') IS NULL
BEGIN
    CREATE TABLE CrmPipelineStages(
        ID          int IDENTITY(1,1) PRIMARY KEY,
        CompanyID   int NOT NULL,
        PipelineId  int NOT NULL,
        Name        nvarchar(80) NOT NULL,   -- code (UI localizes): Prospecting/Qualification/...
        NameEn      nvarchar(80) NULL,
        Sort        int NOT NULL DEFAULT(0),
        Probability int NOT NULL DEFAULT(0),
        IsWon       bit NOT NULL CONSTRAINT DF_CrmStage_Won DEFAULT(0),
        IsLost      bit NOT NULL CONSTRAINT DF_CrmStage_Lost DEFAULT(0),
        CreatedAt   datetime2 NULL
    );
    CREATE INDEX IX_CrmPipelineStages_Pl ON CrmPipelineStages(CompanyID, PipelineId, Sort);
    PRINT 'created CrmPipelineStages';
END ELSE PRINT 'CrmPipelineStages exists';

IF COL_LENGTH('Opportunities','PipelineId') IS NULL BEGIN ALTER TABLE Opportunities ADD PipelineId int NULL; PRINT 'ADD Opportunities.PipelineId'; END ELSE PRINT 'SKIP PipelineId';
IF COL_LENGTH('Opportunities','StageId') IS NULL BEGIN ALTER TABLE Opportunities ADD StageId int NULL; PRINT 'ADD Opportunities.StageId'; END ELSE PRINT 'SKIP StageId';
IF COL_LENGTH('Opportunities','WinLossReason') IS NULL BEGIN ALTER TABLE Opportunities ADD WinLossReason nvarchar(300) NULL; PRINT 'ADD Opportunities.WinLossReason'; END ELSE PRINT 'SKIP WinLossReason';
GO

-- ============================ cb_crm_3_3b.sql ============================
SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET NOCOUNT ON;
IF OBJECT_ID('OpportunityProducts','U') IS NULL
BEGIN
    CREATE TABLE OpportunityProducts(
        ID              int IDENTITY(1,1) PRIMARY KEY,
        CompanyID       int NOT NULL,
        OpportunityId   int NOT NULL,
        ItemId          int NULL,
        ItemDescription nvarchar(300) NULL,
        Qty             decimal(19,4) NOT NULL DEFAULT(1),
        UnitPrice       decimal(19,4) NOT NULL DEFAULT(0),
        DiscountPercent decimal(19,4) NOT NULL DEFAULT(0),
        LineTotal       decimal(19,4) NOT NULL DEFAULT(0)
    );
    CREATE INDEX IX_OpportunityProducts_Opp ON OpportunityProducts(CompanyID, OpportunityId);
    PRINT 'created OpportunityProducts';
END ELSE PRINT 'OpportunityProducts exists';
GO

-- ============================ cb_crm_3_4.sql ============================
SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET NOCOUNT ON;

-- CRM 3-4: polymorphic activity timeline + reminders.
IF COL_LENGTH('Activities','EntityType') IS NULL ALTER TABLE Activities ADD EntityType nvarchar(20) NULL;
IF COL_LENGTH('Activities','EntityId')   IS NULL ALTER TABLE Activities ADD EntityId int NULL;
IF COL_LENGTH('Activities','ReminderAt') IS NULL ALTER TABLE Activities ADD ReminderAt datetime2 NULL;
IF COL_LENGTH('Activities','Reminded')   IS NULL ALTER TABLE Activities ADD Reminded bit NOT NULL CONSTRAINT DF_Activities_Reminded DEFAULT(0);
GO

-- Backfill the new polymorphic columns from the legacy typed FKs (Opportunity > Lead > Customer priority).
UPDATE Activities SET EntityType='Opportunity', EntityId=OpportunityId WHERE EntityType IS NULL AND OpportunityId IS NOT NULL;
UPDATE Activities SET EntityType='Lead',        EntityId=LeadId        WHERE EntityType IS NULL AND LeadId        IS NOT NULL;
UPDATE Activities SET EntityType='Customer',    EntityId=CustomerId    WHERE EntityType IS NULL AND CustomerId    IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Activities_Entity')
    CREATE INDEX IX_Activities_Entity ON Activities(CompanyID, EntityType, EntityId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Activities_Reminder')
    CREATE INDEX IX_Activities_Reminder ON Activities(CompanyID, Reminded, ReminderAt) WHERE ReminderAt IS NOT NULL;
GO
PRINT 'cb_crm_3_4 done';
GO

-- ============================ cb_crm_3_5.sql ============================
SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET NOCOUNT ON;

-- CRM 3-5: marketing — campaign members + reusable marketing lists.
IF OBJECT_ID('CampaignMembers','U') IS NULL
BEGIN
    CREATE TABLE CampaignMembers(
        ID          int IDENTITY(1,1) PRIMARY KEY,
        CompanyID   int NOT NULL,
        CampaignId  int NOT NULL,
        EntityType  nvarchar(20) NOT NULL,
        EntityId    int NOT NULL,
        MemberName  nvarchar(200) NULL,
        Status      nvarchar(20) NOT NULL CONSTRAINT DF_CampMem_Status DEFAULT('Targeted'),
        RespondedAt datetime2 NULL,
        Notes       nvarchar(1000) NULL,
        CreatedAt   datetime2 NULL
    );
    CREATE INDEX IX_CampaignMembers_Campaign ON CampaignMembers(CompanyID, CampaignId);
    -- a given entity appears at most once per campaign
    CREATE UNIQUE INDEX UX_CampaignMembers ON CampaignMembers(CampaignId, EntityType, EntityId);
END

IF OBJECT_ID('CrmMarketingLists','U') IS NULL
BEGIN
    CREATE TABLE CrmMarketingLists(
        ID          int IDENTITY(1,1) PRIMARY KEY,
        CompanyID   int NOT NULL,
        Name        nvarchar(200) NOT NULL,
        NameEn      nvarchar(200) NULL,
        Description nvarchar(1000) NULL,
        IsActive    bit NOT NULL CONSTRAINT DF_MktList_Active DEFAULT(1),
        OwnerEmployeeId int NULL,
        CreatedBy   nvarchar(256) NULL,
        CreatedAt   datetime2 NULL
    );
    CREATE INDEX IX_CrmMarketingLists_Company ON CrmMarketingLists(CompanyID);
END

IF OBJECT_ID('CrmListMembers','U') IS NULL
BEGIN
    CREATE TABLE CrmListMembers(
        ID          int IDENTITY(1,1) PRIMARY KEY,
        CompanyID   int NOT NULL,
        ListId      int NOT NULL,
        EntityType  nvarchar(20) NOT NULL,
        EntityId    int NOT NULL,
        MemberName  nvarchar(200) NULL,
        CreatedAt   datetime2 NULL
    );
    CREATE INDEX IX_CrmListMembers_List ON CrmListMembers(CompanyID, ListId);
    CREATE UNIQUE INDEX UX_CrmListMembers ON CrmListMembers(ListId, EntityType, EntityId);
END
GO
PRINT 'cb_crm_3_5 done';
GO

-- ============================ cb_crm_3_6.sql ============================
SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET NOCOUNT ON;

-- CRM 3-6: customer-service tickets + SLA policies.
IF OBJECT_ID('CrmSlaPolicies','U') IS NULL
BEGIN
    CREATE TABLE CrmSlaPolicies(
        ID                int IDENTITY(1,1) PRIMARY KEY,
        CompanyID         int NOT NULL,
        Name              nvarchar(150) NOT NULL,
        Priority          nvarchar(20) NOT NULL CONSTRAINT DF_Sla_Priority DEFAULT('Normal'), -- Low|Normal|High|Urgent
        FirstResponseMins int NOT NULL CONSTRAINT DF_Sla_FR DEFAULT(240),
        ResolutionMins    int NOT NULL CONSTRAINT DF_Sla_Res DEFAULT(1440),
        IsActive          bit NOT NULL CONSTRAINT DF_Sla_Active DEFAULT(1),
        CreatedAt         datetime2 NULL
    );
    CREATE INDEX IX_CrmSlaPolicies_Company ON CrmSlaPolicies(CompanyID, Priority, IsActive);
END

IF OBJECT_ID('CrmTickets','U') IS NULL
BEGIN
    CREATE TABLE CrmTickets(
        ID                 int IDENTITY(1,1) PRIMARY KEY,
        CompanyID          int NOT NULL,
        Subject            nvarchar(250) NOT NULL,
        Description        nvarchar(max) NULL,
        AccountId          int NULL,
        ContactId          int NULL,
        CustomerId         int NULL,
        Category           nvarchar(50) NULL,
        Priority           nvarchar(20) NOT NULL CONSTRAINT DF_Tkt_Priority DEFAULT('Normal'),
        Status             nvarchar(20) NOT NULL CONSTRAINT DF_Tkt_Status DEFAULT('New'), -- New|Open|Pending|Resolved|Closed
        OwnerEmployeeId    int NULL,            -- assignee (data-scope owner)
        SlaPolicyId        int NULL,
        FirstResponseDueAt datetime2 NULL,
        ResolutionDueAt    datetime2 NULL,
        FirstRespondedAt   datetime2 NULL,
        ResolvedAt         datetime2 NULL,
        ClosedAt           datetime2 NULL,
        CreatedBy          nvarchar(256) NULL,
        CreatedAt          datetime2 NULL
    );
    CREATE INDEX IX_CrmTickets_Company ON CrmTickets(CompanyID, Status, Priority);
    CREATE INDEX IX_CrmTickets_Account ON CrmTickets(CompanyID, AccountId);
END
GO
PRINT 'cb_crm_3_6 done';
GO

-- ============================ cb_crm_3_7.sql ============================
SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET NOCOUNT ON;

-- CRM 3-7: lead scoring + routing + forecasting settings.
IF COL_LENGTH('Leads','Score') IS NULL ALTER TABLE Leads ADD Score int NOT NULL CONSTRAINT DF_Leads_Score DEFAULT(0);
GO

-- scoring rules: add Points when a lead's Field matches Value by Operator.
IF OBJECT_ID('CrmScoringRules','U') IS NULL
BEGIN
    CREATE TABLE CrmScoringRules(
        ID        int IDENTITY(1,1) PRIMARY KEY,
        CompanyID int NOT NULL,
        Name      nvarchar(150) NOT NULL,
        Field     nvarchar(40) NOT NULL,   -- Source | Segment | Status | EstimatedValue
        Operator  nvarchar(10) NOT NULL CONSTRAINT DF_Score_Op DEFAULT('eq'),  -- eq | contains | gte
        Value     nvarchar(100) NULL,
        Points    int NOT NULL CONSTRAINT DF_Score_Pts DEFAULT(0),
        IsActive  bit NOT NULL CONSTRAINT DF_Score_Active DEFAULT(1),
        CreatedAt datetime2 NULL
    );
    CREATE INDEX IX_CrmScoringRules_Company ON CrmScoringRules(CompanyID, IsActive);
END

-- one settings row per company (routing toggle + score band thresholds).
IF OBJECT_ID('CrmSettings','U') IS NULL
BEGIN
    CREATE TABLE CrmSettings(
        ID             int IDENTITY(1,1) PRIMARY KEY,
        CompanyID      int NOT NULL,
        AutoRouteLeads bit NOT NULL CONSTRAINT DF_CrmSet_Route DEFAULT(0),
        HotScore       int NOT NULL CONSTRAINT DF_CrmSet_Hot  DEFAULT(50),
        WarmScore      int NOT NULL CONSTRAINT DF_CrmSet_Warm DEFAULT(20),
        CreatedAt      datetime2 NULL
    );
    CREATE UNIQUE INDEX UX_CrmSettings_Company ON CrmSettings(CompanyID);
END
GO
PRINT 'cb_crm_3_7 done';
GO
