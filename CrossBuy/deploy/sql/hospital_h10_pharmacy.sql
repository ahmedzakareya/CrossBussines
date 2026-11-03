-- ==========================================================================================
-- Hospital H10 — the pharmacy, dispensing from the inventory. The company's pharmacy settings (which
-- warehouse is the pharmacy store); dispenses born one-to-one from a signed visit's prescription
-- (New → Verified → Dispensed) and their lines, each naming an inventory item or a drug the hospital
-- does not stock. Stock leaves through the inventory's own movements; the dispense keeps what left,
-- from which batches, and the bill line it was charged on. A prescription line may now name its item.
--
-- THREE NEW TABLES, one nullable column on HospitalPrescriptionLines, and the bill-line source 'pharmacy'. Idempotent: safe to run repeatedly.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPrescriptionLines','U') IS NULL OR OBJECT_ID('dbo.HospitalBills','U') IS NULL
    THROW 51990, 'Run hospital_h5_consultation.sql and hospital_h6_billing.sql first.', 1;

-- A bill line may now come from the pharmacy.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalBillLines_Source' AND definition LIKE '%pharmacy%')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalBillLines_Source')
        ALTER TABLE dbo.HospitalBillLines DROP CONSTRAINT CK_HospitalBillLines_Source;
    ALTER TABLE dbo.HospitalBillLines ADD CONSTRAINT CK_HospitalBillLines_Source CHECK (Source IN ('consultation','order','accommodation','manual','pharmacy'));
END

IF COL_LENGTH('dbo.HospitalPrescriptionLines','ItemId') IS NULL
    ALTER TABLE dbo.HospitalPrescriptionLines ADD ItemId int NULL;

IF OBJECT_ID('dbo.HospitalPharmacySettings','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPharmacySettings (
        ID             int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPharmacySettings PRIMARY KEY,
        CompanyId      int NOT NULL,
        WarehouseId    int NULL,
        NearExpiryDays int NOT NULL CONSTRAINT DF_HospitalPharmacySettings_Near DEFAULT(60),
        CONSTRAINT CK_HospitalPharmacySettings_Near CHECK (NearExpiryDays BETWEEN 1 AND 365)
    );
    CREATE UNIQUE INDEX UX_HospitalPharmacySettings_Company ON dbo.HospitalPharmacySettings (CompanyId);
END

IF OBJECT_ID('dbo.HospitalDispenses','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDispenses (
        ID                     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalDispenses PRIMARY KEY,
        CompanyId              int            NOT NULL,
        RxNo                   nvarchar(30)   NOT NULL,
        PatientId              int            NOT NULL,
        EncounterId            int            NULL,
        Source                 nvarchar(12)   NOT NULL CONSTRAINT DF_HospitalDispenses_Source DEFAULT('Outpatient'),
        PrescribedByEmployeeId int            NULL,
        PractitionerId         int            NULL,
        Status                 nvarchar(10)   NOT NULL CONSTRAINT DF_HospitalDispenses_Status DEFAULT('New'),
        PrescribedAt           datetime2      NOT NULL,
        VerifiedAt             datetime2      NULL,
        VerifiedByEmployeeId   int            NULL,
        PharmacistNote         nvarchar(400)  NULL,
        DispensedAt            datetime2      NULL,
        DispensedByEmployeeId  int            NULL,
        WarehouseId            int            NULL,
        BillId                 int            NULL,
        CONSTRAINT CK_HospitalDispenses_Status CHECK (Status IN ('New','Verified','Dispensed')),
        CONSTRAINT CK_HospitalDispenses_Source CHECK (Source IN ('Outpatient','Inpatient','Emergency')),
        CONSTRAINT CK_HospitalDispenses_Dispensed CHECK (Status <> 'Dispensed' OR (DispensedAt IS NOT NULL AND WarehouseId IS NOT NULL))
    );
    CREATE UNIQUE INDEX UX_HospitalDispenses_RxNo ON dbo.HospitalDispenses (CompanyId, RxNo);
    CREATE UNIQUE INDEX UX_HospitalDispenses_Encounter ON dbo.HospitalDispenses (CompanyId, EncounterId) WHERE EncounterId IS NOT NULL;
    CREATE INDEX IX_HospitalDispenses_Status ON dbo.HospitalDispenses (CompanyId, Status);
    CREATE INDEX IX_HospitalDispenses_Patient ON dbo.HospitalDispenses (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalDispenseLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDispenseLines (
        ID                 int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalDispenseLines PRIMARY KEY,
        CompanyId          int            NOT NULL,
        DispenseId         int            NOT NULL CONSTRAINT FK_HospitalDispenseLines_Dispense REFERENCES dbo.HospitalDispenses(ID),
        PrescriptionLineId int            NULL,
        ItemId             int            NULL,
        Drug               nvarchar(200)  NOT NULL,
        Instructions       nvarchar(400)  NULL,
        Quantity           decimal(18,4)  NOT NULL,
        DispensedQty       decimal(18,4)  NULL,
        UnitPrice          decimal(18,4)  NULL,
        UnitCost           decimal(18,4)  NULL,
        Batches            nvarchar(400)  NULL,
        NearestExpiry      datetime2      NULL,
        BillLineId         int            NULL,
        SortOrder          int            NOT NULL CONSTRAINT DF_HospitalDispenseLines_Sort DEFAULT(0),
        CONSTRAINT CK_HospitalDispenseLines_Qty CHECK (Quantity > 0 AND (DispensedQty IS NULL OR DispensedQty >= 0))
    );
    CREATE INDEX IX_HospitalDispenseLines_Dispense ON dbo.HospitalDispenseLines (CompanyId, DispenseId);
END

COMMIT;
