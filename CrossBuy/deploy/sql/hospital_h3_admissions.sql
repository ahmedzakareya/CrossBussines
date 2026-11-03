-- ==========================================================================================
-- Hospital H3 — accommodation: admissions, bed stays, and the patient's photo.
--
-- An ADMISSION is one inpatient episode of one patient. Inside it, a BED STAY is each stretch
-- of time in one bed: a transfer ends one stay and starts the next, so the days spent in each
-- room type stay countable for the bill.
--
-- The database itself keeps the two rules a race could break: a patient has at most one open
-- admission, and a bed has at most one open stay (filtered unique indexes).
--
-- TWO NEW TABLES and ONE NULLABLE COLUMN on HospitalPatients (PhotoPath, H2's own table).
-- Idempotent: safe to run repeatedly.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51920, 'HospitalPatients is missing: run hospital_h2_patients.sql first.', 1;

-- ---- the patient's photo (shown on the file, the registry and the bed) --------------------
IF COL_LENGTH('dbo.HospitalPatients','PhotoPath') IS NULL
    ALTER TABLE dbo.HospitalPatients ADD PhotoPath nvarchar(400) NULL;

-- ---- admissions: one inpatient episode ----------------------------------------------------
IF OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalAdmissions (
        ID                     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalAdmissions PRIMARY KEY,
        CompanyId              int            NOT NULL,
        PatientId              int            NOT NULL,
        Status                 nvarchar(20)   NOT NULL CONSTRAINT DF_HospitalAdmissions_Status DEFAULT('Active'),
        AdmittedAt             datetime2      NOT NULL,
        DischargedAt           datetime2      NULL,
        Diagnosis              nvarchar(400)  NULL,
        AttendingEmployeeId    int            NULL,
        CreatedAt              datetime2      NOT NULL CONSTRAINT DF_HospitalAdmissions_Created DEFAULT(SYSUTCDATETIME()),
        CreatedByEmployeeId    int            NULL,
        DischargedByEmployeeId int            NULL,
        CONSTRAINT CK_HospitalAdmissions_Status CHECK (Status IN ('Active','Discharged')),
        CONSTRAINT CK_HospitalAdmissions_Discharge CHECK ((Status = 'Active' AND DischargedAt IS NULL) OR (Status = 'Discharged' AND DischargedAt IS NOT NULL))
    );
    CREATE UNIQUE INDEX UX_HospitalAdmissions_OneOpenPerPatient ON dbo.HospitalAdmissions (CompanyId, PatientId) WHERE Status = 'Active';
    CREATE INDEX IX_HospitalAdmissions_Patient ON dbo.HospitalAdmissions (CompanyId, PatientId, AdmittedAt);
END

-- ---- bed stays: each stretch in one bed -----------------------------------------------------
IF OBJECT_ID('dbo.HospitalBedStays','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalBedStays (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalBedStays PRIMARY KEY,
        CompanyId           int            NOT NULL,
        AdmissionId         int            NOT NULL CONSTRAINT FK_HospitalBedStays_Admission REFERENCES dbo.HospitalAdmissions(ID),
        PatientId           int            NOT NULL,
        BedId               int            NOT NULL,
        RoomId              int            NOT NULL,
        StartedAt           datetime2      NOT NULL,
        EndedAt             datetime2      NULL,
        EndReason           nvarchar(20)   NULL,
        CreatedByEmployeeId int            NULL,
        CONSTRAINT CK_HospitalBedStays_End CHECK (EndedAt IS NULL OR EndedAt >= StartedAt),
        CONSTRAINT CK_HospitalBedStays_Reason CHECK (EndReason IS NULL OR EndReason IN ('transfer','discharge'))
    );
    CREATE UNIQUE INDEX UX_HospitalBedStays_OneOpenPerBed ON dbo.HospitalBedStays (CompanyId, BedId) WHERE EndedAt IS NULL;
    CREATE INDEX IX_HospitalBedStays_Admission ON dbo.HospitalBedStays (CompanyId, AdmissionId);
    CREATE INDEX IX_HospitalBedStays_Room ON dbo.HospitalBedStays (CompanyId, RoomId, EndedAt);
END

-- ---- schema guard -------------------------------------------------------------------------
IF COL_LENGTH('dbo.HospitalAdmissions','Status') IS NULL OR COL_LENGTH('dbo.HospitalBedStays','EndedAt') IS NULL
    THROW 51921, 'Hospital admission tables exist with the wrong shape: schema mismatch.', 1;

COMMIT;
