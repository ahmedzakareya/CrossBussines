-- ==========================================================================================
-- Hospital H208 - the sick leave certificate for Kuwait: a QR anyone can verify, and the Ministry's authentication.
--
--   HospitalSickLeaves   + VerifyCode (unique; existing certificates get one), MohAuthRef, MohAuthAt, MohAuthByEmployeeId
--
-- Columns, a backfill of codes, one filtered unique index. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalSickLeaves','U') IS NULL
    THROW 52008, 'Run the sick leave slice (hospital_h56_sick_leave.sql) first.', 1;

IF COL_LENGTH('dbo.HospitalSickLeaves', 'VerifyCode') IS NULL
    ALTER TABLE dbo.HospitalSickLeaves ADD VerifyCode nvarchar(16) NULL;
IF COL_LENGTH('dbo.HospitalSickLeaves', 'MohAuthRef') IS NULL
    ALTER TABLE dbo.HospitalSickLeaves ADD MohAuthRef nvarchar(60) NULL;
IF COL_LENGTH('dbo.HospitalSickLeaves', 'MohAuthAt') IS NULL
    ALTER TABLE dbo.HospitalSickLeaves ADD MohAuthAt datetime2 NULL;
IF COL_LENGTH('dbo.HospitalSickLeaves', 'MohAuthByEmployeeId') IS NULL
    ALTER TABLE dbo.HospitalSickLeaves ADD MohAuthByEmployeeId int NULL;

COMMIT TRANSACTION;
GO

-- every certificate issued before H208 gets its code (10 characters, no 0/O/1/I)
UPDATE dbo.HospitalSickLeaves SET VerifyCode = UPPER(LEFT(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(CONVERT(nvarchar(36), NEWID()), '-', ''), '0', 'Z'), '1', 'Y'), 'O', 'X'), 'I', 'W'), 10)) WHERE VerifyCode IS NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_HospitalSickLeaves_VerifyCode' AND object_id = OBJECT_ID('dbo.HospitalSickLeaves'))
    CREATE UNIQUE INDEX IX_HospitalSickLeaves_VerifyCode ON dbo.HospitalSickLeaves (VerifyCode) WHERE VerifyCode IS NOT NULL;
GO
