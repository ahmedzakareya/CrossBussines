-- =============================================================================================
-- PURCHASE INVOICE LINES — BIN LOCATION
--
-- One column, missing from estates that were built before the bin-level receiving work and never
-- carried by a script. Read from the catalogue of a database that already has it: INT NULL, no
-- default, no foreign key — the same shape, so this script cannot disagree with the estate it was
-- taken from.
--
-- ADDITIVE AND IDEMPOTENT. It writes no rows: an existing line simply has no bin recorded, which
-- is exactly what NULL means here.
-- =============================================================================================

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID('dbo.PurchaseInvoiceLines') IS NOT NULL
   AND COL_LENGTH('dbo.PurchaseInvoiceLines', 'BinLocationId') IS NULL
    ALTER TABLE dbo.PurchaseInvoiceLines ADD BinLocationId INT NULL;
GO

PRINT 'purchase_invoice_line_bin.sql complete. No row was updated, inserted or deleted.';
GO
