-- ==========================================================================================
-- Hospital H207 - a transfusion is started at the bedside with the wristband and the bag scanned.
--
--   HospitalBloodUnits   + TransfusedAt, TransfusedByEmployeeId (when the transfusion was started, and by whom)
--
-- Columns only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalBloodUnits','U') IS NULL
    THROW 52007, 'Run the blood bank slice (hospital_h41_blood_bank.sql) first.', 1;

IF COL_LENGTH('dbo.HospitalBloodUnits', 'TransfusedAt') IS NULL
    ALTER TABLE dbo.HospitalBloodUnits ADD TransfusedAt datetime2 NULL;
IF COL_LENGTH('dbo.HospitalBloodUnits', 'TransfusedByEmployeeId') IS NULL
    ALTER TABLE dbo.HospitalBloodUnits ADD TransfusedByEmployeeId int NULL;

COMMIT TRANSACTION;
