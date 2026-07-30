-- =============================================================================
-- CrossBuy — consolidated idempotent schema sync (session 2026-07)
-- Brings a replaced / older database (same name) up to date with the app's model.
-- SAFE to run multiple times. Additive only — never drops or alters existing data.
-- Run:  sqlcmd -S <server> -d <database> -i schema_sync_2026-07.sql
-- IMPORTANT: this file is saved UTF-8 with BOM so the Arabic seed values import correctly.
--           If your tool mangles Arabic, run with:  sqlcmd ... -f 65001
-- =============================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ============================================================================
   1) RECRUITMENT (HR-11): required-document catalog + applications + documents
   ============================================================================ */
IF OBJECT_ID('dbo.RequiredDocumentTypes','U') IS NULL
BEGIN
    CREATE TABLE dbo.RequiredDocumentTypes
    (
        ID          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RequiredDocumentTypes PRIMARY KEY,
        CompanyID   INT           NOT NULL,
        Name        NVARCHAR(200) NOT NULL,
        NameEn      NVARCHAR(200) NULL,
        IsMandatory BIT           NOT NULL CONSTRAINT DF_ReqDoc_Mandatory DEFAULT (1),
        SortOrder   INT           NOT NULL CONSTRAINT DF_ReqDoc_Sort DEFAULT (0),
        IsActive    BIT           NOT NULL CONSTRAINT DF_ReqDoc_Active DEFAULT (1),
        CreatedAt   DATETIME2     NULL
    );
    CREATE INDEX IX_ReqDoc_Company ON dbo.RequiredDocumentTypes (CompanyID);
    PRINT 'CREATED RequiredDocumentTypes';
END ELSE PRINT 'RequiredDocumentTypes EXISTS';
GO

IF OBJECT_ID('dbo.JobApplications','U') IS NULL
BEGIN
    CREATE TABLE dbo.JobApplications
    (
        ID              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_JobApplications PRIMARY KEY,
        CompanyID       INT           NOT NULL,
        ApplicationNo   INT           NOT NULL,
        FirstName       NVARCHAR(150) NOT NULL,
        LastName        NVARCHAR(150) NOT NULL,
        FullName        NVARCHAR(300) NULL,
        FullNameEn      NVARCHAR(300) NULL,
        Email           NVARCHAR(200) NULL,
        PhoneNumber     NVARCHAR(50)  NULL,
        Address         NVARCHAR(500) NULL,
        PhotoPath       NVARCHAR(500) NULL,
        DateOfBirth     DATETIME2     NULL,
        Gender          NVARCHAR(20)  NULL,
        MaritalStatus   NVARCHAR(20)  NULL,
        CountryID       INT           NULL,
        JobTitleID      INT           NULL,
        BranchID        INT           NULL,
        EmpCompanyID    INT           NULL,
        DepartmentID    INT           NULL,
        EmploymentType  NVARCHAR(50)  NULL,
        ExpectedSalary  DECIMAL(19,4) NULL,
        Source          NVARCHAR(100) NULL,
        Status          NVARCHAR(20)  NOT NULL CONSTRAINT DF_JobApp_Status DEFAULT ('New'),
        AppliedAt       DATETIME2     NULL,
        ReviewedAt      DATETIME2     NULL,
        InterviewAt     DATETIME2     NULL,
        DecisionAt      DATETIME2     NULL,
        HiredAt         DATETIME2     NULL,
        DecisionNote    NVARCHAR(1000) NULL,
        HiredEmployeeID INT           NULL,
        Notes           NVARCHAR(1000) NULL,
        CreatedAt       DATETIME2     NULL,
        CreatedBy       INT           NULL,
        UpdatedAt       DATETIME2     NULL,
        UpdatedBy       INT           NULL
    );
    CREATE INDEX IX_JobApp_Company ON dbo.JobApplications (CompanyID, Status);
    PRINT 'CREATED JobApplications';
