-- ============================================================================
-- Purchase invoice lines gain a storage location (Section / Rack).
--
-- WHY: a purchase invoice line could name a Warehouse and nothing finer, so the
-- one document that most often BRINGS GOODS IN had no way to say where they were
-- put. Goods Receipt, Delivery and Transfer all carried a BinLocationId already;
-- the invoice path was the hole, and StockMovement.BinLocationId came out null on
-- every row it posted.
--
-- PURE LOCATIONAL, like every other use of BinLocations: it never carries value
-- and never appears in a GL posting. Valuation stays at (Item, Warehouse) on
-- StockBalances and StockService remains the sole stock writer. 8 invariants
-- unchanged.
--
-- NULLABLE on purpose. A blank is not an error: StockService resolves the item's
-- own default location (ItemWarehouseSetting.DefaultBinLocationId, else
-- DefaultSectionId) at posting time, so an invoice typed without a location still
-- records one. An explicit choice always wins over the default.
--
-- Idempotent: safe to re-run.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.PurchaseInvoiceLines') AND name = N'BinLocationId')
BEGIN
    ALTER TABLE dbo.PurchaseInvoiceLines ADD BinLocationId INT NULL;
END
GO

-- No foreign key, deliberately, matching ItemWarehouseSettings.DefaultSectionId and
-- StockMovements.BinLocationId: a location may be retired while the documents that
-- referenced it stay readable. StockService re-checks that a stored location is still
-- live and in the right warehouse before it stamps a movement with it.

-- Lines that name a warehouse are the only ones a location can apply to; an index on
-- that pair is what the plan screens read back through.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.PurchaseInvoiceLines') AND name = N'IX_PurchaseInvoiceLines_Bin')
BEGIN
    CREATE INDEX IX_PurchaseInvoiceLines_Bin
        ON dbo.PurchaseInvoiceLines (WarehouseId, BinLocationId)
        WHERE BinLocationId IS NOT NULL;
END
GO
