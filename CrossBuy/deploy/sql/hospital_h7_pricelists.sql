-- ==========================================================================================
-- Hospital H7 — price lists. Services, medicines and consumables are priced through LISTS: the
-- main list is the selling price on each card (HospitalServices.Price, Items.SalesPrice for an
-- inventory item in a category mapped to the hospital), and every other list has one currency,
-- a validity, a version and a discount rule, with lines that are reviewed before the list is
-- approved. A bill is priced from the patient's list and keeps its currency.
--
-- FOUR NEW TABLES and NULLABLE COLUMNS on four H2/H6 tables (HospitalPatients.PriceListId,
-- HospitalBills.PriceListId/CurrencyId/ExchangeRate, HospitalBillLines.OutsideList,
-- HospitalPayments.BaseAmount, HospitalBillingSettings.FxDifferenceAccountId).
-- Idempotent: safe to run repeatedly.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalBills','U') IS NULL OR OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51960, 'Run hospital_h2_patients.sql and hospital_h6_billing.sql first.', 1;

IF OBJECT_ID('dbo.HospitalPriceLists','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPriceLists (
        ID                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPriceLists PRIMARY KEY,
        CompanyId            int            NOT NULL,
        Code                 nvarchar(40)   NOT NULL,
        Name                 nvarchar(200)  NOT NULL,
        NameEn               nvarchar(200)  NOT NULL,
        Kind                 nvarchar(20)   NOT NULL,
        CurrencyId           int            NULL,
        FrozenRate           decimal(18,8)  NOT NULL CONSTRAINT DF_HospitalPriceLists_Rate DEFAULT(1),
        ServicesAdjPct       decimal(7,2)   NOT NULL CONSTRAINT DF_HospitalPriceLists_S DEFAULT(0),
        MedicinesAdjPct      decimal(7,2)   NOT NULL CONSTRAINT DF_HospitalPriceLists_M DEFAULT(0),
        ConsumablesAdjPct    decimal(7,2)   NOT NULL CONSTRAINT DF_HospitalPriceLists_C DEFAULT(0),
        RoundingStep         decimal(18,4)  NULL,
        ValidFrom            date           NOT NULL,
        ValidTo              date           NULL,
        Version              int            NOT NULL CONSTRAINT DF_HospitalPriceLists_Ver DEFAULT(1),
        PreviousVersionId    int            NULL,
        Status               nvarchar(20)   NOT NULL CONSTRAINT DF_HospitalPriceLists_Status DEFAULT('Draft'),
        CreatedAt            datetime2      NOT NULL CONSTRAINT DF_HospitalPriceLists_Created DEFAULT(SYSUTCDATETIME()),
        CreatedByEmployeeId  int            NULL,
        ApprovedAt           datetime2      NULL,
        ApprovedByEmployeeId int            NULL,
        CONSTRAINT CK_HospitalPriceLists_Kind CHECK (Kind IN ('Cash','Insurer','Corporate','Government')),
        CONSTRAINT CK_HospitalPriceLists_Status CHECK (Status IN ('Draft','Active','Archived')),
        CONSTRAINT CK_HospitalPriceLists_Rate CHECK (FrozenRate > 0),
        CONSTRAINT CK_HospitalPriceLists_Adj CHECK (ServicesAdjPct >= -100 AND MedicinesAdjPct >= -100 AND ConsumablesAdjPct >= -100),
        CONSTRAINT CK_HospitalPriceLists_Step CHECK (RoundingStep IS NULL OR RoundingStep > 0),
        CONSTRAINT CK_HospitalPriceLists_Dates CHECK (ValidTo IS NULL OR ValidTo >= ValidFrom)
    );
    CREATE UNIQUE INDEX UX_HospitalPriceLists_CodeVersion ON dbo.HospitalPriceLists (CompanyId, Code, Version);
END

