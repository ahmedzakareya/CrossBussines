-- HM-7 batch-1: batch-aware physical count. StockCountLine records WHICH batch each counted line belongs to, so a
-- positive diff on an expiry-tracked item posts an Adjustment carrying the batch (passing the HM-6 batch-force guard)
-- and a negative diff issues from THAT batch (not FEFO). BatchCreatedInCount flags a line whose batch did not exist and
-- was created by the count itself (goods on the shelf with an unregistered batch) — surfaced as a COUNTED classification.
-- Additive + idempotent. Nullable columns ⇒ non-tracked count lines are untouched (zero regression). NOT a migration.
SET NOCOUNT ON;

IF COL_LENGTH('StockCountLines','BatchNo') IS NULL
    ALTER TABLE StockCountLines ADD BatchNo NVARCHAR(100) NULL;
IF COL_LENGTH('StockCountLines','ExpiryDate') IS NULL
    ALTER TABLE StockCountLines ADD ExpiryDate DATE NULL;
IF COL_LENGTH('StockCountLines','BatchCreatedInCount') IS NULL
    ALTER TABLE StockCountLines ADD BatchCreatedInCount BIT NOT NULL CONSTRAINT DF_StockCountLines_BatchCreatedInCount DEFAULT 0;

-- verify
SELECT COL_LENGTH('StockCountLines','BatchNo') AS BatchNo, COL_LENGTH('StockCountLines','ExpiryDate') AS ExpiryDate,
       COL_LENGTH('StockCountLines','BatchCreatedInCount') AS BatchCreatedInCount;
