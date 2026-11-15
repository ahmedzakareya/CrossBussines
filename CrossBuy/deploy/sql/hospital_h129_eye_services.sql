-- ==========================================================================================
-- Hospital H129 — the eye clinic's two ordered tests (OCT, visual fields) name their services from the services list.
-- The eye visit found them by fixed hidden codes (OCT, VF) the price-keeper had to know; they are now two service-only
-- entries of the unit settings (H122/H123), chosen in the eye clinic's settings:
--   HospitalUnitKits.Unit   may also be EyeOct or EyeVf (SessionServiceId = the test's service; no kit, no store)
--
-- A wider rule only; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalUnitKits','U') IS NULL
    THROW 51990, 'Run hospital_h122_unit_kits.sql and hospital_h123_unit_settings.sql first.', 1;

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalUnitKits_Unit' AND definition NOT LIKE '%EyeVf%')
BEGIN
    ALTER TABLE dbo.HospitalUnitKits DROP CONSTRAINT CK_HospitalUnitKits_Unit;
    ALTER TABLE dbo.HospitalUnitKits WITH CHECK ADD CONSTRAINT CK_HospitalUnitKits_Unit CHECK (Unit IN ('Dialysis','Chemo','Physio','EyeOct','EyeVf'));
END

COMMIT TRANSACTION;
