-- ==========================================================================================
-- Hospital H216 - anatomic pathology: a tissue specimen requested, received, described and cut (gross), read and
-- signed out by the pathologist; a signed report is amended beside, never edited.
--
--   HospitalPathCases        (new)
--   HospitalPathAmendments   (new)
--
-- New tables only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 52016, 'Run the hospital slices first.', 1;

IF OBJECT_ID('dbo.HospitalPathCases','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPathCases (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        CaseNo nvarchar(20) NOT NULL,
        PatientId int NOT NULL,
        EncounterId int NULL,
        Source nvarchar(10) NOT NULL,
        SourceRefId int NULL,
        Specimen nvarchar(200) NOT NULL,
        Site nvarchar(200) NULL,
        ClinicalHistory nvarchar(1000) NULL,
        Containers int NOT NULL,
        Status nvarchar(10) NOT NULL,
        RequestedAt datetime2 NOT NULL,
        RequestedByEmployeeId int NULL,
        ReceivedAt datetime2 NULL,
        ReceivedByEmployeeId int NULL,
        Gross nvarchar(2000) NULL,
        Blocks int NULL,
        GrossedAt datetime2 NULL,
        GrossedByEmployeeId int NULL,
        Microscopic nvarchar(4000) NULL,
        Diagnosis nvarchar(1000) NULL,
        Malignant bit NOT NULL,
        ReportedAt datetime2 NULL,
        ReportedByEmployeeId int NULL,
        CancelReason nvarchar(300) NULL,
        CONSTRAINT PK_HospitalPathCases PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalPathCases_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalPathCases_Source CHECK (Source IN ('Clinic', 'Theatre', 'Endoscopy', 'Ward')),
        CONSTRAINT CK_HospitalPathCases_Status CHECK (Status IN ('Requested', 'Received', 'Grossed', 'Reported', 'Cancelled')),
        CONSTRAINT CK_HospitalPathCases_Containers CHECK (Containers BETWEEN 1 AND 50),
        -- a signed report carries its diagnosis and its signature; a cancelled request its reason
        CONSTRAINT CK_HospitalPathCases_Reported CHECK (Status <> 'Reported' OR (Diagnosis IS NOT NULL AND ReportedAt IS NOT NULL)),
        CONSTRAINT CK_HospitalPathCases_Cancelled CHECK (Status <> 'Cancelled' OR CancelReason IS NOT NULL)
    );
    CREATE UNIQUE INDEX IX_HospitalPathCases_CompanyId_CaseNo ON dbo.HospitalPathCases (CompanyId, CaseNo);
    CREATE INDEX IX_HospitalPathCases_CompanyId_Status ON dbo.HospitalPathCases (CompanyId, Status);
    CREATE INDEX IX_HospitalPathCases_CompanyId_PatientId ON dbo.HospitalPathCases (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalPathAmendments','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPathAmendments (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        CaseId int NOT NULL,
        Text nvarchar(2000) NOT NULL,
        Reason nvarchar(300) NOT NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalPathAmendments PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalPathAmendments_Case FOREIGN KEY (CaseId) REFERENCES dbo.HospitalPathCases (ID)
    );
    CREATE INDEX IX_HospitalPathAmendments_CompanyId_CaseId ON dbo.HospitalPathAmendments (CompanyId, CaseId);
END

COMMIT TRANSACTION;
