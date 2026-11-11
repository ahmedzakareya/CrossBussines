-- ==========================================================================================
-- Hospital H97 — the allergy visit (the approved prototype screen sc-allergy).
--   HospitalImmunoCourses   an allergen immunotherapy course (one extract), active / held / stopped
--   HospitalImmunoDoses     its doses: vial, mL, the 30-minute observation, the reaction
--   HospitalConcepts.Calculator may be 'IgeClass' (the specific-IgE class 0–6 from kU/L)
--
-- Additive only (two new tables, and one more allowed calculator). Idempotent (OBJECT_ID and constraint guards).
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

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalConcepts','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql and hospital_h93_clinical_content.sql first.', 1;

IF OBJECT_ID('dbo.HospitalImmunoCourses','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalImmunoCourses (
        ID                   int IDENTITY(1,1) NOT NULL,
        CompanyId            int            NOT NULL,
        PatientId            int            NOT NULL,
        Extract              nvarchar(150)  NOT NULL,
        ExtractEn            nvarchar(150)  NOT NULL,
        Status               nvarchar(10)   NOT NULL,
        StatusNote           nvarchar(300)  NULL,
        StartedAt            datetime2      NOT NULL,
        StartedByEmployeeId  int            NULL,
        UpdatedAt            datetime2      NULL,
        UpdatedByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalImmunoCourses PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalImmunoCourses_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalImmunoCourses_Status CHECK (Status IN ('Active','Held','Stopped'))
    );
    CREATE INDEX IX_HospitalImmunoCourses_CompanyId_PatientId ON dbo.HospitalImmunoCourses (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalImmunoDoses','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalImmunoDoses (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        CourseId              int            NOT NULL,
        PatientId             int            NOT NULL,
        Seq                   int            NOT NULL,
        Vial                  nvarchar(10)   NOT NULL,
        DoseMl                decimal(6,2)   NOT NULL,
        GivenAt               datetime2      NOT NULL,
        GivenByEmployeeId     int            NULL,
        ObserveUntil          datetime2      NOT NULL,
        Reaction              nvarchar(12)   NOT NULL,
        WhealMm               int            NULL,
        ReactionNote          nvarchar(300)  NULL,
        ReactionAt            datetime2      NULL,
        ReactionByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalImmunoDoses PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalImmunoDoses_Course FOREIGN KEY (CourseId) REFERENCES dbo.HospitalImmunoCourses(ID),
        CONSTRAINT FK_HospitalImmunoDoses_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalImmunoDoses_Reaction CHECK (Reaction IN ('Pending','None','Local','LargeLocal','Systemic')),
        CONSTRAINT CK_HospitalImmunoDoses_Dose CHECK (DoseMl > 0 AND DoseMl <= 1)
    );
    CREATE UNIQUE INDEX IX_HospitalImmunoDoses_CourseId_Seq ON dbo.HospitalImmunoDoses (CourseId, Seq);
    CREATE INDEX IX_HospitalImmunoDoses_CompanyId_PatientId ON dbo.HospitalImmunoDoses (CompanyId, PatientId);
END

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalConcepts_Calculator' AND definition NOT LIKE '%IgeClass%')
BEGIN
    ALTER TABLE dbo.HospitalConcepts DROP CONSTRAINT CK_HospitalConcepts_Calculator;
    ALTER TABLE dbo.HospitalConcepts WITH CHECK ADD CONSTRAINT CK_HospitalConcepts_Calculator CHECK (Calculator IS NULL OR Calculator IN ('Bmi','EgfrCkdEpi2021','Kdigo','Sum','IgeClass'));
END

COMMIT TRANSACTION;
