-- ==========================================================================================
-- Hospital H130 — a chemotherapy cycle given and a physiotherapy session attended draw their unit's kit from its store,
-- as a finished dialysis session does (H122). Each remembers when its kit left, so it never leaves twice:
--   HospitalChemoCycles.KitIssuedAt     null until the cycle's kit has left the oncology unit's store
--   HospitalRehabSessions.KitIssuedAt   null until the session's kit has left the physiotherapy store
--
-- Two nullable columns; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalChemoCycles','U') IS NULL OR OBJECT_ID('dbo.HospitalRehabSessions','U') IS NULL OR OBJECT_ID('dbo.HospitalUnitKits','U') IS NULL
    THROW 51990, 'Run hospital_h88_oncology.sql, hospital_h89_rehab.sql and hospital_h122_unit_kits.sql first.', 1;

IF COL_LENGTH('dbo.HospitalChemoCycles', 'KitIssuedAt') IS NULL ALTER TABLE dbo.HospitalChemoCycles ADD KitIssuedAt datetime2 NULL;
IF COL_LENGTH('dbo.HospitalRehabSessions', 'KitIssuedAt') IS NULL ALTER TABLE dbo.HospitalRehabSessions ADD KitIssuedAt datetime2 NULL;

COMMIT TRANSACTION;
