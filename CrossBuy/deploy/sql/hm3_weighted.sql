-- HM-3: weighted items + scale-barcode ingestion. Idempotent; NOT a migration.
-- All additions are backward-compatible (IsWeighted defaults 0; ScaleCode + the 6 branch scale-config columns are NULL),
-- so the restaurant path and every non-weighted item behave exactly as before.

SET NOCOUNT ON;

-- Item: is-weighted flag + the numeric scale code embedded in the scale barcode.
-- NB: ADD ... NOT NULL DEFAULT stamps the default into every existing row (see the ALTER-on-large-table rule in
-- AUDIT-DEVIATIONS.md). Items here is small (~172 rows) so this is instant; measured duration is in the HM-3 report.
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Items' AND COLUMN_NAME='IsWeighted')
    ALTER TABLE Items ADD IsWeighted BIT NOT NULL CONSTRAINT DF_Items_IsWeighted DEFAULT 0;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Items' AND COLUMN_NAME='ScaleCode')
    ALTER TABLE Items ADD ScaleCode INT NULL;
GO
-- Batch break: the filtered index below references ScaleCode added above; index DDL has no deferred name resolution.

-- ScaleCode unique per company, only where set (a FILTERED unique index — non-weighted items keep NULL, no collision).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Items_Company_ScaleCode' AND object_id=OBJECT_ID('Items'))
    CREATE UNIQUE INDEX UX_Items_Company_ScaleCode ON Items(CompanyID, ScaleCode) WHERE ScaleCode IS NOT NULL;

-- Branch-level scale-barcode format (the scale is a device in the branch). NULL prefix = no scale barcodes at this branch.
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='BranchPosSettings' AND COLUMN_NAME='ScaleBarcodePrefix')
    ALTER TABLE BranchPosSettings ADD ScaleBarcodePrefix NVARCHAR(8) NULL;
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='BranchPosSettings' AND COLUMN_NAME='ScaleItemCodeLength')
    ALTER TABLE BranchPosSettings ADD ScaleItemCodeLength INT NULL;
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='BranchPosSettings' AND COLUMN_NAME='ScaleValueLength')
    ALTER TABLE BranchPosSettings ADD ScaleValueLength INT NULL;
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='BranchPosSettings' AND COLUMN_NAME='ScaleValueDecimals')
    ALTER TABLE BranchPosSettings ADD ScaleValueDecimals INT NULL;
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='BranchPosSettings' AND COLUMN_NAME='ScaleValueType')
    ALTER TABLE BranchPosSettings ADD ScaleValueType NVARCHAR(10) NULL;   -- 'Weight' (implemented) | 'Price' (rejected: not supported yet)
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='BranchPosSettings' AND COLUMN_NAME='ScaleCheckAlgo')
    ALTER TABLE BranchPosSettings ADD ScaleCheckAlgo NVARCHAR(20) NULL;   -- 'EanMod10'

SELECT (SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Items' AND COLUMN_NAME IN ('IsWeighted','ScaleCode')) AS ItemCols,
       (SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='BranchPosSettings' AND COLUMN_NAME LIKE 'Scale%') AS BranchScaleCols,
       (SELECT COUNT(*) FROM sys.indexes WHERE name='UX_Items_Company_ScaleCode') AS FilteredIndex;
