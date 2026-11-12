-- ==========================================================================================
-- Hospital H106 — a field alert that cannot wait (the approved prototype, psychiatry: "PHQ-9 item 9 > 0 — the on-duty
-- doctor is notified now and a C-SSRS suicide-risk assessment opens before the patient leaves"):
--   HospitalClinicalTemplateFields.AlertNotify       the alert notifies the department's on-call doctor at once
--   HospitalClinicalTemplateFields.AlertTemplateId   the alert opens this template on the visit
--
-- Additive only (two columns, a default and a foreign key). Idempotent (COL_LENGTH and constraint guards).
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

IF COL_LENGTH('dbo.HospitalClinicalTemplateFields', 'AlertNotify') IS NULL
    ALTER TABLE dbo.HospitalClinicalTemplateFields ADD AlertNotify bit NOT NULL CONSTRAINT DF_HospitalClinicalTemplateFields_AlertNotify DEFAULT (0);

IF COL_LENGTH('dbo.HospitalClinicalTemplateFields', 'AlertTemplateId') IS NULL
    ALTER TABLE dbo.HospitalClinicalTemplateFields ADD AlertTemplateId int NULL;

COMMIT TRANSACTION;
GO

IF OBJECT_ID('dbo.FK_HospitalClinicalTemplateFields_AlertTemplate','F') IS NULL
    ALTER TABLE dbo.HospitalClinicalTemplateFields WITH CHECK ADD CONSTRAINT FK_HospitalClinicalTemplateFields_AlertTemplate
        FOREIGN KEY (AlertTemplateId) REFERENCES dbo.HospitalClinicalTemplates(ID);
