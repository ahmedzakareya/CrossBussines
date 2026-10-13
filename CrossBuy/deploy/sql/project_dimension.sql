-- ============================================================================
-- Project dimension activation. Idempotent. ProjectId stays nullable everywhere (analytic dimension,
-- does NOT change GL/stock posting). JournalEntryLines.ProjectId already exists (dormant → now populated).
-- ============================================================================
IF COL_LENGTH('Projects','StartDate') IS NULL ALTER TABLE Projects ADD StartDate datetime2(7) NULL;
GO
IF COL_LENGTH('Projects','EndDate') IS NULL ALTER TABLE Projects ADD EndDate datetime2(7) NULL;
GO
IF COL_LENGTH('Projects','Budget') IS NULL ALTER TABLE Projects ADD Budget decimal(19,4) NULL;
GO
IF COL_LENGTH('SalesInvoices','ProjectId')    IS NULL ALTER TABLE SalesInvoices    ADD ProjectId int NULL;
GO
IF COL_LENGTH('PurchaseInvoices','ProjectId') IS NULL ALTER TABLE PurchaseInvoices ADD ProjectId int NULL;
GO
IF COL_LENGTH('SalesOrders','ProjectId')      IS NULL ALTER TABLE SalesOrders      ADD ProjectId int NULL;
GO
IF COL_LENGTH('PurchaseOrders','ProjectId')   IS NULL ALTER TABLE PurchaseOrders   ADD ProjectId int NULL;
GO
