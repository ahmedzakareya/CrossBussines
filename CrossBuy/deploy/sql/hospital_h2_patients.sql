-- ==========================================================================================
-- Hospital H2 — the patient registry: patients and the record-access log.
--
-- TWO NEW TABLES, no ALTER of any existing table, so an environment that has not run this
-- script keeps every other module working; only the patient screens are absent.
--
-- A patient is identified by a generated MRN, unique in the company; a national ID, when one
-- is given, opens at most one file per company (filtered unique index).
--
-- Idempotent: safe to run repeatedly.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- ---- patients ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPatients (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPatients PRIMARY KEY,
        CompanyId           int            NOT NULL,
        Mrn                 nvarchar(30)   NOT NULL,
        Name                nvarchar(200)  NOT NULL,
        NameEn              nvarchar(200)  NOT NULL,
        Sex                 nvarchar(1)    NOT NULL,
        BirthDate           datetime2      NULL,
        NationalId          nvarchar(30)   NULL,
        NationalityId       int            NULL,   -- CountriesLookup.ID
        Phone               nvarchar(30)   NULL,
        BloodGroup          nvarchar(3)    NULL,
        Allergies           nvarchar(1000) NULL,
        ChronicConditions   nvarchar(1000) NULL,
        EmergencyName       nvarchar(200)  NULL,
        EmergencyRelation   nvarchar(60)   NULL,
        EmergencyPhone      nvarchar(30)   NULL,
        ConsentSigned       bit            NOT NULL CONSTRAINT DF_HospitalPatients_Consent DEFAULT(0),
        IsActive            bit            NOT NULL CONSTRAINT DF_HospitalPatients_Active DEFAULT(1),
        CreatedAt           datetime2      NOT NULL CONSTRAINT DF_HospitalPatients_Created DEFAULT(SYSUTCDATETIME()),
        CreatedByEmployeeId int            NULL,
        UpdatedAt           datetime2      NULL,
        UpdatedByEmployeeId int            NULL,
        CONSTRAINT CK_HospitalPatients_Sex CHECK (Sex IN ('M','F')),
        CONSTRAINT CK_HospitalPatients_Blood CHECK (BloodGroup IS NULL OR BloodGroup IN ('O+','O-','A+','A-','B+','B-','AB+','AB-'))
    );
    CREATE UNIQUE INDEX UX_HospitalPatients_Mrn ON dbo.HospitalPatients (CompanyId, Mrn);
    CREATE UNIQUE INDEX UX_HospitalPatients_NationalId ON dbo.HospitalPatients (CompanyId, NationalId) WHERE NationalId IS NOT NULL;
    CREATE INDEX IX_HospitalPatients_Phone ON dbo.HospitalPatients (CompanyId, Phone);
END

-- ---- record-access log: who opened, created or changed which file, and when ---------------
IF OBJECT_ID('dbo.HospitalPatientAccessLogs','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPatientAccessLogs (
        ID                  bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPatientAccessLogs PRIMARY KEY,
        CompanyId           int            NOT NULL,
        PatientId           int            NOT NULL,
        EmployeeId          int            NULL,
        Action              nvarchar(20)   NOT NULL,
        At                  datetime2      NOT NULL CONSTRAINT DF_HospitalPatientAccessLogs_At DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT CK_HospitalPatientAccessLogs_Action CHECK (Action IN ('open','create','update'))
    );
    CREATE INDEX IX_HospitalPatientAccessLogs_Patient ON dbo.HospitalPatientAccessLogs (CompanyId, PatientId, At);
END

-- ---- schema guard: a table that exists with the wrong shape is a failure, not a pass ------
IF COL_LENGTH('dbo.HospitalPatients','Mrn') IS NULL OR COL_LENGTH('dbo.HospitalPatients','NameEn') IS NULL
    THROW 51911, 'HospitalPatients exists but lacks the H2 identity columns: schema mismatch.', 1;
IF COL_LENGTH('dbo.HospitalPatientAccessLogs','Action') IS NULL
    THROW 51912, 'HospitalPatientAccessLogs exists but lacks Action: schema mismatch.', 1;

COMMIT;
