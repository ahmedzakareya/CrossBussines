-- ==========================================================================================
-- Hospital H110 — the clinical canvas's calculator results become observations (burn %TBSA, DAS28, hearing average per
-- ear), written when the visit is signed, so they are searched and trended like every other reading:
--   HospitalClinicalObservations.Source may be 'Canvas'
--
-- Widens one check constraint; changes no data. Idempotent (the constraint is replaced only while it lacks 'Canvas').
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

IF OBJECT_ID('dbo.HospitalClinicalObservations','U') IS NULL
    THROW 51990, 'Run hospital_h93_clinical_content.sql first.', 1;

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalClinicalObservations_Source' AND definition NOT LIKE '%Canvas%')
BEGIN
    ALTER TABLE dbo.HospitalClinicalObservations DROP CONSTRAINT CK_HospitalClinicalObservations_Source;
    ALTER TABLE dbo.HospitalClinicalObservations WITH CHECK ADD CONSTRAINT CK_HospitalClinicalObservations_Source CHECK (Source IN ('Form','Lab','Canvas'));
END

COMMIT TRANSACTION;