END ELSE PRINT 'JobApplications EXISTS';
GO
-- ensure the optional photo column exists even if the table pre-dated it
IF OBJECT_ID('dbo.JobApplications','U') IS NOT NULL AND COL_LENGTH('dbo.JobApplications','PhotoPath') IS NULL
    ALTER TABLE dbo.JobApplications ADD PhotoPath NVARCHAR(500) NULL;
GO

IF OBJECT_ID('dbo.ApplicationDocuments','U') IS NULL
BEGIN
    CREATE TABLE dbo.ApplicationDocuments
    (
        ID                      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ApplicationDocuments PRIMARY KEY,
        ApplicationID           INT           NOT NULL,
        RequiredDocumentTypeID  INT           NULL,
        DocType                 NVARCHAR(100) NULL,
        FilePath                NVARCHAR(500) NULL,
        FileName                NVARCHAR(300) NULL,
        DocNumber               NVARCHAR(100) NULL,
        IssueDate               DATETIME2     NULL,
        ExpiryDate              DATETIME2     NULL,
        UploadedAt              DATETIME2     NULL,
        CONSTRAINT FK_AppDoc_Application FOREIGN KEY (ApplicationID) REFERENCES dbo.JobApplications(ID) ON DELETE CASCADE
    );
    CREATE INDEX IX_AppDoc_Application ON dbo.ApplicationDocuments (ApplicationID);
    PRINT 'CREATED ApplicationDocuments';
END ELSE PRINT 'ApplicationDocuments EXISTS';
GO

-- seed a sensible default catalog for company 1 (only if empty)
IF NOT EXISTS (SELECT 1 FROM dbo.RequiredDocumentTypes WHERE CompanyID = 1)
BEGIN
    INSERT INTO dbo.RequiredDocumentTypes (CompanyID, Name, NameEn, IsMandatory, SortOrder, IsActive, CreatedAt) VALUES
        (1, N'صورة بطاقة الرقم القومي', N'National ID copy',        1, 1, 1, SYSUTCDATETIME()),
        (1, N'المؤهل الدراسي',          N'Qualification',           1, 2, 1, SYSUTCDATETIME()),
        (1, N'شهادة الخبرة',            N'Experience certificate',  0, 3, 1, SYSUTCDATETIME()),
        (1, N'صورة شخصية',              N'Personal photo',          1, 4, 1, SYSUTCDATETIME()),
        (1, N'شهادة التأمينات',         N'Insurance statement',     0, 5, 1, SYSUTCDATETIME()),
        (1, N'الموقف من التجنيد',        N'Military status',         0, 6, 1, SYSUTCDATETIME());
    PRINT 'SEEDED RequiredDocumentTypes';
END ELSE PRINT 'RequiredDocumentTypes already has data — seed skipped';
GO

/* ============================================================================
   2) EQUIPMENT DEPRECIATION ALLOCATION (Projects P6-هـ)
   ============================================================================ */
IF OBJECT_ID('dbo.EquipmentDepreciationAllocations','U') IS NULL
BEGIN
    CREATE TABLE dbo.EquipmentDepreciationAllocations
    (
        ID              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EquipmentDepreciationAllocations PRIMARY KEY,
        CompanyID       INT             NOT NULL,
        ProjectId       INT             NOT NULL,
        AllocationNo    INT             NOT NULL,
        FixedAssetId    INT             NOT NULL,
        PeriodDate      DATETIME2       NOT NULL,
        Hours           DECIMAL(19,4)   NULL,
        Rate            DECIMAL(19,4)   NULL,
        Amount          DECIMAL(19,4)   NOT NULL CONSTRAINT DF_EquipDep_Amount DEFAULT (0),
        Note            NVARCHAR(500)   NULL,
        Status          NVARCHAR(20)    NOT NULL CONSTRAINT DF_EquipDep_Status DEFAULT ('Draft'),
        JournalEntryId  INT             NULL,
        CreatedAt       DATETIME2       NULL,
        CreatedBy       INT             NULL,
        PostedAt        DATETIME2       NULL,
        PostedBy        INT             NULL
    );
    CREATE INDEX IX_EquipDep_Project ON dbo.EquipmentDepreciationAllocations (CompanyID, ProjectId);
    PRINT 'CREATED EquipmentDepreciationAllocations';
