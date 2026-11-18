-- ==========================================================================================
-- Hospital H187 - the lab case's impression disinfection and the check of the work on receipt.
--
--   HospitalDentalLabCases   + ImpressionKind (Digital / Conventional), Disinfectant, DisinfectedAt, QcAt, QcByEmployeeId
--
-- Nullable columns; a case prescribed before this slice names no impression. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDentalLabCases','U') IS NULL
    THROW 51987, 'Run the dental practice slice first.', 1;

IF COL_LENGTH('dbo.HospitalDentalLabCases', 'ImpressionKind') IS NULL ALTER TABLE dbo.HospitalDentalLabCases ADD ImpressionKind nvarchar(12) NULL;
IF COL_LENGTH('dbo.HospitalDentalLabCases', 'Disinfectant') IS NULL ALTER TABLE dbo.HospitalDentalLabCases ADD Disinfectant nvarchar(60) NULL;
IF COL_LENGTH('dbo.HospitalDentalLabCases', 'DisinfectedAt') IS NULL ALTER TABLE dbo.HospitalDentalLabCases ADD DisinfectedAt datetime2 NULL;
IF COL_LENGTH('dbo.HospitalDentalLabCases', 'QcAt') IS NULL ALTER TABLE dbo.HospitalDentalLabCases ADD QcAt datetime2 NULL;
IF COL_LENGTH('dbo.HospitalDentalLabCases', 'QcByEmployeeId') IS NULL ALTER TABLE dbo.HospitalDentalLabCases ADD QcByEmployeeId int NULL;

COMMIT TRANSACTION;
GO

SET QUOTED_IDENTIFIER ON;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalDentalLabCases_Impression')
    ALTER TABLE dbo.HospitalDentalLabCases ADD CONSTRAINT CK_HospitalDentalLabCases_Impression CHECK (ImpressionKind IS NULL OR ImpressionKind IN ('Digital', 'Conventional'));
GO
