-- ==========================================================================================
-- Hospital H88 — oncology (chemotherapy). Phase 4 (specialty modules).
--   HospitalChemoRegimens       the hospital's protocol library (no clinical content shipped)
--   HospitalChemoRegimenDrugs   each regimen's drugs: dose and basis, route, days, lifetime limit
--   HospitalChemoCourses        a patient on a regimen: diagnosis, stage, intent, height and weight
--   HospitalChemoCycles         each cycle given or held, with what cleared it
--   HospitalChemoDoses          each drug's dose as given in a cycle (frozen)
--
-- Additive only (five new tables). Idempotent (OBJECT_ID guards).
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

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql first.', 1;

IF OBJECT_ID('dbo.HospitalChemoRegimens','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalChemoRegimens (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        Code                  nvarchar(30)   NOT NULL,
        Name                  nvarchar(150)  NOT NULL,
        NameEn                nvarchar(150)  NULL,
        CycleDays             int            NOT NULL,
        MaxCycles             int            NOT NULL,
        Note                  nvarchar(500)  NULL,
        IsActive              bit            NOT NULL,
        CreatedAt             datetime2      NOT NULL,
        CreatedByEmployeeId   int            NULL,
        UpdatedAt             datetime2      NULL,
        CONSTRAINT PK_HospitalChemoRegimens PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalChemoRegimens_Cycle CHECK (CycleDays BETWEEN 7 AND 56 AND MaxCycles BETWEEN 1 AND 52)
    );
    CREATE UNIQUE INDEX IX_HospitalChemoRegimens_CompanyId_Code ON dbo.HospitalChemoRegimens (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalChemoRegimenDrugs','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalChemoRegimenDrugs (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        RegimenId             int            NOT NULL,
        SortOrder             int            NOT NULL,
        DrugName              nvarchar(100)  NOT NULL,
        Dose                  decimal(9,2)   NOT NULL,
        Basis                 nvarchar(4)    NOT NULL,
        Route                 nvarchar(3)    NOT NULL,
        Days                  nvarchar(30)   NOT NULL,
        LifetimeMgM2          decimal(9,1)   NULL,
        CONSTRAINT PK_HospitalChemoRegimenDrugs PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalChemoRegimenDrugs_Regimen FOREIGN KEY (RegimenId) REFERENCES dbo.HospitalChemoRegimens(ID),
        CONSTRAINT CK_HospitalChemoRegimenDrugs_Basis CHECK (Basis IN ('MgM2','MgKg','Mg','AUC')),
        CONSTRAINT CK_HospitalChemoRegimenDrugs_Route CHECK (Route IN ('IV','PO','SC','IM')),
        CONSTRAINT CK_HospitalChemoRegimenDrugs_Dose CHECK (Dose > 0)
    );
    CREATE INDEX IX_HospitalChemoRegimenDrugs_CompanyId_RegimenId ON dbo.HospitalChemoRegimenDrugs (CompanyId, RegimenId);
END

IF OBJECT_ID('dbo.HospitalChemoCourses','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalChemoCourses (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        CourseNo              nvarchar(20)   NOT NULL,
        PatientId             int            NOT NULL,
        RegimenId             int            NOT NULL,
        Diagnosis             nvarchar(200)  NOT NULL,
        Stage                 nvarchar(30)   NULL,
        Intent                nvarchar(12)   NOT NULL,
        HeightCm              decimal(5,1)   NOT NULL,
        WeightKg              decimal(5,1)   NOT NULL,
        StartDate             date           NOT NULL,
        Status                nvarchar(10)   NOT NULL,
        EndReason             nvarchar(300)  NULL,
        EndedAt               datetime2      NULL,
        CreatedAt             datetime2      NOT NULL,
        CreatedByEmployeeId   int            NULL,
        CONSTRAINT PK_HospitalChemoCourses PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalChemoCourses_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalChemoCourses_Regimen FOREIGN KEY (RegimenId) REFERENCES dbo.HospitalChemoRegimens(ID),
        CONSTRAINT CK_HospitalChemoCourses_Status CHECK (Status IN ('Active','Completed','Stopped')),
        CONSTRAINT CK_HospitalChemoCourses_Intent CHECK (Intent IN ('Curative','Adjuvant','Neoadjuvant','Palliative'))
    );
    CREATE UNIQUE INDEX IX_HospitalChemoCourses_CompanyId_CourseNo ON dbo.HospitalChemoCourses (CompanyId, CourseNo);
    CREATE INDEX IX_HospitalChemoCourses_CompanyId_PatientId ON dbo.HospitalChemoCourses (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalChemoCycles','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalChemoCycles (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        CourseId              int            NOT NULL,
        PatientId             int            NOT NULL,
        CycleNo               int            NOT NULL,
        Status                nvarchar(5)    NOT NULL,
        CycleDate             date           NOT NULL,
        HeldUntil             date           NULL,
        WeightKg              decimal(5,1)   NULL,
        BsaM2                 decimal(4,2)   NULL,
        Anc                   decimal(5,2)   NULL,
        Platelets             int            NULL,
        Creatinine            decimal(5,2)   NULL,
        DoseReductionPct      int            NOT NULL,
        ToxicityGrade         int            NULL,
        ToxicityNote          nvarchar(300)  NULL,
        OverrideReason        nvarchar(300)  NULL,
        Reason                nvarchar(300)  NULL,
        CreatedAt             datetime2      NOT NULL,
        CreatedByEmployeeId   int            NULL,
        CONSTRAINT PK_HospitalChemoCycles PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalChemoCycles_Course FOREIGN KEY (CourseId) REFERENCES dbo.HospitalChemoCourses(ID),
        CONSTRAINT FK_HospitalChemoCycles_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalChemoCycles_Status CHECK (Status IN ('Given','Held')),
        CONSTRAINT CK_HospitalChemoCycles_Given CHECK (Status = 'Held' OR (WeightKg IS NOT NULL AND BsaM2 IS NOT NULL)),
        CONSTRAINT CK_HospitalChemoCycles_Held CHECK (Status = 'Given' OR (HeldUntil IS NOT NULL AND Reason IS NOT NULL)),
        CONSTRAINT CK_HospitalChemoCycles_Reduction CHECK (DoseReductionPct BETWEEN 0 AND 75),
        CONSTRAINT CK_HospitalChemoCycles_Toxicity CHECK (ToxicityGrade IS NULL OR ToxicityGrade BETWEEN 0 AND 4)
    );
    CREATE INDEX IX_HospitalChemoCycles_CompanyId_CourseId_CycleNo ON dbo.HospitalChemoCycles (CompanyId, CourseId, CycleNo);
    -- a cycle is given once
    CREATE UNIQUE INDEX UX_HospitalChemoCycles_Given ON dbo.HospitalChemoCycles (CourseId, CycleNo) WHERE Status = 'Given';
END

IF OBJECT_ID('dbo.HospitalChemoDoses','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalChemoDoses (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        CycleId               int            NOT NULL,
        PatientId             int            NOT NULL,
        DrugName              nvarchar(100)  NOT NULL,
        Dose                  decimal(9,2)   NOT NULL,
        Basis                 nvarchar(4)    NOT NULL,
        Route                 nvarchar(3)    NOT NULL,
        Days                  nvarchar(30)   NOT NULL,
        DoseMg                decimal(9,1)   NOT NULL,
        CumulativeMgM2        decimal(9,1)   NULL,
        CONSTRAINT PK_HospitalChemoDoses PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalChemoDoses_Cycle FOREIGN KEY (CycleId) REFERENCES dbo.HospitalChemoCycles(ID),
        CONSTRAINT FK_HospitalChemoDoses_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID)
    );
    CREATE INDEX IX_HospitalChemoDoses_CompanyId_CycleId ON dbo.HospitalChemoDoses (CompanyId, CycleId);
END

COMMIT TRANSACTION;
