-- ==========================================================================================
-- Hospital H169 - dental sterilisation: a cycle is RELEASED, not just "not failed". The physical record of the run, the
-- implantable-load flag (the biological indicator must pass before release), who released it and when; a pack is
-- checked intact before use; a failure found after release recalls the cycle and lists every patient it was used on
-- for infection control.
--
--   HospitalSterilCycles  + ExposureMinutes, TemperatureC, PressureKpa, PhysicalOk, ImplantLoad, ReleasedAt,
--                           ReleasedByEmployeeId, RecalledAt, RecallReason
--   HospitalSterilUses    + PackIntact
--   HospitalSterilRecalls   (new)
--
-- Cycles already recorded keep their indicators; a cycle whose chemical indicator had passed before H169 is taken as
-- released at the time it was recorded (it was usable then), so no kit history changes meaning.
-- Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalSterilCycles','U') IS NULL OR OBJECT_ID('dbo.HospitalSterilUses','U') IS NULL
    THROW 51986, 'Run hospital_h16_dental_practice.sql first.', 1;

IF COL_LENGTH('dbo.HospitalSterilCycles', 'ExposureMinutes') IS NULL ALTER TABLE dbo.HospitalSterilCycles ADD ExposureMinutes int NULL;
IF COL_LENGTH('dbo.HospitalSterilCycles', 'TemperatureC') IS NULL ALTER TABLE dbo.HospitalSterilCycles ADD TemperatureC decimal(5,1) NULL;
IF COL_LENGTH('dbo.HospitalSterilCycles', 'PressureKpa') IS NULL ALTER TABLE dbo.HospitalSterilCycles ADD PressureKpa decimal(6,1) NULL;
IF COL_LENGTH('dbo.HospitalSterilCycles', 'PhysicalOk') IS NULL ALTER TABLE dbo.HospitalSterilCycles ADD PhysicalOk bit NULL;
IF COL_LENGTH('dbo.HospitalSterilCycles', 'ImplantLoad') IS NULL ALTER TABLE dbo.HospitalSterilCycles ADD ImplantLoad bit NOT NULL CONSTRAINT DF_HospitalSterilCycles_ImplantLoad DEFAULT (0);
IF COL_LENGTH('dbo.HospitalSterilCycles', 'ReleasedAt') IS NULL
BEGIN
    ALTER TABLE dbo.HospitalSterilCycles ADD ReleasedAt datetime2 NULL;
    -- once, as the column arrives: the cycles usable before H169 (chemical passed, nothing failed) stay usable, released
    -- when they were recorded. A later run never touches a cycle still waiting for its release.
    EXEC (N'UPDATE dbo.HospitalSterilCycles SET ReleasedAt = CreatedAt WHERE Chemical = ''Pass'' AND Biological <> ''Fail''');
END
IF COL_LENGTH('dbo.HospitalSterilCycles', 'ReleasedByEmployeeId') IS NULL ALTER TABLE dbo.HospitalSterilCycles ADD ReleasedByEmployeeId int NULL;
IF COL_LENGTH('dbo.HospitalSterilCycles', 'RecalledAt') IS NULL ALTER TABLE dbo.HospitalSterilCycles ADD RecalledAt datetime2 NULL;
IF COL_LENGTH('dbo.HospitalSterilCycles', 'RecallReason') IS NULL ALTER TABLE dbo.HospitalSterilCycles ADD RecallReason nvarchar(300) NULL;
IF COL_LENGTH('dbo.HospitalSterilUses', 'PackIntact') IS NULL ALTER TABLE dbo.HospitalSterilUses ADD PackIntact bit NULL;

IF OBJECT_ID('dbo.HospitalSterilRecalls','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSterilRecalls (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        CycleId int NOT NULL,
        PatientId int NOT NULL,
        UseId int NOT NULL,
        CreatedAt datetime2 NOT NULL,
        NotifiedAt datetime2 NULL,
        NotifiedByEmployeeId int NULL,
        Outcome nvarchar(300) NULL,
        ClosedAt datetime2 NULL,
        ClosedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalSterilRecalls PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalSterilRecalls_Cycle FOREIGN KEY (CycleId) REFERENCES dbo.HospitalSterilCycles (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalSterilRecalls_CompanyId_CycleId_UseId ON dbo.HospitalSterilRecalls (CompanyId, CycleId, UseId);
    CREATE INDEX IX_HospitalSterilRecalls_CompanyId_PatientId ON dbo.HospitalSterilRecalls (CompanyId, PatientId);
END

COMMIT TRANSACTION;
