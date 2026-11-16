-- ==========================================================================================
-- Hospital H146 - medicine returned by the patient: back on the shelf, off the open bill, any overpaid excess as a
-- credit on the patient's deposit balance. One row per return.
--
-- One new table; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDispenseFills','U') IS NULL
    THROW 51990, 'Run hospital_h145_partial_dispense.sql first.', 1;

IF OBJECT_ID('dbo.HospitalDispenseReturns','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDispenseReturns (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        DispenseId int NOT NULL,
        LineId int NOT NULL,
        Quantity decimal(18,4) NOT NULL,
        Reason nvarchar(300) NOT NULL,
        Credited decimal(18,4) NOT NULL,
        DepositId int NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalDispenseReturns PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalDispenseReturns_Quantity CHECK (Quantity > 0)
    );
    CREATE INDEX IX_HospitalDispenseReturns_CompanyId_DispenseId ON dbo.HospitalDispenseReturns (CompanyId, DispenseId);
END

COMMIT TRANSACTION;
