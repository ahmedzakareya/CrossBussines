-- ==========================================================================================
-- Hospital H167 - an orthodontic instalment is money taken at the cashier: each payment row links the charge it put on
-- the patient's bill and the receipt that paid it (in the cashier's shift, posted to the ledger), and how it was paid.
-- The orthodontics unit names the service an instalment is billed as, in the unit settings.
--
--   HospitalOrthoPayments  + Method, BillLineId, PaymentId (all nullable: payments recorded before H167 have none)
--   HospitalUnitKits.Unit  may also be DentalOrtho  (keeps every unit the H129 constraint allowed)
--
-- Nothing existing is changed or removed. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalOrthoPayments','U') IS NULL
    THROW 51989, 'Run hospital_h17_dental_cases.sql first.', 1;
IF OBJECT_ID('dbo.HospitalUnitKits','U') IS NULL
    THROW 51988, 'Run hospital_h122_unit_kits.sql and hospital_h123_unit_settings.sql first.', 1;

IF COL_LENGTH('dbo.HospitalOrthoPayments', 'Method') IS NULL
    ALTER TABLE dbo.HospitalOrthoPayments ADD Method nvarchar(20) NULL;
IF COL_LENGTH('dbo.HospitalOrthoPayments', 'BillLineId') IS NULL
    ALTER TABLE dbo.HospitalOrthoPayments ADD BillLineId int NULL;
IF COL_LENGTH('dbo.HospitalOrthoPayments', 'PaymentId') IS NULL
    ALTER TABLE dbo.HospitalOrthoPayments ADD PaymentId int NULL;

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalUnitKits_Unit' AND definition NOT LIKE '%DentalOrtho%')
BEGIN
    ALTER TABLE dbo.HospitalUnitKits DROP CONSTRAINT CK_HospitalUnitKits_Unit;
    ALTER TABLE dbo.HospitalUnitKits WITH CHECK ADD CONSTRAINT CK_HospitalUnitKits_Unit CHECK (Unit IN ('Dialysis','Chemo','Physio','EyeOct','EyeVf','DentalOrtho'));
END

COMMIT TRANSACTION;
