-- ==========================================================================================
-- Hospital H78 — fluid balance (intake and output chart). Phase 2 of the 2026-11-09 gap research.
--   HospitalFluidEntries  each measured volume in or out, at its time; a wrong one is voided with a reason, never deleted
--
-- Additive only (one new table). Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL OR OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h3_admissions.sql first.', 1;

IF OBJECT_ID('dbo.HospitalFluidEntries','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalFluidEntries (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        AdmissionId           int            NOT NULL,
        PatientId             int            NOT NULL,
        Direction             nvarchar(3)    NOT NULL,
        Kind                  nvarchar(10)   NOT NULL,
        VolumeMl              int            NOT NULL,
        Note                  nvarchar(200)  NULL,
        At                    datetime2      NOT NULL,
        CreatedAt             datetime2      NOT NULL,
        CreatedByEmployeeId   int            NULL,
        VoidedAt              datetime2      NULL,
        VoidedByEmployeeId    int            NULL,
        VoidReason            nvarchar(200)  NULL,
        CONSTRAINT PK_HospitalFluidEntries PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalFluidEntries_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalFluidEntries_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalFluidEntries_Direction CHECK (Direction IN ('In','Out')),
        CONSTRAINT CK_HospitalFluidEntries_Kind CHECK (
            (Direction = 'In'  AND Kind IN ('Oral','IV','Enteral','Blood','OtherIn')) OR
            (Direction = 'Out' AND Kind IN ('Urine','Drain','Vomit','NG','Stool','BloodLoss','OtherOut'))),
        CONSTRAINT CK_HospitalFluidEntries_Volume CHECK (VolumeMl BETWEEN 1 AND 5000),
        CONSTRAINT CK_HospitalFluidEntries_Void CHECK (VoidedAt IS NULL OR VoidReason IS NOT NULL)
    );
    CREATE INDEX IX_HospitalFluidEntries_CompanyId_AdmissionId_At ON dbo.HospitalFluidEntries (CompanyId, AdmissionId, At);
END

COMMIT TRANSACTION;
