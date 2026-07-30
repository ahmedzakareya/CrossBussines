SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
-- =====================================================================================
-- CrossBuy — PRODUCTION clean-slate purge.
-- RUN THIS ONCE, ON THE SERVER, AFTER restoring CrossBuyDB2 from the dev backup,
-- to wipe all DEMO/TEST data while PRESERVING masters & configuration.
--
-- KEEPS (config/reference): Companies, Branches, Currencies, ExchangeRateTypes,
--   ExchangeRates, Accounts (chart of accounts), *Settings, FiscalYears/Periods,
--   Warehouses, ItemCategories, UoM, TaxBrackets, Users/Roles/Permissions, Menu,
--   CrmPipelines/Stages, CrmUserRoles, AccountingUserRole, AppCache (sessions).
-- DELETES: all transactional/ledger data + all CRM/marketing demo entities (below).
--
-- Every statement is guarded (dynamic SQL + OBJECT_ID check) so a table that does
-- not exist on this database simply skips — the run never aborts.
-- Children are deleted before parents (FK-safe).
-- =====================================================================================

DECLARE @tables TABLE (name SYSNAME, ord INT);
INSERT INTO @tables (name, ord) VALUES
 -- ---- GL ----
 ('JournalEntryLines',1),('JournalEntries',2),
 -- ---- stock ledger ----
 ('StockMovements',1),('StockCostLayers',1),('StockBatches',1),('StockSerials',1),('StockBalances',2),
 -- ---- AR docs ----
 ('SalesInvoiceLines',1),('SalesInvoices',2),('SalesReturnLines',1),('SalesReturns',2),
 ('ReceiptAllocations',1),('Receipts',2),
 -- ---- AP docs ----
 ('PurchaseInvoiceLines',1),('PurchaseInvoices',2),('PurchaseReturnLines',1),('PurchaseReturns',2),
 ('PaymentAllocations',1),('Payments',2),
 -- ---- FX revaluation (multi-currency) ----
 ('FxRevaluationRuns',2),
 -- ---- bank ----
 ('BankReconciliationLines',1),('BankReconciliations',2),
 -- ---- procurement / selling docs ----
 ('GoodsReceiptLines',1),('GoodsReceipts',2),('PurchaseOrderLines',1),('PurchaseOrders',2),
 ('DeliveryNoteLines',1),('DeliveryNotes',2),('SalesOrderLines',1),('SalesOrders',2),
 ('QuotationLines',1),('Quotations',2),
 -- ---- inventory movements/adjustments ----
 ('StockTransferLines',1),('StockTransfers',2),('StockCountLines',1),('StockCounts',2),
 ('StockWriteOffLines',1),('StockWriteOffs',2),('LandedCostCharges',1),('LandedCosts',2),
 ('InventoryApprovals',2),('OpeningBalanceControls',1),('OpeningBalances',2),
 -- ---- fixed assets ----
 ('DepreciationLines',1),('DepreciationRuns',2),('FixedAssets',3),
 -- ---- tax / closing ----
 ('VatReturns',2),('YearEndClosings',2),
 -- ---- HR GL-posting docs ----
 ('Payslips',2),('FinalSettlements',2),('LeaveEncashments',2),('LeaveProvisionRuns',2),('LeaveCarryOvers',2),
 -- ---- CRM transactional + demo entities (3-1 .. 3-5) ----
 ('OpportunityProducts',1),('Activities',1),('CampaignMembers',1),('CrmListMembers',1),
 ('Opportunities',2),('CrmMarketingLists',3),('Campaigns',3),
 ('CrmContacts',2),('CrmAccounts',3),('Leads',4),
 -- ---- logs ----
 ('IntegrityCheckRuns',2),('Notifications',2);

DECLARE @name SYSNAME, @sql NVARCHAR(400);
DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT name FROM @tables ORDER BY ord ASC, name;
OPEN c; FETCH NEXT FROM c INTO @name;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(@name,'U') IS NOT NULL
    BEGIN
        SET @sql = N'DELETE FROM ' + QUOTENAME(@name) + N';';
        EXEC sp_executesql @sql;
        PRINT 'purged ' + @name;
    END
    FETCH NEXT FROM c INTO @name;
END
CLOSE c; DEALLOCATE c;

-- =====================================================================================
-- OPTIONAL — master parties/items. DELETE these ONLY if every Customer / Vendor / Item
-- in the dev DB is demo data you do NOT want in production. If you created any REAL
-- master records, comment the relevant lines out. Uncomment to use.
-- =====================================================================================
-- DELETE FROM ItemBarcodes;  DELETE FROM Items;
-- DELETE FROM Customers;
-- DELETE FROM Vendors;

PRINT 'CrossBuy production purge complete.';
