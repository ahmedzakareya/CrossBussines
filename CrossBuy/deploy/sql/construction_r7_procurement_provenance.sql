-- R7/C2 durable source relation. No historical note matching, no money or stock writes.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.PurchaseOrders','U') IS NULL OR OBJECT_ID('dbo.PurchaseInvoices','U') IS NULL
 THROW 51300,'Purchase order and invoice schemas are required before conversion provenance.',1;
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.PurchaseOrders') AND name='AK_PurchaseOrders_ID_CompanyID')
 ALTER TABLE dbo.PurchaseOrders ADD CONSTRAINT AK_PurchaseOrders_ID_CompanyID UNIQUE(ID,CompanyID);
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.PurchaseInvoices') AND name='AK_PurchaseInvoices_ID_CompanyID')
 ALTER TABLE dbo.PurchaseInvoices ADD CONSTRAINT AK_PurchaseInvoices_ID_CompanyID UNIQUE(ID,CompanyID);
IF OBJECT_ID('dbo.PurchaseOrderInvoiceLinks','U') IS NULL
 CREATE TABLE dbo.PurchaseOrderInvoiceLinks(
  ID int IDENTITY NOT NULL CONSTRAINT PK_PurchaseOrderInvoiceLinks PRIMARY KEY,
  CompanyID int NOT NULL, PurchaseOrderId int NOT NULL, PurchaseInvoiceId int NOT NULL,
  CreatedAt datetime2 NOT NULL, CreatedByUserId nvarchar(450) NULL, RowVersion rowversion NOT NULL,
  CONSTRAINT CK_PurchaseOrderInvoiceLinks_Identity CHECK(CompanyID>0 AND PurchaseOrderId>0 AND PurchaseInvoiceId>0),
  CONSTRAINT UQ_PurchaseOrderInvoiceLinks_Order UNIQUE(CompanyID,PurchaseOrderId),
  CONSTRAINT UQ_PurchaseOrderInvoiceLinks_Invoice UNIQUE(CompanyID,PurchaseInvoiceId),
  CONSTRAINT FK_PurchaseOrderInvoiceLinks_Order FOREIGN KEY(PurchaseOrderId,CompanyID) REFERENCES dbo.PurchaseOrders(ID,CompanyID),
  CONSTRAINT FK_PurchaseOrderInvoiceLinks_Invoice FOREIGN KEY(PurchaseInvoiceId,CompanyID) REFERENCES dbo.PurchaseInvoices(ID,CompanyID)
 );
GO
CREATE OR ALTER TRIGGER dbo.TR_PurchaseOrderInvoiceLinks_Immutable ON dbo.PurchaseOrderInvoiceLinks AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 51301,'A conversion source relation is historical evidence and cannot be changed or deleted.',1;
END;
GO
COMMIT TRANSACTION;
