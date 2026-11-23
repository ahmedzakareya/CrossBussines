-- ==========================================================================================
-- Hospital H248 - the FHIR R4 clinical read API: a ClinicalRead interface kind (its key is the system's credential).
--
--   HospitalInterfaces.CK_HospitalInterfaces_Kind   (widened: + ClinicalRead)
--
-- Constraint change only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalInterfaces','U') IS NULL
    THROW 52048, 'Run the hospital interface slice first.', 1;

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalInterfaces_Kind' AND definition NOT LIKE '%ClinicalRead%')
    ALTER TABLE dbo.HospitalInterfaces DROP CONSTRAINT CK_HospitalInterfaces_Kind;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalInterfaces_Kind')
    ALTER TABLE dbo.HospitalInterfaces ADD CONSTRAINT CK_HospitalInterfaces_Kind CHECK (Kind IN ('LabResults', 'ImagingWorklist', 'CardioResults', 'ClinicalRead'));

COMMIT TRANSACTION;
