-- ==========================================================================================
-- Hospital H113 — clinical photos under the patient's consent and its scope, and a calibrated measure on the photo
-- (the prototype's canvas layer 4: "into the File Manager with the patient's consent and its scope, and a calibrated
-- measure"):
--   HospitalConsents.PhotoScope        a photography consent's scope: Record | Teaching | Publication
--   HospitalCanvasMarks.PhotoConsentId the consent the mark's photo was taken under
--   HospitalCanvasMarks.MeasureJson    the ruler, the lines and the result of the measure on the photo
--
-- Adds three nullable columns, a check rule and a reference; changes no data. Idempotent.
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

IF OBJECT_ID('dbo.HospitalConsents','U') IS NULL OR OBJECT_ID('dbo.HospitalCanvasMarks','U') IS NULL
    THROW 51990, 'Run hospital_h60_consents.sql and hospital_h99_canvas.sql first.', 1;

IF COL_LENGTH('dbo.HospitalConsents', 'PhotoScope') IS NULL
    ALTER TABLE dbo.HospitalConsents ADD PhotoScope nvarchar(12) NULL;

IF COL_LENGTH('dbo.HospitalCanvasMarks', 'PhotoConsentId') IS NULL
    ALTER TABLE dbo.HospitalCanvasMarks ADD PhotoConsentId int NULL;

IF COL_LENGTH('dbo.HospitalCanvasMarks', 'MeasureJson') IS NULL
    ALTER TABLE dbo.HospitalCanvasMarks ADD MeasureJson nvarchar(1000) NULL;

COMMIT TRANSACTION;
GO
-- the rules name the new columns, so they compile in a batch of their own

SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalConsents_PhotoScope')
    ALTER TABLE dbo.HospitalConsents WITH CHECK ADD CONSTRAINT CK_HospitalConsents_PhotoScope
        CHECK (PhotoScope IS NULL OR (Kind = 'Photography' AND Decision = 'Given' AND PhotoScope IN ('Record','Teaching','Publication')));

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_HospitalCanvasMarks_PhotoConsent')
    ALTER TABLE dbo.HospitalCanvasMarks WITH CHECK ADD CONSTRAINT FK_HospitalCanvasMarks_PhotoConsent
        FOREIGN KEY (PhotoConsentId) REFERENCES dbo.HospitalConsents(ID);

COMMIT TRANSACTION;
