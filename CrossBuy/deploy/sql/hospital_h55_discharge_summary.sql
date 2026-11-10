-- ==========================================================================================
-- Hospital H55 — the discharge summary of a stay: one per admission, written by the doctor (final diagnosis, course
-- in hospital, condition at discharge, medicines to take home, follow-up), signed once and then final. The stay's own
-- facts (patient, dates, ward, operations) are read from the stay, not stored here. Printed through Report Studio.
--   HospitalDischargeSummaries  one row per admission
--
-- Additive only (one new table). Idempotent (OBJECT_ID guard).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL OR OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h3_admissions.sql first.', 1;

IF OBJECT_ID('dbo.HospitalDischargeSummaries','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDischargeSummaries (
        ID                  int IDENTITY(1,1) NOT NULL,
        CompanyId           int             NOT NULL,
        AdmissionId         int             NOT NULL,
        PatientId           int             NOT NULL,
        FinalDiagnosis      nvarchar(1000)  NULL,
        Course              nvarchar(4000)  NULL,
        Procedures          nvarchar(2000)  NULL,
        Condition           nvarchar(20)    NULL,
        Medications         nvarchar(2000)  NULL,
        Instructions        nvarchar(2000)  NULL,
        FollowUpOn          date            NULL,
        FollowUpWith        nvarchar(200)   NULL,
        Status              nvarchar(10)    NOT NULL,
        CreatedAt           datetime2       NOT NULL,
        CreatedByEmployeeId int             NULL,
        UpdatedAt           datetime2       NULL,
        UpdatedByEmployeeId int             NULL,
        SignedAt            datetime2       NULL,
        SignedByEmployeeId  int             NULL,
        CONSTRAINT PK_HospitalDischargeSummaries PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalDischargeSummaries_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalDischargeSummaries_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalDischargeSummaries_Status CHECK (Status IN ('Draft','Signed')),
        CONSTRAINT CK_HospitalDischargeSummaries_Condition CHECK (Condition IS NULL OR Condition IN ('Improved','Stable','Unchanged','Referred','AgainstAdvice','Deceased')),
        -- a signed summary has its signature, its final diagnosis and the condition at discharge
        CONSTRAINT CK_HospitalDischargeSummaries_Signed CHECK (Status = 'Draft' OR (SignedAt IS NOT NULL AND FinalDiagnosis IS NOT NULL AND Condition IS NOT NULL))
    );
    CREATE UNIQUE INDEX IX_HospitalDischargeSummaries_AdmissionId ON dbo.HospitalDischargeSummaries (AdmissionId);
    CREATE INDEX IX_HospitalDischargeSummaries_CompanyId_PatientId ON dbo.HospitalDischargeSummaries (CompanyId, PatientId);
END

COMMIT TRANSACTION;
