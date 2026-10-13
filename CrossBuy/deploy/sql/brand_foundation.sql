-- ============================================================================
-- Brand foundation — trade name under a Company (NOT a legal entity). Single ledger + analytic dimension.
-- Idempotent. BrandId is nullable everywhere; no change to GL/stock posting. Invariants stay green.
-- ============================================================================

IF OBJECT_ID('dbo.Brands','U') IS NULL
CREATE TABLE dbo.Brands (
    ID              int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyId       int NOT NULL,
    Code            nvarchar(50) NOT NULL,
    Name            nvarchar(200) NOT NULL,
    NameEn          nvarchar(200) NULL,
    IsActive        bit NOT NULL CONSTRAINT DF_Brands_Active DEFAULT(1),
    CreatedAt       datetime2(7) NULL,
    LogoPath        nvarchar(400) NULL,
    ColorPrimary    nvarchar(20) NULL,
    ColorSecondary  nvarchar(20) NULL,
    ColorAccent     nvarchar(20) NULL,
    TradeName       nvarchar(300) NULL,
    Address         nvarchar(500) NULL,
    Phone           nvarchar(50) NULL,
    Email           nvarchar(200) NULL,
    Website         nvarchar(200) NULL,
    ReceiptFooterAr nvarchar(1000) NULL,
    ReceiptFooterEn nvarchar(1000) NULL
);
GO
IF OBJECT_ID('dbo.Brands','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Brands_Company' AND object_id = OBJECT_ID('dbo.Brands'))
    CREATE INDEX IX_Brands_Company ON dbo.Brands(CompanyId, IsActive);
GO

-- Location (Branch) gains an optional Brand link (nullable → backward-compatible)
IF COL_LENGTH('Branches','BrandId') IS NULL
    ALTER TABLE Branches ADD BrandId int NULL;
GO

-- Org tree: register Brand as a new node type (6) between Company (1) and Branch (2)
IF NOT EXISTS (SELECT 1 FROM HierarchicalTypes WHERE ID = 6)
BEGIN
    SET IDENTITY_INSERT HierarchicalTypes ON;
    INSERT INTO HierarchicalTypes (ID, TypeNameAr, TypeNameEn) VALUES (6, N'علامة تجارية', N'Brand');
    SET IDENTITY_INSERT HierarchicalTypes OFF;
END
GO
