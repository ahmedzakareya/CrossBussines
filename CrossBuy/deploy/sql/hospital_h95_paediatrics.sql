-- ==========================================================================================
-- Hospital H95 — the paediatric visit (the approved prototype screen sc-peds). A child's dose by weight joins the
-- drug-safety dose limits (H73) rather than a second table:
--   HospitalDoseLimits.PerKgMg           mg per kg per dose
--   HospitalDoseLimits.EveryHours        every so many hours
--   HospitalDoseLimits.StrengthMgPerMl   the liquid's strength, so a dose in mg is also given in mL
-- The WHO growth references are files of the application (BL/Hospital/Growth), not tables.
--
-- Additive only (three nullable columns). Idempotent (COL_LENGTH guards).
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

IF OBJECT_ID('dbo.HospitalDoseLimits','U') IS NULL
    THROW 51990, 'Run hospital_h73_drug_safety.sql first.', 1;

IF COL_LENGTH('dbo.HospitalDoseLimits', 'PerKgMg') IS NULL
    ALTER TABLE dbo.HospitalDoseLimits ADD PerKgMg decimal(12,3) NULL;

IF COL_LENGTH('dbo.HospitalDoseLimits', 'EveryHours') IS NULL
    ALTER TABLE dbo.HospitalDoseLimits ADD EveryHours int NULL;

IF COL_LENGTH('dbo.HospitalDoseLimits', 'StrengthMgPerMl') IS NULL
    ALTER TABLE dbo.HospitalDoseLimits ADD StrengthMgPerMl decimal(12,3) NULL;

COMMIT TRANSACTION;
