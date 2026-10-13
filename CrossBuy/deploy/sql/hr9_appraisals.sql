-- ============================================================================
-- HR-9 — Performance appraisal (cycles, templates, criteria, appraisals, lines)
-- Idempotent. No GL impact.
-- ============================================================================

IF OBJECT_ID('dbo.AppraisalCycles','U') IS NULL
CREATE TABLE dbo.AppraisalCycles (
    ID        int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID int NOT NULL,
    Name      nvarchar(200) NOT NULL,
    NameEn    nvarchar(200) NULL,
    Year      int NOT NULL,
    StartDate datetime2(7) NULL,
    EndDate   datetime2(7) NULL,
    Status    nvarchar(10) NOT NULL CONSTRAINT DF_AprCycle_Status DEFAULT('Open'),
    CreatedAt datetime2(7) NULL
);
GO
IF OBJECT_ID('dbo.AppraisalTemplates','U') IS NULL
CREATE TABLE dbo.AppraisalTemplates (
    ID        int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID int NOT NULL,
    Name      nvarchar(200) NOT NULL,
    NameEn    nvarchar(200) NULL,
    IsActive  bit NOT NULL CONSTRAINT DF_AprTpl_Active DEFAULT(1),
    CreatedAt datetime2(7) NULL
);
GO
IF OBJECT_ID('dbo.AppraisalCriteria','U') IS NULL
CREATE TABLE dbo.AppraisalCriteria (
    ID         int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    TemplateId int NOT NULL,
    Name       nvarchar(200) NOT NULL,
    NameEn     nvarchar(200) NULL,
    Weight     decimal(19,4) NOT NULL CONSTRAINT DF_AprCrit_Weight DEFAULT(0),
    MaxScore   decimal(19,4) NOT NULL CONSTRAINT DF_AprCrit_Max DEFAULT(5),
    SortOrder  int NOT NULL CONSTRAINT DF_AprCrit_Sort DEFAULT(0)
);
GO
IF OBJECT_ID('dbo.Appraisals','U') IS NULL
CREATE TABLE dbo.Appraisals (
    ID                int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID         int NOT NULL,
    CycleId           int NOT NULL,
    TemplateId        int NOT NULL,
    EmployeeID        int NOT NULL,
    ManagerEmployeeID int NOT NULL,
    Status            int NOT NULL CONSTRAINT DF_Apr_Status DEFAULT(0),   -- 0 draft / 1 submitted / 2 acknowledged
    TotalScore        decimal(19,4) NOT NULL CONSTRAINT DF_Apr_Score DEFAULT(0),
    ManagerComment    nvarchar(2000) NULL,
    EmployeeComment   nvarchar(2000) NULL,
    SubmittedAt       datetime2(7) NULL,
    AcknowledgedAt    datetime2(7) NULL,
    CreatedAt         datetime2(7) NULL,
    CreatedBy         int NULL
);
CREATE INDEX IX_Appraisals_Cycle ON dbo.Appraisals(CompanyID, CycleId);
CREATE INDEX IX_Appraisals_Emp ON dbo.Appraisals(EmployeeID);
GO
IF OBJECT_ID('dbo.AppraisalLines','U') IS NULL
CREATE TABLE dbo.AppraisalLines (
    ID          int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    AppraisalId int NOT NULL,
    CriterionId int NOT NULL,
    Score       decimal(19,4) NOT NULL CONSTRAINT DF_AprLine_Score DEFAULT(0),
    Note        nvarchar(1000) NULL
);
CREATE INDEX IX_AprLines_Appr ON dbo.AppraisalLines(AppraisalId);
GO
