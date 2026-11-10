-- ==========================================================================================
-- Hospital H56 — the sick leave certificate: issued by a doctor on the patient's file, numbered, final once issued
-- (a wrong one is voided with a reason, never deleted or edited). The diagnosis prints only when the doctor ticks
-- it. Printed through Report Studio.
--   HospitalSickLeaves  one row per certificate
--
-- Additive only (one new table). Idempotent (OBJECT_ID guard).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql first.', 1;

IF OBJECT_ID('dbo.HospitalSickLeaves','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSickLeaves (
        ID                  int IDENTITY(1,1) NOT NULL,
        CompanyId           int            NOT NULL,
        LeaveNo             nvarchar(20)   NOT NULL,
        PatientId           int            NOT NULL,
        EncounterId         int            NULL,
        PractitionerId      int            NULL,
        FromDate            date           NOT NULL,
        ToDate              date           NOT NULL,
        Days                int            NOT NULL,
        Kind                nvarchar(20)   NOT NULL,
        Diagnosis           nvarchar(500)  NULL,
        ShowDiagnosis       bit            NOT NULL,
        Note                nvarchar(500)  NULL,
        IssuedAt            datetime2      NOT NULL,
        IssuedByEmployeeId  int            NOT NULL,
        VoidedAt            datetime2      NULL,
        VoidedByEmployeeId  int            NULL,
        VoidReason          nvarchar(300)  NULL,
        CONSTRAINT PK_HospitalSickLeaves PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalSickLeaves_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalSickLeaves_Kind CHECK (Kind IN ('Rest','LightDuty','Hospitalised')),
        CONSTRAINT CK_HospitalSickLeaves_Dates CHECK (ToDate >= FromDate AND Days = DATEDIFF(day, FromDate, ToDate) + 1),
        -- a diagnosis printed must exist; a void carries its reason
        CONSTRAINT CK_HospitalSickLeaves_Diagnosis CHECK (ShowDiagnosis = 0 OR Diagnosis IS NOT NULL),
        CONSTRAINT CK_HospitalSickLeaves_Void CHECK ((VoidedAt IS NULL AND VoidReason IS NULL) OR (VoidedAt IS NOT NULL AND VoidReason IS NOT NULL))
    );
    CREATE UNIQUE INDEX IX_HospitalSickLeaves_CompanyId_LeaveNo ON dbo.HospitalSickLeaves (CompanyId, LeaveNo);
    CREATE INDEX IX_HospitalSickLeaves_CompanyId_PatientId ON dbo.HospitalSickLeaves (CompanyId, PatientId);
END

COMMIT TRANSACTION;
