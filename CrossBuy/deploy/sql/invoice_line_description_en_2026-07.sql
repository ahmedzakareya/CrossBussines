-- =============================================================================
-- CrossBuy — invoice-line English description columns (session 2026-07)
-- Adds ItemDescriptionEn to Sales/Purchase invoice lines so English/French UI
-- shows English line text instead of the stored Arabic. Additive + idempotent.
-- Run:  sqlcmd -S <server> -d <database> -E -C -i invoice_line_description_en_2026-07.sql
-- Pure DDL — plain UTF-8 is fine, no BOM required.
-- =============================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF COL_LENGTH('dbo.SalesInvoiceLines','ItemDescriptionEn') IS NULL
BEGIN
    ALTER TABLE dbo.SalesInvoiceLines ADD ItemDescriptionEn NVARCHAR(400) NULL;
    PRINT 'ADDED SalesInvoiceLines.ItemDescriptionEn';
END ELSE PRINT 'SalesInvoiceLines.ItemDescriptionEn EXISTS';
GO

IF COL_LENGTH('dbo.PurchaseInvoiceLines','ItemDescriptionEn') IS NULL
BEGIN
    ALTER TABLE dbo.PurchaseInvoiceLines ADD ItemDescriptionEn NVARCHAR(400) NULL;
    PRINT 'ADDED PurchaseInvoiceLines.ItemDescriptionEn';
END ELSE PRINT 'PurchaseInvoiceLines.ItemDescriptionEn EXISTS';
GO
