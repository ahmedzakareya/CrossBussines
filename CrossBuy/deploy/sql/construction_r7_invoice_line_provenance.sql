-- Forward-only PO -> invoice -> posted journal net-line identities. No backfill or financial writes.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.PurchaseOrderInvoiceLinks','U') IS NULL
 OR OBJECT_ID('dbo.JournalEntryLines','U') IS NULL
 THROW 51700,'Procurement provenance and the general ledger must precede line provenance.',1;
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.PurchaseOrderInvoiceLinks') AND name='AK_PurchaseOrderInvoiceLinks_Order_Invoice_Company')
 ALTER TABLE dbo.PurchaseOrderInvoiceLinks ADD CONSTRAINT AK_PurchaseOrderInvoiceLinks_Order_Invoice_Company UNIQUE(PurchaseOrderId,PurchaseInvoiceId,CompanyID);
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.PurchaseOrderLines') AND name='AK_PurchaseOrderLines_ID_PurchaseOrderId')
 ALTER TABLE dbo.PurchaseOrderLines ADD CONSTRAINT AK_PurchaseOrderLines_ID_PurchaseOrderId UNIQUE(ID,PurchaseOrderId);
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.PurchaseInvoiceLines') AND name='AK_PurchaseInvoiceLines_ID_PurchaseInvoiceId')
 ALTER TABLE dbo.PurchaseInvoiceLines ADD CONSTRAINT AK_PurchaseInvoiceLines_ID_PurchaseInvoiceId UNIQUE(ID,PurchaseInvoiceId);
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.JournalEntries') AND name='AK_JournalEntries_ID_CompanyID')
 ALTER TABLE dbo.JournalEntries ADD CONSTRAINT AK_JournalEntries_ID_CompanyID UNIQUE(ID,CompanyID);
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.JournalEntryLines') AND name='AK_JournalEntryLines_ID_JournalEntryId')
 ALTER TABLE dbo.JournalEntryLines ADD CONSTRAINT AK_JournalEntryLines_ID_JournalEntryId UNIQUE(ID,JournalEntryId);
IF OBJECT_ID('dbo.PurchaseOrderInvoiceLineLinks','U') IS NULL
 CREATE TABLE dbo.PurchaseOrderInvoiceLineLinks(
 ID int IDENTITY NOT NULL CONSTRAINT PK_PurchaseOrderInvoiceLineLinks PRIMARY KEY,
 CompanyID int NOT NULL, PurchaseOrderId int NOT NULL, PurchaseOrderLineId int NOT NULL,
 PurchaseInvoiceId int NOT NULL, PurchaseInvoiceLineId int NOT NULL,
 JournalEntryId int NOT NULL, JournalEntryLineId int NOT NULL, CreatedAt datetime2 NOT NULL,
 CONSTRAINT CK_PurchaseOrderInvoiceLineLinks_Identity CHECK(CompanyID>0 AND PurchaseOrderId>0 AND PurchaseOrderLineId>0 AND PurchaseInvoiceId>0 AND PurchaseInvoiceLineId>0 AND JournalEntryId>0 AND JournalEntryLineId>0),
 CONSTRAINT UQ_PurchaseOrderInvoiceLineLinks_OrderLine UNIQUE(CompanyID,PurchaseOrderLineId),
 CONSTRAINT UQ_PurchaseOrderInvoiceLineLinks_InvoiceLine UNIQUE(CompanyID,PurchaseInvoiceLineId),
 CONSTRAINT UQ_PurchaseOrderInvoiceLineLinks_JournalLine UNIQUE(CompanyID,JournalEntryLineId),
 CONSTRAINT FK_PurchaseOrderInvoiceLineLinks_Header FOREIGN KEY(PurchaseOrderId,PurchaseInvoiceId,CompanyID) REFERENCES dbo.PurchaseOrderInvoiceLinks(PurchaseOrderId,PurchaseInvoiceId,CompanyID),
 CONSTRAINT FK_PurchaseOrderInvoiceLineLinks_OrderLine FOREIGN KEY(PurchaseOrderLineId,PurchaseOrderId) REFERENCES dbo.PurchaseOrderLines(ID,PurchaseOrderId),
 CONSTRAINT FK_PurchaseOrderInvoiceLineLinks_InvoiceLine FOREIGN KEY(PurchaseInvoiceLineId,PurchaseInvoiceId) REFERENCES dbo.PurchaseInvoiceLines(ID,PurchaseInvoiceId),
 CONSTRAINT FK_PurchaseOrderInvoiceLineLinks_Journal FOREIGN KEY(JournalEntryId,CompanyID) REFERENCES dbo.JournalEntries(ID,CompanyID),
 CONSTRAINT FK_PurchaseOrderInvoiceLineLinks_JournalLine FOREIGN KEY(JournalEntryLineId,JournalEntryId) REFERENCES dbo.JournalEntryLines(ID,JournalEntryId));
GO
CREATE OR ALTER TRIGGER dbo.TR_PurchaseOrderInvoiceLineLinks_Immutable ON dbo.PurchaseOrderInvoiceLineLinks AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 51701,'Invoice line provenance is immutable historical evidence.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_PurchaseOrderInvoiceLineLinks_Source ON dbo.PurchaseOrderInvoiceLineLinks AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i
 JOIN dbo.PurchaseOrders po ON po.ID=i.PurchaseOrderId AND po.CompanyID=i.CompanyID
 JOIN dbo.PurchaseInvoices inv ON inv.ID=i.PurchaseInvoiceId AND inv.CompanyID=i.CompanyID
 JOIN dbo.PurchaseInvoiceLines il ON il.ID=i.PurchaseInvoiceLineId AND il.PurchaseInvoiceId=inv.ID
 JOIN dbo.JournalEntries je ON je.ID=i.JournalEntryId AND je.CompanyID=i.CompanyID
 JOIN dbo.JournalEntryLines jl ON jl.ID=i.JournalEntryLineId AND jl.JournalEntryId=je.ID
 WHERE inv.JournalEntryId<>je.ID OR inv.JournalEntryId IS NULL
 OR je.SourceType IS NULL OR je.SourceType<>'PurchaseInvoice' OR je.SourceId IS NULL OR je.SourceId<>inv.ID
 OR inv.Status<>'Posted' OR je.Status<>'Posted'
 OR ISNULL(po.ProjectId,0)<>ISNULL(inv.ProjectId,0) OR ISNULL(jl.ProjectId,0)<>ISNULL(inv.ProjectId,0)
 OR jl.AccountId<>il.ExpenseAccountId OR ISNULL(jl.CostCenterId,0)<>ISNULL(il.CostCenterId,0)
 OR jl.[LineNo]<>il.[LineNo])
 THROW 51702,'Invoice provenance must identify the original posted net line and its scoped source.',1;
END;
GO
COMMIT TRANSACTION;
