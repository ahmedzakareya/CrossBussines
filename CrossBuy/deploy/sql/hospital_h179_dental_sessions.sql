-- ==========================================================================================
-- Hospital H179 - the signed dental treatment session, and the informed consent of a plan phase.
--
--   HospitalDentalSessions      (new) one signed record per sitting
--   HospitalDentalPlanLines     + SessionId (the session the work was done in)
--   HospitalConsents            + DentalPhaseId (the plan phase a procedure consent covers)
--   HospitalClinicalAddenda     subject check widened with 'DentalSession'
--
-- Nullable columns and a new table; nothing existing changes meaning. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDentalPlanLines','U') IS NULL OR OBJECT_ID('dbo.HospitalConsents','U') IS NULL OR OBJECT_ID('dbo.HospitalClinicalAddenda','U') IS NULL
    THROW 51981, 'Run the dental, consent and addenda slices (hospital_h177_clinical_addenda.sql) first.', 1;

IF OBJECT_ID('dbo.HospitalDentalSessions','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDentalSessions (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        SessionNo nvarchar(20) NOT NULL,
        PatientId int NOT NULL,
        PractitionerId int NULL,
        SessionAt datetime2 NOT NULL,
        Anaesthetic nvarchar(100) NULL,
        AnaestheticAmount nvarchar(40) NULL,
        AnaestheticSite nvarchar(100) NULL,
        Materials nvarchar(500) NULL,
        Complications nvarchar(500) NULL,
        PostOpInstructions nvarchar(500) NULL,
        Note nvarchar(1000) NULL,
        SignedAt datetime2 NOT NULL,
        SignedByEmployeeId int NOT NULL,
        CONSTRAINT PK_HospitalDentalSessions PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalDentalSessions_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalDentalSessions_CompanyId_SessionNo ON dbo.HospitalDentalSessions (CompanyId, SessionNo);
    CREATE INDEX IX_HospitalDentalSessions_CompanyId_PatientId_SessionAt ON dbo.HospitalDentalSessions (CompanyId, PatientId, SessionAt);
END

IF COL_LENGTH('dbo.HospitalDentalPlanLines', 'SessionId') IS NULL ALTER TABLE dbo.HospitalDentalPlanLines ADD SessionId int NULL;
IF COL_LENGTH('dbo.HospitalConsents', 'DentalPhaseId') IS NULL ALTER TABLE dbo.HospitalConsents ADD DentalPhaseId int NULL;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalClinicalAddenda_Subject' AND definition LIKE '%DentalSession%')
BEGIN
    IF OBJECT_ID('dbo.CK_HospitalClinicalAddenda_Subject','C') IS NOT NULL ALTER TABLE dbo.HospitalClinicalAddenda DROP CONSTRAINT CK_HospitalClinicalAddenda_Subject;
    ALTER TABLE dbo.HospitalClinicalAddenda ADD CONSTRAINT CK_HospitalClinicalAddenda_Subject CHECK (Subject IN ('Encounter','Canvas','Labour','DentalSession'));
END

COMMIT TRANSACTION;
