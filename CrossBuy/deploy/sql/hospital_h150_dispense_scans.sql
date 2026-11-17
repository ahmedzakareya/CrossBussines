-- ==========================================================================================
-- Hospital H150 - packs scanned at the pharmacy counter (GS1 DataMatrix: GTIN, expiry, batch, serial), one row per pack;
-- dispensing issues a scanned pack from its own batch.
--
-- One new table; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDispenseFills','U') IS NULL
    THROW 51992, 'Run hospital_h145_partial_dispense.sql first.', 1;

IF OBJECT_ID('dbo.HospitalDispenseScans','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDispenseScans (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        DispenseId int NOT NULL,
        LineId int NOT NULL,
        ItemId int NOT NULL,
        Gtin nvarchar(14) NOT NULL,
        BatchNo nvarchar(20) NULL,
        Expiry datetime2 NULL,
        Serial nvarchar(20) NULL,
        FillId int NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalDispenseScans PRIMARY KEY (ID)
    );
    CREATE INDEX IX_HospitalDispenseScans_CompanyId_DispenseId ON dbo.HospitalDispenseScans (CompanyId, DispenseId);
    CREATE INDEX IX_HospitalDispenseScans_CompanyId_Gtin_Serial ON dbo.HospitalDispenseScans (CompanyId, Gtin, Serial);
END

COMMIT TRANSACTION;
