-- ==========================================================================================
-- Hospital H209 - notifiable diseases: the Ministry of Health is told of a case within 24 hours (group A) or within
-- the week (group B).
--
--   HospitalNotifiableDiseases   (new) the hospital's list, by ICD-10 code prefix (seeded by the application per company)
--   HospitalDiseaseReports       (new) one case per visit and disease: due time, the Ministry's reference or why dismissed
--
-- New tables only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalEncounters','U') IS NULL
    THROW 52009, 'Run the outpatient and consultation slices first.', 1;

IF OBJECT_ID('dbo.HospitalNotifiableDiseases','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalNotifiableDiseases (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        CodePrefix nvarchar(10) NOT NULL,
        Name nvarchar(200) NOT NULL,
        NameEn nvarchar(200) NULL,
        Urgency nvarchar(10) NOT NULL,
        IsActive bit NOT NULL,
        CreatedAt datetime2 NOT NULL,
        CONSTRAINT PK_HospitalNotifiableDiseases PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalNotifiableDiseases_Urgency CHECK (Urgency IN ('Immediate', 'Weekly'))
    );
    CREATE UNIQUE INDEX IX_HospitalNotifiableDiseases_CompanyId_CodePrefix ON dbo.HospitalNotifiableDiseases (CompanyId, CodePrefix);
END

IF OBJECT_ID('dbo.HospitalDiseaseReports','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDiseaseReports (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        ReportNo nvarchar(20) NOT NULL,
        PatientId int NOT NULL,
        EncounterId int NOT NULL,
        DiseaseId int NOT NULL,
        Code nvarchar(20) NOT NULL,
        DiagnosisName nvarchar(300) NOT NULL,
        DiagnosisNameEn nvarchar(300) NULL,
        PractitionerId int NULL,
        DiagnosedAt datetime2 NOT NULL,
        DueAt datetime2 NOT NULL,
        Status nvarchar(10) NOT NULL,
        MohRef nvarchar(60) NULL,
        DismissReason nvarchar(500) NULL,
        ClosedAt datetime2 NULL,
        ClosedByEmployeeId int NULL,
        CreatedAt datetime2 NOT NULL,
        CONSTRAINT PK_HospitalDiseaseReports PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalDiseaseReports_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT FK_HospitalDiseaseReports_Encounter FOREIGN KEY (EncounterId) REFERENCES dbo.HospitalEncounters (ID),
        CONSTRAINT FK_HospitalDiseaseReports_Disease FOREIGN KEY (DiseaseId) REFERENCES dbo.HospitalNotifiableDiseases (ID),
        CONSTRAINT CK_HospitalDiseaseReports_Status CHECK (Status IN ('Due', 'Reported', 'Dismissed')),
        -- a reported case carries the Ministry's reference; a dismissed one its reason
        CONSTRAINT CK_HospitalDiseaseReports_Close CHECK ((Status <> 'Reported' OR MohRef IS NOT NULL) AND (Status <> 'Dismissed' OR DismissReason IS NOT NULL))
    );
    CREATE UNIQUE INDEX IX_HospitalDiseaseReports_CompanyId_ReportNo ON dbo.HospitalDiseaseReports (CompanyId, ReportNo);
    CREATE UNIQUE INDEX IX_HospitalDiseaseReports_CompanyId_EncounterId_DiseaseId ON dbo.HospitalDiseaseReports (CompanyId, EncounterId, DiseaseId);
    CREATE INDEX IX_HospitalDiseaseReports_CompanyId_Status_DueAt ON dbo.HospitalDiseaseReports (CompanyId, Status, DueAt);
    CREATE INDEX IX_HospitalDiseaseReports_CompanyId_PatientId ON dbo.HospitalDiseaseReports (CompanyId, PatientId);
END

COMMIT TRANSACTION;
