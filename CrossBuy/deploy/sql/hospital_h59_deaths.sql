-- ==========================================================================================
-- Hospital H59 — deaths: the medical certificate of cause of death (the WHO international form: Part I, the chain from
-- the immediate cause down to the underlying cause; Part II, other significant conditions; the manner of death),
-- certified by a doctor, numbered, final once certified (a wrong one is voided with a reason). The patient's file is
-- marked deceased (DeceasedAt), so it is no longer booked or admitted. Printed through Report Studio.
--   HospitalPatients  + DeceasedAt
--   HospitalDeaths    one certificate per death (one live certificate per patient)
--
-- Additive only (one nullable column, one new table). Idempotent (COL_LENGTH / OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql and hospital_h3_admissions.sql first.', 1;

IF COL_LENGTH('dbo.HospitalPatients', 'DeceasedAt') IS NULL
    ALTER TABLE dbo.HospitalPatients ADD DeceasedAt datetime2 NULL;

IF OBJECT_ID('dbo.HospitalDeaths','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDeaths (
        ID                      int IDENTITY(1,1) NOT NULL,
        CompanyId               int             NOT NULL,
        DeathNo                 nvarchar(20)    NOT NULL,
        PatientId               int             NOT NULL,
        AdmissionId             int             NULL,
        DiedAt                  datetime2       NOT NULL,
        Place                   nvarchar(12)    NOT NULL,
        ImmediateCause          nvarchar(300)   NOT NULL,
        DueTo1                  nvarchar(300)   NULL,
        DueTo2                  nvarchar(300)   NULL,
        UnderlyingCause         nvarchar(300)   NULL,
        OtherConditions         nvarchar(500)   NULL,
        Manner                  nvarchar(14)    NOT NULL,
        AutopsyRequested        bit             NOT NULL,
        PractitionerId          int             NULL,
        CertifiedAt             datetime2       NOT NULL,
        CertifiedByEmployeeId   int             NOT NULL,
        VoidedAt                datetime2       NULL,
        VoidedByEmployeeId      int             NULL,
        VoidReason              nvarchar(300)   NULL,
        CONSTRAINT PK_HospitalDeaths PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalDeaths_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalDeaths_Place CHECK (Place IN ('Ward','Icu','Er','Theatre','OnArrival','Other')),
        CONSTRAINT CK_HospitalDeaths_Manner CHECK (Manner IN ('Natural','Accident','Suicide','Homicide','Pending','Undetermined')),
        CONSTRAINT CK_HospitalDeaths_Void CHECK ((VoidedAt IS NULL AND VoidReason IS NULL) OR (VoidedAt IS NOT NULL AND VoidReason IS NOT NULL))
    );
    CREATE UNIQUE INDEX IX_HospitalDeaths_CompanyId_DeathNo ON dbo.HospitalDeaths (CompanyId, DeathNo);
    -- a patient dies once: one certificate that is not void
    CREATE UNIQUE INDEX UX_HospitalDeaths_LivePatient ON dbo.HospitalDeaths (CompanyId, PatientId) WHERE VoidedAt IS NULL;
END

COMMIT TRANSACTION;
