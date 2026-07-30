-- ============================================================================
-- Operations platform (POS) — SETUP tables. Additive + backward compatible.
-- Configuration only: NEVER posts GL or writes stock. The 8 financial invariants are untouched.
-- Idempotent: safe to re-run.
-- IMPORTANT: this file contains Arabic seed data — run it with the UTF-8 codepage:
--   sqlcmd -S . -d CrossBuyDB2 -E -C -b -f 65001 -i pos_setup.sql
-- (without -f 65001 sqlcmd mis-decodes the N'..' Arabic literals and stores mojibake).
-- ============================================================================

-- Branch gets the activity-type preset code (nullable = not an operations location).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Branches') AND name = N'ActivityPresetCode')
    ALTER TABLE dbo.Branches ADD ActivityPresetCode NVARCHAR(30) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'ActivityPresets')
    CREATE TABLE dbo.ActivityPresets (
        ID INT IDENTITY(1,1) PRIMARY KEY, Code NVARCHAR(30) NOT NULL, Name NVARCHAR(100) NOT NULL, NameEn NVARCHAR(100) NOT NULL, Sort INT NOT NULL DEFAULT(0),
        CONSTRAINT UQ_ActivityPresets_Code UNIQUE (Code));
GO
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'ActivityPresetCapabilities')
    CREATE TABLE dbo.ActivityPresetCapabilities (
        ID INT IDENTITY(1,1) PRIMARY KEY, PresetId INT NOT NULL, CapabilityKey NVARCHAR(40) NOT NULL, DefaultEnabled BIT NOT NULL DEFAULT(0));
GO
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'BranchCapabilities')
    CREATE TABLE dbo.BranchCapabilities (
        ID INT IDENTITY(1,1) PRIMARY KEY, BranchId INT NOT NULL, CapabilityKey NVARCHAR(40) NOT NULL, Enabled BIT NOT NULL DEFAULT(0),
        CONSTRAINT UQ_BranchCapabilities UNIQUE (BranchId, CapabilityKey));
GO
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'BranchPosSettings')
    CREATE TABLE dbo.BranchPosSettings (
        ID INT IDENTITY(1,1) PRIMARY KEY, BranchId INT NOT NULL, DefaultSalesWarehouseId INT NULL, DefaultPriceListId INT NULL,
        ServiceChargePct DECIMAL(9,4) NULL, DefaultCurrencyId INT NULL,
        CONSTRAINT UQ_BranchPosSettings UNIQUE (BranchId));
GO
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'DiningAreas')
    CREATE TABLE dbo.DiningAreas (
        ID INT IDENTITY(1,1) PRIMARY KEY, BranchId INT NOT NULL, Code NVARCHAR(30) NOT NULL, Name NVARCHAR(100) NOT NULL, Sort INT NOT NULL DEFAULT(0), IsActive BIT NOT NULL DEFAULT(1));
GO
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'KitchenStations')
    CREATE TABLE dbo.KitchenStations (
        ID INT IDENTITY(1,1) PRIMARY KEY, BranchId INT NOT NULL, Code NVARCHAR(30) NOT NULL, Name NVARCHAR(100) NOT NULL, StationType NVARCHAR(30) NOT NULL DEFAULT(N'Kitchen'), IsActive BIT NOT NULL DEFAULT(1));
GO
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'RestaurantTables')
    CREATE TABLE dbo.RestaurantTables (
        ID INT IDENTITY(1,1) PRIMARY KEY, DiningAreaId INT NOT NULL, Code NVARCHAR(30) NOT NULL, Seats INT NOT NULL DEFAULT(4),
        X DECIMAL(9,2) NOT NULL DEFAULT(0), Y DECIMAL(9,2) NOT NULL DEFAULT(0), W DECIMAL(9,2) NOT NULL DEFAULT(80), H DECIMAL(9,2) NOT NULL DEFAULT(80),
        Shape NVARCHAR(20) NOT NULL DEFAULT(N'Square'), QrToken NVARCHAR(64) NULL, IsActive BIT NOT NULL DEFAULT(1));
    CREATE INDEX IX_RestaurantTables_area ON dbo.RestaurantTables (DiningAreaId);
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
