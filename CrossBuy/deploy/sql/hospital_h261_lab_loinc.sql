-- ==========================================================================================
-- Hospital H261 - LOINC on laboratory analytes: sent beside the local code in HL7 ORU (OBX-3) and FHIR Observation.
--
--   HospitalLabAnalytes.Loinc   (new column, nullable)
--
-- One column. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalLabAnalytes','U') IS NULL
    THROW 52061, 'Run the hospital lab slice (hospital_h8_lab.sql) first.', 1;

IF COL_LENGTH('dbo.HospitalLabAnalytes','Loinc') IS NULL ALTER TABLE dbo.HospitalLabAnalytes ADD Loinc nvarchar(10) NULL;

COMMIT TRANSACTION;
