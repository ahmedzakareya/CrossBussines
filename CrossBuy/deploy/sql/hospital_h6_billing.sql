-- ==========================================================================================
-- Hospital H6 — billing and the cashier: the service catalogue, billing settings (tax and the
-- GL account map), bills and their lines, consultation orders, payments and cashier shifts.
--
-- Money posts to the general ledger through the accounting module's own journal engine; these
-- tables keep the hospital's side (what was charged, collected and counted) and the id of the
-- journal entry each event produced.
--
-- SEVEN NEW TABLES and ONE NULLABLE COLUMN on HospitalPractitioners (ConsultationServiceId, H4's
-- own table). Idempotent: safe to run repeatedly.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPractitioners','U') IS NULL OR OBJECT_ID('dbo.HospitalEncounters','U') IS NULL
    THROW 51950, 'Run hospital_h4_outpatient.sql and hospital_h5_consultation.sql first.', 1;

IF OBJECT_ID('dbo.HospitalBillingSettings','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalBillingSettings (
        ID                     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalBillingSettings PRIMARY KEY,
        CompanyId              int            NOT NULL,
        VatRatePercent         decimal(5,2)   NOT NULL CONSTRAINT DF_HospitalBillingSettings_Vat DEFAULT(0),
        CashAccountId          int            NULL,
        BankAccountId          int            NULL,
        ReceivableAccountId    int            NULL,
        RevenueAccountId       int            NULL,
        VatAccountId           int            NULL,
        CashOverShortAccountId int            NULL,
        UpdatedAt              datetime2      NULL,
        UpdatedByEmployeeId    int            NULL,
        CONSTRAINT CK_HospitalBillingSettings_Vat CHECK (VatRatePercent BETWEEN 0 AND 100)
    );
    CREATE UNIQUE INDEX UX_HospitalBillingSettings_Company ON dbo.HospitalBillingSettings (CompanyId);
END

IF OBJECT_ID('dbo.HospitalServices','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalServices (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalServices PRIMARY KEY,
        CompanyId           int            NOT NULL,
        Code                nvarchar(40)   NOT NULL,
        Name                nvarchar(200)  NOT NULL,
        NameEn              nvarchar(200)  NOT NULL,
        Category            nvarchar(20)   NOT NULL,
        DepartmentId        int            NULL,
        Price               decimal(18,4)  NOT NULL CONSTRAINT DF_HospitalServices_Price DEFAULT(0),
        Taxable             bit            NOT NULL CONSTRAINT DF_HospitalServices_Taxable DEFAULT(0),
        RevenueAccountId    int            NULL,
        SortOrder           int            NOT NULL CONSTRAINT DF_HospitalServices_Sort DEFAULT(0),
        IsActive            bit            NOT NULL CONSTRAINT DF_HospitalServices_Active DEFAULT(1),
        CreatedAt           datetime2      NOT NULL CONSTRAINT DF_HospitalServices_Created DEFAULT(SYSUTCDATETIME()),
        CreatedByEmployeeId int            NULL,
        UpdatedAt           datetime2      NULL,
        UpdatedByEmployeeId int            NULL,
        CONSTRAINT CK_HospitalServices_Category CHECK (Category IN ('Consultation','Lab','Radiology','Procedure','Accommodation','Pharmacy','Other')),
        CONSTRAINT CK_HospitalServices_Price CHECK (Price >= 0)
    );
    CREATE UNIQUE INDEX UX_HospitalServices_Code ON dbo.HospitalServices (CompanyId, Code);
END

IF COL_LENGTH('dbo.HospitalPractitioners','ConsultationServiceId') IS NULL
    ALTER TABLE dbo.HospitalPractitioners ADD ConsultationServiceId int NULL;

IF OBJECT_ID('dbo.HospitalEncounterOrders','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalEncounterOrders (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalEncounterOrders PRIMARY KEY,
        CompanyId           int            NOT NULL,
        EncounterId         int            NOT NULL CONSTRAINT FK_HospitalEncounterOrders_Encounter REFERENCES dbo.HospitalEncounters(ID),
        ServiceId           int            NOT NULL,
        Code                nvarchar(40)   NOT NULL,
        Name                nvarchar(200)  NOT NULL,
        NameEn              nvarchar(200)  NOT NULL,
        Category            nvarchar(20)   NOT NULL,
        Price               decimal(18,4)  NOT NULL
    );
    CREATE INDEX IX_HospitalEncounterOrders_Encounter ON dbo.HospitalEncounterOrders (CompanyId, EncounterId);
END

