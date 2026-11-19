-- ==========================================================================================
-- Hospital H203 - referrals: to another department of the hospital (a consult) or to another facility.
--
--   HospitalReferrals   (new) the question, the summary, where it goes, its urgency and due time, its answer
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalDepartments','U') IS NULL
    THROW 52003, 'Run the outpatient slice first.', 1;

IF OBJECT_ID('dbo.HospitalReferrals','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalReferrals (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        ReferralNo nvarchar(20) NOT NULL,
        PatientId int NOT NULL,
        EncounterId int NULL,
        Kind nvarchar(10) NOT NULL,
        FromPractitionerId int NULL,
        FromDepartmentId int NULL,
        ToDepartmentId int NULL,
        ToPractitionerId int NULL,
        ToFacility nvarchar(200) NULL,
        ToSpecialty nvarchar(100) NULL,
        Urgency nvarchar(10) NOT NULL,
        Reason nvarchar(1000) NOT NULL,
        Summary nvarchar(2000) NULL,
        Status nvarchar(12) NOT NULL,
        DueAt datetime2 NOT NULL,
        CreatedAt datetime2 NOT NULL,
        CreatedByEmployeeId int NULL,
        AcceptedAt datetime2 NULL,
        AcceptedByEmployeeId int NULL,
        Reply nvarchar(2000) NULL,
        CloseReason nvarchar(500) NULL,
        ClosedAt datetime2 NULL,
        ClosedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalReferrals PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalReferrals_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT FK_HospitalReferrals_ToDepartment FOREIGN KEY (ToDepartmentId) REFERENCES dbo.HospitalDepartments (ID),
        CONSTRAINT CK_HospitalReferrals_Kind CHECK (Kind IN ('Internal', 'External')),
        CONSTRAINT CK_HospitalReferrals_Urgency CHECK (Urgency IN ('Routine', 'Urgent', 'Emergency')),
        CONSTRAINT CK_HospitalReferrals_Status CHECK (Status IN ('Sent', 'Accepted', 'Declined', 'Completed', 'Cancelled'))
    );
    CREATE UNIQUE INDEX IX_HospitalReferrals_CompanyId_ReferralNo ON dbo.HospitalReferrals (CompanyId, ReferralNo);
    CREATE INDEX IX_HospitalReferrals_CompanyId_Status_DueAt ON dbo.HospitalReferrals (CompanyId, Status, DueAt);
    CREATE INDEX IX_HospitalReferrals_CompanyId_PatientId ON dbo.HospitalReferrals (CompanyId, PatientId);
END

COMMIT TRANSACTION;
