-- HM-4: pricing management (bulk price change) + shelf labels + price-check.
-- Idempotent; NOT a migration. Adds ONE audit table; no change to PriceList/PriceListLine schema.
-- (HM-D43 — the MinQty<=0?1 normalization — is a CODE change in PricingService.SaveAsync, not a schema change.)

SET NOCOUNT ON;

-- PriceChangeLogs — the durable, append-only record of every bulk price change (and every undo).
-- One row per (list, item, unit) touched, per batch. References (PriceListId, ItemId, UoMId) NOT LineId,
-- because PricingService.SaveAsync full-replaces a list's lines and renumbers LineIds (HM-D47).
-- Table name is PLURAL to match the EF DbSet<PriceChangeLog> PriceChangeLogs (EF maps to the property name).
-- Decimals are decimal(19,4) to match PriceListLine.UnitPrice (the model's global convention is (19,4),
-- so EF sees model==DB and the ef_precision guard stays green).
IF OBJECT_ID('PriceChangeLogs','U') IS NULL
CREATE TABLE PriceChangeLogs (
    ID                bigint IDENTITY(1,1) PRIMARY KEY,
    CompanyID         int NOT NULL,
    BatchId           uniqueidentifier NOT NULL,          -- one id per bulk operation (and per undo)
    PriceListId       int NOT NULL,
    ItemId            int NOT NULL,
    UoMId             int NULL,                            -- the priced unit (null = base/any line)
    OldPrice          decimal(19,4) NULL,
    NewPrice          decimal(19,4) NULL,
    AdjustType        nvarchar(10) NOT NULL,               -- Percent | Amount | Undo
    AdjustValue       decimal(19,4) NOT NULL,              -- the % or amount applied (0 for Undo)
    PriceRounding     nvarchar(14) NOT NULL,               -- None | Nearest5Fils | Nearest10Fils
    Reason            nvarchar(400) NOT NULL,              -- mandatory business reason
    ReversalOfBatchId uniqueidentifier NULL,               -- set on Undo rows → the batch they reverse
    PerformedBy       nvarchar(256) NULL,
    PerformedAt       datetime2 NOT NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_PriceChangeLogs_Batch' AND object_id=OBJECT_ID('PriceChangeLogs'))
    CREATE INDEX IX_PriceChangeLogs_Batch ON PriceChangeLogs(CompanyID, BatchId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_PriceChangeLogs_Line' AND object_id=OBJECT_ID('PriceChangeLogs'))
    CREATE INDEX IX_PriceChangeLogs_Line ON PriceChangeLogs(CompanyID, PriceListId, ItemId, UoMId);

SELECT (SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='PriceChangeLogs') AS PriceChangeLogsTable,
       (SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID('PriceChangeLogs') AND name LIKE 'IX_PriceChangeLogs%') AS Indexes;
