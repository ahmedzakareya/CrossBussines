-- ============================================================================
-- Warehouse internal subdivision (Section/Rack) + hierarchical item classification (Category/Group)
-- Foundation for the multi-activity platform. ADDITIVE + backward compatible.
-- GOLDEN RULE: both are pure locational / analytic dimensions. They NEVER carry
-- value or appear in any GL posting. Valuation stays at (Item, Warehouse) on
-- StockBalances; StockService remains the sole stock writer. -> 8 invariants unchanged.
-- Idempotent: safe to re-run.
-- ============================================================================

-- 1) BinLocation gets a LocationType discriminator (Section / Rack / Bin) over the existing ParentId tree.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.BinLocations') AND name = N'LocationType')
BEGIN
    ALTER TABLE dbo.BinLocations ADD LocationType NVARCHAR(20) NOT NULL CONSTRAINT DF_BinLocations_LocationType DEFAULT N'Section';
END
GO

-- 2) ItemCategory gets a Kind discriminator (Category root / Group child). Existing rows default to Category (= root, still valid).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ItemCategories') AND name = N'Kind')
BEGIN
    ALTER TABLE dbo.ItemCategories ADD Kind NVARCHAR(20) NOT NULL CONSTRAINT DF_ItemCategories_Kind DEFAULT N'Category';
END
GO

-- 3) ItemWarehouseSetting gets a default Section (required by UI, nullable in DB). Rack stays optional on the existing DefaultBinLocationId.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ItemWarehouseSettings') AND name = N'DefaultSectionId')
BEGIN
    ALTER TABLE dbo.ItemWarehouseSettings ADD DefaultSectionId INT NULL;
END
GO
