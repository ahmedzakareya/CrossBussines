-- ==========================================================================================
-- Hospital H221 - ECG machines: an ECG study takes the machine's measurements and reading (HL7 v2 ORU^R01 through
-- the interface channel), kept as the DEVICE reading until the cardiologist confirms and signs; the interface gains
-- its third kind.
--
--   HospitalCardioStudies   + DeviceName, DeviceInterpretation, DeviceReadAt
--   HospitalInterfaces      Kind may be 'CardioResults'
--
-- Columns and one check constraint replaced. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalCardioStudies','U') IS NULL OR OBJECT_ID('dbo.HospitalInterfaces','U') IS NULL
    THROW 52021, 'Run the cardiology (h218) and interfaces (h215) slices first.', 1;

IF COL_LENGTH('dbo.HospitalCardioStudies', 'DeviceName') IS NULL
    ALTER TABLE dbo.HospitalCardioStudies ADD DeviceName nvarchar(60) NULL;
IF COL_LENGTH('dbo.HospitalCardioStudies', 'DeviceInterpretation') IS NULL
    ALTER TABLE dbo.HospitalCardioStudies ADD DeviceInterpretation nvarchar(1000) NULL;
IF COL_LENGTH('dbo.HospitalCardioStudies', 'DeviceReadAt') IS NULL
    ALTER TABLE dbo.HospitalCardioStudies ADD DeviceReadAt datetime2 NULL;

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalInterfaces_Kind' AND definition NOT LIKE '%CardioResults%')
    ALTER TABLE dbo.HospitalInterfaces DROP CONSTRAINT CK_HospitalInterfaces_Kind;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalInterfaces_Kind')
    ALTER TABLE dbo.HospitalInterfaces ADD CONSTRAINT CK_HospitalInterfaces_Kind CHECK (Kind IN ('LabResults', 'ImagingWorklist', 'CardioResults'));

COMMIT TRANSACTION;