END ELSE PRINT 'EquipmentDepreciationAllocations EXISTS';
GO

/* ============================================================================
   3) VARIATION ORDERS (Projects P6-د)
   ============================================================================ */
-- flag BOQ items added by a variation order (guarded: only if BoqItems exists)
IF OBJECT_ID('dbo.BoqItems','U') IS NOT NULL AND COL_LENGTH('dbo.BoqItems','VariationOrderId') IS NULL
    ALTER TABLE dbo.BoqItems ADD VariationOrderId INT NULL;
GO

IF OBJECT_ID('dbo.VariationOrders','U') IS NULL
BEGIN
    CREATE TABLE dbo.VariationOrders(
        ID          INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CompanyID   INT           NOT NULL,
        ProjectId   INT           NOT NULL,
        VoNo        INT           NOT NULL,
        Description NVARCHAR(400) NULL,
        Reason      NVARCHAR(1000) NULL,
        Value       DECIMAL(19,4) NOT NULL CONSTRAINT DF_VariationOrders_Value DEFAULT(0),
        Status      NVARCHAR(20)  NOT NULL CONSTRAINT DF_VariationOrders_Status DEFAULT('Draft'),
        CreatedAt   DATETIME2     NULL,
        CreatedBy   INT           NULL,
        ApprovedAt  DATETIME2     NULL,
        ApprovedBy  INT           NULL
    );
    CREATE INDEX IX_VariationOrders_Project ON dbo.VariationOrders(CompanyID, ProjectId, VoNo);
    PRINT 'CREATED VariationOrders';
END ELSE PRINT 'VariationOrders EXISTS';
GO

IF OBJECT_ID('dbo.VariationOrderLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.VariationOrderLines(
        ID              INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        VariationOrderId INT          NOT NULL,
        Kind            NVARCHAR(10)  NOT NULL,
        BoqItemId       INT           NULL,
        Code            NVARCHAR(50)  NULL,
        Description     NVARCHAR(400) NULL,
        DescriptionEn   NVARCHAR(400) NULL,
        Unit            NVARCHAR(50)  NULL,
        Quantity        DECIMAL(19,4) NOT NULL CONSTRAINT DF_VOLines_Qty DEFAULT(0),
        UnitPrice       DECIMAL(19,4) NOT NULL CONSTRAINT DF_VOLines_Price DEFAULT(0),
        MaterialCost    DECIMAL(19,4) NULL,
        LaborCost       DECIMAL(19,4) NULL,
        SubcontractCost DECIMAL(19,4) NULL,
        EquipmentCost   DECIMAL(19,4) NULL,
        OldQuantity     DECIMAL(19,4) NULL,
        OldUnitPrice    DECIMAL(19,4) NULL,
        CONSTRAINT FK_VariationOrderLines_Header FOREIGN KEY (VariationOrderId) REFERENCES dbo.VariationOrders(ID)
    );
    CREATE INDEX IX_VariationOrderLines_Header ON dbo.VariationOrderLines(VariationOrderId);
    PRINT 'CREATED VariationOrderLines';
END ELSE PRINT 'VariationOrderLines EXISTS';
GO

/* ============================================================================
   VERIFY — object presence report
   ============================================================================ */
SELECT name AS TableExists FROM sys.tables
 WHERE name IN ('RequiredDocumentTypes','JobApplications','ApplicationDocuments',
                'EquipmentDepreciationAllocations','VariationOrders','VariationOrderLines')
 ORDER BY name;
SELECT 'JobApplications.PhotoPath'   AS Col, CASE WHEN COL_LENGTH('dbo.JobApplications','PhotoPath')   IS NULL THEN 0 ELSE 1 END AS Present
UNION ALL
SELECT 'BoqItems.VariationOrderId',       CASE WHEN COL_LENGTH('dbo.BoqItems','VariationOrderId')      IS NULL THEN 0 ELSE 1 END;
GO
