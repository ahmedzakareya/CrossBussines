-- ==========================================================================================
-- Hospital H51 — an issued blood request is charged to the patient's bill. Each blood product (whole blood, packed
-- cells, plasma, platelets) is priced as a catalogue service the billing settings name; issuing a request adds ONE
-- bill line (quantity = the bags issued) to the stay's bill when admitted, otherwise to the patient's open bill, and
-- the request keeps the line so it is never charged twice.
--   HospitalTransfusionRequests   + BillLineId
--   HospitalBloodProductServices  one row per company and product: the service it is charged as
--
-- Additive only (one nullable column, one new table). Idempotent (COL_LENGTH / OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalTransfusionRequests','U') IS NULL OR OBJECT_ID('dbo.HospitalServices','U') IS NULL OR OBJECT_ID('dbo.HospitalBillLines','U') IS NULL
    THROW 51990, 'Run hospital_h6_billing.sql and hospital_h41_blood_bank.sql first.', 1;

IF COL_LENGTH('dbo.HospitalTransfusionRequests', 'BillLineId') IS NULL
    ALTER TABLE dbo.HospitalTransfusionRequests ADD BillLineId int NULL;

IF OBJECT_ID('dbo.HospitalBloodProductServices','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalBloodProductServices (
        ID          int IDENTITY(1,1) NOT NULL,
        CompanyId   int            NOT NULL,
        Product     nvarchar(20)   NOT NULL,
        ServiceId   int            NOT NULL,
        UpdatedAt   datetime2      NOT NULL,
        UpdatedByEmployeeId int    NULL,
        CONSTRAINT PK_HospitalBloodProductServices PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalBloodProductServices_Service FOREIGN KEY (ServiceId) REFERENCES dbo.HospitalServices(ID),
        CONSTRAINT CK_HospitalBloodProductServices_Product CHECK (Product IN ('WholeBlood','PackedCells','Plasma','Platelets'))
    );
    CREATE UNIQUE INDEX IX_HospitalBloodProductServices_CompanyId_Product ON dbo.HospitalBloodProductServices (CompanyId, Product);
END

COMMIT TRANSACTION;
