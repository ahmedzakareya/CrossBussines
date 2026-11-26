-- Private R7 partial invoice QUANTITY provenance foundation. No posting, backfill or runtime activation.
-- A future writer must create invoice + header + complete slices + seal in ONE transaction.
-- Readers MUST refuse any unsealed history. This DDL alone does not implement partial invoicing.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.PurchaseOrderInvoiceLinks','U') IS NULL
 OR OBJECT_ID('dbo.PurchaseOrderInvoiceLineLinks','U') IS NULL
 OR OBJECT_ID('dbo.GoodsReceiptLines','U') IS NULL
 THROW 51900,'Original procurement and invoice line provenance must precede partial provenance.',1;
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.Projects') AND name='AK_Projects_ID_CompanyID')
 ALTER TABLE dbo.Projects ADD CONSTRAINT AK_Projects_ID_CompanyID UNIQUE(ID,CompanyID);
IF OBJECT_ID('dbo.ConstructionPartialInvoices','U') IS NULL
 CREATE TABLE dbo.ConstructionPartialInvoices(
 ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionPartialInvoices PRIMARY KEY,
 CompanyID int NOT NULL,ProjectId int NOT NULL,PurchaseOrderId int NOT NULL,PurchaseInvoiceId int NOT NULL,JournalEntryId int NOT NULL,
 RequestId uniqueidentifier NOT NULL,RequestHash nvarchar(64) NOT NULL,SourceVersion nvarchar(64) NOT NULL,
 CreatedBy int NOT NULL,CreatedAt datetime2 NOT NULL,IsSealed bit NOT NULL,
 CONSTRAINT CK_ConstructionPartialInvoices_Identity CHECK(CompanyID>0 AND ProjectId>0 AND PurchaseOrderId>0 AND PurchaseInvoiceId>0 AND JournalEntryId>0 AND CreatedBy>0
 AND RequestId<>'00000000-0000-0000-0000-000000000000' AND DATALENGTH(RequestHash)=128 AND DATALENGTH(SourceVersion)=128),
 CONSTRAINT AK_ConstructionPartialInvoices_Scope UNIQUE(ID,CompanyID,ProjectId,PurchaseOrderId,PurchaseInvoiceId,JournalEntryId),
 CONSTRAINT UQ_ConstructionPartialInvoices_Request UNIQUE(CompanyID,RequestId),
 CONSTRAINT UQ_ConstructionPartialInvoices_Invoice UNIQUE(CompanyID,PurchaseInvoiceId),
 CONSTRAINT FK_ConstructionPartialInvoices_Project FOREIGN KEY(ProjectId,CompanyID) REFERENCES dbo.Projects(ID,CompanyID),
 CONSTRAINT FK_ConstructionPartialInvoices_Order FOREIGN KEY(PurchaseOrderId,CompanyID) REFERENCES dbo.PurchaseOrders(ID,CompanyID),
 CONSTRAINT FK_ConstructionPartialInvoices_Invoice FOREIGN KEY(PurchaseInvoiceId,CompanyID) REFERENCES dbo.PurchaseInvoices(ID,CompanyID),
 CONSTRAINT FK_ConstructionPartialInvoices_Journal FOREIGN KEY(JournalEntryId,CompanyID) REFERENCES dbo.JournalEntries(ID,CompanyID));
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionPartialInvoices') AND name='IX_ConstructionPartialInvoices_Order')
 CREATE INDEX IX_ConstructionPartialInvoices_Order ON dbo.ConstructionPartialInvoices(CompanyID,PurchaseOrderId);
