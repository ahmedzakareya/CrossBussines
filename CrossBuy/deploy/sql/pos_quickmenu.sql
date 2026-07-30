-- RC-1 cashier quick menu: per-branch menu groups (tabs) + quick items (buttons) + item PLU (Item.QuickCode).
-- Idempotent, additive. Base PosMenuGroups — PosMenuGroups.KitchenStationId is added by pos_kds_stations.sql.
-- Run AFTER pos_setup.sql (branches) and the Items table already exists.
IF OBJECT_ID('PosMenuGroups','U') IS NULL
CREATE TABLE PosMenuGroups (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    BranchId INT NOT NULL,
    Name NVARCHAR(100) NOT NULL,
    NameEn NVARCHAR(100) NULL,
    Sort INT NOT NULL DEFAULT 0,
    IsActive BIT NOT NULL DEFAULT 1
);   -- KitchenStationId added by pos_kds_stations.sql
GO
IF OBJECT_ID('PosQuickItems','U') IS NULL
CREATE TABLE PosQuickItems (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    BranchId INT NOT NULL,
    GroupId INT NULL,
    ItemId INT NOT NULL,
    Sort INT NOT NULL DEFAULT 0,
    IsActive BIT NOT NULL DEFAULT 1
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_PosQuickItems_branch' AND object_id=OBJECT_ID('PosQuickItems'))
    CREATE INDEX IX_PosQuickItems_branch ON PosQuickItems (BranchId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UQ_PosQuickItems' AND object_id=OBJECT_ID('PosQuickItems'))
    CREATE UNIQUE INDEX UQ_PosQuickItems ON PosQuickItems (BranchId, ItemId);
GO
-- Item PLU / quick code (nullable; uniqueness per company enforced in the service layer)
IF COL_LENGTH('Items','QuickCode') IS NULL ALTER TABLE Items ADD QuickCode NVARCHAR(30) NULL;
GO
