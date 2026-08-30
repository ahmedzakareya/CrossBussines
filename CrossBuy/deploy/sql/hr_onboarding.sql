/* ============================================================================================================
   HR Product Batch 1 — employee onboarding.

   CANONICAL AUTHORED SLICE (D-38), under CrossBuy/deploy/sql — the authored root apply-sql-slices.ps1 treats as
   canonical. Named hr_onboarding.sql so it sits with the other HR slices this tab authored and matches TAB-2's
   ownership pattern (CrossBuy/deploy/sql/hr*.sql) without needing a workaround name.

   IDEMPOTENT AND ADDITIVE. Every table, index and constraint is created only when absent. Nothing here alters
   or drops an existing object, so the file is safe to re-run.

   NOT EXECUTED BY THIS BATCH. No DDL was applied to CrossBuyDev or anywhere else.

   FOUR TABLES, AND NO DOCUMENT COLUMNS. A required passport is not a column here — it is a PlatformDocument of
   a PlatformDocumentType, owned by the Central Document platform. An onboarding item stores only the TYPE it
   requires (RequiredDocumentTypeID); the platform answers whether a valid document of that type exists. That is
   why there is no PassportNumber, no FilePath and no StorageKey anywhere in this file.

   NO IsOverdue COLUMN either. Overdue is (DueDate in the past) AND (not settled) — a function of two columns
   already here. A stored flag would be a second answer that goes stale at midnight.

   Run with sqlcmd -I (QUOTED_IDENTIFIER ON) so the filtered unique indexes below build.
   ============================================================================================================ */

IF OBJECT_ID('dbo.OnboardingTemplates','U') IS NULL
CREATE TABLE dbo.OnboardingTemplates (
    ID                  int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID           int NOT NULL,
    NameAr              nvarchar(200) NOT NULL,
    NameEn              nvarchar(200) NOT NULL,
    IsActive            bit NOT NULL CONSTRAINT DF_OnbTpl_Active DEFAULT(1),
    IsDefault           bit NOT NULL CONSTRAINT DF_OnbTpl_Default DEFAULT(0),
    DefaultDurationDays int NULL,
    CreatedBy           int NULL,
    CreatedAt           datetime2(7) NULL,
    UpdatedBy           int NULL,
    UpdatedAt           datetime2(7) NULL
);
GO

/*  AT MOST ONE DEFAULT PER COMPANY, enforced here rather than in C#. "Check then insert" in application code
    is a race: two concurrent requests both read zero defaults and both write one. A filtered unique index
    cannot be raced.  */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_OnbTpl_Company_Default' AND object_id=OBJECT_ID('dbo.OnboardingTemplates'))
CREATE UNIQUE INDEX UX_OnbTpl_Company_Default ON dbo.OnboardingTemplates (CompanyID)
    WHERE IsDefault = 1;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_OnbTpl_Company' AND object_id=OBJECT_ID('dbo.OnboardingTemplates'))
CREATE INDEX IX_OnbTpl_Company ON dbo.OnboardingTemplates (CompanyID, IsActive);
GO

