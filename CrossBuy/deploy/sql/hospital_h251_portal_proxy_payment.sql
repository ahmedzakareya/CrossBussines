-- ==========================================================================================
-- Hospital H251 - portal proxy access and online bill payment: a family link's scope and end date, and the payment
-- provider's checkout address (entered by the administrator; empty = pay at the cashier).
--
--   HospitalPortalFamilyLinks.Scope, ExpiresAt     (new columns)
--   HospitalPortalSettings.PayLinkTemplate         (new column)
--
-- Additive columns only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPortalFamilyLinks','U') IS NULL OR OBJECT_ID('dbo.HospitalPortalSettings','U') IS NULL
    THROW 52051, 'Run the hospital portal slices first.', 1;

IF COL_LENGTH('dbo.HospitalPortalFamilyLinks', 'Scope') IS NULL
    ALTER TABLE dbo.HospitalPortalFamilyLinks ADD Scope nvarchar(8) NOT NULL CONSTRAINT DF_HospitalPortalFamilyLinks_Scope DEFAULT ('Full');
IF COL_LENGTH('dbo.HospitalPortalFamilyLinks', 'ExpiresAt') IS NULL
    ALTER TABLE dbo.HospitalPortalFamilyLinks ADD ExpiresAt datetime2 NULL;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalPortalFamilyLinks_Scope')
    EXEC('ALTER TABLE dbo.HospitalPortalFamilyLinks ADD CONSTRAINT CK_HospitalPortalFamilyLinks_Scope CHECK (Scope IN (''Full'',''Booking''))');

IF COL_LENGTH('dbo.HospitalPortalSettings', 'PayLinkTemplate') IS NULL
    ALTER TABLE dbo.HospitalPortalSettings ADD PayLinkTemplate nvarchar(300) NULL;

COMMIT TRANSACTION;
