-- ==========================================================================================
-- Hospital H145 - partial dispensing: a prescription can leave the store in parts; what is not yet given is owed to the
-- patient (status 'Partial').
--   · HospitalDispenses.Status may be 'Partial' (the status rule is widened; a partial dispense, like a full one, has a
--     dispensing time and a store);
--   · HospitalDispenseFills: one row each time a line leaves the store.
--
-- One new table and one widened check; no data changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDispenses','U') IS NULL
    THROW 51990, 'Run hospital_h10_pharmacy.sql first.', 1;

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalDispenses_Status' AND parent_object_id = OBJECT_ID('dbo.HospitalDispenses')
           AND definition NOT LIKE '%Partial%')
    ALTER TABLE dbo.HospitalDispenses DROP CONSTRAINT CK_HospitalDispenses_Status;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalDispenses_Status' AND parent_object_id = OBJECT_ID('dbo.HospitalDispenses'))
    ALTER TABLE dbo.HospitalDispenses ADD CONSTRAINT CK_HospitalDispenses_Status CHECK (Status IN ('New','Verified','Dispensed','Rejected','Partial'));

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalDispenses_Dispensed' AND parent_object_id = OBJECT_ID('dbo.HospitalDispenses')
           AND definition NOT LIKE '%Partial%')
    ALTER TABLE dbo.HospitalDispenses DROP CONSTRAINT CK_HospitalDispenses_Dispensed;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalDispenses_Dispensed' AND parent_object_id = OBJECT_ID('dbo.HospitalDispenses'))
    ALTER TABLE dbo.HospitalDispenses ADD CONSTRAINT CK_HospitalDispenses_Dispensed CHECK (Status NOT IN ('Dispensed','Partial') OR (DispensedAt IS NOT NULL AND WarehouseId IS NOT NULL));

IF OBJECT_ID('dbo.HospitalDispenseFills','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDispenseFills (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        DispenseId int NOT NULL,
        LineId int NOT NULL,
        Quantity decimal(18,4) NOT NULL,
        UnitCost decimal(18,4) NULL,
        UnitPrice decimal(18,4) NULL,
        Batches nvarchar(400) NULL,
        BillLineId int NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalDispenseFills PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalDispenseFills_Quantity CHECK (Quantity > 0)
    );
    CREATE INDEX IX_HospitalDispenseFills_CompanyId_DispenseId ON dbo.HospitalDispenseFills (CompanyId, DispenseId);
END

COMMIT TRANSACTION;
