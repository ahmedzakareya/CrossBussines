-- ==========================================================================================
-- Hospital H5 — the consultation: encounters, their diagnoses, prescriptions, and the
-- diagnosis code catalogue.
--
-- An ENCOUNTER is the doctor's record of one appointment (one per appointment): the SOAP note,
-- the vitals, the follow-up and referral. It is a Draft while the doctor writes and Signed once
-- closed; a signed encounter is never edited again.
--
-- FOUR NEW TABLES. Idempotent: safe to run repeatedly.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDiagnosisCodes','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDiagnosisCodes (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalDiagnosisCodes PRIMARY KEY,
        CompanyId           int            NOT NULL,
        Code                nvarchar(20)   NOT NULL,   -- ICD-10, or the country's edition of it
        Name                nvarchar(300)  NOT NULL,
        NameEn              nvarchar(300)  NOT NULL,
        IsActive            bit            NOT NULL CONSTRAINT DF_HospitalDiagnosisCodes_Active DEFAULT(1),
        CreatedAt           datetime2      NOT NULL CONSTRAINT DF_HospitalDiagnosisCodes_Created DEFAULT(SYSUTCDATETIME())
    );
    CREATE UNIQUE INDEX UX_HospitalDiagnosisCodes_Code ON dbo.HospitalDiagnosisCodes (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalEncounters','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalEncounters (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalEncounters PRIMARY KEY,
        CompanyId           int            NOT NULL,
        AppointmentId       int            NOT NULL,
        PatientId           int            NOT NULL,
        PractitionerId      int            NOT NULL,
        Status              nvarchar(20)   NOT NULL CONSTRAINT DF_HospitalEncounters_Status DEFAULT('Draft'),
        ChiefComplaint      nvarchar(400)  NULL,
        History             nvarchar(4000) NULL,   -- S
        Examination         nvarchar(4000) NULL,   -- O
        PlanNote            nvarchar(4000) NULL,   -- P (free text)
        BpSystolic          int            NULL,
        BpDiastolic         int            NULL,
        HeartRate           int            NULL,
        TemperatureC        decimal(4,1)   NULL,
        SpO2                int            NULL,
        WeightKg            decimal(5,1)   NULL,
        HeightCm            decimal(5,1)   NULL,
        FollowUpDays        int            NULL,
        ReferToDepartmentId int            NULL,
        StartedAt           datetime2      NOT NULL,
        SignedAt            datetime2      NULL,
        SignedByEmployeeId  int            NULL,
        CreatedAt           datetime2      NOT NULL CONSTRAINT DF_HospitalEncounters_Created DEFAULT(SYSUTCDATETIME()),
        CreatedByEmployeeId int            NULL,
        UpdatedAt           datetime2      NULL,
        UpdatedByEmployeeId int            NULL,
        CONSTRAINT CK_HospitalEncounters_Status CHECK (Status IN ('Draft','Signed')),
        CONSTRAINT CK_HospitalEncounters_Signed CHECK ((Status = 'Draft' AND SignedAt IS NULL) OR (Status = 'Signed' AND SignedAt IS NOT NULL))
    );
    CREATE UNIQUE INDEX UX_HospitalEncounters_Appointment ON dbo.HospitalEncounters (CompanyId, AppointmentId);
    CREATE INDEX IX_HospitalEncounters_Patient ON dbo.HospitalEncounters (CompanyId, PatientId, StartedAt);
END

IF OBJECT_ID('dbo.HospitalEncounterDiagnoses','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalEncounterDiagnoses (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalEncounterDiagnoses PRIMARY KEY,
        CompanyId           int            NOT NULL,
        EncounterId         int            NOT NULL CONSTRAINT FK_HospitalEncounterDiagnoses_Encounter REFERENCES dbo.HospitalEncounters(ID),
        Code                nvarchar(20)   NOT NULL,
        Name                nvarchar(300)  NOT NULL,
        NameEn              nvarchar(300)  NOT NULL,
        IsPrimary           bit            NOT NULL CONSTRAINT DF_HospitalEncounterDiagnoses_Primary DEFAULT(0),
        SortOrder           int            NOT NULL CONSTRAINT DF_HospitalEncounterDiagnoses_Sort DEFAULT(0)
    );
    CREATE INDEX IX_HospitalEncounterDiagnoses_Encounter ON dbo.HospitalEncounterDiagnoses (CompanyId, EncounterId);
END

IF OBJECT_ID('dbo.HospitalPrescriptionLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPrescriptionLines (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPrescriptionLines PRIMARY KEY,
        CompanyId           int            NOT NULL,
        EncounterId         int            NOT NULL CONSTRAINT FK_HospitalPrescriptionLines_Encounter REFERENCES dbo.HospitalEncounters(ID),
        Drug                nvarchar(200)  NOT NULL,
        Instructions        nvarchar(400)  NULL,   -- dose, frequency, duration
        Quantity            int            NOT NULL CONSTRAINT DF_HospitalPrescriptionLines_Qty DEFAULT(1),
        SortOrder           int            NOT NULL CONSTRAINT DF_HospitalPrescriptionLines_Sort DEFAULT(0),
        CONSTRAINT CK_HospitalPrescriptionLines_Qty CHECK (Quantity BETWEEN 1 AND 10000)
    );
    CREATE INDEX IX_HospitalPrescriptionLines_Encounter ON dbo.HospitalPrescriptionLines (CompanyId, EncounterId);
END

IF COL_LENGTH('dbo.HospitalEncounters','ChiefComplaint') IS NULL OR COL_LENGTH('dbo.HospitalEncounterDiagnoses','IsPrimary') IS NULL
    THROW 51941, 'Hospital consultation tables exist with the wrong shape: schema mismatch.', 1;

COMMIT;
