-- ============================================================================
-- BinStock: per-(item, warehouse, bin) QUANTITY only. Rack-level stock for the hypermarket.
-- GOLDEN RULE: quantity only — NO value, NO GL. Never part of the 8 financial invariants.
-- Reconciliation (quantity check, not a GL invariant): Σ BinStock.QtyOnHand per (item, warehouse) <= StockBalances.QtyOnHand.
-- Maintained by StockService whenever a movement carries a BinLocationId. Idempotent.
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'BinStocks')
BEGIN
    CREATE TABLE dbo.BinStocks (
        ID              INT IDENTITY(1,1) PRIMARY KEY,
        CompanyID       INT           NOT NULL,
        WarehouseId     INT           NOT NULL,
        BinLocationId   INT           NOT NULL,
        ItemId          INT           NOT NULL,
        QtyOnHand       DECIMAL(19,4) NOT NULL CONSTRAINT DF_BinStocks_Qty DEFAULT (0),
        LastMovementAt  DATETIME2     NULL
    );
    CREATE UNIQUE INDEX UX_BinStocks_item_wh_bin ON dbo.BinStocks (CompanyID, WarehouseId, BinLocationId, ItemId);
    CREATE INDEX IX_BinStocks_bin ON dbo.BinStocks (BinLocationId);
END
GO
