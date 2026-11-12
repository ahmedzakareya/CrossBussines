-- ==========================================================================================
-- Hospital H104 — dosing by the kidney (the approved prototype screen sc-im: "Metformin with eGFR 47 — review the dose
-- below 45, stop below 30"). The thresholds join the drug-safety dose limits (H73) rather than a second table:
--   HospitalDoseLimits.RenalReviewBelow   review the dose below this eGFR (mL/min/1.73 m²)
--   HospitalDoseLimits.RenalStopBelow     stop the medicine below this eGFR
--
-- Additive only (two nullable columns and their range check). Idempotent (COL_LENGTH and constraint guards).
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

IF OBJECT_ID('dbo.HospitalDoseLimits','U') IS NULL
    THROW 51990, 'Run hospital_h73_drug_safety.sql first.', 1;

IF COL_LENGTH('dbo.HospitalDoseLimits', 'RenalReviewBelow') IS NULL
    ALTER TABLE dbo.HospitalDoseLimits ADD RenalReviewBelow decimal(5,1) NULL;

IF COL_LENGTH('dbo.HospitalDoseLimits', 'RenalStopBelow') IS NULL
    ALTER TABLE dbo.HospitalDoseLimits ADD RenalStopBelow decimal(5,1) NULL;

COMMIT TRANSACTION;
GO

-- the range check is added after the columns exist (a separate batch, so the columns are known)
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalDoseLimits_Renal')
    ALTER TABLE dbo.HospitalDoseLimits WITH CHECK ADD CONSTRAINT CK_HospitalDoseLimits_Renal CHECK (
        (RenalReviewBelow IS NULL OR RenalReviewBelow BETWEEN 5 AND 120)
        AND (RenalStopBelow IS NULL OR RenalStopBelow BETWEEN 5 AND 120)
        AND (RenalReviewBelow IS NULL OR RenalStopBelow IS NULL OR RenalStopBelow < RenalReviewBelow));
