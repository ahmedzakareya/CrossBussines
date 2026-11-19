-- ==========================================================================================
-- Hospital H210 - a payer's claim sheet: the columns, headers, separator and date format of the spreadsheet a
-- third-party administrator (TPA) takes a batch's claims in.
--
--   HospitalPayerClaimSheets   (new) one per payer
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPayers','U') IS NULL OR OBJECT_ID('dbo.HospitalClaimExchanges','U') IS NULL
    THROW 52010, 'Run the insurance and electronic claims slices first.', 1;

IF OBJECT_ID('dbo.HospitalPayerClaimSheets','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPayerClaimSheets (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        PayerId int NOT NULL,
        Columns nvarchar(2000) NOT NULL,
        Delimiter nvarchar(3) NOT NULL,
        DateFormat nvarchar(20) NOT NULL,
        UpdatedAt datetime2 NOT NULL,
        UpdatedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalPayerClaimSheets PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalPayerClaimSheets_Payer FOREIGN KEY (PayerId) REFERENCES dbo.HospitalPayers (ID),
        CONSTRAINT CK_HospitalPayerClaimSheets_Delimiter CHECK (Delimiter IN (',', ';', 'TAB'))
    );
    CREATE UNIQUE INDEX IX_HospitalPayerClaimSheets_CompanyId_PayerId ON dbo.HospitalPayerClaimSheets (CompanyId, PayerId);
END

COMMIT TRANSACTION;
