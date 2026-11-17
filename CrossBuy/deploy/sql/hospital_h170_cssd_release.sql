-- ==========================================================================================
-- Hospital H170 - central sterile supply: the release policy of the dental cycles (H169) for the theatre sets.
-- A cycle that ends with its physical and chemical checks met puts its sets in QUARANTINE; a person releases them
-- (a load with implantable items only after its biological indicator passes); a set is issued only from a pack checked
-- intact; a failure found after release recalls the cycle and lists every theatre case its sets reached.
--
--   HospitalInstrumentSets        status Quarantine allowed
--   HospitalSetEvents             kinds Released, Recalled allowed; + PackIntact
--   HospitalSterilisationCycles   + ImplantLoad, ExposureMinutes, TemperatureC, PressureKpa, Chemical, Biological,
--                                   ReleasedAt, ReleasedByEmployeeId, RecalledAt, RecallReason
--   HospitalCssdRecalls           (new)
--
-- A cycle that had passed before H170 made its sets sterile at once: it is taken as chemically passed and released
-- when it ended, by whoever ended it (once, as the column arrives), so no set's history changes meaning.
-- Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalSterilisationCycles','U') IS NULL OR OBJECT_ID('dbo.HospitalInstrumentSets','U') IS NULL OR OBJECT_ID('dbo.HospitalSetEvents','U') IS NULL
    THROW 51985, 'Run hospital_h42_cssd.sql first.', 1;

-- the two status lists, widened
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalInstrumentSets_Status' AND definition LIKE '%Quarantine%')
BEGIN
    IF OBJECT_ID('dbo.CK_HospitalInstrumentSets_Status','C') IS NOT NULL ALTER TABLE dbo.HospitalInstrumentSets DROP CONSTRAINT CK_HospitalInstrumentSets_Status;
    ALTER TABLE dbo.HospitalInstrumentSets ADD CONSTRAINT CK_HospitalInstrumentSets_Status CHECK (Status IN (N'AwaitingWash', N'Packed', N'InAutoclave', N'Quarantine', N'Sterile', N'InUse'));
END
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalSetEvents_Kind' AND definition LIKE '%Recalled%')
BEGIN
    IF OBJECT_ID('dbo.CK_HospitalSetEvents_Kind','C') IS NOT NULL ALTER TABLE dbo.HospitalSetEvents DROP CONSTRAINT CK_HospitalSetEvents_Kind;
    ALTER TABLE dbo.HospitalSetEvents ADD CONSTRAINT CK_HospitalSetEvents_Kind CHECK (Kind IN (N'Washed', N'Loaded', N'Sterilised', N'CycleFailed', N'Issued', N'Returned', N'Reprocess', N'Released', N'Recalled'));
END
IF COL_LENGTH('dbo.HospitalSetEvents', 'PackIntact') IS NULL ALTER TABLE dbo.HospitalSetEvents ADD PackIntact bit NULL;

-- the cycle's record and its release
IF COL_LENGTH('dbo.HospitalSterilisationCycles', 'ImplantLoad') IS NULL ALTER TABLE dbo.HospitalSterilisationCycles ADD ImplantLoad bit NOT NULL CONSTRAINT DF_HospitalSterilisationCycles_ImplantLoad DEFAULT (0);
IF COL_LENGTH('dbo.HospitalSterilisationCycles', 'ExposureMinutes') IS NULL ALTER TABLE dbo.HospitalSterilisationCycles ADD ExposureMinutes int NULL;
IF COL_LENGTH('dbo.HospitalSterilisationCycles', 'TemperatureC') IS NULL ALTER TABLE dbo.HospitalSterilisationCycles ADD TemperatureC decimal(5,1) NULL;
IF COL_LENGTH('dbo.HospitalSterilisationCycles', 'PressureKpa') IS NULL ALTER TABLE dbo.HospitalSterilisationCycles ADD PressureKpa decimal(6,1) NULL;
IF COL_LENGTH('dbo.HospitalSterilisationCycles', 'Chemical') IS NULL
    ALTER TABLE dbo.HospitalSterilisationCycles ADD Chemical nvarchar(10) NOT NULL CONSTRAINT DF_HospitalSterilisationCycles_Chemical DEFAULT (N'Pending')
        CONSTRAINT CK_HospitalSterilisationCycles_Chemical CHECK (Chemical IN (N'Pending', N'Pass', N'Fail'));
IF COL_LENGTH('dbo.HospitalSterilisationCycles', 'Biological') IS NULL
    ALTER TABLE dbo.HospitalSterilisationCycles ADD Biological nvarchar(10) NOT NULL CONSTRAINT DF_HospitalSterilisationCycles_Biological DEFAULT (N'Pending')
        CONSTRAINT CK_HospitalSterilisationCycles_Biological CHECK (Biological IN (N'Pending', N'Pass', N'Fail'));
IF COL_LENGTH('dbo.HospitalSterilisationCycles', 'ReleasedByEmployeeId') IS NULL ALTER TABLE dbo.HospitalSterilisationCycles ADD ReleasedByEmployeeId int NULL;
IF COL_LENGTH('dbo.HospitalSterilisationCycles', 'ReleasedAt') IS NULL
BEGIN
    ALTER TABLE dbo.HospitalSterilisationCycles ADD ReleasedAt datetime2 NULL;
    -- once, as the column arrives: a cycle passed before H170 made its sets sterile at once — it stays released
    EXEC (N'UPDATE dbo.HospitalSterilisationCycles SET ReleasedAt = EndedAt, ReleasedByEmployeeId = EndedByEmployeeId, Chemical = N''Pass'' WHERE Result = N''Passed''');
END
IF COL_LENGTH('dbo.HospitalSterilisationCycles', 'RecalledAt') IS NULL ALTER TABLE dbo.HospitalSterilisationCycles ADD RecalledAt datetime2 NULL;
IF COL_LENGTH('dbo.HospitalSterilisationCycles', 'RecallReason') IS NULL ALTER TABLE dbo.HospitalSterilisationCycles ADD RecallReason nvarchar(300) NULL;

-- the theatre cases a recalled cycle's sets reached
IF OBJECT_ID('dbo.HospitalCssdRecalls','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCssdRecalls (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        CycleId int NOT NULL,
        SetId int NOT NULL,
        EventId int NOT NULL,
        SurgeryId int NOT NULL,
        PatientId int NOT NULL,
        CreatedAt datetime2 NOT NULL,
        NotifiedAt datetime2 NULL,
        NotifiedByEmployeeId int NULL,
        Outcome nvarchar(300) NULL,
        ClosedAt datetime2 NULL,
        ClosedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalCssdRecalls PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalCssdRecalls_Cycle FOREIGN KEY (CycleId) REFERENCES dbo.HospitalSterilisationCycles (ID),
        CONSTRAINT FK_HospitalCssdRecalls_Set FOREIGN KEY (SetId) REFERENCES dbo.HospitalInstrumentSets (ID),
        CONSTRAINT FK_HospitalCssdRecalls_Event FOREIGN KEY (EventId) REFERENCES dbo.HospitalSetEvents (ID),
        CONSTRAINT FK_HospitalCssdRecalls_Surgery FOREIGN KEY (SurgeryId) REFERENCES dbo.HospitalSurgeries (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalCssdRecalls_CompanyId_CycleId_EventId ON dbo.HospitalCssdRecalls (CompanyId, CycleId, EventId);
    CREATE INDEX IX_HospitalCssdRecalls_CompanyId_PatientId ON dbo.HospitalCssdRecalls (CompanyId, PatientId);
END

COMMIT TRANSACTION;