IF OBJECT_ID('dbo.HospitalBills','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalBills (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalBills PRIMARY KEY,
        CompanyId           int            NOT NULL,
        BillNo              nvarchar(30)   NOT NULL,
        PatientId           int            NOT NULL,
        EncounterId         int            NULL,   -- an outpatient visit
        AdmissionId         int            NULL,   -- an inpatient stay
        Status              nvarchar(20)   NOT NULL CONSTRAINT DF_HospitalBills_Status DEFAULT('Open'),
        OpenedAt            datetime2      NOT NULL,
        ClosedAt            datetime2      NULL,
        ClosedByEmployeeId  int            NULL,
        JournalEntryId      int            NULL,
        CreatedByEmployeeId int            NULL,
        CONSTRAINT CK_HospitalBills_Status CHECK (Status IN ('Open','Closed')),
        CONSTRAINT CK_HospitalBills_Closed CHECK ((Status = 'Open' AND ClosedAt IS NULL) OR (Status = 'Closed' AND ClosedAt IS NOT NULL))
    );
    CREATE UNIQUE INDEX UX_HospitalBills_No ON dbo.HospitalBills (CompanyId, BillNo);
    CREATE UNIQUE INDEX UX_HospitalBills_Encounter ON dbo.HospitalBills (CompanyId, EncounterId) WHERE EncounterId IS NOT NULL;
    CREATE UNIQUE INDEX UX_HospitalBills_Admission ON dbo.HospitalBills (CompanyId, AdmissionId) WHERE AdmissionId IS NOT NULL;
    CREATE INDEX IX_HospitalBills_Patient ON dbo.HospitalBills (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalBillLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalBillLines (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalBillLines PRIMARY KEY,
        CompanyId           int            NOT NULL,
        BillId              int            NOT NULL CONSTRAINT FK_HospitalBillLines_Bill REFERENCES dbo.HospitalBills(ID),
        ServiceId           int            NULL,
        Code                nvarchar(40)   NOT NULL,
        Description         nvarchar(300)  NOT NULL,
        DescriptionEn       nvarchar(300)  NOT NULL,
        Category            nvarchar(20)   NOT NULL,
        DepartmentId        int            NULL,
        Quantity            decimal(9,2)   NOT NULL,
        UnitPrice           decimal(18,4)  NOT NULL,
        Taxable             bit            NOT NULL CONSTRAINT DF_HospitalBillLines_Taxable DEFAULT(0),
        VatRatePercent      decimal(5,2)   NOT NULL CONSTRAINT DF_HospitalBillLines_Vat DEFAULT(0),
        RevenueAccountId    int            NULL,
        Source              nvarchar(20)   NOT NULL,
        AddedAt             datetime2      NOT NULL,
        AddedByEmployeeId   int            NULL,
        CONSTRAINT CK_HospitalBillLines_Qty CHECK (Quantity > 0 AND UnitPrice >= 0),
        CONSTRAINT CK_HospitalBillLines_Source CHECK (Source IN ('consultation','order','accommodation','manual'))
    );
    CREATE INDEX IX_HospitalBillLines_Bill ON dbo.HospitalBillLines (CompanyId, BillId);
END

IF OBJECT_ID('dbo.HospitalCashierShifts','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCashierShifts (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalCashierShifts PRIMARY KEY,
        CompanyId           int            NOT NULL,
        Desk                nvarchar(100)  NOT NULL,
        CashierEmployeeId   int            NOT NULL,
        Status              nvarchar(20)   NOT NULL CONSTRAINT DF_HospitalCashierShifts_Status DEFAULT('Open'),
        OpenedAt            datetime2      NOT NULL,
        OpeningFloat        decimal(18,4)  NOT NULL CONSTRAINT DF_HospitalCashierShifts_Float DEFAULT(0),
        ClosedAt            datetime2      NULL,
        ExpectedCash        decimal(18,4)  NULL,
        CountedCash         decimal(18,4)  NULL,
        Variance            decimal(18,4)  NULL,
        CountJson           nvarchar(2000) NULL,
        HandoverTo          nvarchar(200)  NULL,
        JournalEntryId      int            NULL,
        CONSTRAINT CK_HospitalCashierShifts_Status CHECK (Status IN ('Open','Closed')),
        CONSTRAINT CK_HospitalCashierShifts_Float CHECK (OpeningFloat >= 0)
    );
    CREATE UNIQUE INDEX UX_HospitalCashierShifts_OneOpen ON dbo.HospitalCashierShifts (CompanyId, CashierEmployeeId) WHERE Status = 'Open';
    CREATE INDEX IX_HospitalCashierShifts_Day ON dbo.HospitalCashierShifts (CompanyId, OpenedAt);
END

IF OBJECT_ID('dbo.HospitalPayments','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPayments (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPayments PRIMARY KEY,
        CompanyId           int            NOT NULL,
        BillId              int            NOT NULL CONSTRAINT FK_HospitalPayments_Bill REFERENCES dbo.HospitalBills(ID),
        ShiftId             int            NOT NULL CONSTRAINT FK_HospitalPayments_Shift REFERENCES dbo.HospitalCashierShifts(ID),
        ReceiptNo           nvarchar(30)   NOT NULL,
        Amount              decimal(18,4)  NOT NULL,
        Method              nvarchar(20)   NOT NULL,
        ReceivedAt          datetime2      NOT NULL,
        CashierEmployeeId   int            NOT NULL,
        JournalEntryId      int            NULL,
        CONSTRAINT CK_HospitalPayments_Amount CHECK (Amount > 0),
        CONSTRAINT CK_HospitalPayments_Method CHECK (Method IN ('Cash','Card','Transfer','Wallet'))
    );
    CREATE UNIQUE INDEX UX_HospitalPayments_Receipt ON dbo.HospitalPayments (CompanyId, ReceiptNo);
    CREATE INDEX IX_HospitalPayments_Bill ON dbo.HospitalPayments (CompanyId, BillId);
    CREATE INDEX IX_HospitalPayments_Shift ON dbo.HospitalPayments (CompanyId, ShiftId);
END

IF COL_LENGTH('dbo.HospitalBills','BillNo') IS NULL OR COL_LENGTH('dbo.HospitalPayments','ShiftId') IS NULL
    THROW 51951, 'Hospital billing tables exist with the wrong shape: schema mismatch.', 1;

COMMIT;
