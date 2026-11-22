-- ==========================================================================================
-- Hospital H229 - the Ministry of Health gateway's connection, kept by the administrator (it used to be two server
-- settings). One row per company: the address, the key PROTECTED by the application (never in the clear) and whether
-- it is switched on.
--
--   HospitalMohSettings   (new)
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 52029, 'Run the hospital slices first.', 1;

IF OBJECT_ID('dbo.HospitalMohSettings','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalMohSettings (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        Url nvarchar(300) NULL,
        KeyProtected nvarchar(2000) NULL,
        IsEnabled bit NOT NULL,
        UpdatedAt datetime2 NOT NULL,
        UpdatedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalMohSettings PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalMohSettings_Enabled CHECK (IsEnabled = 0 OR Url IS NOT NULL)
    );
    CREATE UNIQUE INDEX IX_HospitalMohSettings_CompanyId ON dbo.HospitalMohSettings (CompanyId);
END

COMMIT TRANSACTION;
