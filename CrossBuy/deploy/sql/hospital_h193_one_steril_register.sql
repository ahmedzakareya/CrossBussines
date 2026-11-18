-- ==========================================================================================
-- Hospital H193 - one register of sterilisation cycles (owner decision 2026-11-18: merge the two systems).
--
--   HospitalSterilisationCycles  + Load, PhysicalOk, LegacyDentalCycleId
--   moved                         every dental cycle (HospitalSterilCycles) into the one register, once each
--   repointed                     HospitalSterilUses.CycleId and HospitalSterilRecalls.CycleId to the moved rows;
--                                 their foreign keys now reference the one register
--   kept                          HospitalSterilCycles, untouched, as the read-only archive it came from
--
-- Idempotent: a moved cycle is found by its LegacyDentalCycleId; the kits and recalls are repointed only while their
-- foreign keys still reference the old table (the same transaction then moves the keys).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.HospitalSterilisationCycles','U') IS NULL OR OBJECT_ID('dbo.HospitalSterilCycles','U') IS NULL OR OBJECT_ID('dbo.HospitalSterilUses','U') IS NULL
    THROW 51993, 'Run the CSSD (hospital_h42_cssd.sql) and dental practice slices first.', 1;

IF COL_LENGTH('dbo.HospitalSterilisationCycles', 'Load') IS NULL ALTER TABLE dbo.HospitalSterilisationCycles ADD Load nvarchar(300) NULL;
IF COL_LENGTH('dbo.HospitalSterilisationCycles', 'PhysicalOk') IS NULL ALTER TABLE dbo.HospitalSterilisationCycles ADD PhysicalOk bit NULL;
IF COL_LENGTH('dbo.HospitalSterilisationCycles', 'LegacyDentalCycleId') IS NULL ALTER TABLE dbo.HospitalSterilisationCycles ADD LegacyDentalCycleId int NULL;
GO

SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- the dental cycles, into the one register (once each)
INSERT INTO dbo.HospitalSterilisationCycles (CompanyId, CycleNo, Autoclave, Result, Note, StartedAt, StartedByEmployeeId, EndedAt, EndedByEmployeeId,
    ImplantLoad, ExposureMinutes, TemperatureC, PressureKpa, Chemical, Biological, ReleasedAt, ReleasedByEmployeeId, RecalledAt, RecallReason, Load, PhysicalOk, LegacyDentalCycleId)
SELECT d.CompanyId, d.CycleNo, d.Unit,
       CASE WHEN d.Chemical = 'Fail' OR d.Biological = 'Fail' OR d.PhysicalOk = 0 THEN 'Failed' WHEN d.ReleasedAt IS NOT NULL THEN 'Passed' ELSE 'Running' END,
       NULL, d.RunAt, d.OperatorEmployeeId, d.ReleasedAt, d.ReleasedByEmployeeId,
       d.ImplantLoad, d.ExposureMinutes, d.TemperatureC, d.PressureKpa, d.Chemical, d.Biological, d.ReleasedAt, d.ReleasedByEmployeeId, d.RecalledAt, d.RecallReason, d.Load, d.PhysicalOk, d.ID
FROM dbo.HospitalSterilCycles d
WHERE NOT EXISTS (SELECT 1 FROM dbo.HospitalSterilisationCycles s WHERE s.CompanyId = d.CompanyId AND s.LegacyDentalCycleId = d.ID);

-- the kits and recalls follow their cycle, and their keys move to the one register
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_HospitalSterilUses_Cycle' AND referenced_object_id = OBJECT_ID('dbo.HospitalSterilCycles'))
BEGIN
    ALTER TABLE dbo.HospitalSterilUses DROP CONSTRAINT FK_HospitalSterilUses_Cycle;
    UPDATE u SET u.CycleId = s.ID FROM dbo.HospitalSterilUses u JOIN dbo.HospitalSterilisationCycles s ON s.CompanyId = u.CompanyId AND s.LegacyDentalCycleId = u.CycleId;
    ALTER TABLE dbo.HospitalSterilUses WITH CHECK ADD CONSTRAINT FK_HospitalSterilUses_Cycle FOREIGN KEY (CycleId) REFERENCES dbo.HospitalSterilisationCycles (ID);
END

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_HospitalSterilRecalls_Cycle' AND referenced_object_id = OBJECT_ID('dbo.HospitalSterilCycles'))
BEGIN
    ALTER TABLE dbo.HospitalSterilRecalls DROP CONSTRAINT FK_HospitalSterilRecalls_Cycle;
    UPDATE r SET r.CycleId = s.ID FROM dbo.HospitalSterilRecalls r JOIN dbo.HospitalSterilisationCycles s ON s.CompanyId = r.CompanyId AND s.LegacyDentalCycleId = r.CycleId;
    ALTER TABLE dbo.HospitalSterilRecalls WITH CHECK ADD CONSTRAINT FK_HospitalSterilRecalls_Cycle FOREIGN KEY (CycleId) REFERENCES dbo.HospitalSterilisationCycles (ID);
END

COMMIT TRANSACTION;
GO