IF OBJECT_ID('dbo.ConstructionPartialInvoiceSlices','U') IS NULL
 CREATE TABLE dbo.ConstructionPartialInvoiceSlices(
 ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionPartialInvoiceSlices PRIMARY KEY,
 HeaderId int NOT NULL,CompanyID int NOT NULL,ProjectId int NOT NULL,PurchaseOrderId int NOT NULL,PurchaseInvoiceId int NOT NULL,JournalEntryId int NOT NULL,
 PurchaseOrderLineId int NOT NULL,GoodsReceiptId int NOT NULL,GoodsReceiptLineId int NOT NULL,PurchaseInvoiceLineId int NOT NULL,JournalEntryLineId int NOT NULL,
 Quantity decimal(28,8) NOT NULL,
 CONSTRAINT CK_ConstructionPartialInvoiceSlices_Quantity CHECK(Quantity>0),
 CONSTRAINT UQ_ConstructionPartialInvoiceSlices_Edge UNIQUE(HeaderId,PurchaseInvoiceLineId,GoodsReceiptLineId),
 CONSTRAINT FK_ConstructionPartialInvoiceSlices_Header FOREIGN KEY(HeaderId,CompanyID,ProjectId,PurchaseOrderId,PurchaseInvoiceId,JournalEntryId)
 REFERENCES dbo.ConstructionPartialInvoices(ID,CompanyID,ProjectId,PurchaseOrderId,PurchaseInvoiceId,JournalEntryId),
 CONSTRAINT FK_ConstructionPartialInvoiceSlices_OrderLine FOREIGN KEY(PurchaseOrderLineId,PurchaseOrderId) REFERENCES dbo.PurchaseOrderLines(ID,PurchaseOrderId),
 CONSTRAINT FK_ConstructionPartialInvoiceSlices_InvoiceLine FOREIGN KEY(PurchaseInvoiceLineId,PurchaseInvoiceId) REFERENCES dbo.PurchaseInvoiceLines(ID,PurchaseInvoiceId),
 CONSTRAINT FK_ConstructionPartialInvoiceSlices_JournalLine FOREIGN KEY(JournalEntryLineId,JournalEntryId) REFERENCES dbo.JournalEntryLines(ID,JournalEntryId),
 CONSTRAINT FK_ConstructionPartialInvoiceSlices_Receipt FOREIGN KEY(GoodsReceiptId) REFERENCES dbo.GoodsReceipts(ID),
 CONSTRAINT FK_ConstructionPartialInvoiceSlices_ReceiptLine FOREIGN KEY(GoodsReceiptLineId) REFERENCES dbo.GoodsReceiptLines(ID));
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionPartialInvoiceSlices') AND name='IX_ConstructionPartialInvoiceSlices_Receipt')
 CREATE INDEX IX_ConstructionPartialInvoiceSlices_Receipt ON dbo.ConstructionPartialInvoiceSlices(CompanyID,GoodsReceiptLineId) INCLUDE(Quantity);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionPartialInvoiceSlices') AND name='IX_ConstructionPartialInvoiceSlices_OrderLine')
 CREATE INDEX IX_ConstructionPartialInvoiceSlices_OrderLine ON dbo.ConstructionPartialInvoiceSlices(CompanyID,PurchaseOrderLineId) INCLUDE(Quantity);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionPartialInvoiceSlices') AND name='IX_ConstructionPartialInvoiceSlices_InvoiceLine')
 CREATE INDEX IX_ConstructionPartialInvoiceSlices_InvoiceLine ON dbo.ConstructionPartialInvoiceSlices(CompanyID,PurchaseInvoiceLineId) INCLUDE(Quantity);
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPartialInvoices_SourceAndSeal ON dbo.ConstructionPartialInvoices AFTER INSERT,UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 DECLARE @owners TABLE(ID int PRIMARY KEY);
 INSERT @owners SELECT DISTINCT p.ID FROM dbo.PurchaseOrders p WITH(UPDLOCK,HOLDLOCK)
 JOIN inserted i ON i.PurchaseOrderId=p.ID AND i.CompanyID=p.CompanyID;
 IF EXISTS(SELECT 1 FROM deleted)
 BEGIN
  IF NOT EXISTS(SELECT 1 FROM inserted) OR EXISTS(SELECT 1 FROM deleted d JOIN inserted i ON i.ID=d.ID WHERE d.IsSealed<>0 OR i.IsSealed<>1)
   THROW 51901,'Partial invoice history is immutable; only the initial seal transition is permitted.',1;
  IF EXISTS(SELECT ID,CompanyID,ProjectId,PurchaseOrderId,PurchaseInvoiceId,JournalEntryId,RequestId,
    RequestHash COLLATE Latin1_General_100_BIN2,SourceVersion COLLATE Latin1_General_100_BIN2,CreatedBy,CreatedAt FROM inserted
   EXCEPT SELECT ID,CompanyID,ProjectId,PurchaseOrderId,PurchaseInvoiceId,JournalEntryId,RequestId,
    RequestHash COLLATE Latin1_General_100_BIN2,SourceVersion COLLATE Latin1_General_100_BIN2,CreatedBy,CreatedAt FROM deleted)
   THROW 51901,'Partial invoice source identities cannot be rewritten.',1;
 END
 ELSE IF EXISTS(SELECT 1 FROM inserted WHERE IsSealed<>0)
  THROW 51902,'Insert unsealed provenance before attaching complete invoice slices.',1;
 IF EXISTS(SELECT 1 FROM inserted h
  JOIN dbo.PurchaseOrders p ON p.ID=h.PurchaseOrderId AND p.CompanyID=h.CompanyID
  JOIN dbo.PurchaseInvoices v ON v.ID=h.PurchaseInvoiceId AND v.CompanyID=h.CompanyID
  JOIN dbo.JournalEntries j ON j.ID=h.JournalEntryId AND j.CompanyID=h.CompanyID
  WHERE p.ProjectId IS NULL OR p.ProjectId<>h.ProjectId OR v.ProjectId IS NULL OR v.ProjectId<>h.ProjectId
   OR p.VendorId<>v.VendorId OR p.Status NOT IN('Approved','Received') OR v.Status<>'Posted' OR j.Status<>'Posted'
   OR v.JournalEntryId IS NULL OR v.JournalEntryId<>j.ID OR j.SourceType IS NULL OR j.SourceType<>'PurchaseInvoice' OR j.SourceId IS NULL OR j.SourceId<>v.ID
   OR p.CurrencyId IS NULL OR v.CurrencyId IS NULL OR p.CurrencyId<>v.CurrencyId
   OR p.ExchangeRate IS NULL OR p.ExchangeRate<>1 OR v.ExchangeRate IS NULL OR v.ExchangeRate<>1
   OR j.FunctionalCurrencyId IS NULL OR j.FunctionalCurrencyId<>v.CurrencyId
   OR j.FunctionalCurrencyDecimals IS NULL OR j.FunctionalCurrencyDecimals NOT BETWEEN 0 AND 8
   OR EXISTS(SELECT 1 FROM dbo.PurchaseOrderInvoiceLinks old WHERE old.CompanyID=h.CompanyID AND old.PurchaseOrderId=h.PurchaseOrderId))
  THROW 51903,'Partial provenance requires an owned posted project invoice and no legacy full conversion.',1;
 IF EXISTS(SELECT 1 FROM inserted h WHERE h.IsSealed=1 AND
  (NOT EXISTS(SELECT 1 FROM dbo.ConstructionPartialInvoiceSlices s WHERE s.HeaderId=h.ID)
   OR NOT EXISTS(SELECT 1 FROM dbo.PurchaseInvoiceLines l WHERE l.PurchaseInvoiceId=h.PurchaseInvoiceId)
   OR EXISTS(SELECT 1 FROM dbo.PurchaseInvoiceLines l WHERE l.PurchaseInvoiceId=h.PurchaseInvoiceId AND
     (l.Qty<=0 OR l.Qty<>COALESCE((SELECT SUM(s.Quantity) FROM dbo.ConstructionPartialInvoiceSlices s WHERE s.HeaderId=h.ID AND s.PurchaseInvoiceLineId=l.ID),0)))))
  THROW 51904,'Complete invoice quantity coverage is required before sealing.',1;
 -- Revalidate mutable parent evidence at seal, not only when each slice was inserted.
 IF EXISTS(SELECT 1 FROM inserted h
  JOIN dbo.ConstructionPartialInvoiceSlices s ON s.HeaderId=h.ID
  JOIN dbo.PurchaseOrders p ON p.ID=s.PurchaseOrderId AND p.CompanyID=s.CompanyID
  JOIN dbo.PurchaseOrderLines ol ON ol.ID=s.PurchaseOrderLineId
  JOIN dbo.GoodsReceipts r ON r.ID=s.GoodsReceiptId
  JOIN dbo.GoodsReceiptLines rl ON rl.ID=s.GoodsReceiptLineId
  JOIN dbo.PurchaseInvoiceLines il ON il.ID=s.PurchaseInvoiceLineId
  JOIN dbo.JournalEntryLines jl ON jl.ID=s.JournalEntryLineId
  WHERE h.IsSealed=1 AND (r.CompanyID<>s.CompanyID OR r.PurchaseOrderId IS NULL OR r.PurchaseOrderId<>p.ID
   OR r.VendorId IS NULL OR r.VendorId<>p.VendorId OR r.Status<>'Posted' OR r.InvoiceId IS NOT NULL
   OR r.CurrencyId IS NULL OR r.CurrencyId<>p.CurrencyId OR r.ExchangeRate IS NULL OR r.ExchangeRate<>1
   OR rl.GoodsReceiptId<>r.ID OR rl.PurchaseOrderLineId IS NULL OR rl.PurchaseOrderLineId<>ol.ID
   OR ol.ItemId IS NULL OR rl.ItemId<>ol.ItemId OR ISNULL(rl.UoMId,0)<>ISNULL(ol.UoMId,0)
   OR rl.Qty<=0 OR ol.Qty<=0 OR jl.ProjectId IS NULL OR jl.ProjectId<>s.ProjectId
   OR jl.AccountId<>il.ExpenseAccountId OR jl.[LineNo]<>il.[LineNo] OR ISNULL(jl.CostCenterId,0)<>ISNULL(il.CostCenterId,0)
   OR jl.Credit<>0 OR jl.Debit<>il.LineTotal
   OR rl.Qty<(SELECT SUM(v.Quantity) FROM dbo.ConstructionPartialInvoiceSlices v WHERE v.CompanyID=s.CompanyID AND v.GoodsReceiptLineId=s.GoodsReceiptLineId)
   OR ol.Qty<(SELECT SUM(v.Quantity) FROM dbo.ConstructionPartialInvoiceSlices v WHERE v.CompanyID=s.CompanyID AND v.PurchaseOrderLineId=s.PurchaseOrderLineId)))
  THROW 51910,'Partial invoice sources changed before the immutable seal.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_PurchaseOrderInvoiceLinks_NoPartialMix ON dbo.PurchaseOrderInvoiceLinks AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 DECLARE @owners TABLE(ID int PRIMARY KEY);
 INSERT @owners SELECT DISTINCT p.ID FROM dbo.PurchaseOrders p WITH(UPDLOCK,HOLDLOCK)
 JOIN inserted i ON i.PurchaseOrderId=p.ID AND i.CompanyID=p.CompanyID;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionPartialInvoices h ON h.CompanyID=i.CompanyID AND h.PurchaseOrderId=i.PurchaseOrderId)
  THROW 51911,'A partial invoice source cannot also become a legacy whole-order conversion.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPartialInvoiceSlices_Immutable ON dbo.ConstructionPartialInvoiceSlices AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 51905,'Partial invoice receipt slices are immutable historical evidence.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPartialInvoiceSlices_SourceAndCap ON dbo.ConstructionPartialInvoiceSlices AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 -- The future writer locks the PO BEFORE writing. This trigger also serializes direct inserts;
 -- a conflicting writer may be a deadlock victim, but cannot commit a double-consumed quantity.
 DECLARE @owners TABLE(ID int PRIMARY KEY);
 INSERT @owners SELECT DISTINCT p.ID FROM dbo.PurchaseOrders p WITH(UPDLOCK,HOLDLOCK)
 JOIN inserted i ON i.PurchaseOrderId=p.ID AND i.CompanyID=p.CompanyID;
 IF EXISTS(SELECT 1 FROM inserted i
  JOIN dbo.ConstructionPartialInvoices h ON h.ID=i.HeaderId
  JOIN dbo.PurchaseOrders p ON p.ID=i.PurchaseOrderId AND p.CompanyID=i.CompanyID
  JOIN dbo.PurchaseOrderLines ol ON ol.ID=i.PurchaseOrderLineId AND ol.PurchaseOrderId=p.ID
  JOIN dbo.GoodsReceipts r ON r.ID=i.GoodsReceiptId
  JOIN dbo.GoodsReceiptLines rl ON rl.ID=i.GoodsReceiptLineId
  JOIN dbo.PurchaseInvoiceLines il ON il.ID=i.PurchaseInvoiceLineId AND il.PurchaseInvoiceId=i.PurchaseInvoiceId
  JOIN dbo.JournalEntryLines jl ON jl.ID=i.JournalEntryLineId AND jl.JournalEntryId=i.JournalEntryId
  WHERE h.IsSealed<>0 OR r.CompanyID<>i.CompanyID OR r.PurchaseOrderId IS NULL OR r.PurchaseOrderId<>p.ID
   OR r.VendorId IS NULL OR r.VendorId<>p.VendorId OR r.Status<>'Posted' OR r.InvoiceId IS NOT NULL
   OR r.CurrencyId IS NULL OR r.CurrencyId<>p.CurrencyId OR r.ExchangeRate IS NULL OR r.ExchangeRate<>1
   OR rl.GoodsReceiptId<>r.ID OR rl.PurchaseOrderLineId IS NULL OR rl.PurchaseOrderLineId<>ol.ID
   OR ol.ItemId IS NULL OR rl.ItemId<>ol.ItemId OR ISNULL(rl.UoMId,0)<>ISNULL(ol.UoMId,0)
   OR rl.Qty<=0 OR ol.Qty<=0 OR jl.ProjectId IS NULL OR jl.ProjectId<>i.ProjectId
   OR jl.AccountId<>il.ExpenseAccountId OR jl.[LineNo]<>il.[LineNo] OR ISNULL(jl.CostCenterId,0)<>ISNULL(il.CostCenterId,0)
   OR jl.Credit<>0 OR jl.Debit<>il.LineTotal
   OR EXISTS(SELECT 1 FROM dbo.ConstructionPartialInvoiceSlices other WHERE other.CompanyID=i.CompanyID AND other.PurchaseInvoiceLineId=i.PurchaseInvoiceLineId
     AND (other.PurchaseOrderLineId<>i.PurchaseOrderLineId OR other.JournalEntryLineId<>i.JournalEntryLineId)))
  THROW 51906,'Receipt slices must identify the original scoped receipt and posted invoice net line.',1;
 IF EXISTS(SELECT 1 FROM dbo.ConstructionPartialInvoiceSlices s WITH(UPDLOCK,HOLDLOCK)
  JOIN dbo.GoodsReceiptLines r ON r.ID=s.GoodsReceiptLineId
  WHERE EXISTS(SELECT 1 FROM inserted i WHERE i.CompanyID=s.CompanyID AND i.GoodsReceiptLineId=s.GoodsReceiptLineId)
  GROUP BY s.CompanyID,s.GoodsReceiptLineId,r.Qty HAVING SUM(s.Quantity)>r.Qty)
  THROW 51907,'Cumulative billed quantity exceeds the original receipt.',1;
 IF EXISTS(SELECT 1 FROM dbo.ConstructionPartialInvoiceSlices s WITH(UPDLOCK,HOLDLOCK)
  JOIN dbo.PurchaseOrderLines p ON p.ID=s.PurchaseOrderLineId
  WHERE EXISTS(SELECT 1 FROM inserted i WHERE i.CompanyID=s.CompanyID AND i.PurchaseOrderLineId=s.PurchaseOrderLineId)
  GROUP BY s.CompanyID,s.PurchaseOrderLineId,p.Qty HAVING SUM(s.Quantity)>p.Qty)
  THROW 51908,'Cumulative billed quantity exceeds the original purchase order line.',1;
 IF EXISTS(SELECT 1 FROM dbo.ConstructionPartialInvoiceSlices s WITH(UPDLOCK,HOLDLOCK)
  JOIN dbo.PurchaseInvoiceLines v ON v.ID=s.PurchaseInvoiceLineId
  WHERE EXISTS(SELECT 1 FROM inserted i WHERE i.CompanyID=s.CompanyID AND i.PurchaseInvoiceLineId=s.PurchaseInvoiceLineId)
  GROUP BY s.CompanyID,s.PurchaseInvoiceLineId,v.Qty HAVING SUM(s.Quantity)>v.Qty)
  THROW 51909,'Receipt mapping exceeds the posted invoice line quantity.',1;
END;
GO
COMMIT TRANSACTION;
