-- ==========================================================================================
-- Hospital H137 - blind counts of the controlled drugs, witnessed. A difference is a discrepancy, closed within the shift
-- with a reason (miscount after a recount, an unrecorded movement, or a loss written off through the inventory).
--
-- One new table; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPharmacySettings','U') IS NULL
    THROW 51990, 'Run hospital_h10_pharmacy.sql first.', 1;

IF OBJECT_ID('dbo.HospitalControlledCounts','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalControlledCounts (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        CountNo nvarchar(20) NOT NULL,
        WarehouseId int NOT NULL,
        ItemId int NOT NULL,
        Expected decimal(18,4) NOT NULL,
        Counted decimal(18,4) NOT NULL,
        Difference decimal(18,4) NOT NULL,
        Status nvarchar(10) NOT NULL,
        CountedAt datetime2 NOT NULL,
        CountedByEmployeeId int NULL,
        WitnessEmployeeId int NULL,
        Resolution nvarchar(20) NULL,
        ResolutionNote nvarchar(400) NULL,
        Recounted decimal(18,4) NULL,
        ResolvedAt datetime2 NULL,
        ResolvedByEmployeeId int NULL,
        ResolutionWitnessEmployeeId int NULL,
        DocumentNo nvarchar(40) NULL,
        CONSTRAINT PK_HospitalControlledCounts PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalControlledCounts_CompanyId_CountNo ON dbo.HospitalControlledCounts (CompanyId, CountNo);
    CREATE INDEX IX_HospitalControlledCounts_CompanyId_WarehouseId_ItemId_CountedAt ON dbo.HospitalControlledCounts (CompanyId, WarehouseId, ItemId, CountedAt);
    CREATE INDEX IX_HospitalControlledCounts_CompanyId_Status ON dbo.HospitalControlledCounts (CompanyId, Status);
END

COMMIT TRANSACTION;
