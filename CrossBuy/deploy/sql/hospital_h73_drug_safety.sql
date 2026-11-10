-- ==========================================================================================
-- Hospital H73 — the hospital's own drug-safety knowledge base and the record of alerts a doctor went past.
-- Phase 1 of the 2026-11-09 gap research.
--   HospitalDrugInteractions  pairs of medicines that interact, with severity and effect (Arabic and English)
--   HospitalDoseLimits        the highest single and daily dose of a medicine, by route
--   HospitalAlertOverrides    a stopping alert a doctor went past, and why
--
-- Additive only (three new tables). Idempotent (OBJECT_ID guards). The knowledge base starts empty: the pharmacy fills
-- it (no clinical content is shipped by a deploy script).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql first.', 1;

IF OBJECT_ID('dbo.HospitalDrugInteractions','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDrugInteractions (
        ID                   int IDENTITY(1,1) NOT NULL,
        CompanyId            int            NOT NULL,
        DrugA                nvarchar(80)   NOT NULL,
        DrugB                nvarchar(80)   NOT NULL,
        Severity             nvarchar(10)   NOT NULL,
        EffectAr             nvarchar(400)  NOT NULL,
        EffectEn             nvarchar(400)  NOT NULL,
        IsActive             bit            NOT NULL,
        UpdatedAt            datetime2      NOT NULL,
        UpdatedByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalDrugInteractions PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalDrugInteractions_Severity CHECK (Severity IN ('Contra','Major','Moderate')),
        -- two different medicines (the application stores each pair once, in ordinal order — a collation-dependent
        -- "<" here could disagree with it on a hyphen or a digit)
        CONSTRAINT CK_HospitalDrugInteractions_Pair CHECK (DrugA <> DrugB)
    );
    CREATE UNIQUE INDEX IX_HospitalDrugInteractions_CompanyId_DrugA_DrugB ON dbo.HospitalDrugInteractions (CompanyId, DrugA, DrugB);
END

IF OBJECT_ID('dbo.HospitalDoseLimits','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDoseLimits (
        ID                   int IDENTITY(1,1) NOT NULL,
        CompanyId            int            NOT NULL,
        Drug                 nvarchar(80)   NOT NULL,
        Route                nvarchar(10)   NULL,
        Unit                 nvarchar(10)   NOT NULL,
        MaxSingle            decimal(12,3)  NULL,
        MaxDaily             decimal(12,3)  NULL,
        Note                 nvarchar(300)  NULL,
        IsActive             bit            NOT NULL,
        UpdatedAt            datetime2      NOT NULL,
        UpdatedByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalDoseLimits PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalDoseLimits_Unit CHECK (Unit IN ('mg','mcg','g','units','ml')),
        CONSTRAINT CK_HospitalDoseLimits_Some CHECK (MaxSingle IS NOT NULL OR MaxDaily IS NOT NULL),
        CONSTRAINT CK_HospitalDoseLimits_Positive CHECK ((MaxSingle IS NULL OR MaxSingle > 0) AND (MaxDaily IS NULL OR MaxDaily > 0))
    );
    CREATE INDEX IX_HospitalDoseLimits_CompanyId_Drug ON dbo.HospitalDoseLimits (CompanyId, Drug);
END

IF OBJECT_ID('dbo.HospitalAlertOverrides','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalAlertOverrides (
        ID            int IDENTITY(1,1) NOT NULL,
        CompanyId     int            NOT NULL,
        PatientId     int            NOT NULL,
        AdmissionId   int            NULL,
        SourceType    nvarchar(20)   NOT NULL,
        SourceId      int            NOT NULL,
        Kind          nvarchar(12)   NOT NULL,
        Detail        nvarchar(300)  NOT NULL,
        Reason        nvarchar(500)  NOT NULL,
        At            datetime2      NOT NULL,
        ByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalAlertOverrides PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalAlertOverrides_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalAlertOverrides_Kind CHECK (Kind IN ('Interaction','Dose','Allergy'))
    );
    CREATE INDEX IX_HospitalAlertOverrides_CompanyId_PatientId ON dbo.HospitalAlertOverrides (CompanyId, PatientId);
END

COMMIT TRANSACTION;