IF OBJECT_ID('dbo.OnboardingTemplateItems','U') IS NULL
CREATE TABLE dbo.OnboardingTemplateItems (
    ID                      int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID               int NOT NULL,
    TemplateID              int NOT NULL,
    ItemKey                 nvarchar(100) NOT NULL,
    TitleAr                 nvarchar(300) NOT NULL,
    TitleEn                 nvarchar(300) NOT NULL,
    DescriptionAr           nvarchar(1000) NULL,
    DescriptionEn           nvarchar(1000) NULL,
    Responsibility          nvarchar(30) NOT NULL CONSTRAINT DF_OnbTplItem_Resp DEFAULT('Hr'),
    IsMandatory             bit NOT NULL CONSTRAINT DF_OnbTplItem_Mand DEFAULT(1),
    -- Days from the employee's joining date. A template outlives any one hire, so it cannot hold a date.
    DueOffsetDays           int NULL,
    SortOrder               int NOT NULL CONSTRAINT DF_OnbTplItem_Sort DEFAULT(0),
    RequiredDocumentTypeID  bigint NULL,
    CreatedBy               int NULL,
    CreatedAt               datetime2(7) NULL,
    UpdatedBy               int NULL,
    UpdatedAt               datetime2(7) NULL,
    CONSTRAINT FK_OnbTplItem_Template FOREIGN KEY (TemplateID)
        REFERENCES dbo.OnboardingTemplates(ID) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_OnbTplItem_Template_Key' AND object_id=OBJECT_ID('dbo.OnboardingTemplateItems'))
CREATE UNIQUE INDEX UX_OnbTplItem_Template_Key ON dbo.OnboardingTemplateItems (TemplateID, ItemKey);
GO

/* ------------------------------------------------------------------------------------------------------------
   THE PLAN.
   ------------------------------------------------------------------------------------------------------------ */
IF OBJECT_ID('dbo.EmployeeOnboardings','U') IS NULL
CREATE TABLE dbo.EmployeeOnboardings (
    ID                   int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID            int NOT NULL,
    EmployeeID           int NOT NULL,
    -- Provenance, not a live link: editing a template later must not rewrite an in-flight plan, so there is
    -- deliberately NO cascade and no FK enforcement back onto the template's items.
    TemplateID           int NULL,
    Status               nvarchar(20) NOT NULL CONSTRAINT DF_Onb_Status DEFAULT('NotStarted'),
    StartedAt            datetime2(7) NULL,
    TargetCompletionDate datetime2(7) NULL,
    CompletedAt          datetime2(7) NULL,
    CompletedBy          int NULL,
    Notes                nvarchar(2000) NULL,
    CreatedBy            int NULL,
    CreatedAt            datetime2(7) NULL,
    UpdatedBy            int NULL,
    UpdatedAt            datetime2(7) NULL,
    CONSTRAINT FK_Onb_Employee FOREIGN KEY (EmployeeID) REFERENCES dbo.Employee(ID)
);
GO

/*  ONE PLAN PER EMPLOYEE, enforced by the database. The service checks for an existing plan and returns it
    instead of creating a second — but that check is a read followed by a write, and two concurrent starts
    would both pass it. This index is what makes "start is idempotent" true rather than merely likely.  */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Onb_Company_Employee' AND object_id=OBJECT_ID('dbo.EmployeeOnboardings'))
CREATE UNIQUE INDEX UX_Onb_Company_Employee ON dbo.EmployeeOnboardings (CompanyID, EmployeeID);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Onb_Company_Status' AND object_id=OBJECT_ID('dbo.EmployeeOnboardings'))
CREATE INDEX IX_Onb_Company_Status ON dbo.EmployeeOnboardings (CompanyID, Status)
    INCLUDE (EmployeeID, TargetCompletionDate);
GO

/* ------------------------------------------------------------------------------------------------------------
   THE ITEMS.
   ------------------------------------------------------------------------------------------------------------ */
IF OBJECT_ID('dbo.EmployeeOnboardingItems','U') IS NULL
CREATE TABLE dbo.EmployeeOnboardingItems (
    ID                     int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    -- Repeated from the parent on purpose: "every overdue item in this company" is a real query, and a
    -- join-only boundary would leave it silently unfiltered the first time somebody omitted the Include.
    CompanyID              int NOT NULL,
    OnboardingID           int NOT NULL,
    ItemKey                nvarchar(100) NOT NULL,
    TitleAr                nvarchar(300) NOT NULL,
    TitleEn                nvarchar(300) NOT NULL,
    DescriptionAr          nvarchar(1000) NULL,
    DescriptionEn          nvarchar(1000) NULL,
    Responsibility         nvarchar(30) NOT NULL CONSTRAINT DF_OnbItem_Resp DEFAULT('Hr'),
    ResponsibleEmployeeID  int NULL,
    IsMandatory            bit NOT NULL CONSTRAINT DF_OnbItem_Mand DEFAULT(1),
    DueDate                datetime2(7) NULL,
    SortOrder              int NOT NULL CONSTRAINT DF_OnbItem_Sort DEFAULT(0),
    Status                 nvarchar(20) NOT NULL CONSTRAINT DF_OnbItem_Status DEFAULT('Pending'),
    -- A TYPE, never a document id: a document that expires gets replaced, and an id here would go stale.
    RequiredDocumentTypeID bigint NULL,
    CompletedAt            datetime2(7) NULL,
    CompletedBy            int NULL,
    -- A waiver is three facts or it is not a waiver: who, why, when.
    WaivedAt               datetime2(7) NULL,
    WaivedBy               int NULL,
    WaiverReason           nvarchar(1000) NULL,
    Notes                  nvarchar(2000) NULL,
    CreatedBy              int NULL,
    CreatedAt              datetime2(7) NULL,
    UpdatedBy              int NULL,
    UpdatedAt              datetime2(7) NULL,
    CONSTRAINT FK_OnbItem_Onboarding FOREIGN KEY (OnboardingID)
        REFERENCES dbo.EmployeeOnboardings(ID) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_OnbItem_Onboarding' AND object_id=OBJECT_ID('dbo.EmployeeOnboardingItems'))
CREATE INDEX IX_OnbItem_Onboarding ON dbo.EmployeeOnboardingItems (OnboardingID, SortOrder);
GO

/*  The overdue query: this company's unsettled items with a due date. Filtered so the index carries only the
    rows that can ever be overdue — a settled item never appears in it, which is most of them once onboarding
    is working.  */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_OnbItem_Company_Due_Open' AND object_id=OBJECT_ID('dbo.EmployeeOnboardingItems'))
CREATE INDEX IX_OnbItem_Company_Due_Open ON dbo.EmployeeOnboardingItems (CompanyID, DueDate)
    INCLUDE (OnboardingID, TitleAr, TitleEn, Responsibility, IsMandatory)
    WHERE Status IN ('Pending','InProgress') AND DueDate IS NOT NULL;
GO

/*  DOCUMENT REQUIREMENT LOOKUP. Not a foreign key to PlatformDocumentTypes on purpose: the document platform
    owns that table and its lifecycle, and an FK from an HR table would make an HR deployment depend on the
    document slice having been applied first. The service resolves the type and treats an unknown id the same
    way it treats a missing document — the requirement is simply not satisfied.  */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_OnbItem_DocType' AND object_id=OBJECT_ID('dbo.EmployeeOnboardingItems'))
CREATE INDEX IX_OnbItem_DocType ON dbo.EmployeeOnboardingItems (RequiredDocumentTypeID)
    WHERE RequiredDocumentTypeID IS NOT NULL;
GO
