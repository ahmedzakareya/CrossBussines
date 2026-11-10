-- ==========================================================================================
-- Hospital H38 — consignment implants (the prototype's "Consignment implants: owned by the supplier until used; a
-- purchase order is then raised automatically", #/requisitions). A consigned implant is the SUPPLIER's: it is not in
-- the hospital's stock or its books until it is used, so it is registered here, by serial, not received into
-- inventory. When it is used on a case it is tied to the patient and the case, charged to the patient's bill, and a
-- DRAFT purchase order for it is raised to the supplier through the procurement module's own service.
--   HospitalConsignments   one row per serial: supplier, item, lot, expiry, agreed price, state, and what it became
--
-- New table only; nothing existing changes. Idempotent (OBJECT_ID guard).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalSurgeries','U') IS NULL OR OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql and hospital_h29_theatre.sql first.', 1;

IF OBJECT_ID('dbo.HospitalConsignments','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalConsignments (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        VendorId              int            NOT NULL,
        ItemId                int            NOT NULL,
        SerialNo              nvarchar(60)   NOT NULL,
        Lot                   nvarchar(60)   NULL,
        ExpiryDate            datetime2      NULL,
        UnitPrice             decimal(18,3)  NOT NULL,
        Status                nvarchar(15)   NOT NULL,
        ReceivedAt            datetime2      NOT NULL,
        ReceivedByEmployeeId  int            NULL,
        UsedAt                datetime2      NULL,
        UsedByEmployeeId      int            NULL,
        PatientId             int            NULL,
        SurgeryId             int            NULL,
        PurchaseOrderId       int            NULL,
        BillLineId            int            NULL,
        ReturnedAt            datetime2      NULL,
        Note                  nvarchar(300)  NULL,
        CONSTRAINT PK_HospitalConsignments PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalConsignments_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalConsignments_Surgery FOREIGN KEY (SurgeryId) REFERENCES dbo.HospitalSurgeries(ID),
        CONSTRAINT CK_HospitalConsignments_Status CHECK (Status IN (N'InStore', N'Used', N'Returned'))
    );
    CREATE UNIQUE INDEX IX_HospitalConsignments_CompanyId_ItemId_SerialNo ON dbo.HospitalConsignments (CompanyId, ItemId, SerialNo);
    CREATE INDEX IX_HospitalConsignments_CompanyId_Status ON dbo.HospitalConsignments (CompanyId, Status);
END

COMMIT TRANSACTION;
