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
