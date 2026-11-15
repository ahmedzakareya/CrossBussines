-- ==========================================================================================
-- Hospital H135 - what leaves the pharmacy for good: an expired, damaged or recalled batch destroyed (the inventory's
-- write-off, witnessed) or returned to its supplier (the purchase return's debit note). One row per act.
--
-- One new table; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPharmacySettings','U') IS NULL
    THROW 51990, 'Run hospital_h10_pharmacy.sql first.', 1;

IF OBJECT_ID('dbo.HospitalStockDisposals','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalStockDisposals (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        DisposalNo nvarchar(20) NOT NULL,
        Kind nvarchar(20) NOT NULL,
        ItemId int NOT NULL,
        BatchNo nvarchar(80) NOT NULL,
        Expiry datetime2 NULL,
        WarehouseId int NOT NULL,
        Qty decimal(18,4) NOT NULL,
        Value decimal(18,4) NOT NULL,
        Reason nvarchar(20) NOT NULL,
        Method nvarchar(20) NULL,
        VendorId int NULL,
        DocumentNo nvarchar(40) NULL,
        DocumentId int NULL,
        RecallId int NULL,
        WitnessEmployeeId int NULL,
        DoneByEmployeeId int NULL,
        DoneAt datetime2 NOT NULL,
        Note nvarchar(400) NULL,
        CONSTRAINT PK_HospitalStockDisposals PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalStockDisposals_CompanyId_DisposalNo ON dbo.HospitalStockDisposals (CompanyId, DisposalNo);
    CREATE INDEX IX_HospitalStockDisposals_CompanyId_DoneAt ON dbo.HospitalStockDisposals (CompanyId, DoneAt);
END

COMMIT TRANSACTION;
