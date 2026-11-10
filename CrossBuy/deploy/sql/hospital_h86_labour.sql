-- ==========================================================================================
-- Hospital H86 — the partograph (labour monitoring). Phase 4 (maternity).
--   HospitalLabours             a labour on the mother's stay: open, then closed with how it ended
--   HospitalLabourObservations  each observation: cervix, descent, contractions, fetal heart, liquor, mother's vitals
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

IF OBJECT_ID('dbo.HospitalLabours','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalLabours (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        AdmissionId           int            NOT NULL,
        PatientId             int            NOT NULL,
        Status                nvarchar(8)    NOT NULL,
        StartedAt             datetime2      NOT NULL,
        StartedByEmployeeId   int            NULL,
        Outcome               nvarchar(12)   NULL,
        ClosedAt              datetime2      NULL,
        ClosedByEmployeeId    int            NULL,
        Note                  nvarchar(500)  NULL,
        CONSTRAINT PK_HospitalLabours PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalLabours_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalLabours_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalLabours_Status CHECK (Status IN ('Active','Closed')),
        CONSTRAINT CK_HospitalLabours_Outcome CHECK (Outcome IS NULL OR Outcome IN ('Vaginal','Assisted','Caesarean','Transferred')),
        CONSTRAINT CK_HospitalLabours_Closed CHECK (Status = 'Active' OR (ClosedAt IS NOT NULL AND Outcome IS NOT NULL))
    );
    CREATE INDEX IX_HospitalLabours_CompanyId_AdmissionId ON dbo.HospitalLabours (CompanyId, AdmissionId);
    -- one labour in progress per stay
    CREATE UNIQUE INDEX UX_HospitalLabours_Active ON dbo.HospitalLabours (CompanyId, AdmissionId) WHERE Status = 'Active';
END

IF OBJECT_ID('dbo.HospitalLabourObservations','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalLabourObservations (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        LabourId              int            NOT NULL,
        PatientId             int            NOT NULL,
        At                    datetime2      NOT NULL,
        CervixCm              decimal(3,1)   NULL,
        DescentFifths         int            NULL,
        ContractionsPer10     int            NULL,
        ContractionSeconds    int            NULL,
        FetalHeartRate        int            NULL,
        Liquor                nvarchar(1)    NULL,
        BpSystolic            int            NULL,
        BpDiastolic           int            NULL,
        Pulse                 int            NULL,
        TemperatureC          decimal(4,1)   NULL,
        Note                  nvarchar(300)  NULL,
        CreatedAt             datetime2      NOT NULL,
        CreatedByEmployeeId   int            NULL,
        CONSTRAINT PK_HospitalLabourObservations PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalLabourObservations_Labour FOREIGN KEY (LabourId) REFERENCES dbo.HospitalLabours(ID),
        CONSTRAINT FK_HospitalLabourObservations_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalLabourObservations_Cervix CHECK (CervixCm IS NULL OR CervixCm BETWEEN 0 AND 10),
        CONSTRAINT CK_HospitalLabourObservations_Descent CHECK (DescentFifths IS NULL OR DescentFifths BETWEEN 0 AND 5),
        CONSTRAINT CK_HospitalLabourObservations_Contractions CHECK (ContractionsPer10 IS NULL OR ContractionsPer10 BETWEEN 0 AND 10),
        CONSTRAINT CK_HospitalLabourObservations_Liquor CHECK (Liquor IS NULL OR Liquor IN ('I','C','M','B')),
        CONSTRAINT CK_HospitalLabourObservations_Fhr CHECK (FetalHeartRate IS NULL OR FetalHeartRate BETWEEN 50 AND 240)
    );
    CREATE INDEX IX_HospitalLabourObservations_CompanyId_LabourId_At ON dbo.HospitalLabourObservations (CompanyId, LabourId, At);
END

COMMIT TRANSACTION;
