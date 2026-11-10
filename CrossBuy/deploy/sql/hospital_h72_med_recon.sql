-- ==========================================================================================
-- Hospital H72 — medication reconciliation at admission and discharge. Phase 1 of the 2026-11-09 gap research.
--   HospitalMedReconLines  the medicines reconciled: home, given in hospital, or new for home — with each stage's decision
--   HospitalMedRecons      a stage signed complete (once per admission and stage)
--
-- Additive only (two new tables). Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL OR OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h3_admissions.sql first.', 1;

IF OBJECT_ID('dbo.HospitalMedReconLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalMedReconLines (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        AdmissionId           int            NOT NULL,
        PatientId             int            NOT NULL,
        Kind                  nvarchar(10)   NOT NULL,
        MedOrderId            int            NULL,
        Drug                  nvarchar(200)  NOT NULL,
        Dose                  nvarchar(60)   NULL,
        Route                 nvarchar(10)   NULL,
        Frequency             nvarchar(100)  NULL,
        LastTaken             nvarchar(60)   NULL,
        Source                nvarchar(10)   NULL,
        AdmitDecision         nvarchar(10)   NULL,
        AdmitReason           nvarchar(300)  NULL,
        DischargeDecision     nvarchar(10)   NULL,
        DischargeNote         nvarchar(300)  NULL,
        CreatedAt             datetime2      NOT NULL,
        CreatedByEmployeeId   int            NULL,
        DecidedAt             datetime2      NULL,
        DecidedByEmployeeId   int            NULL,
        CONSTRAINT PK_HospitalMedReconLines PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalMedReconLines_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalMedReconLines_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalMedReconLines_Kind CHECK (Kind IN ('Home','Inpatient','New')),
        CONSTRAINT CK_HospitalMedReconLines_Source CHECK (Source IS NULL OR Source IN ('Patient','Family','Records','Pharmacy','Other')),
        CONSTRAINT CK_HospitalMedReconLines_Admit CHECK (AdmitDecision IS NULL OR AdmitDecision IN ('Continue','Change','Hold','Stop')),
        CONSTRAINT CK_HospitalMedReconLines_Discharge CHECK (DischargeDecision IS NULL OR DischargeDecision IN ('Continue','Change','Stop')),
        -- an admission decision is for a home medicine; an inpatient line names its order
        CONSTRAINT CK_HospitalMedReconLines_Shape CHECK ((Kind = 'Home') OR (AdmitDecision IS NULL AND (Kind = 'New' OR MedOrderId IS NOT NULL)))
    );
    CREATE INDEX IX_HospitalMedReconLines_CompanyId_AdmissionId ON dbo.HospitalMedReconLines (CompanyId, AdmissionId);
END

IF OBJECT_ID('dbo.HospitalMedRecons','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalMedRecons (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        AdmissionId            int            NOT NULL,
        PatientId              int            NOT NULL,
        Stage                  nvarchar(10)   NOT NULL,
        NoHomeMeds             bit            NOT NULL,
        CompletedAt            datetime2      NOT NULL,
        CompletedByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalMedRecons PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalMedRecons_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalMedRecons_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalMedRecons_Stage CHECK (Stage IN ('Admission','Discharge'))
    );
    CREATE UNIQUE INDEX IX_HospitalMedRecons_CompanyId_AdmissionId_Stage ON dbo.HospitalMedRecons (CompanyId, AdmissionId, Stage);
END

COMMIT TRANSACTION;
