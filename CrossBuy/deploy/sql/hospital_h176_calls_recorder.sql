-- ==========================================================================================
-- Hospital H176 (recording asked from the mobile app) - whose browser records the file when it is not the requester's.
-- A phone cannot mix both sides of a call into one file, so a recording asked for from the app is recorded by the
-- browser at the other end; the request and the consent stay in the requester's name. NULL = the requester records
-- (every recording before this slice).
--
-- Additive only. Idempotent. Needs hospital_h176_call_recording.sql.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH('dbo.HospitalCallRecordings', 'RecorderEmployeeId') IS NULL
    ALTER TABLE dbo.HospitalCallRecordings ADD RecorderEmployeeId int NULL;

COMMIT TRANSACTION;
