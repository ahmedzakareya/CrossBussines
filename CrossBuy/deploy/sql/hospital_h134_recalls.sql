-- ==========================================================================================
-- Hospital H134 - batch recalls. A recall names one batch of one medicine. Opening it moves the batch from every store into
-- the pharmacy's quarantine store, then traces the patients who received it.
--   HospitalPharmacySettings.QuarantineWarehouseId   the quarantine store (null until chosen)
--   HospitalLotRecalls                               one row per recall
--
-- One nullable column and one new table; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPharmacySettings','U') IS NULL
    THROW 51990, 'Run hospital_h10_pharmacy.sql first.', 1;

IF COL_LENGTH('dbo.HospitalPharmacySettings', 'QuarantineWarehouseId') IS NULL ALTER TABLE dbo.HospitalPharmacySettings ADD QuarantineWarehouseId int NULL;

IF OBJECT_ID('dbo.HospitalLotRecalls','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalLotRecalls (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        RecallNo nvarchar(20) NOT NULL,
        ItemId int NOT NULL,
        BatchNo nvarchar(80) NOT NULL,
        RecallClass nvarchar(5) NOT NULL,
        Source nvarchar(20) NOT NULL,
        Reference nvarchar(100) NULL,
        Reason nvarchar(400) NOT NULL,
        Status nvarchar(20) NOT NULL,
        QuarantineWarehouseId int NOT NULL,
        QuarantinedQty decimal(18,4) NOT NULL,
        Transfers nvarchar(400) NULL,
        OpenedAt datetime2 NOT NULL,
        OpenedByEmployeeId int NULL,
        EndedAt datetime2 NULL,
        EndedByEmployeeId int NULL,
        EndNote nvarchar(400) NULL,
        CONSTRAINT PK_HospitalLotRecalls PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalLotRecalls_CompanyId_RecallNo ON dbo.HospitalLotRecalls (CompanyId, RecallNo);
    CREATE INDEX IX_HospitalLotRecalls_CompanyId_ItemId_BatchNo ON dbo.HospitalLotRecalls (CompanyId, ItemId, BatchNo);
    CREATE INDEX IX_HospitalLotRecalls_CompanyId_Status ON dbo.HospitalLotRecalls (CompanyId, Status);
END

COMMIT TRANSACTION;
