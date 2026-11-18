-- ==========================================================================================
-- Hospital H199 - the imaging archive (PACS): the web viewer's address and the name order messages go to.
--
--   HospitalPacsSettings   (new) one row per company
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalRadOrders','U') IS NULL
    THROW 51999, 'Run the radiology slice first.', 1;

IF OBJECT_ID('dbo.HospitalPacsSettings','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPacsSettings (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        ViewerUrl nvarchar(500) NULL,
        ReceivingApp nvarchar(60) NULL,
        UpdatedAt datetime2 NOT NULL,
        UpdatedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalPacsSettings PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalPacsSettings_CompanyId ON dbo.HospitalPacsSettings (CompanyId);
END

COMMIT TRANSACTION;