IF OBJECT_ID('dbo.HospitalPriceListLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPriceListLines (
        ID                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPriceListLines PRIMARY KEY,
        CompanyId            int            NOT NULL,
        PriceListId          int            NOT NULL CONSTRAINT FK_HospitalPriceListLines_List REFERENCES dbo.HospitalPriceLists(ID),
        ServiceId            int            NULL,
        ItemId               int            NULL,
        FixedPrice           decimal(18,4)  NULL,
        IsExcluded           bit            NOT NULL CONSTRAINT DF_HospitalPriceListLines_Excl DEFAULT(0),
        NeedsReview          bit            NOT NULL CONSTRAINT DF_HospitalPriceListLines_Review DEFAULT(1),
        ConfirmedMainPrice   decimal(18,4)  NULL,
        AddedAt              datetime2      NOT NULL CONSTRAINT DF_HospitalPriceListLines_Added DEFAULT(SYSUTCDATETIME()),
        ReviewedAt           datetime2      NULL,
        ReviewedByEmployeeId int            NULL,
        CONSTRAINT CK_HospitalPriceListLines_Target CHECK ((ServiceId IS NULL AND ItemId IS NOT NULL) OR (ServiceId IS NOT NULL AND ItemId IS NULL)),
        CONSTRAINT CK_HospitalPriceListLines_Price CHECK (FixedPrice IS NULL OR FixedPrice >= 0)
    );
    CREATE UNIQUE INDEX UX_HospitalPriceListLines_Service ON dbo.HospitalPriceListLines (PriceListId, ServiceId) WHERE ServiceId IS NOT NULL;
    CREATE UNIQUE INDEX UX_HospitalPriceListLines_Item ON dbo.HospitalPriceListLines (PriceListId, ItemId) WHERE ItemId IS NOT NULL;
END

IF OBJECT_ID('dbo.HospitalItemCategories','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalItemCategories (
        ID             int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalItemCategories PRIMARY KEY,
        CompanyId      int           NOT NULL,
        ItemCategoryId int           NOT NULL,
        Kind           nvarchar(20)  NOT NULL,
        CONSTRAINT CK_HospitalItemCategories_Kind CHECK (Kind IN ('Medicine','Consumable'))
    );
    CREATE UNIQUE INDEX UX_HospitalItemCategories ON dbo.HospitalItemCategories (CompanyId, ItemCategoryId);
END

IF OBJECT_ID('dbo.HospitalItemPublicPrices','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalItemPublicPrices (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalItemPublicPrices PRIMARY KEY,
        CompanyId           int            NOT NULL,
        ItemId              int            NOT NULL,
        PublicPrice         decimal(18,4)  NOT NULL,
        UpdatedAt           datetime2      NOT NULL CONSTRAINT DF_HospitalItemPublicPrices_At DEFAULT(SYSUTCDATETIME()),
        UpdatedByEmployeeId int            NULL,
        CONSTRAINT CK_HospitalItemPublicPrices_Price CHECK (PublicPrice >= 0)
    );
    CREATE UNIQUE INDEX UX_HospitalItemPublicPrices ON dbo.HospitalItemPublicPrices (CompanyId, ItemId);
END

IF COL_LENGTH('dbo.HospitalPatients','PriceListId') IS NULL
    ALTER TABLE dbo.HospitalPatients ADD PriceListId int NULL;
IF COL_LENGTH('dbo.HospitalBills','PriceListId') IS NULL
    ALTER TABLE dbo.HospitalBills ADD PriceListId int NULL;
IF COL_LENGTH('dbo.HospitalBills','CurrencyId') IS NULL
    ALTER TABLE dbo.HospitalBills ADD CurrencyId int NULL;
IF COL_LENGTH('dbo.HospitalBills','ExchangeRate') IS NULL
    ALTER TABLE dbo.HospitalBills ADD ExchangeRate decimal(18,8) NULL;
IF COL_LENGTH('dbo.HospitalBillLines','OutsideList') IS NULL
    ALTER TABLE dbo.HospitalBillLines ADD OutsideList bit NOT NULL CONSTRAINT DF_HospitalBillLines_Outside DEFAULT(0);
IF COL_LENGTH('dbo.HospitalPayments','BaseAmount') IS NULL
    ALTER TABLE dbo.HospitalPayments ADD BaseAmount decimal(18,4) NULL;
IF COL_LENGTH('dbo.HospitalBillingSettings','FxDifferenceAccountId') IS NULL
    ALTER TABLE dbo.HospitalBillingSettings ADD FxDifferenceAccountId int NULL;

COMMIT;
