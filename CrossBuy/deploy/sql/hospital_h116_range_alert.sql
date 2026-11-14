-- ==========================================================================================
-- Hospital H116 — a field alert on the PATIENT's own range (H115): besides its fixed lines, a template field may alert
-- when the value is outside this patient's normal range — always, or only while she is pregnant ("TSH out of range
-- in pregnancy", the specialty catalogue's endocrinology alert).
--   HospitalClinicalTemplateFields.AlertRange   NULL | 'Any' | 'Pregnant'
--
-- Adds one nullable column and its check rule; changes no data. Idempotent.
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

IF OBJECT_ID('dbo.HospitalClinicalTemplateFields','U') IS NULL
    THROW 51990, 'Run hospital_h93_clinical_content.sql first.', 1;

IF COL_LENGTH('dbo.HospitalClinicalTemplateFields', 'AlertRange') IS NULL
    ALTER TABLE dbo.HospitalClinicalTemplateFields ADD AlertRange nvarchar(8) NULL;

COMMIT TRANSACTION;
GO
-- the rule names the new column, so it compiles in a batch of its own
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalClinicalTemplateFields_AlertRange')
    ALTER TABLE dbo.HospitalClinicalTemplateFields WITH CHECK ADD CONSTRAINT CK_HospitalClinicalTemplateFields_AlertRange
        CHECK (AlertRange IS NULL OR AlertRange IN ('Any','Pregnant'));

COMMIT TRANSACTION;
