-- ==========================================================================================
-- Hospital H71 — ward observations scored as NEWS2, and the nursing risk assessments (Morse falls, Braden pressure
-- injury). Phase 1 of the 2026-11-09 gap research: patient safety.
--   HospitalObservations     one set of vital signs, its NEWS2 total and clinical risk as they were on save
--   HospitalRiskAssessments  one Morse or Braden assessment: its item scores, total and level
--
-- Additive only (two new tables). Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL OR OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h3_admissions.sql first.', 1;

IF OBJECT_ID('dbo.HospitalObservations','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalObservations (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        AdmissionId           int            NOT NULL,
        PatientId             int            NOT NULL,
        TakenAt               datetime2      NOT NULL,
        RespRate              int            NULL,
        SpO2                  int            NULL,
        SpO2Scale2            bit            NOT NULL,
        OnOxygen              bit            NOT NULL,
        SystolicBp            int            NULL,
        DiastolicBp           int            NULL,
        HeartRate             int            NULL,
        Consciousness         nvarchar(1)    NULL,
        TempC                 decimal(4,1)   NULL,
        PainScore             int            NULL,
        News2                 int            NULL,
        News2Risk             nvarchar(10)   NULL,
        Note                  nvarchar(300)  NULL,
        RecordedByEmployeeId  int            NULL,
        RecordedAt            datetime2      NOT NULL,
        CONSTRAINT PK_HospitalObservations PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalObservations_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalObservations_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalObservations_Consciousness CHECK (Consciousness IS NULL OR Consciousness IN ('A','C','V','P','U')),
        CONSTRAINT CK_HospitalObservations_Risk CHECK (News2Risk IS NULL OR News2Risk IN ('Low','LowSingle','Medium','High')),
        CONSTRAINT CK_HospitalObservations_Pain CHECK (PainScore IS NULL OR PainScore BETWEEN 0 AND 10),
        -- a score and its risk go together; NEWS2 runs 0 to 20
        CONSTRAINT CK_HospitalObservations_Score CHECK ((News2 IS NULL AND News2Risk IS NULL) OR (News2 BETWEEN 0 AND 20 AND News2Risk IS NOT NULL))
    );
    CREATE INDEX IX_HospitalObservations_CompanyId_AdmissionId_TakenAt ON dbo.HospitalObservations (CompanyId, AdmissionId, TakenAt);
END

IF OBJECT_ID('dbo.HospitalRiskAssessments','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRiskAssessments (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        AdmissionId           int            NOT NULL,
        PatientId             int            NOT NULL,
        Kind                  nvarchar(10)   NOT NULL,
        Items                 nvarchar(40)   NOT NULL,
        Score                 int            NOT NULL,
        Level                 nvarchar(10)   NOT NULL,
        Note                  nvarchar(300)  NULL,
        AssessedAt            datetime2      NOT NULL,
        AssessedByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalRiskAssessments PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalRiskAssessments_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalRiskAssessments_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalRiskAssessments_Kind CHECK (Kind IN ('Morse','Braden')),
        -- Morse runs 0 to 125, Braden 6 to 23
        CONSTRAINT CK_HospitalRiskAssessments_Score CHECK ((Kind = 'Morse' AND Score BETWEEN 0 AND 125) OR (Kind = 'Braden' AND Score BETWEEN 6 AND 23))
    );
    CREATE INDEX IX_HospitalRiskAssessments_CompanyId_AdmissionId_Kind_AssessedAt ON dbo.HospitalRiskAssessments (CompanyId, AdmissionId, Kind, AssessedAt);
END

COMMIT TRANSACTION;
