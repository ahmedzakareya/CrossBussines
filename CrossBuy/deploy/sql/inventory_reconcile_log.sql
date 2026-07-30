-- HM-D6 item 5: permanent audit trail for inv-reconcile (one row per changed StockBalance). Idempotent.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'InventoryReconcileLogs')
BEGIN
    CREATE TABLE dbo.InventoryReconcileLogs (
        ID              INT IDENTITY(1,1) PRIMARY KEY,
        CompanyID       INT            NOT NULL,
        ItemId          INT            NOT NULL,
        WarehouseId     INT            NOT NULL,
        QtyBefore       DECIMAL(19,4)  NOT NULL,
        QtyAfter        DECIMAL(19,4)  NOT NULL,
        ValueBefore     DECIMAL(19,4)  NOT NULL,
        ValueAfter      DECIMAL(19,4)  NOT NULL,
        AvgBefore       DECIMAL(19,4)  NOT NULL,
        AvgAfter        DECIMAL(19,4)  NOT NULL,
        RanBy           NVARCHAR(128)  NULL,
        RanAt           DATETIME2      NOT NULL,
        JournalEntryId  INT            NULL,
        Reason          NVARCHAR(400)  NULL
    );
    CREATE INDEX IX_InvReconcileLog_Item ON dbo.InventoryReconcileLogs (CompanyID, ItemId, WarehouseId);
END
