-- ==========================================================================================
-- Hospital H85 — antenatal care. Phase 4 (maternity).
--   HospitalPregnancies      a pregnancy on the mother's file: LMP or ultrasound EDD, G/P/A, risks; delivered or lost
--   HospitalAntenatalVisits  each antenatal check: BP, weight, fundal height, fetal heart rate, urine protein, next visit
--
-- Additive only (two new tables). Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalBirths','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql and hospital_h58_births.sql first.', 1;

IF OBJECT_ID('dbo.HospitalPregnancies','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPregnancies (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        PregnancyNo           nvarchar(20)   NOT NULL,
        PatientId             int            NOT NULL,
        Lmp                   date           NULL,
        UltrasoundEdd         date           NULL,
        Gravida               int            NOT NULL,
        Para                  int            NOT NULL,
        Abortions             int            NOT NULL,
        Risks                 nvarchar(500)  NULL,
        Status                nvarchar(10)   NOT NULL,
        BirthId               int            NULL,
        EndedAt               datetime2      NULL,
        EndNote               nvarchar(300)  NULL,
        CreatedAt             datetime2      NOT NULL,
        CreatedByEmployeeId   int            NULL,
        CONSTRAINT PK_HospitalPregnancies PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalPregnancies_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalPregnancies_Birth FOREIGN KEY (BirthId) REFERENCES dbo.HospitalBirths(ID),
        CONSTRAINT CK_HospitalPregnancies_Status CHECK (Status IN ('Active','Delivered','Loss')),
        CONSTRAINT CK_HospitalPregnancies_Dates CHECK (Lmp IS NOT NULL OR UltrasoundEdd IS NOT NULL),
        CONSTRAINT CK_HospitalPregnancies_GPA CHECK (Gravida BETWEEN 1 AND 30 AND Para >= 0 AND Abortions >= 0 AND Para + Abortions < Gravida),
        CONSTRAINT CK_HospitalPregnancies_Ended CHECK (Status = 'Active' OR EndedAt IS NOT NULL)
    );
    CREATE UNIQUE INDEX IX_HospitalPregnancies_CompanyId_PregnancyNo ON dbo.HospitalPregnancies (CompanyId, PregnancyNo);
    CREATE INDEX IX_HospitalPregnancies_CompanyId_PatientId ON dbo.HospitalPregnancies (CompanyId, PatientId);
    -- one active pregnancy per mother
    CREATE UNIQUE INDEX UX_HospitalPregnancies_Active ON dbo.HospitalPregnancies (CompanyId, PatientId) WHERE Status = 'Active';
END

IF OBJECT_ID('dbo.HospitalAntenatalVisits','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalAntenatalVisits (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        PregnancyId           int            NOT NULL,
        PatientId             int            NOT NULL,
        VisitDate             date           NOT NULL,
        BpSystolic            int            NULL,
        BpDiastolic           int            NULL,
        WeightKg              decimal(5,1)   NULL,
        FundalHeightCm        decimal(4,1)   NULL,
        FetalHeartRate        int            NULL,
        UrineProtein          nvarchar(5)    NULL,
        Note                  nvarchar(500)  NULL,
        NextVisitOn           date           NULL,
        CreatedAt             datetime2      NOT NULL,
        CreatedByEmployeeId   int            NULL,
        CONSTRAINT PK_HospitalAntenatalVisits PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalAntenatalVisits_Pregnancy FOREIGN KEY (PregnancyId) REFERENCES dbo.HospitalPregnancies(ID),
        CONSTRAINT FK_HospitalAntenatalVisits_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalAntenatalVisits_Protein CHECK (UrineProtein IS NULL OR UrineProtein IN ('Neg','Trace','1+','2+','3+')),
        CONSTRAINT CK_HospitalAntenatalVisits_Bp CHECK ((BpSystolic IS NULL AND BpDiastolic IS NULL) OR (BpSystolic BETWEEN 50 AND 260 AND BpDiastolic BETWEEN 30 AND 160 AND BpDiastolic < BpSystolic)),
        CONSTRAINT CK_HospitalAntenatalVisits_Fhr CHECK (FetalHeartRate IS NULL OR FetalHeartRate BETWEEN 50 AND 240)
    );
    CREATE INDEX IX_HospitalAntenatalVisits_CompanyId_PregnancyId ON dbo.HospitalAntenatalVisits (CompanyId, PregnancyId);
END

COMMIT TRANSACTION;
