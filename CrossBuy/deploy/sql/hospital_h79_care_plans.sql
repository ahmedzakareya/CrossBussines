-- ==========================================================================================
-- Hospital H79 — the problem list and the nursing care plan. Phase 2 of the 2026-11-09 gap research.
--   HospitalProblems       the patient's problems across visits (chronic or acute, active or resolved, ICD-10 when known)
--   HospitalCarePlanItems  a stay's nursing care plan: need, goal, interventions, review date, closed as met or discontinued
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

IF OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL OR OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h3_admissions.sql first.', 1;

IF OBJECT_ID('dbo.HospitalProblems','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalProblems (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        PatientId              int            NOT NULL,
        Code                   nvarchar(10)   NULL,
        Name                   nvarchar(200)  NOT NULL,
        NameEn                 nvarchar(200)  NULL,
        Kind                   nvarchar(8)    NOT NULL,
        Status                 nvarchar(10)   NOT NULL,
        OnsetDate              date           NULL,
        Note                   nvarchar(500)  NULL,
        CreatedAt              datetime2      NOT NULL,
        CreatedByEmployeeId    int            NULL,
        ResolvedAt             datetime2      NULL,
        ResolvedByEmployeeId   int            NULL,
        CONSTRAINT PK_HospitalProblems PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalProblems_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalProblems_Kind CHECK (Kind IN ('Chronic','Acute')),
        CONSTRAINT CK_HospitalProblems_Status CHECK (Status IN ('Active','Resolved')),
        CONSTRAINT CK_HospitalProblems_Resolved CHECK (Status = 'Active' OR ResolvedAt IS NOT NULL)
    );
    CREATE INDEX IX_HospitalProblems_CompanyId_PatientId ON dbo.HospitalProblems (CompanyId, PatientId);
    -- one active entry per coded problem
    CREATE UNIQUE INDEX UX_HospitalProblems_ActiveCode ON dbo.HospitalProblems (CompanyId, PatientId, Code) WHERE Status = 'Active' AND Code IS NOT NULL;
END

IF OBJECT_ID('dbo.HospitalCarePlanItems','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCarePlanItems (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int             NOT NULL,
        AdmissionId           int             NOT NULL,
        PatientId             int             NOT NULL,
        Need                  nvarchar(16)    NOT NULL,
        NeedText              nvarchar(200)   NULL,
        Goal                  nvarchar(500)   NOT NULL,
        Interventions         nvarchar(2000)  NOT NULL,
        ReviewBy              date            NOT NULL,
        Status                nvarchar(14)    NOT NULL,
        Outcome               nvarchar(500)   NULL,
        CreatedAt             datetime2       NOT NULL,
        CreatedByEmployeeId   int             NULL,
        ClosedAt              datetime2       NULL,
        ClosedByEmployeeId    int             NULL,
        CONSTRAINT PK_HospitalCarePlanItems PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalCarePlanItems_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalCarePlanItems_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalCarePlanItems_Need CHECK (Need IN ('Falls','PressureInjury','Pain','Infection','Nutrition','Mobility','Other')),
        CONSTRAINT CK_HospitalCarePlanItems_Status CHECK (Status IN ('Active','Met','Discontinued')),
        CONSTRAINT CK_HospitalCarePlanItems_Closed CHECK (Status = 'Active' OR (ClosedAt IS NOT NULL AND Outcome IS NOT NULL)),
        CONSTRAINT CK_HospitalCarePlanItems_Other CHECK (Need <> 'Other' OR NeedText IS NOT NULL)
    );
    CREATE INDEX IX_HospitalCarePlanItems_CompanyId_AdmissionId ON dbo.HospitalCarePlanItems (CompanyId, AdmissionId);
END

COMMIT TRANSACTION;
