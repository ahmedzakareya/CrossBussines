-- PROPOSAL ONLY: NOT APPLIED / NOT RELEASE ACCEPTED.
-- SliceId: R1-POS-SETUP-001
-- Supersedes both inputs after review; originals remain intact.
-- Input: CrossBuy/deploy/sql/pos_setup.sql SHA256 b287595b7fd7110188fea6de272402c5ddc84ab0767d3d8da538a99d8ee270ac
-- Input: deploy/sql/pos_setup.sql SHA256 babdddeabc668ecfbf641f996bf3e9317489fbc6d57c88bb31069816600360d0
IF COL_LENGTH('dbo.Branches','ActivityPresetCode') IS NULL ALTER TABLE dbo.Branches ADD ActivityPresetCode NVARCHAR(30) NULL;
GO
-- POS setup foundation (activity presets, capabilities, branch POS settings, dining areas, kitchen stations, tables).
-- Idempotent, additive. Base tables — later columns (DiningAreas.NameEn, KitchenStations.NameEn) are added by
-- pos_dining_area_nameen.sql / pos_station_nameen.sql. Run BEFORE those and before pos_quickmenu.sql.
IF OBJECT_ID('ActivityPresets','U') IS NULL
CREATE TABLE ActivityPresets (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    Code NVARCHAR(30) NOT NULL,
    Name NVARCHAR(100) NOT NULL,
    NameEn NVARCHAR(100) NOT NULL,
    Sort INT NOT NULL DEFAULT 0
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UQ_ActivityPresets_Code' AND object_id=OBJECT_ID('ActivityPresets'))
    CREATE UNIQUE INDEX UQ_ActivityPresets_Code ON ActivityPresets (Code);
GO
IF OBJECT_ID('ActivityPresetCapabilities','U') IS NULL
CREATE TABLE ActivityPresetCapabilities (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    PresetId INT NOT NULL,
    CapabilityKey NVARCHAR(40) NOT NULL,
    DefaultEnabled BIT NOT NULL DEFAULT 0
);
GO
IF OBJECT_ID('BranchCapabilities','U') IS NULL
CREATE TABLE BranchCapabilities (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    BranchId INT NOT NULL,
    CapabilityKey NVARCHAR(40) NOT NULL,
    Enabled BIT NOT NULL DEFAULT 0
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UQ_BranchCapabilities' AND object_id=OBJECT_ID('BranchCapabilities'))
    CREATE UNIQUE INDEX UQ_BranchCapabilities ON BranchCapabilities (BranchId, CapabilityKey);
GO
IF OBJECT_ID('BranchPosSettings','U') IS NULL
CREATE TABLE BranchPosSettings (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    BranchId INT NOT NULL,
    DefaultSalesWarehouseId INT NULL,
    DefaultPriceListId INT NULL,
    ServiceChargePct DECIMAL(9,4) NULL,
    DefaultCurrencyId INT NULL,
    DefaultDeliveryFee DECIMAL(19,4) NULL,
    DeliveryRevenueAccountId INT NULL,
    DeliveryTaxExempt BIT NOT NULL DEFAULT 0
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UQ_BranchPosSettings' AND object_id=OBJECT_ID('BranchPosSettings'))
    CREATE UNIQUE INDEX UQ_BranchPosSettings ON BranchPosSettings (BranchId);
GO
IF OBJECT_ID('DiningAreas','U') IS NULL
CREATE TABLE DiningAreas (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    BranchId INT NOT NULL,
    Code NVARCHAR(30) NOT NULL,
    Name NVARCHAR(100) NOT NULL,
    Sort INT NOT NULL DEFAULT 0,
    IsActive BIT NOT NULL DEFAULT 1
);   -- NameEn added by pos_dining_area_nameen.sql
GO
IF OBJECT_ID('KitchenStations','U') IS NULL
CREATE TABLE KitchenStations (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    BranchId INT NOT NULL,
    Code NVARCHAR(30) NOT NULL,
    Name NVARCHAR(100) NOT NULL,
    StationType NVARCHAR(30) NOT NULL DEFAULT N'Kitchen',
    IsActive BIT NOT NULL DEFAULT 1
);   -- NameEn added by pos_station_nameen.sql
GO
IF OBJECT_ID('RestaurantTables','U') IS NULL
CREATE TABLE RestaurantTables (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    DiningAreaId INT NOT NULL,
    Code NVARCHAR(30) NOT NULL,
    Seats INT NOT NULL DEFAULT 4,
    X DECIMAL(9,2) NOT NULL DEFAULT 0,
    Y DECIMAL(9,2) NOT NULL DEFAULT 0,
    W DECIMAL(9,2) NOT NULL DEFAULT 80,
    H DECIMAL(9,2) NOT NULL DEFAULT 80,
    Shape NVARCHAR(20) NOT NULL DEFAULT N'Square',
    QrToken NVARCHAR(64) NULL,
    IsActive BIT NOT NULL DEFAULT 1,
    Status NVARCHAR(20) NOT NULL DEFAULT N'Available'
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_RestaurantTables_area' AND object_id=OBJECT_ID('RestaurantTables'))
    CREATE INDEX IX_RestaurantTables_area ON RestaurantTables (DiningAreaId);
GO

-- Reconcile a database that received the narrower package-tree base first.
IF COL_LENGTH('dbo.BranchPosSettings','DefaultDeliveryFee') IS NULL ALTER TABLE dbo.BranchPosSettings ADD DefaultDeliveryFee DECIMAL(19,4) NULL;
GO
IF COL_LENGTH('dbo.BranchPosSettings','DeliveryRevenueAccountId') IS NULL ALTER TABLE dbo.BranchPosSettings ADD DeliveryRevenueAccountId INT NULL;
GO
IF COL_LENGTH('dbo.BranchPosSettings','DeliveryTaxExempt') IS NULL ALTER TABLE dbo.BranchPosSettings ADD DeliveryTaxExempt BIT NOT NULL CONSTRAINT DF_R1_BranchPosSettings_DeliveryTaxExempt DEFAULT 0;
GO
IF COL_LENGTH('dbo.RestaurantTables','Status') IS NULL ALTER TABLE dbo.RestaurantTables ADD Status NVARCHAR(20) NOT NULL CONSTRAINT DF_R1_RestaurantTables_Status DEFAULT N'Available';
GO
-- ===== seed the preset catalog (idempotent) =====
IF NOT EXISTS (SELECT 1 FROM dbo.ActivityPresets WHERE Code = N'Restaurant') INSERT dbo.ActivityPresets (Code, Name, NameEn, Sort) VALUES (N'Restaurant', N'مطعم', N'Restaurant', 1);
IF NOT EXISTS (SELECT 1 FROM dbo.ActivityPresets WHERE Code = N'Cafe')       INSERT dbo.ActivityPresets (Code, Name, NameEn, Sort) VALUES (N'Cafe', N'كافيه', N'Cafe', 2);
IF NOT EXISTS (SELECT 1 FROM dbo.ActivityPresets WHERE Code = N'Hyper')      INSERT dbo.ActivityPresets (Code, Name, NameEn, Sort) VALUES (N'Hyper', N'هايبر ماركت', N'Hypermarket', 3);
IF NOT EXISTS (SELECT 1 FROM dbo.ActivityPresets WHERE Code = N'Retail')     INSERT dbo.ActivityPresets (Code, Name, NameEn, Sort) VALUES (N'Retail', N'تجزئة', N'Retail', 4);
GO

-- preset -> default capabilities (insert each row once)
DECLARE @caps TABLE (Code NVARCHAR(30), Cap NVARCHAR(40), En BIT);
INSERT @caps VALUES
 (N'Restaurant',N'Tables',1),(N'Restaurant',N'Kitchen',1),(N'Restaurant',N'Manufacturing',1),(N'Restaurant',N'Modifiers',1),(N'Restaurant',N'QrOrder',1),(N'Restaurant',N'Barcode',0),(N'Restaurant',N'Weight',0),
 (N'Cafe',N'Tables',1),(N'Cafe',N'Kitchen',1),(N'Cafe',N'Modifiers',1),(N'Cafe',N'QrOrder',1),(N'Cafe',N'Manufacturing',0),(N'Cafe',N'Barcode',0),(N'Cafe',N'Weight',0),
 (N'Hyper',N'Barcode',1),(N'Hyper',N'Weight',1),(N'Hyper',N'Manufacturing',1),(N'Hyper',N'Tables',0),(N'Hyper',N'Kitchen',0),(N'Hyper',N'Modifiers',0),(N'Hyper',N'QrOrder',0),
 (N'Retail',N'Barcode',1),(N'Retail',N'Weight',0),(N'Retail',N'Tables',0),(N'Retail',N'Kitchen',0),(N'Retail',N'Manufacturing',0),(N'Retail',N'Modifiers',0),(N'Retail',N'QrOrder',0);
INSERT dbo.ActivityPresetCapabilities (PresetId, CapabilityKey, DefaultEnabled)
SELECT p.ID, c.Cap, c.En FROM @caps c JOIN dbo.ActivityPresets p ON p.Code = c.Code
WHERE NOT EXISTS (SELECT 1 FROM dbo.ActivityPresetCapabilities x WHERE x.PresetId = p.ID AND x.CapabilityKey = c.Cap);
GO
