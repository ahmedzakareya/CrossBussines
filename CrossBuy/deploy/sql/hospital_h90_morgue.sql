-- ==========================================================================================
-- Hospital H90 — the mortuary. Phase 4 (specialty modules).
--   HospitalMorgueCompartments   the cold chambers' places
--   HospitalMorgueCases          each body received: where it lies, the holds on it, its release or transfer
--   HospitalMorgueBelongings     what was found with the body
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

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalDeaths','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql and the H59 death certificate slice first.', 1;

IF OBJECT_ID('dbo.HospitalMorgueCompartments','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalMorgueCompartments (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        Code                  nvarchar(10)   NOT NULL,
        Kind                  nvarchar(8)    NOT NULL,
        IsActive              bit            NOT NULL,
        Note                  nvarchar(200)  NULL,
        CONSTRAINT PK_HospitalMorgueCompartments PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalMorgueCompartments_Kind CHECK (Kind IN ('Cold','Freezer'))
    );
    CREATE UNIQUE INDEX IX_HospitalMorgueCompartments_CompanyId_Code ON dbo.HospitalMorgueCompartments (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalMorgueCases','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalMorgueCases (
        ID                         int IDENTITY(1,1) NOT NULL,
        CompanyId                  int            NOT NULL,
        CaseNo                     nvarchar(20)   NOT NULL,
        PatientId                  int            NULL,
        DeathId                    int            NULL,
        Description                nvarchar(500)  NULL,
        TagNo                      nvarchar(30)   NOT NULL,
        CompartmentId              int            NOT NULL,
        Status                     nvarchar(12)   NOT NULL,
        ReceivedAt                 datetime2      NOT NULL,
        ReceivedByEmployeeId       int            NULL,
        BroughtFrom                nvarchar(150)  NULL,
        MedicoLegal                bit            NOT NULL,
        PoliceRef                  nvarchar(60)   NULL,
        PoliceClearedAt            datetime2      NULL,
        PoliceClearedByEmployeeId  int            NULL,
        AutopsyStatus              nvarchar(10)   NOT NULL,
        AutopsyDoneAt              datetime2      NULL,
        AutopsyNote                nvarchar(500)  NULL,
        ReleasedAt                 datetime2      NULL,
        ReleasedByEmployeeId       int            NULL,
        ReleasedTo                 nvarchar(150)  NULL,
        ReleasedToRelation         nvarchar(50)   NULL,
        ReleasedToIdNo             nvarchar(30)   NULL,
        ReleasedToPhone            nvarchar(30)   NULL,
        Destination                nvarchar(150)  NULL,
        Note                       nvarchar(500)  NULL,
        CONSTRAINT PK_HospitalMorgueCases PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalMorgueCases_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalMorgueCases_Death FOREIGN KEY (DeathId) REFERENCES dbo.HospitalDeaths(ID),
        CONSTRAINT FK_HospitalMorgueCases_Compartment FOREIGN KEY (CompartmentId) REFERENCES dbo.HospitalMorgueCompartments(ID),
        CONSTRAINT CK_HospitalMorgueCases_Status CHECK (Status IN ('InStorage','Released','Transferred')),
        CONSTRAINT CK_HospitalMorgueCases_Autopsy CHECK (AutopsyStatus IN ('None','Requested','Done')),
        CONSTRAINT CK_HospitalMorgueCases_Who CHECK (PatientId IS NOT NULL OR (Description IS NOT NULL AND MedicoLegal = 1)),
        CONSTRAINT CK_HospitalMorgueCases_Out CHECK (Status = 'InStorage' OR (ReleasedAt IS NOT NULL AND ReleasedTo IS NOT NULL))
    );
    CREATE UNIQUE INDEX IX_HospitalMorgueCases_CompanyId_CaseNo ON dbo.HospitalMorgueCases (CompanyId, CaseNo);
    CREATE INDEX IX_HospitalMorgueCases_CompanyId_Status ON dbo.HospitalMorgueCases (CompanyId, Status);
    -- one body in a compartment
    CREATE UNIQUE INDEX UX_HospitalMorgueCases_Compartment ON dbo.HospitalMorgueCases (CompartmentId) WHERE Status = 'InStorage';
END

IF OBJECT_ID('dbo.HospitalMorgueBelongings','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalMorgueBelongings (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        CaseId                int            NOT NULL,
        Item                  nvarchar(150)  NOT NULL,
        Quantity              int            NOT NULL,
        Note                  nvarchar(200)  NULL,
        RecordedAt            datetime2      NOT NULL,
        RecordedByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalMorgueBelongings PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalMorgueBelongings_Case FOREIGN KEY (CaseId) REFERENCES dbo.HospitalMorgueCases(ID),
        CONSTRAINT CK_HospitalMorgueBelongings_Quantity CHECK (Quantity BETWEEN 1 AND 999)
    );
    CREATE INDEX IX_HospitalMorgueBelongings_CompanyId_CaseId ON dbo.HospitalMorgueBelongings (CompanyId, CaseId);
END

COMMIT TRANSACTION;
