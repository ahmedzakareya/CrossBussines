-- ==========================================================================================
-- Hospital H94 — the specialty visit (the approved prototype screen sc-im).
--   HospitalClinicalTemplateVersions.ChangeNoteEn   the English twin of a version's change note
--   HospitalClinicalTemplateFields.ShowTrend        the field heads the visit (a figure on top, a trend tab)
--   HospitalOrderSets.DiagnosisCodes                the diagnosis codes (ICD-10 prefixes) that propose the set on a visit
--   HospitalClinicalObservations.Flag may be 'U'    "no range to compare with" — never stored as normal
--
-- Additive only (three nullable / defaulted columns, and one more allowed flag value). Idempotent (COL_LENGTH and
-- constraint-definition guards).
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

IF OBJECT_ID('dbo.HospitalClinicalTemplateVersions','U') IS NULL OR OBJECT_ID('dbo.HospitalClinicalTemplateFields','U') IS NULL OR OBJECT_ID('dbo.HospitalOrderSets','U') IS NULL
    THROW 51990, 'Run hospital_h74_order_sets.sql and hospital_h93_clinical_content.sql first.', 1;

IF COL_LENGTH('dbo.HospitalClinicalTemplateVersions', 'ChangeNoteEn') IS NULL
    ALTER TABLE dbo.HospitalClinicalTemplateVersions ADD ChangeNoteEn nvarchar(300) NULL;

IF COL_LENGTH('dbo.HospitalClinicalTemplateFields', 'ShowTrend') IS NULL
    ALTER TABLE dbo.HospitalClinicalTemplateFields ADD ShowTrend bit NOT NULL CONSTRAINT DF_HospitalClinicalTemplateFields_ShowTrend DEFAULT (0);

IF COL_LENGTH('dbo.HospitalOrderSets', 'DiagnosisCodes') IS NULL
    ALTER TABLE dbo.HospitalOrderSets ADD DiagnosisCodes nvarchar(200) NULL;

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalClinicalObservations_Flag' AND definition NOT LIKE '%''U''%')
BEGIN
    ALTER TABLE dbo.HospitalClinicalObservations DROP CONSTRAINT CK_HospitalClinicalObservations_Flag;
    ALTER TABLE dbo.HospitalClinicalObservations WITH CHECK ADD CONSTRAINT CK_HospitalClinicalObservations_Flag CHECK (Flag IN ('N','L','H','LL','HH','U'));
END

COMMIT TRANSACTION;
