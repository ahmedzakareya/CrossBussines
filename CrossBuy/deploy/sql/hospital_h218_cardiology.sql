-- ==========================================================================================
-- Hospital H218 - cardiology: a 12-lead ECG or a transthoracic echo, requested and reported, a critical finding
-- flagged.
--
--   HospitalCardioStudies   (new)
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 52018, 'Run the hospital slices first.', 1;

IF OBJECT_ID('dbo.HospitalCardioStudies','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCardioStudies (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        StudyNo nvarchar(20) NOT NULL,
        PatientId int NOT NULL,
        Kind nvarchar(5) NOT NULL,
        Indication nvarchar(500) NOT NULL,
        Status nvarchar(10) NOT NULL,
        RequestedAt datetime2 NOT NULL,
        RequestedByEmployeeId int NULL,
        HeartRate int NULL,
        Rhythm nvarchar(100) NULL,
        PrMs int NULL,
        QrsMs int NULL,
        QtMs int NULL,
        QtcMs int NULL,
        Axis int NULL,
        EfPercent int NULL,
        Chambers nvarchar(500) NULL,
        Valves nvarchar(500) NULL,
        WallMotion nvarchar(500) NULL,
        Pericardium nvarchar(200) NULL,
        Interpretation nvarchar(2000) NULL,
        Critical bit NOT NULL,
        CriticalWhy nvarchar(200) NULL,
        ReportedAt datetime2 NULL,
        ReportedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalCardioStudies PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalCardioStudies_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalCardioStudies_Kind CHECK (Kind IN ('ECG', 'Echo')),
        CONSTRAINT CK_HospitalCardioStudies_Status CHECK (Status IN ('Requested', 'Reported')),
        CONSTRAINT CK_HospitalCardioStudies_Reported CHECK (Status <> 'Reported' OR (Interpretation IS NOT NULL AND ReportedAt IS NOT NULL)),
        CONSTRAINT CK_HospitalCardioStudies_Ef CHECK (EfPercent IS NULL OR EfPercent BETWEEN 5 AND 90)
    );
    CREATE UNIQUE INDEX IX_HospitalCardioStudies_CompanyId_StudyNo ON dbo.HospitalCardioStudies (CompanyId, StudyNo);
    CREATE INDEX IX_HospitalCardioStudies_CompanyId_Status ON dbo.HospitalCardioStudies (CompanyId, Status);
    CREATE INDEX IX_HospitalCardioStudies_CompanyId_PatientId ON dbo.HospitalCardioStudies (CompanyId, PatientId);
END

COMMIT TRANSACTION;
