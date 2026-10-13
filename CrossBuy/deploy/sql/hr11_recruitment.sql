-- HR-11 / Recruitment R0: hiring pipeline + required-document catalog + application documents.
-- Idempotent. Additive only — does NOT touch EmployeeRequest (ESS) or the Employee/EmployeeDocument tables.

-- 1) Required-document catalog (global, mandatory/optional) — drives the per-application checklist.
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
END
GO

-- 2) Job applications (hiring requests / applicants).
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
END
GO

-- 3) Application documents (mirror EmployeeDocument → clean carry-over on hire).
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
END
GO

-- 4) Seed a sensible default catalog (only if empty) for company 1.
IF NOT EXISTS (SELECT 1 FROM dbo.RequiredDocumentTypes WHERE CompanyID = 1)
BEGIN
    INSERT INTO dbo.RequiredDocumentTypes (CompanyID, Name, NameEn, IsMandatory, SortOrder, IsActive, CreatedAt) VALUES
        (1, N'صورة بطاقة الرقم القومي', N'National ID copy',        1, 1, 1, SYSUTCDATETIME()),
        (1, N'المؤهل الدراسي',          N'Qualification',           1, 2, 1, SYSUTCDATETIME()),
        (1, N'شهادة الخبرة',            N'Experience certificate',  0, 3, 1, SYSUTCDATETIME()),
        (1, N'صورة شخصية',              N'Personal photo',          1, 4, 1, SYSUTCDATETIME()),
        (1, N'شهادة التأمينات',         N'Insurance statement',     0, 5, 1, SYSUTCDATETIME()),
        (1, N'الموقف من التجنيد',        N'Military status',         0, 6, 1, SYSUTCDATETIME());
END
GO

-- add optional applicant photo (idempotent)
IF COL_LENGTH('dbo.JobApplications','PhotoPath') IS NULL ALTER TABLE dbo.JobApplications ADD PhotoPath NVARCHAR(500) NULL;
GO
