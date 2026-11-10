-- ==========================================================================================
-- Hospital H82 — payer recoupment. Phase 3 of the 2026-11-09 gap research.
--   HospitalClaimLines        + RecoupedAmount / RecoupedAt / RecoupReason / RecoupedByEmployeeId: what a payer took back
--                               of a line it had accepted (once; never more than accepted; always with its reason)
--   HospitalFeeStatementLines   the part check widens to 'Recoup': a negative line giving back the doctor's share
--
-- Additive (four nullable columns, one widened check). Idempotent (COL_LENGTH / constraint-definition guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalClaimLines','U') IS NULL OR OBJECT_ID('dbo.HospitalFeeStatementLines','U') IS NULL
    THROW 51990, 'Run hospital_h11_insurance.sql and hospital_h19_doctor_fees.sql first.', 1;

IF COL_LENGTH('dbo.HospitalClaimLines', 'RecoupedAmount') IS NULL
    ALTER TABLE dbo.HospitalClaimLines ADD RecoupedAmount decimal(18,4) NULL;
IF COL_LENGTH('dbo.HospitalClaimLines', 'RecoupedAt') IS NULL
    ALTER TABLE dbo.HospitalClaimLines ADD RecoupedAt datetime2 NULL;
IF COL_LENGTH('dbo.HospitalClaimLines', 'RecoupReason') IS NULL
    ALTER TABLE dbo.HospitalClaimLines ADD RecoupReason nvarchar(300) NULL;
IF COL_LENGTH('dbo.HospitalClaimLines', 'RecoupedByEmployeeId') IS NULL
    ALTER TABLE dbo.HospitalClaimLines ADD RecoupedByEmployeeId int NULL;

-- a recoupment belongs to an accepted line, is positive, never above what was accepted, and says why
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalClaimLines_Recoup')
    EXEC(N'ALTER TABLE dbo.HospitalClaimLines WITH CHECK ADD CONSTRAINT CK_HospitalClaimLines_Recoup CHECK (RecoupedAmount IS NULL OR (Status = ''Accepted'' AND RecoupedAmount > 0 AND RecoupedAmount <= COALESCE(AcceptedAmount, InsurerAmount) AND RecoupedAt IS NOT NULL AND RecoupReason IS NOT NULL))');

-- the fee line's part: Patient, Payer, or Recoup (a payer's give-back; it names the payer)
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalFeeStatementLines_Part' AND definition NOT LIKE '%Recoup%')
    ALTER TABLE dbo.HospitalFeeStatementLines DROP CONSTRAINT CK_HospitalFeeStatementLines_Part;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalFeeStatementLines_Part')
    ALTER TABLE dbo.HospitalFeeStatementLines WITH CHECK ADD CONSTRAINT CK_HospitalFeeStatementLines_Part CHECK (Part IN ('Patient','Payer','Recoup') AND (Part = 'Patient' OR PayerId IS NOT NULL));

COMMIT TRANSACTION;
