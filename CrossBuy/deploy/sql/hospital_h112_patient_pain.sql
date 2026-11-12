-- ==========================================================================================
-- Hospital H112 — the patient draws their pain on the clinic's tablet (the prototype's canvas layer 8: "kept apart as
-- patient-reported"):
--   HospitalCanvasSheets.Kind may be 'Pain'              the patient's own drawing, apart from the doctor's body map
--   HospitalClinicalObservations.Source may be 'Patient' their pain now (0–10), never mixed with a clinician's reading
--   HospitalClinicalObservations.Source is nvarchar(10)  it was nvarchar(6): 'Patient' is seven letters
--
-- Widens one column and two check constraints; changes no data. Idempotent (each step runs only while still needed).
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

IF OBJECT_ID('dbo.HospitalCanvasSheets','U') IS NULL OR OBJECT_ID('dbo.HospitalClinicalObservations','U') IS NULL
    THROW 51990, 'Run hospital_h93_clinical_content.sql and hospital_h99_canvas.sql first.', 1;

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalCanvasSheets_Kind' AND definition NOT LIKE '%Pain%')
BEGIN
    ALTER TABLE dbo.HospitalCanvasSheets DROP CONSTRAINT CK_HospitalCanvasSheets_Kind;
    ALTER TABLE dbo.HospitalCanvasSheets WITH CHECK ADD CONSTRAINT CK_HospitalCanvasSheets_Kind CHECK (Kind IN ('Body','Fundus','Burn','Audio','Joints','Pedigree','Pain'));
END

IF COLUMNPROPERTY(OBJECT_ID('dbo.HospitalClinicalObservations'), 'Source', 'CharMaxLen') < 10
BEGIN
    -- the check rule names the column, so it is lifted while the column widens and put back after
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalClinicalObservations_Source')
        ALTER TABLE dbo.HospitalClinicalObservations DROP CONSTRAINT CK_HospitalClinicalObservations_Source;
    ALTER TABLE dbo.HospitalClinicalObservations ALTER COLUMN Source nvarchar(10) NOT NULL;
END

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalClinicalObservations_Source' AND definition LIKE '%Patient%')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalClinicalObservations_Source')
        ALTER TABLE dbo.HospitalClinicalObservations DROP CONSTRAINT CK_HospitalClinicalObservations_Source;
    ALTER TABLE dbo.HospitalClinicalObservations WITH CHECK ADD CONSTRAINT CK_HospitalClinicalObservations_Source CHECK (Source IN ('Form','Lab','Canvas','Patient'));
END

COMMIT TRANSACTION;
