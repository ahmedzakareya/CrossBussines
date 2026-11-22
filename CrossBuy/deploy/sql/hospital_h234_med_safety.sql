-- ==========================================================================================
-- Hospital H234 - medication safety: duplicate therapy, pregnancy and breastfeeding, and the antimicrobial flag that
-- stewardship (H235) reads.
--
--   HospitalDrugs.PregnancyRisk, LactationRisk, IsAntimicrobial   (new columns)
--   CK_HospitalAlertOverrides_Kind widened to the new alert kinds (Duplicate, Pregnancy)
--
-- Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDrugs','U') IS NULL OR OBJECT_ID('dbo.HospitalAlertOverrides','U') IS NULL
    THROW 52034, 'Run the hospital drug master (H133) and drug safety (H73) slices first.', 1;

IF COL_LENGTH('dbo.HospitalDrugs','PregnancyRisk') IS NULL ALTER TABLE dbo.HospitalDrugs ADD PregnancyRisk nvarchar(10) NULL;
IF COL_LENGTH('dbo.HospitalDrugs','LactationRisk') IS NULL ALTER TABLE dbo.HospitalDrugs ADD LactationRisk nvarchar(10) NULL;
IF COL_LENGTH('dbo.HospitalDrugs','IsAntimicrobial') IS NULL ALTER TABLE dbo.HospitalDrugs ADD IsAntimicrobial bit NOT NULL CONSTRAINT DF_HospitalDrugs_IsAntimicrobial DEFAULT(0);

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalDrugs_PregnancyRisk')
    EXEC('ALTER TABLE dbo.HospitalDrugs ADD CONSTRAINT CK_HospitalDrugs_PregnancyRisk CHECK (PregnancyRisk IS NULL OR PregnancyRisk IN (''Avoid'',''Caution''))');
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalDrugs_LactationRisk')
    EXEC('ALTER TABLE dbo.HospitalDrugs ADD CONSTRAINT CK_HospitalDrugs_LactationRisk CHECK (LactationRisk IS NULL OR LactationRisk IN (''Avoid'',''Caution''))');

-- the override log keeps the new kinds of stopping alert
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalAlertOverrides_Kind' AND definition NOT LIKE '%Duplicate%')
    ALTER TABLE dbo.HospitalAlertOverrides DROP CONSTRAINT CK_HospitalAlertOverrides_Kind;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalAlertOverrides_Kind')
    ALTER TABLE dbo.HospitalAlertOverrides ADD CONSTRAINT CK_HospitalAlertOverrides_Kind CHECK (Kind IN ('Interaction','Dose','Allergy','Duplicate','Pregnancy'));

COMMIT TRANSACTION;
