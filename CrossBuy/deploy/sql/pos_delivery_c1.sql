-- POS-C1: Delivery data + fee (idempotent). Operational tables + frozen order columns + branch settings.

-- DeliveryZones (per-branch zone + fee)
IF OBJECT_ID('DeliveryZones','U') IS NULL
CREATE TABLE DeliveryZones (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    BranchId INT NOT NULL,
    Name NVARCHAR(200) NOT NULL,
    NameEn NVARCHAR(200) NULL,
    Fee DECIMAL(19,4) NOT NULL DEFAULT 0,
    IsActive BIT NOT NULL DEFAULT 1
);
GO

-- CustomerAddresses (a customer can have many saved delivery addresses)
IF OBJECT_ID('CustomerAddresses','U') IS NULL
CREATE TABLE CustomerAddresses (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    CompanyId INT NOT NULL,
    CustomerId INT NOT NULL,
    DeliveryZoneId INT NULL,
    Area NVARCHAR(200) NOT NULL DEFAULT '',
    Address NVARCHAR(1000) NOT NULL DEFAULT '',
    Phone NVARCHAR(50) NOT NULL DEFAULT '',
    IsDefault BIT NOT NULL DEFAULT 0,
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- PosOrders: frozen delivery snapshot
IF COL_LENGTH('PosOrders','DeliveryZoneId') IS NULL ALTER TABLE PosOrders ADD DeliveryZoneId INT NULL;
IF COL_LENGTH('PosOrders','DeliveryAddress') IS NULL ALTER TABLE PosOrders ADD DeliveryAddress NVARCHAR(1000) NULL;
IF COL_LENGTH('PosOrders','DeliveryArea') IS NULL ALTER TABLE PosOrders ADD DeliveryArea NVARCHAR(200) NULL;
IF COL_LENGTH('PosOrders','DeliveryPhone') IS NULL ALTER TABLE PosOrders ADD DeliveryPhone NVARCHAR(50) NULL;
IF COL_LENGTH('PosOrders','DeliveryFee') IS NULL ALTER TABLE PosOrders ADD DeliveryFee DECIMAL(19,4) NOT NULL DEFAULT 0;
GO

-- BranchPosSettings: delivery config
IF COL_LENGTH('BranchPosSettings','DefaultDeliveryFee') IS NULL ALTER TABLE BranchPosSettings ADD DefaultDeliveryFee DECIMAL(19,4) NULL;
IF COL_LENGTH('BranchPosSettings','DeliveryRevenueAccountId') IS NULL ALTER TABLE BranchPosSettings ADD DeliveryRevenueAccountId INT NULL;
IF COL_LENGTH('BranchPosSettings','DeliveryTaxExempt') IS NULL ALTER TABLE BranchPosSettings ADD DeliveryTaxExempt BIT NOT NULL DEFAULT 0;
GO

-- Seed delivery zones for branch 15 (only if none yet)
IF NOT EXISTS (SELECT 1 FROM DeliveryZones WHERE BranchId = 15)
INSERT INTO DeliveryZones (BranchId, Name, NameEn, Fee, IsActive) VALUES
    (15, N'وسط البلد', 'Downtown', 20, 1),
    (15, N'المعادي',   'Maadi',    35, 1),
    (15, N'مدينة نصر', 'Nasr City',30, 1);
GO

-- Branch 15 default delivery fee (fallback), if not set
UPDATE BranchPosSettings SET DefaultDeliveryFee = 25 WHERE BranchId = 15 AND DefaultDeliveryFee IS NULL;
GO
