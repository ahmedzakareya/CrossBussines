-- HM-2: multi-barcode + multi-unit selling. Two nullable unit dimensions, both backward-compatible (NULL = base / current
-- behaviour, so the restaurant path is unaffected). Idempotent; NOT a migration.
--   PosOrderLines.UoMId    — the unit a cart line is sold in (null = base). Read at pay-time to deduct the correct base qty.
--   PriceListLines.UoMId   — the unit a price line applies to (null = base/any). Lets a carton and a piece be priced directly
--                            (explicit KWD prices, no multiply — the HM-D18 lesson: multiplying a base price makes half-fils).

SET NOCOUNT ON;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='PosOrderLines' AND COLUMN_NAME='UoMId')
    ALTER TABLE PosOrderLines ADD UoMId INT NULL;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='PriceListLines' AND COLUMN_NAME='UoMId')
    ALTER TABLE PriceListLines ADD UoMId INT NULL;

-- SalesInvoiceLines.UoMId carries the sold unit from the invoice line to the stock-out movement (so 1 carton deducts 144 base).
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='SalesInvoiceLines' AND COLUMN_NAME='UoMId')
    ALTER TABLE SalesInvoiceLines ADD UoMId INT NULL;

SELECT (SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='PosOrderLines' AND COLUMN_NAME='UoMId') AS PosOrderLine_UoMId,
       (SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='PriceListLines' AND COLUMN_NAME='UoMId') AS PriceListLine_UoMId,
       (SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='SalesInvoiceLines' AND COLUMN_NAME='UoMId') AS SalesInvoiceLine_UoMId;
