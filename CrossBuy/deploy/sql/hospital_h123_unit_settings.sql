-- ==========================================================================================
-- Hospital H123 — each treatment unit names its session service from the services list. A completed dialysis session,
-- chemotherapy cycle or physiotherapy session was charged from a service found by a fixed hidden code (HD-SESSION,
-- CHEMO-ADMIN, PT-SESSION): whoever kept the price list had to know the code. The unit's settings now name the service:
--   HospitalUnitKits.SessionServiceId   the catalogue service one session is charged as (priced by the bill's list)
--   HospitalUnitKits.Unit               may be Dialysis, Chemo or Physio
--
-- One nullable column + a wider rule; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalUnitKits','U') IS NULL OR OBJECT_ID('dbo.HospitalServices','U') IS NULL
    THROW 51990, 'Run hospital_h122_unit_kits.sql first.', 1;

IF COL_LENGTH('dbo.HospitalUnitKits', 'SessionServiceId') IS NULL ALTER TABLE dbo.HospitalUnitKits ADD SessionServiceId int NULL;

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalUnitKits_Unit' AND definition NOT LIKE '%Physio%')
BEGIN
    ALTER TABLE dbo.HospitalUnitKits DROP CONSTRAINT CK_HospitalUnitKits_Unit;
    ALTER TABLE dbo.HospitalUnitKits WITH CHECK ADD CONSTRAINT CK_HospitalUnitKits_Unit CHECK (Unit IN ('Dialysis','Chemo','Physio'));
END

COMMIT TRANSACTION;
GO
-- the reference names the new column, so it compiles in a batch of its own
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_HospitalUnitKits_Service')
    ALTER TABLE dbo.HospitalUnitKits WITH CHECK ADD CONSTRAINT FK_HospitalUnitKits_Service FOREIGN KEY (SessionServiceId) REFERENCES dbo.HospitalServices(ID);
COMMIT TRANSACTION;
