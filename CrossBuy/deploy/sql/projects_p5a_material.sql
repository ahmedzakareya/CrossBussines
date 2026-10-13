-- Projects & Contracting — P5-أ (actual project cost: material issue).
-- Adds a dedicated project-execution cost account + the material-issue voucher tables.
-- Inventory is written ONLY via StockService (Cr inventory unchanged → stock_gl intact); Dr = 510104, tagged ProjectId.
-- No new accounting writer. Idempotent. Company 1.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- 1) Project execution cost (510104), postable, under 51 (مصروفات تشغيل) — flows into ProfitabilityAsync (5xxx net debit by project)
IF NOT EXISTS (SELECT 1 FROM Accounts WHERE CompanyID=1 AND Code='510104')
    INSERT INTO Accounts (CompanyID, Code, Name, NameEn, AccountTypeId, ParentId, IsPostable, IsActive, RequireCostCenter, RequireProject, CashFlowCategory, CreatedAt)
    SELECT 1, '510104', N'تكلفة تنفيذ مشاريع', N'Project execution cost',
           (SELECT ID FROM AccountTypes WHERE Code='EXP'),
           (SELECT ID FROM Accounts WHERE CompanyID=1 AND Code='51'),
           1, 1, 0, 0, 'Operating', SYSUTCDATETIME();
GO

-- 2) Project material-issue voucher header (أذن صرف مواد للمشروع)
IF OBJECT_ID('dbo.ProjectMaterialIssues','U') IS NULL
BEGIN
    CREATE TABLE dbo.ProjectMaterialIssues(
        ID          INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CompanyID   INT           NOT NULL,
        ProjectId   INT           NOT NULL,
        IssueNo     INT           NOT NULL,           -- sequence per project
        IssueDate   DATETIME2     NOT NULL,
        WarehouseId INT           NOT NULL,
        Note        NVARCHAR(1000) NULL,
        Status      NVARCHAR(20)  NOT NULL CONSTRAINT DF_ProjectMaterialIssues_Status DEFAULT('Draft'),  -- Draft / Posted
        CreatedAt   DATETIME2     NULL,
        CreatedBy   INT           NULL,
        PostedAt    DATETIME2     NULL,
        PostedBy    INT           NULL
    );
    CREATE INDEX IX_ProjectMaterialIssues_Project ON dbo.ProjectMaterialIssues(CompanyID, ProjectId, IssueNo);
    PRINT 'CREATED ProjectMaterialIssues';
END
ELSE PRINT 'ProjectMaterialIssues EXISTS';
GO

-- 3) Project material-issue line (item/qty + optional BOQ item + cost snapshot on post)
IF OBJECT_ID('dbo.ProjectMaterialIssueLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.ProjectMaterialIssueLines(
        ID              INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        IssueId         INT           NOT NULL,
        ItemId          INT           NOT NULL,
        Qty             DECIMAL(19,4) NOT NULL CONSTRAINT DF_ProjectMaterialIssueLines_Qty DEFAULT(0),
        BoqItemId       INT           NULL,            -- optional link to a BOQ line (for estimate-vs-actual later)
        UnitCost        DECIMAL(19,4) NOT NULL CONSTRAINT DF_ProjectMaterialIssueLines_UC  DEFAULT(0),  -- snapshot on post
        TotalCost       DECIMAL(19,4) NOT NULL CONSTRAINT DF_ProjectMaterialIssueLines_TC  DEFAULT(0),  -- snapshot on post
        StockMovementId INT           NULL,            -- the StockService movement created on post
        CONSTRAINT FK_ProjectMaterialIssueLines_Header FOREIGN KEY (IssueId) REFERENCES dbo.ProjectMaterialIssues(ID)
    );
    CREATE INDEX IX_ProjectMaterialIssueLines_Header ON dbo.ProjectMaterialIssueLines(IssueId);
    PRINT 'CREATED ProjectMaterialIssueLines';
END
ELSE PRINT 'ProjectMaterialIssueLines EXISTS';
GO

SELECT Code, Name, IsPostable FROM Accounts WHERE CompanyID=1 AND Code='510104';
GO
