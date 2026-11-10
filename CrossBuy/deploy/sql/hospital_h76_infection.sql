-- ==========================================================================================
-- Hospital H76 — infection prevention and control. Phase 2 of the 2026-11-09 gap research.
--   HospitalIsolations       the precautions a patient is nursed under, until ended (one live per patient and precaution)
--   HospitalInvasiveDevices  central lines, urinary catheters and ventilators, inserted and removed (the device-days)
--   HospitalInfectionCases   infections reported by the ward, confirmed or ruled out by infection control
--
-- Additive only (three new tables). Idempotent (OBJECT_ID guards).
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

IF OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL OR OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalWards','U') IS NULL
    THROW 51990, 'Run hospital_h1_facility.sql and hospital_h3_admissions.sql first.', 1;

IF OBJECT_ID('dbo.HospitalInfectionCases','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalInfectionCases (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        CaseNo                 nvarchar(20)   NOT NULL,
        PatientId              int            NOT NULL,
        AdmissionId            int            NULL,
        Kind                   nvarchar(8)    NOT NULL,
        Organism               nvarchar(100)  NULL,
        OnsetDate              date           NOT NULL,
        HospitalAcquired       bit            NOT NULL,
        WardId                 int            NULL,
        DeviceId               int            NULL,
        Status                 nvarchar(10)   NOT NULL,
        Notes                  nvarchar(1000) NULL,
        ReportedAt             datetime2      NOT NULL,
        ReportedByEmployeeId   int            NULL,
        DecidedAt              datetime2      NULL,
        DecidedByEmployeeId    int            NULL,
        DecisionNote           nvarchar(500)  NULL,
        CONSTRAINT PK_HospitalInfectionCases PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalInfectionCases_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalInfectionCases_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalInfectionCases_Ward FOREIGN KEY (WardId) REFERENCES dbo.HospitalWards(ID),
        CONSTRAINT CK_HospitalInfectionCases_Kind CHECK (Kind IN ('CLABSI','CAUTI','VAP','SSI','CDI','MDRO','Other')),
        CONSTRAINT CK_HospitalInfectionCases_Status CHECK (Status IN ('Suspected','Confirmed','RuledOut')),
        -- an MDRO names its organism; a decided case says who decided
        CONSTRAINT CK_HospitalInfectionCases_Organism CHECK (Kind <> 'MDRO' OR Organism IS NOT NULL),
        CONSTRAINT CK_HospitalInfectionCases_Decided CHECK ((Status = 'Suspected') OR (DecidedAt IS NOT NULL))
    );
    CREATE UNIQUE INDEX IX_HospitalInfectionCases_CompanyId_CaseNo ON dbo.HospitalInfectionCases (CompanyId, CaseNo);
    CREATE INDEX IX_HospitalInfectionCases_CompanyId_OnsetDate ON dbo.HospitalInfectionCases (CompanyId, OnsetDate);
END

IF OBJECT_ID('dbo.HospitalIsolations','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalIsolations (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        PatientId              int            NOT NULL,
        AdmissionId            int            NULL,
        Precaution             nvarchar(12)   NOT NULL,
        Organism               nvarchar(100)  NULL,
        Reason                 nvarchar(300)  NULL,
        CaseId                 int            NULL,
        StartedAt              datetime2      NOT NULL,
        StartedByEmployeeId    int            NULL,
        EndedAt                datetime2      NULL,
        EndedByEmployeeId      int            NULL,
        EndNote                nvarchar(300)  NULL,
        CONSTRAINT PK_HospitalIsolations PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalIsolations_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalIsolations_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalIsolations_Case FOREIGN KEY (CaseId) REFERENCES dbo.HospitalInfectionCases(ID),
        CONSTRAINT CK_HospitalIsolations_Precaution CHECK (Precaution IN ('Contact','Droplet','Airborne','Protective')),
        CONSTRAINT CK_HospitalIsolations_Ended CHECK (EndedAt IS NULL OR EndedAt >= StartedAt)
    );
    CREATE INDEX IX_HospitalIsolations_CompanyId_PatientId ON dbo.HospitalIsolations (CompanyId, PatientId);
    -- one live isolation per patient and precaution
    CREATE UNIQUE INDEX UX_HospitalIsolations_Live ON dbo.HospitalIsolations (CompanyId, PatientId, Precaution) WHERE EndedAt IS NULL;
END

IF OBJECT_ID('dbo.HospitalInvasiveDevices','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalInvasiveDevices (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        AdmissionId            int            NOT NULL,
        PatientId              int            NOT NULL,
        Kind                   nvarchar(16)   NOT NULL,
        Site                   nvarchar(100)  NULL,
        InsertedAt             datetime2      NOT NULL,
        InsertedByEmployeeId   int            NULL,
        RemovedAt              datetime2      NULL,
        RemovedByEmployeeId    int            NULL,
        CreatedAt              datetime2      NOT NULL,
        CONSTRAINT PK_HospitalInvasiveDevices PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalInvasiveDevices_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalInvasiveDevices_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalInvasiveDevices_Kind CHECK (Kind IN ('CentralLine','UrinaryCatheter','Ventilator')),
        CONSTRAINT CK_HospitalInvasiveDevices_Removed CHECK (RemovedAt IS NULL OR RemovedAt >= InsertedAt)
    );
    CREATE INDEX IX_HospitalInvasiveDevices_CompanyId_AdmissionId ON dbo.HospitalInvasiveDevices (CompanyId, AdmissionId);
    -- one device of a kind in place per stay
    CREATE UNIQUE INDEX UX_HospitalInvasiveDevices_InPlace ON dbo.HospitalInvasiveDevices (CompanyId, AdmissionId, Kind) WHERE RemovedAt IS NULL;
END

IF OBJECT_ID('dbo.FK_HospitalInfectionCases_Device','F') IS NULL
    ALTER TABLE dbo.HospitalInfectionCases WITH CHECK ADD CONSTRAINT FK_HospitalInfectionCases_Device FOREIGN KEY (DeviceId) REFERENCES dbo.HospitalInvasiveDevices(ID);

COMMIT TRANSACTION;
